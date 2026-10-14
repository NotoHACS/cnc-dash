using CncDash.Server.Data;
using CncDash.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CncDash.Server.Services;

public class MetricsEngine
{
    private readonly IServiceScopeFactory scopes;

    public MetricsEngine(IServiceScopeFactory scopes) => this.scopes = scopes;

    public async Task<MachineStatus?> ComputeAsync(string machineId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CncDbContext>();

        var machine = await db.Machines.AsNoTracking()
            .Include(m => m.Config)
            .FirstOrDefaultAsync(m => m.Id == machineId);
        if (machine?.Config is null) return null;

        return await ComputeAsync(machine, machine.Config, db);
    }

    public async Task<MachineStatus> ComputeAsync(Machine machine, MachineConfig cfg, CncDbContext db)
    {
        var nowUtc = DateTime.UtcNow;
        var status = new MachineStatus
        {
            MachineId = machine.Id,
            DisplayName = machine.DisplayName,
            PartName = cfg.PartName,
        };

        // Active shifts can hold 0..2 windows (overlap);
        var active = ShiftEngine.ActiveShifts(cfg, nowUtc);
        var shift = active.FirstOrDefault();
        if (shift is null)
        {
            // no shift running — show last window if any for reference
            status.Color = StatusColor.Off;
            status.StatusReason = "No shift running";
            status.Tools = await GetToolsAsync(db, machine.Id, cfg);
            return status;
        }
        status.ShiftNumber = shift.Number;
        status.ShiftName = $"Shift {shift.Number}";
        status.ShiftStartUtc = shift.StartUtc;
        status.ShiftEndUtcNext = shift.EndUtc;
        status.ShiftElapsedMinutes = (nowUtc - shift.StartUtc).TotalMinutes;

        // ---- timeline from samples -------------------------------------------------
        // Include one pre-window row per series for continuity (state + counter baseline).
        var samples = await db.Samples.AsNoTracking()
            .Where(s => s.MachineId == machine.Id && s.TsUtc >= shift.StartUtc)
            .OrderBy(s => s.TsUtc).ThenBy(s => s.Id)
            .ToListAsync();

        var lastCountRow = await db.Samples.AsNoTracking()
            .Where(s => s.MachineId == machine.Id && s.DataItemId == "Mppartcount" && s.TsUtc < shift.StartUtc)
            .OrderByDescending(s => s.TsUtc).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync();

        var lastToolRow = await db.Samples.AsNoTracking()
            .Where(s => s.MachineId == machine.Id && s.DataItemId == "Mp1CurrentTool" && s.TsUtc < shift.StartUtc)
            .OrderByDescending(s => s.TsUtc)
            .FirstOrDefaultAsync();

        var lastSample = samples.LastOrDefault() ?? lastCountRow;
        status.LastSampleUtc = lastSample?.TsUtc;
        status.Online = lastSample is not null &&
                        (nowUtc - lastSample.TsUtc).TotalMinutes < 10;

        // ---- spindle state & uptime ------------------------------------------------
        var speedRows = samples.Where(s => s.DataItemId == "MS1speed").ToList();

        var lastSpeedBefore = await db.Samples.AsNoTracking()
            .Where(s => s.MachineId == machine.Id && s.DataItemId == "MS1speed" && s.TsUtc < shift.StartUtc)
            .OrderByDescending(s => s.TsUtc).FirstOrDefaultAsync();
        if (lastSpeedBefore != null)
        {
            // clamp the pre-shift baseline to the window start so up-time
            // intervals never accumulate before the shift began
            lastSpeedBefore.TsUtc = lastSpeedBefore.TsUtc > shift.StartUtc
                ? lastSpeedBefore.TsUtc
                : shift.StartUtc;
            speedRows.Insert(0, lastSpeedBefore);
        }

        // up-intervals: [ts of sample showing "running", ts of next state change)
        double upSeconds = 0;
        DateTime? runFrom = null;
        foreach (var row in speedRows.OrderBy(r => r.TsUtc))
        {
            var running = (row.SpindleSpeed ?? 0) >= cfg.SpindleRunningRpm;
            if (running && runFrom == null) runFrom = row.TsUtc;
            else if (!running && runFrom != null)
            {
                upSeconds += (row.TsUtc - runFrom.Value).TotalSeconds;
                runFrom = null;
            }
        }
        if (runFrom != null)
            upSeconds += (nowUtc - runFrom.Value).TotalSeconds;

        var lastSpeed = speedRows.LastOrDefault()?.SpindleSpeed;
        if (lastSpeed.HasValue)
        {
            status.SpindleRunning = lastSpeed >= cfg.SpindleRunningRpm;
        }
        else
        {
            // No speed data at all (agent not exposing MS1speed): fall back to
            // execution state so we don't show RED forever.
            var lastExec = samples.LastOrDefault(s => s.Execution != null)?.Execution;
            status.SpindleRunning = lastExec == "ACTIVE" || lastSpeedBefore?.SpindleSpeed >= cfg.SpindleRunningRpm;
            if (status.SpindleRunning)
                status.StatusReason = "running (execution state; no speed data)";
            status.SpindleUptimePercent = 0;
        }
        if (lastSpeed.HasValue)
        {
            status.SpindleUptimePercent = status.ShiftElapsedMinutes > 0.5
                ? Math.Min(100, upSeconds / (status.ShiftElapsedMinutes * 60) * 100)
                : 0;
        }

        // ---- parts & cycle learning -------------------------------------------------
        var countRows = samples.Where(s => s.DataItemId == "Mppartcount").OrderBy(r => r.TsUtc).ToList();
        long partsThisShift = 0;
        var cycleSeconds = new List<double>();
        DateTime? prevCountTs = null;

        // Baseline = last count row before the shift. Without one (server restart),
        // the first in-window row only SEEDS the counter — its absolute value belongs
        // to the part of the cycle already completed before this shift, so we must
        // not credit it.
        long prevCount;
        bool seededWithoutBaseline = false;
        if (lastCountRow != null)
        {
            prevCount = lastCountRow.PartCount ?? 0;
            prevCountTs = lastCountRow.TsUtc;
        }
        else
        {
            prevCount = countRows.FirstOrDefault()?.PartCount ?? 0;
            seededWithoutBaseline = countRows.Count > 0;
        }

        // skip the seed row when we seeded from an in-window row
        var rows = seededWithoutBaseline ? countRows.Skip(1) : countRows;
        foreach (var row in rows)
        {
            var cur = row.PartCount ?? prevCount;
            long delta;
            if (cur >= prevCount) delta = cur - prevCount;
            else delta = cur; // counter reset (program restart): count what's now on the counter
            partsThisShift += delta;

            if (delta > 0 && prevCountTs != null)
            {
                var dt = (row.TsUtc - prevCountTs.Value).TotalSeconds;
                if (dt > 5 && dt < 24 * 3600)
                    cycleSeconds.Add(dt);
            }

            prevCount = cur;
            if (delta > 0 || prevCountTs == null) prevCountTs = row.TsUtc;
        }

        status.PartsThisShift = partsThisShift;
        status.CyclesCollected = countRows.Count - (lastCountRow != null ? 1 : 0);

        // learned cycle: rolling median of window, blended with seeded expectation
        if (cycleSeconds.Count > 0)
        {
            cycleSeconds.Sort();
            var median = cycleSeconds.Count % 2 == 1
                ? cycleSeconds[cycleSeconds.Count / 2]
                : (cycleSeconds[cycleSeconds.Count / 2 - 1] + cycleSeconds[cycleSeconds.Count / 2]) / 2;
            status.LearnedCycleSeconds = median;
        }
        else if (cfg.ExpectedCycleSeconds > 0)
            status.LearnedCycleSeconds = cfg.ExpectedCycleSeconds;

        if (status.LearnedCycleSeconds is > 0)
        {
            status.MaxPartsPerHour = 3600.0 / status.LearnedCycleSeconds.Value * cfg.PartsPerCycle;
        }

        // ---- status color -----------------------------------------------------------
        if (!status.Online)
        {
            status.Color = StatusColor.Off;
            status.StatusReason = "No recent data from collector";
        }
        else if (!status.SpindleRunning)
        {
            status.Color = StatusColor.Red;
            status.StatusReason = "Spindle stopped";
        }
        else if (status.LearnedCycleSeconds is null)
        {
            status.Color = StatusColor.Learning;
            status.StatusReason = "Learning cycle time";
        }
        else
        {
            var maxRate = status.MaxPartsPerHour!.Value;
            status.ExpectedParts = Math.Floor(maxRate * status.ShiftElapsedMinutes / 60.0);
            status.PercentOfExpected = status.ExpectedParts > 0
                ? partsThisShift / status.ExpectedParts * 100
                : 0;
            if (status.PercentOfExpected >= cfg.GreenThresholdPercent)
            {
                status.Color = StatusColor.Green;
                status.StatusReason = $"Running at {status.PercentOfExpected:0}% of target";
            }
            else
            {
                status.Color = StatusColor.Yellow;
                status.StatusReason = $"Below {cfg.GreenThresholdPercent:0}% target ({status.PercentOfExpected:0}%)";
            }
        }

        status.CurrentTool = samples.LastOrDefault(s => s.CurrentTool != null)?.CurrentTool
                             ?? lastToolRow?.CurrentTool;
        status.Program = samples.LastOrDefault(s => s.Program != null)?.Program;

        status.Tools = await GetToolsAsync(db, machine.Id, cfg);
        return status;
    }

