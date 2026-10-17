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

                    // capture — RESET-ANCHORED, per operator: the machine app's
                    // tally resets shortly after each SHIFT START (06:22 closes
                    // shift3's count, ~14:05 closes shift1's, ~22:15 closes
                    // shift2's — carryover sits in the tally until then). So for
                    // shift N: opening reset ∈ [start, start+35min], closing
                    // reset ∈ [end-35min, end+5min], and parts = the tally PEAK
                    // strictly between them (the tally zeroes at the opening
                    // reset and climbs all shift, so peak = final count).
                    var winStartExt = win.StartUtc - TimeSpan.FromMinutes(35);
                    var winEndExt = win.EndUtc + TimeSpan.FromMinutes(35);
                    var tallyRows = await db.Samples.AsNoTracking()
                        .Where(s => s.MachineId == m.Id && s.DataItemId == "Mp1CommonVariable" && s.PartsTally != null
                                    && s.TsUtc >= winStartExt && s.TsUtc <= winEndExt)
                        .OrderBy(s => s.TsUtc).ToListAsync(ct);

                    // has any positive tally at all? (machines without tally data
                    // — e.g. cnc13 pre-adapter-fix — would produce 0-part noise)
                    if (!tallyRows.Any(r => r.PartsTally > 0))
                        continue;

                    // reset EPISODES: first <=2 reading after a >5 within 2 min,
                    // rearmed only when tally climbs back above 2
                    var episodes = new List<DateTime>();
                    bool armed = true;
                    double lastNonzero = 0;
                    DateTime lastNonzeroTs = DateTime.MinValue;
                    foreach (var r in tallyRows)
                    {
                        var v = r.PartsTally!.Value;
                        if (v > 2)
                        {
                            lastNonzero = v;
                            lastNonzeroTs = r.TsUtc;
                            armed = true;
                        }
                        else if (armed && v <= 2 && lastNonzero > 5
                                 && (r.TsUtc - lastNonzeroTs).TotalMinutes <= 2)
                        {
                            episodes.Add(r.TsUtc);
                            armed = false; // stay disarmed while tally sits at 0
                        }
                    }

                    DateTime? openReset = episodes.FirstOrDefault(ts =>
                        ts >= win.StartUtc && ts <= win.StartUtc + TimeSpan.FromMinutes(35));
                    DateTime? closeReset = episodes.FirstOrDefault(ts =>
                        ts >= win.EndUtc - TimeSpan.FromMinutes(35) && ts <= win.EndUtc + TimeSpan.FromMinutes(5));

                    double peak;
                    if (openReset != null && closeReset != null && closeReset > openReset)
                    {
                        peak = tallyRows
                            .Where(r => r.TsUtc > openReset && r.TsUtc < closeReset)
                            .Select(r => r.PartsTally!.Value)
                            .DefaultIfEmpty(0).Max();
                    }
                    else
                    {
                        // fallback: no clean reset pair (collector gap) — peak
                        // within the shift window proper
                        peak = tallyRows
                            .Where(r => r.TsUtc >= win.StartUtc && r.TsUtc <= win.EndUtc)
                            .Select(r => r.PartsTally!.Value)
                            .DefaultIfEmpty(0).Max();
                    }

                    // PartsAtStart = tally right after the opening reset (0 by
                    // construction); PartsAtEnd = peak — the final count.
                    var partsAtStart = 0L;
                    var partsAtEnd = (long)Math.Max(0, Math.Round(peak));

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