using CncDash.Server.Data;
using CncDash.Server.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CncDash.Server.Services;

// Two background duties in one loop:
//  1. SHIFT HISTORY: for every configured shift window, count the parts whose
//     BATCH STARTED inside that window. The machine increments its tally when
//     a batch COMPLETES, so a batch that starts at 13:59 and finishes at
//     14:32 lands in the 2nd shift's counter — but the operator wants it
//     credited to the 1st shift (the shift that started it). We get that by
//     pairing each part increment with the pallet-swap (cycle var) change that
//     opened its batch: batch k spans [swap_k, swap_k+1) and its parts are the
//     tally increments inside that interval. Attribution = the shift window
//     containing swap_k.
//
//     The tally resets ~3 min after each shift boundary and the reset time
//     DRIFTS (06:22 -> 07:28 over days), so the stored window uses the actual
//     reset-to-reset pair (what the machine really did), never the nominal one.
//  2. SAMPLES RETENTION: delete Samples older than SAMPLE_RETENTION_DAYS
//     (default 7) with a per-machine floor of the newest 1000 rows.
public class ShiftHistoryService : BackgroundService
{
    private readonly IServiceScopeFactory scopes;
    private readonly ILogger<ShiftHistoryService> log;

    public ShiftHistoryService(IServiceScopeFactory scopes, ILogger<ShiftHistoryService> log)
    {
        this.scopes = scopes;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // small startup delay so migrations/hosted services settle
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch { }

        var nextPrune = DateTime.UtcNow.AddMinutes(5);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CaptureDueShiftsAsync(stoppingToken);

                if (DateTime.UtcNow >= nextPrune)
                {
                    await PruneSamplesAsync(stoppingToken);
                    nextPrune = DateTime.UtcNow.AddHours(1);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "shifthistory/prune loop error");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
        }
    }

    private async Task CaptureDueShiftsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CncDbContext>();
        var nowUtc = DateTime.UtcNow;

        var machines = await db.Machines.AsNoTracking().Include(m => m.Config).ToListAsync(ct);
        foreach (var m in machines)
        {
            var cfg = m.Config;
            if (cfg is null) continue;
            var tz = ShiftEngine.GetTz(cfg.TimeZone);
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

            // one query for the last 3 days of common-var rows (tally + vars)
            var from = nowUtc.AddDays(-3);
            var rows = await db.Samples.AsNoTracking()
                .Where(s => s.MachineId == m.Id && s.DataItemId == "Mp1CommonVariable" && s.TsUtc >= from)
                .OrderBy(s => s.TsUtc).ToListAsync(ct);
            if (rows.Count == 0) continue;

            // ---- tally resets (the machine's actual shift boundaries) -------
            var resets = new List<DateTime>();
            bool armed = true;
            double lastNonzero = 0;
            DateTime lastNonzeroTs = DateTime.MinValue;
            foreach (var r in rows)
            {
                var v = r.PartsTally ?? 0;
                if (v > 2) { lastNonzero = v; lastNonzeroTs = r.TsUtc; armed = true; }
                else if (armed && v <= 2 && lastNonzero > 5
                         && (r.TsUtc - lastNonzeroTs).TotalMinutes <= 2)
                {
                    resets.Add(r.TsUtc);
                    armed = false;
                }
            }
            if (resets.Count == 0) continue;

            // ---- tally increments (one per part) ----------------------------
            var increments = new List<DateTime>();
            double prevTally = 0;
            foreach (var r in rows)
            {
                var t = r.PartsTally ?? 0;
                if (t > prevTally + 0.5)
                {
                    var n = (int)Math.Round(t - prevTally);
                    for (int i = 0; i < n; i++) increments.Add(r.TsUtc);
                }
                prevTally = t;
            }

            // ---- batch starts (pallet swap = cycle-var change) -------------
            var batchStarts = new List<DateTime>();
            if (cfg.CycleVarIndex > 0)
            {
                var prefix = cfg.CycleVarIndex.ToString() + "=";
                long? lastNonZeroVar = null;
                foreach (var r in rows)
                {
                    if (r.CommonVars is null) continue;
                    var v = CommonVarValueLong(r.CommonVars, prefix);
                    if (v is null || v <= 0) continue;
                    if (lastNonZeroVar.HasValue && v.Value != lastNonZeroVar.Value)
                        batchStarts.Add(r.TsUtc); // new pallet = new batch
                    lastNonZeroVar = v.Value;
                }
            }

            // ---- shift windows to attribute into ---------------------------
            var windows = new List<(int Num, DateTime Date, DateTime Start, DateTime End)>();
            for (int dayBack = 3; dayBack >= 0; dayBack--)
            {
                var date = nowLocal.Date.AddDays(-dayBack);
                for (int n = 1; n <= 3; n++)
                {
                    var w = ShiftEngine.ShiftOnDate(cfg, n, date);
                    if (w is null) continue;
                    windows.Add((n, date, w.StartUtc, w.EndUtc));
                }
            }

            foreach (var w in windows)
            {
                // capture a shift once it ended AND had time to finish an
                // in-flight batch (45 min grace) — otherwise a straddling
                // batch's parts could still arrive after we snapshot
                if (w.End + TimeSpan.FromMinutes(45) > nowUtc) continue;

                var exists = await db.ShiftHistories.AsNoTracking().AnyAsync(
                    h => h.MachineId == m.Id && h.LocalDate == w.Date && h.ShiftNumber == w.Num, ct);
                if (exists) continue;

                // parts: B-rule — increments whose BATCH START falls in this
                // window. Batch k = [swap_k, swap_k+1).
                long parts = 0;
                var usedBatchStarts = new List<DateTime>();
                if (batchStarts.Count > 0)
                {
                    for (int i = 0; i < batchStarts.Count; i++)
                    {
                        var bs = batchStarts[i];
                        if (bs < w.Start || bs >= w.End) continue;
                        var be = i + 1 < batchStarts.Count ? batchStarts[i + 1] : nowUtc;
                        var n = increments.Count(x => x >= bs && x < be);
                        if (n > 0) { parts += n; usedBatchStarts.Add(bs); }
                    }
                }
                else
                {
                    // no cycle var configured: fall back to tally peak between
                    // the reset pair around this window
                    var open = resets.FirstOrDefault(r => r >= w.Start - TimeSpan.FromMinutes(40)
                                                          && r <= w.Start + TimeSpan.FromHours(7));
                    var close = open == default ? default
                        : resets.FirstOrDefault(r => r > open);
                    if (open != default && close != default)
                        parts = (long)rows.Where(r => r.TsUtc > open && r.TsUtc < close)
                                          .Select(r => Math.Max(0, r.PartsTally ?? 0)).DefaultIfEmpty(0).Max();
                }

                // actual machine window: the reset that opened this shift and
                // the one that closed it (drift-tolerant: resets are ~8h apart
                // so a wide bracket holds at most one)
                var openReset = resets.FirstOrDefault(r => r >= w.Start - TimeSpan.FromMinutes(40)
                                                          && r <= w.Start + TimeSpan.FromHours(7));
                var closeReset = openReset == default ? default : resets.FirstOrDefault(r => r > openReset);
                if (closeReset != default && (closeReset - openReset) > TimeSpan.FromHours(12))
                    closeReset = default;

                string? anchorsJson = usedBatchStarts.Count > 0
                    ? JsonSerializer.Serialize(usedBatchStarts)
                    : null;

                // part number (VC65) + program during the shift: the LAST
                // value seen inside the window wins (the job that ran most of
                // the shift)
                string? partName = null;
                if (cfg.PartNameVarIndex > 0)
                {
                    var pnPrefix = cfg.PartNameVarIndex.ToString() + "=";
                    foreach (var r in rows)
                    {
                        if (r.TsUtc < w.Start || r.TsUtc > w.End || r.CommonVars is null) continue;
                        var v = CommonVarValueLong(r.CommonVars, pnPrefix);
                        if (v.HasValue) partName = v.Value.ToString();
                    }
                }
                partName ??= string.IsNullOrWhiteSpace(cfg.PartName) ? null : cfg.PartName;

                string? program = await db.Samples.AsNoTracking()
                    .Where(s => s.MachineId == m.Id && s.DataItemId == "Mpprogram" && s.Program != null
                                && s.TsUtc >= w.Start && s.TsUtc <= w.End)
                    .OrderBy(s => s.TsUtc).Select(s => s.Program)
                    .LastOrDefaultAsync(ct);

                db.ShiftHistories.Add(new Data.Entities.ShiftHistory
                {
                    MachineId = m.Id,
                    ShiftNumber = w.Num,
                    LocalDate = w.Date,
                    StartUtc = w.Start,
                    EndUtc = w.End,
                    OpenResetUtc = openReset == default ? null : openReset,
                    CloseResetUtc = closeReset == default ? null : closeReset,
                    PartsAtStart = 0,
                    PartsAtEnd = parts,
                    PartsCount = parts,
                    PartName = partName,
                    Program = program,
                    CapturedUtc = nowUtc,
                    AnchorsJson = anchorsJson,
                });
                await db.SaveChangesAsync(ct);
                log.LogInformation("shift history captured: {Machine} {Date} shift{N} parts={P}",
                    m.Id, w.Date, w.Num, parts);
            }
        }
    }

    private async Task PruneSamplesAsync(CancellationToken ct)
    {
        var retentionDaysRaw = Environment.GetEnvironmentVariable("SAMPLE_RETENTION_DAYS");
        int retentionDays = int.TryParse(retentionDaysRaw, out var rd) ? rd : 7;

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CncDbContext>();
        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        // keep at least the last 1000 samples per machine no matter what — a
        // machine offline for a week must not come back to an empty table
        var cutoffSafe = await db.Samples.AsNoTracking()
            .Where(s => s.TsUtc < cutoff)
            .GroupBy(s => s.MachineId)
            .Select(g => new { MachineId = g.Key, Recent = g.OrderByDescending(s => s.TsUtc).Skip(1000).Any() })
            .ToListAsync(ct);

        long deleted = 0;
        foreach (var g in cutoffSafe)
        {
            if (!g.Recent) continue; // machine's whole history is old: keep everything
            var removed = await db.Samples
                .Where(s => s.MachineId == g.MachineId && s.TsUtc < cutoff)
                .OrderByDescending(s => s.TsUtc)
                .Skip(1000)
                .ExecuteDeleteAsync(ct);
            deleted += removed;
        }

        if (deleted > 0)
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
            log.LogInformation("retention: pruned {Deleted} samples older than {Days}d", deleted, retentionDays);
        }
    }

    private static long? CommonVarValueLong(string map, string prefix)
    {
        foreach (var part in map.Split(';'))
        {
            if (part.StartsWith(prefix, StringComparison.Ordinal))
            {
                var v = part[prefix.Length..].Trim();
                return long.TryParse(v, out var n) ? n : null;
            }
        }
        return null;
    }
}