    private static async Task<List<ToolStatus>> GetToolsAsync(CncDbContext db, string machineId, MachineConfig cfg)
    {
        var tools = await db.ToolAssets.AsNoTracking()
            .Where(t => t.MachineId == machineId)
            .ToListAsync();

        return tools.Select(t =>
        {
            double? pct = null;
            var grade = "na";
            if (t.LifeInitial is > 0 && t.LifeValue.HasValue)
            {
                pct = t.CountDirection == "UP"
                    ? (t.LifeInitial.Value - t.LifeValue.Value) / t.LifeInitial.Value * 100
                    : t.LifeValue.Value / t.LifeInitial.Value * 100;
                grade = pct < 0 || IsExpiredStatus(t.Status) ? "ng"
                    : pct < cfg.WarnThresholdPercent ? "warn"
                    : "ok";
            }
            else if (IsExpiredStatus(t.Status))
                grade = "ng";

            return new ToolStatus
            {
                AssetId = t.AssetId,
                ToolNumber = t.ToolNumber,
                Pot = t.Pot,
                Grade = grade,
                LifeType = t.LifeType,
                PercentRemaining = pct,
                Status = t.Status,
            };
        }).OrderBy(t => t.ToolNumber ?? int.MaxValue).ToList();
    }

    private static bool IsExpiredStatus(string? status) =>
        status is not null && status is not ("AVAILABLE" or "NEW" or "USED");
}