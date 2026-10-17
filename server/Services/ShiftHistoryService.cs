using CncDash.Server.Data;
using CncDash.Server.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CncDash.Server.Services;

// Two background duties in one loop:
//  1. SHIFT HISTORY: when local time passes a configured shift end, capture one
//     ShiftHistory row (machine, date, shift#, part count from PartsTally
//     max-inside-window minus baseline, plus VC3 anchor timestamps) — idempotent
//     via a unique check before insert.
//  2. SAMPLES RETENTION: delete Samples older than SAMPLE_RETENTION_DAYS
//     (default 7) and reclaim space with a WAL checkpoint. Runs once per hour.
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

        var lastCheck = DateTime.UtcNow - TimeSpan.FromMinutes(6); // first pass checks last ~6 min + startup
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
            var today = nowLocal.Date;

            // check today's shifts AND yesterday's (a 23:50-07:00 overnight shift
            // with a 6am server blip could straddle) — but only those whose END
            // has passed and which have no row yet
            for (int dayBack = 0; dayBack <= 1; dayBack++)
            {
                var date = today.AddDays(-dayBack);
                for (int n = 1; n <= 3; n++)
                {
                    var win = ShiftEngine.ShiftOnDate(cfg, n, date);
                    if (win is null) continue;
                    if (win.EndUtc > nowUtc) continue; // still running

                    var exists = await db.ShiftHistories.AsNoTracking().AnyAsync(
                        h => h.MachineId == m.Id && h.LocalDate == date && h.ShiftNumber == n, ct);
                    if (exists) continue;

                    // capture — RESET-ANCHORED, per operator: the machine app
                    // resets its tally ~20min AFTER the shift boundary (06:00
                    // shift -> reset ~06:22; carryover from the previous shift
                    // sits in the tally until then). So: find the CLOSING reset
                    // (drop to ~0 after a >5 reading) inside [end-5min,
                    // end+35min]; the shift's parts = peak tally between the
                    // previous reset (or window start) and that closing reset.
                    var winStartExt = win.StartUtc - TimeSpan.FromMinutes(35);
                    var winEndExt = win.EndUtc + TimeSpan.FromMinutes(35);
                    var tallyRows = await db.Samples.AsNoTracking()
                        .Where(s => s.MachineId == m.Id && s.DataItemId == "Mp1CommonVariable" && s.PartsTally != null
                                    && s.TsUtc >= winStartExt && s.TsUtc <= winEndExt)
                        .OrderBy(s => s.TsUtc).ToListAsync(ct);

                    // find all reset moments: reading <=2 preceded by a >5 within 2 min
                    var resets = new List<DateTime>();
                    double prevTally = 0;
                    DateTime prevTs = DateTime.MinValue;
                    foreach (var r in tallyRows)
                    {
                        var v = r.PartsTally!.Value;
                        if (v <= 2 && prevTally > 5 && (r.TsUtc - prevTs).TotalMinutes <= 2)
                            resets.Add(r.TsUtc);
                        if (v > 0) { prevTally = v; prevTs = r.TsUtc; }
                    }

                    // closing reset = last reset in [end-5min, end+35min]
                    var closingReset = resets.LastOrDefault(ts =>
                        ts >= win.EndUtc - TimeSpan.FromMinutes(5) &&
                        ts <= win.EndUtc + TimeSpan.FromMinutes(35));

                    double peakBeforeClosing;
                    double startAfterOpening;
                    if (closingReset != default)
                    {
                        // opening reset = latest reset before the closing one that is
                        // still after (window start - 35min)… strictly, the previous
                        // reset OR the extended window start
                        var earlier = resets.Where(ts => ts < closingReset).ToList();
                        var opening = earlier.Count > 0 ? earlier[^1] : winStartExt;
                        peakBeforeClosing = tallyRows
                            .Where(r => r.TsUtc > opening && r.TsUtc < closingReset)
                            .Select(r => r.PartsTally!.Value)
                            .DefaultIfEmpty(0).Max();
                        // start value: first reading at/after the opening reset
                        startAfterOpening = tallyRows
                            .Where(r => r.TsUtc >= opening && r.TsUtc < closingReset)
                            .Select(r => r.PartsTally!.Value)
                            .DefaultIfEmpty(0).FirstOrDefault();
                    }
                    else
                    {
                        // no closing reset observed (collector gap?): fall back to
                        // peak/max within the shift window proper
                        var inWindow = tallyRows.Where(r => r.TsUtc >= win.StartUtc && r.TsUtc <= win.EndUtc).ToList();
                        peakBeforeClosing = inWindow.Count > 0 ? inWindow.Max(r => r.PartsTally!.Value) : 0;
                        startAfterOpening = inWindow.FirstOrDefault()?.PartsTally ?? 0;
                    }

                    // PartsAtStart = tally right after the opening reset (~0, but
                    // recorded honestly); PartsAtEnd = peak before the closing
                    // reset — that is the final count the operator cares about.
                    var partsAtStart = (long)Math.Max(0, Math.Round(startAfterOpening));
                    var partsAtEnd = (long)Math.Max(0, Math.Round(peakBeforeClosing));

                    string? anchorsJson = null;
                    if (cfg.CycleVarIndex > 0)
                    {
                        var prefix = cfg.CycleVarIndex.ToString() + "=";
                        long? prevV = null;
                        var anchors = new List<DateTime>();
                        foreach (var s in await db.Samples.AsNoTracking()
                            .Where(s => s.MachineId == m.Id && s.DataItemId == "Mp1CommonVariable" && s.CommonVars != null
                                        && s.TsUtc >= win.StartUtc && s.TsUtc <= win.EndUtc)
                            .OrderBy(s => s.TsUtc).ToListAsync(ct))
                        {
                            var v = CommonVarValueLong(s.CommonVars!, prefix);
                            if (v.HasValue)
                            {
                                if (prevV.HasValue && v.Value != prevV.Value) anchors.Add(s.TsUtc);
                                prevV = v.Value;
                            }
                        }
                        anchorsJson = JsonSerializer.Serialize(anchors);
                    }

                    db.ShiftHistories.Add(new Data.Entities.ShiftHistory
                    {
                        MachineId = m.Id,
                        ShiftNumber = n,
                        LocalDate = date,
                        StartUtc = win.StartUtc,
                        EndUtc = win.EndUtc,
                        PartsAtStart = partsAtStart,
                        PartsAtEnd = partsAtEnd,
                        PartsCount = Math.Max(0, partsAtEnd - partsAtStart),
                        CapturedUtc = nowUtc,
                        AnchorsJson = anchorsJson,
                    });
                    await db.SaveChangesAsync(ct);
                    log.LogInformation("shift history captured: {Machine} {Date} shift{N} parts={P}",
                        m.Id, date, n, partsAtEnd - partsAtStart);
                }
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