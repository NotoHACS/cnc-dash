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
            .Where(s => s.MachineId == machine.Id && s.PartCount != null && s.TsUtc < shift.StartUtc)
            .OrderByDescending(s => s.TsUtc).ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync();

        var lastToolRow = await db.Samples.AsNoTracking()
            .Where(s => s.MachineId == machine.Id && s.CurrentTool != null && s.TsUtc < shift.StartUtc)
            .OrderByDescending(s => s.TsUtc)
            .FirstOrDefaultAsync();

        var lastCuttingRow = await db.Samples.AsNoTracking()
            .Where(s => s.MachineId == machine.Id && s.TotalCuttingTime != null && s.TsUtc < shift.StartUtc)
            .OrderByDescending(s => s.TsUtc)
            .FirstOrDefaultAsync();

        var lastFeedCmdRow = await db.Samples.AsNoTracking()
            .Where(s => s.MachineId == machine.Id && s.FeedCmd != null && s.TsUtc < shift.StartUtc)
            .OrderByDescending(s => s.TsUtc)
            .FirstOrDefaultAsync();

        var lastSample = samples.LastOrDefault() ?? lastCountRow;
        status.LastSampleUtc = lastSample?.TsUtc;
        status.Online = lastSample is not null &&
                        (nowUtc - lastSample.TsUtc).TotalMinutes < 10;

        // ---- spindle state & uptime ------------------------------------------------
        var speedRows = samples.Where(s => s.SpindleSpeed != null).ToList();

        var lastSpeedBefore = await db.Samples.AsNoTracking()
            .Where(s => s.MachineId == machine.Id && s.SpindleSpeed != null && s.TsUtc < shift.StartUtc)
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

        // probe exemption config (0 = disabled); tools are >= 1 so comparing
        // against 0 naturally disables it
        var probeTool = cfg.ProbeToolNumber;

        // tool timeline for the probe exemption: Mp1CurrentTool rows land as
        // change events within their ingest batch; the tool is physically
        // constant between change events, so the active tool for any moment is
        // the latest change event at/before that moment. Pre-shift baseline is
        // clamped to the shift start just like the speed baseline.
        var toolTimeline = new List<(DateTime Ts, int Tool)>();
        if (lastToolRow?.CurrentTool is int preShiftTool)
            toolTimeline.Add((shift.StartUtc, preShiftTool));
        toolTimeline.AddRange(samples
            .Where(s => s.CurrentTool != null)
            .Select(s => (s.TsUtc, s.CurrentTool!.Value)));
        toolTimeline.Sort((a, b) => a.Ts.CompareTo(b.Ts));

        // up-intervals: [ts of sample showing "running", stop confirmed after
        // SpindleStopDelaySeconds continuous below threshold) — brief dips
        // (tool changes) count as running time; probe-active periods (probe
        // tool in the spindle, no rotation) count as running too.
        var stopDelay = TimeSpan.FromSeconds(Math.Max(0, cfg.SpindleStopDelaySeconds));

        double upSeconds = 0;
        DateTime? runFrom = null;
        DateTime? dipFrom = null;
        int toolPtr = 0;
        int? ActiveTool(DateTime ts)
        {
            while (toolPtr < toolTimeline.Count && toolTimeline[toolPtr].Ts <= ts) toolPtr++;
            return toolPtr > 0 ? toolTimeline[toolPtr - 1].Tool : (int?)null;
        }
        foreach (var row in speedRows.OrderBy(r => r.TsUtc))
        {
            var toolHere = probeTool > 0 ? ActiveTool(row.TsUtc) : null;
            var running = (row.SpindleSpeed ?? 0) >= cfg.SpindleRunningRpm || toolHere == probeTool;
            if (running)
            {
                if (runFrom == null) runFrom = row.TsUtc;
                dipFrom = null; // resumed before the delay elapsed: interval stays continuous
            }
            else if (runFrom != null && dipFrom == null)
            {
                dipFrom = row.TsUtc;
            }
            else if (runFrom != null && dipFrom != null && row.TsUtc - dipFrom.Value > stopDelay)
            {
                upSeconds += (dipFrom.Value - runFrom.Value).TotalSeconds;
                runFrom = null;
                dipFrom = null;
            }
        }
        if (runFrom != null)
        {
            if (dipFrom != null && nowUtc - dipFrom.Value > stopDelay)
                upSeconds += (dipFrom.Value - runFrom.Value).TotalSeconds;
            else
                upSeconds += (nowUtc - runFrom.Value).TotalSeconds;
        }

        var lastSpeed = speedRows.LastOrDefault()?.SpindleSpeed;
        status.LastSpindleSpeed = lastSpeed;

        // spindle is "running" while ANY sample inside the stop-delay window is
        // at/above threshold, so a momentary dip (tool change) doesn't flip to stopped
        var recentSpeeds = speedRows
            .Where(r => r.TsUtc >= nowUtc - stopDelay)
            .Select(r => r.SpindleSpeed)
            .Where(v => v.HasValue)
            .ToList();
        if (lastSpeed.HasValue)
        {
            status.SpindleRunning = recentSpeeds.Count > 0
                ? recentSpeeds.Max()!.Value >= cfg.SpindleRunningRpm
                : lastSpeed >= cfg.SpindleRunningRpm;
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

        // probe exemption (display): with the probe tool in the spindle the
        // spindle is "running" regardless of RPM — probe taps don't rotate the
        // spindle; the status reason/color cascade proceeds normally.
        if (probeTool > 0 && toolTimeline.Count > 0 && toolTimeline[^1].Tool == probeTool)
            status.SpindleRunning = true;
        if (lastSpeed.HasValue)
        {
            status.SpindleUptimePercent = status.ShiftElapsedMinutes > 0.5
                ? Math.Min(100, upSeconds / (status.ShiftElapsedMinutes * 60) * 100)
                : 0;
        }

        // ---- motion (Mp1Fcmd: commanded feed; > 0 while axes move) ------------------
        // FeedCmd posts on change, so the feed holds its value until the next feed
        // row: intervals [row with feed > 0, next row with feed == 0) are moving
        // time. Pre-shift baseline clamped to the shift start like the speed one.
        var feedRows = samples.Where(s => s.FeedCmd != null).ToList();
        if (lastFeedCmdRow != null)
        {
            lastFeedCmdRow.TsUtc = lastFeedCmdRow.TsUtc > shift.StartUtc
                ? lastFeedCmdRow.TsUtc
                : shift.StartUtc;
            feedRows.Insert(0, lastFeedCmdRow);
        }

        double moveSeconds = 0;
        DateTime? moveFrom = null;
        foreach (var row in feedRows.OrderBy(r => r.TsUtc))
        {
            var mov = row.FeedCmd > 0;
            if (mov && moveFrom == null) moveFrom = row.TsUtc;
            else if (!mov && moveFrom != null)
            {
                moveSeconds += (row.TsUtc - moveFrom.Value).TotalSeconds;
                moveFrom = null;
            }
        }
        if (moveFrom != null)
            moveSeconds += (nowUtc - moveFrom.Value).TotalSeconds;

        status.LastFeedCmd = feedRows.LastOrDefault()?.FeedCmd;
        status.IsMoving = status.LastFeedCmd > 0 || AxisMovedRecently(samples, nowUtc);
        status.MotionUpPercent = status.ShiftElapsedMinutes > 0.5
            ? Math.Min(100, moveSeconds / (status.ShiftElapsedMinutes * 60) * 100)
            : 0;

        // ---- parts & cycle learning -------------------------------------------------
        // Cycle-var (VC3 on cnc12) change collection runs FIRST: the var change
        // cadence is the cycle truth for var-tracked machines, and its interval
        // list feeds the same learning-median as pulses do below.
        var cycleVarChanges = new List<DateTime>();
        if (cfg.CycleVarIndex > 0)
        {
            var prefix = cfg.CycleVarIndex.ToString() + "=";
            long? prevV = null;
            foreach (var r in samples.Where(s => s.CommonVars != null)
                         .OrderBy(r => r.TsUtc))
            {
                var v = CommonVarValueLong(r.CommonVars!, prefix);
                if (v.HasValue)
                {
                    if (prevV.HasValue && v.Value != prevV.Value)
                        cycleVarChanges.Add(r.TsUtc);
                    prevV = v.Value;
                }
            }
        }

        // Mppartcount is a 0/1 cycle-complete pulse (NOT cumulative): each 0->1
        // transition completes one cycle → PartsPerCycle parts. Cycle learning uses
        // the pulse cadence (the flag is a change-only stream, so pulse-to-pulse
        // dt ≈ one full cycle).
        var countRows = samples.Where(s => s.PartCount != null).OrderBy(r => r.TsUtc).ToList();
        var cycleSeconds = new List<double>();
        long flagPulses = 0;
        var pulseTss = new List<DateTime>();
        long prevFlag = lastCountRow?.PartCount ?? 0;
        foreach (var row in countRows)
        {
            var cur = row.PartCount ?? prevFlag;
            if (cur == 1 && prevFlag == 0)
            {
                if (pulseTss.Count > 0)
                {
                    var dt = (row.TsUtc - pulseTss[^1]).TotalSeconds;
                    // tight window: 30s..8h — pulses are per-cycle or per-batch
                    // (cnc12: ~32min batches). Idles longer than that (overnight
                    // gaps, machine off) are NOT cycle samples; keeping them
                    // poisoned the median (8658s learned from 475min outliers).
                    if (dt > 30 && dt < 8 * 3600)
                        cycleSeconds.Add(dt);
                }
                flagPulses++;
                pulseTss.Add(row.TsUtc);
            }
            prevFlag = cur;
        }

        // parts from pulses: one completed cycle each → PartsPerCycle parts
        long flagParts = flagPulses * cfg.PartsPerCycle;

        // learned cycle: VC3-configured machines learn from var-change cadence
        // (the batch length); pulse cadence only for machines without the var.
        if (cfg.CycleVarIndex > 0 && cycleVarChanges.Count > 1)
        {
            cycleSeconds.Clear();
            var prev = cycleVarChanges[0];
            foreach (var ts2 in cycleVarChanges.Skip(1))
            {
                var dt = (ts2 - prev).TotalSeconds;
                if (dt > 30 && dt < 8 * 3600) cycleSeconds.Add(dt);
                prev = ts2;
            }
        }
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

        // cutting-time estimate as fallback when pulses are missed (e.g. collector
        // outage): whole cycles completed since shift start ≈ cutting seconds
        // advanced / learned cycle. Mp1TotalCuttingTime is seconds and monotonic.
        var tcutRows = samples.Where(s => s.TotalCuttingTime != null).ToList();
        double? tcutShiftStart = null;
        if (lastCuttingRow != null)
        {
            tcutShiftStart = lastCuttingRow.TotalCuttingTime;
            lastCuttingRow.TsUtc = lastCuttingRow.TsUtc > shift.StartUtc
                ? lastCuttingRow.TsUtc
                : shift.StartUtc;
            tcutRows.Add(lastCuttingRow);
        }
        tcutRows = tcutRows.OrderBy(r => r.TsUtc).ToList();

        long cuttingParts = 0;
        if (status.LearnedCycleSeconds is > 0 && tcutShiftStart is > 0)
        {
            double tcutNow0 = tcutRows.LastOrDefault()?.TotalCuttingTime ?? tcutShiftStart ?? 0;
            var advanced = Math.Max(0.0, tcutNow0 - tcutShiftStart.Value);
            cuttingParts = (long)Math.Floor(advanced / status.LearnedCycleSeconds.Value) * cfg.PartsPerCycle;
        }

        // tally path: PartsTally (VC41) is a PER-SHIFT tally the machine's app
        // resets; its value IS the parts count — no baseline subtraction and no
        // job-change detection needed (a reset/program change simply lowers the
        // value and parts follow it). parts = current tally when data exists.
        long tallyParts = 0;
        var haveTally = false;
        DateTime? tallyStepTs = null; // last in-shift tally INCREASE (cycle anchor)
        if (cfg.PartsTallyVarIndex > 0)
        {
            var tallyRows = samples
                .Where(s => s.PartsTally != null)
                .OrderBy(r => r.TsUtc).ToList();
            var lastTallySample = tallyRows.LastOrDefault();
            if (lastTallySample?.PartsTally is double tallyVal)
            {
                tallyParts = (long)Math.Max(0, Math.Round(tallyVal));
                haveTally = true;
            }
            // latest increase: any row whose tally exceeds the max seen before it
            long maxSoFar = -1;
            foreach (var r in tallyRows)
            {
                var v = (long)Math.Max(0, Math.Round(r.PartsTally!.Value));
                if (v > maxSoFar)
                {
                    if (maxSoFar >= 0) tallyStepTs = r.TsUtc; // a real increase, not the first reading
                    maxSoFar = v;
                }
            }
        }

        long partsNow;
        if (haveTally)
        {
            partsNow = tallyParts;
            status.PartsSource = "common var tally";
        }
        else
        {
            partsNow = Math.Max(flagParts, cuttingParts);
            status.PartsSource = flagParts >= cuttingParts ? "partcount pulse" : "cutting time est";
        }
        if (cfg.PartsThisShiftOverride is int seed && seed > partsNow)
        {
            // seed = bootstrap floor only: wins until the tally exceeds it
            partsNow = seed;
            status.PartsSource = "manual seed";
        }
        status.PartsThisShift = partsNow;
        status.CyclesCollected = (int)Math.Min(flagPulses, int.MaxValue);

        // stations on (VC40 via StationsOn) for the dashboard when enabled
        status.StationsOn = cfg.StationsVarIndex > 0
            ? samples.LastOrDefault(s => s.StationsOn != null)?.StationsOn
            : null;

        // part name from a common var (e.g. VC65 job number): when configured and
        // present in the latest map, override the displayed PartName; PartNameFromVar
        // stays null when the var is off/absent so the API consumer can tell provenance
        if (cfg.PartNameVarIndex > 0)
        {
            status.PartNameFromVar = CommonVarValue(samples, cfg.PartNameVarIndex);
            if (!string.IsNullOrEmpty(status.PartNameFromVar))
                status.PartName = status.PartNameFromVar;
        }

        // quota gauge: parts / configured per-shift quota — independent of pace
        // and learning; null when the feature is off (QuotaParts = 0). Rendered
        // neutrally (no color grading; statusReason stays tied to efficiency).
        status.PercentOfQuota = cfg.QuotaParts > 0
            ? status.PartsThisShift / (double)cfg.QuotaParts * 100
            : null;
        status.QuotaParts = cfg.QuotaParts;

        // cycle anchor var (cnc12: VC3 = pallet/table id 1<->2). User directive:
        // VC3 IS the cycle truth. Collection happens up in the learning block
        // (cycleVarChanges); here we consume: latest change = anchor, and the
        // count-up runs from there.
        DateTime? cycleVarChangeTs = cycleVarChanges.Count > 0 ? cycleVarChanges[^1] : (DateTime?)null;
        long? cycleVarValue = null;
        if (cfg.CycleVarIndex > 0)
        {
            var prefix = cfg.CycleVarIndex.ToString() + "=";
            var lastCvRow = samples.LastOrDefault(s => s.CommonVars != null);
            if (lastCvRow?.CommonVars is string cvs)
                cycleVarValue = CommonVarValueLong(cvs, prefix);
        }

        // cycle progress anchor: cutting time at the last cycle start. Anchor
        // priority (cnc12: the pallet-swap var VC3 1<->2 IS the cycle boundary —
        // the machine's own truth, no heuristics):
        //   1. Cycle-var change (CycleVarIndex, e.g. VC3)
        //   2. Tally increase (machines without a cycle var)
        //   3. Mppartcount 0->1 pulse (no tally var either)
        //   4. Shift start
        // Mp1line must NEVER be used for this: Okuma subprograms bounce the line
        // number constantly mid-cycle (main 1050 calling sub 1..N, call/return
        // drops like 155->4), and any-decrease-as-restart dragged the anchor
        // seconds into the past repeatedly — progress visibly reset mid-cycle.
        // VC70 was also rejected: flickers 0/2/0 at resets, fires at odd offsets.
        // Cutting time interpolated at the anchor timestamp: tcut = c0 + (c1-c0)
        // * (a - t0)/(t1 - t0) between surrounding rows.
        // cycle progress: VC3-configured machines track WALL CLOCK from the last
        // var change ("reset on change, count up until it changes back"). The
        // cutting-time interpolation path only applies when no cycle var exists.
        if (cycleVarChangeTs != null)
        {
            status.CurrentCycleSeconds = Math.Max(0, (nowUtc - cycleVarChangeTs.Value).TotalSeconds);
        }
        else if (cfg.CycleVarIndex > 0)
        {
            // var configured but no change seen in this shift yet — count from
            // the first sample we have (start of visible history), not null
            var firstTs = samples.FirstOrDefault()?.TsUtc;
            if (firstTs != null)
                status.CurrentCycleSeconds = Math.Max(0, (nowUtc - firstTs.Value).TotalSeconds);
        }
        else
        {
            double? cycleStartCut = null;
        if (tcutRows.Count > 0)
        {
            DateTime? anchorTs = tallyStepTs;
            if (anchorTs == null && pulseTss.Count > 0)
                anchorTs = pulseTss[^1];
            if (anchorTs != null)
            {
                var at = anchorTs.Value;
                var first = tcutRows[0];
                var t0 = first.TsUtc;
                var c0 = first.TotalCuttingTime ?? 0;
                var t1 = t0;
                var c1 = c0;
                foreach (var r in tcutRows)
                {
                    if (r.TsUtc <= at) { t0 = r.TsUtc; c0 = r.TotalCuttingTime!.Value; }
                    else { t1 = r.TsUtc; c1 = r.TotalCuttingTime!.Value; break; }
                }
                var span = (t1 - t0).TotalSeconds;
                cycleStartCut = span > 0
                    ? c0 + (c1 - c0) * (at - t0).TotalSeconds / span
                    : c0;
            }
            else
                cycleStartCut = tcutShiftStart; // nothing yet this shift: assume shift start
        }
        var tcutLatest = tcutRows.LastOrDefault()?.TotalCuttingTime;
        if (tcutLatest != null && cycleStartCut != null)
            status.CurrentCycleSeconds = Math.Max(0, tcutLatest.Value - cycleStartCut.Value);
        else if (tallyStepTs != null)
            status.CurrentCycleSeconds = Math.Max(0, (nowUtc - tallyStepTs.Value).TotalSeconds);
        else if (pulseTss.Count > 0)
            status.CurrentCycleSeconds = Math.Max(0, (nowUtc - pulseTss[^1]).TotalSeconds);
        // else: leave null → view renders plain "in cycle" (nothing to anchor to)
        }

        // ---- status color -----------------------------------------------------------
        if (!status.Online)
        {
            status.Color = StatusColor.Off;
            status.StatusReason = "No recent data from collector";
        }
        else if (!status.SpindleRunning && !status.IsMoving)
        {
            // RED only when nothing active: no spindle rotation, no probe exemption,
            // no commanded/observed machine motion
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
                ? status.PartsThisShift / status.ExpectedParts * 100
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

        // pallet display: latest value of the cycle var (1/2; brief 0 during swap
        // renders as the dash via null)
        if (cfg.CycleVarIndex > 0)
        {
            var cvPrefix = cfg.CycleVarIndex.ToString() + "=";
            var lastCvRow = samples.LastOrDefault(s => s.CommonVars != null);
            if (lastCvRow?.CommonVars is string cvs)
            {
                var pv = CommonVarValueLong(cvs, cvPrefix);
                status.Pallet = pv is > 0 ? pv : null;
            }
        }

        status.Tools = await GetToolsAsync(db, machine.Id, cfg);
        status.ExpiredTools = status.Tools.Count(t => t.Grade == "ng");
        return status;
    }

    private static async Task<List<ToolStatus>> GetToolsAsync(CncDbContext db, string machineId, MachineConfig cfg)
    {
        var tools = await db.ToolAssets.AsNoTracking()
            .Where(t => t.MachineId == machineId)
            .ToListAsync();

        return tools.Select(t =>
        {
            // CountDirection may vary in case across agents — compare
            // case-insensitively everywhere
            var dirUp = string.Equals(t.CountDirection, "UP", StringComparison.OrdinalIgnoreCase);

            double? pct = null;
            var grade = "na";
            if (t.LifeInitial is > 0 && t.LifeValue.HasValue)
            {
                pct = dirUp
                    ? (t.LifeInitial.Value - t.LifeValue.Value) / t.LifeInitial.Value * 100
                    : t.LifeValue.Value / t.LifeInitial.Value * 100;
                grade = pct < 0 || IsExpiredStatus(t.Status) ? "ng"
                    : pct < cfg.WarnThresholdPercent ? "warn"
                    : "ok";
            }
            else if (IsExpiredStatus(t.Status))
                grade = "ng";

            // cycles left: only REAL count-based life is honest to expose. For
            // percent-based types (PARTS_PERCENT / MINUTES_PERCENT) there is no
            // cycle base value on hand, so leave null (view renders an em-dash).
            double? cyclesLeft = null;
            var lt = (t.LifeType ?? "").ToUpperInvariant();
            if (t.LifeValue.HasValue && lt is "PARTS" or "PART_COUNT")
            {
                if (dirUp)
                {
                    if (t.LifeInitial is > 0)
                        cyclesLeft = Math.Max(0, t.LifeInitial.Value - t.LifeValue.Value);
                }
                else
                    cyclesLeft = Math.Max(0, t.LifeValue.Value);
            }

            // "left/set" for the Cycles-left column when the life is count based
            // and the initial budget is known: remaining first, set size second.
            // DOWN direction (Okuma default) counts LifeValue down to 0, so
            // remaining = LifeValue; UP counts up toward initial, so remaining =
            // initial - value. (NOT MTConnect used/set: that read 12/12 for a
            // tool with 0 cycles left.) Missing budget or percent-type life → null.
            string? lifeUsedSet = null;
            if (t.LifeValue.HasValue && t.LifeInitial is > 0 && lt is "PARTS" or "PART_COUNT")
            {
                var left = dirUp
                    ? Math.Max(0, t.LifeInitial.Value - t.LifeValue.Value)
                    : Math.Max(0, t.LifeValue.Value);
                lifeUsedSet = $"{left:0}/{t.LifeInitial.Value:0}";
            }

            return new ToolStatus
            {
                AssetId = t.AssetId,
                ToolNumber = t.ToolNumber,
                Pot = t.Pot,
                Grade = grade,
                LifeType = t.LifeType,
                PercentRemaining = pct,
                CyclesLeft = cyclesLeft,
                LifeUsedSet = lifeUsedSet,
                Status = t.Status,
            };
        }).OrderBy(t => t.ToolNumber ?? int.MaxValue).ToList();
    }

    private static bool IsExpiredStatus(string? status) =>
        status is not null && status is not ("AVAILABLE" or "NEW" or "USED");

    // axis "moving now": any X/Y/Z position row within the last 10s whose value
    // differs by more than 0.1 from the latest row before the window
    private static bool AxisMovedRecently(List<Sample> samples, DateTime nowUtc)
    {
        var winStart = nowUtc - TimeSpan.FromSeconds(10);
        // axis rows are identified by the populated COLUMN, not by the agent's
        // dataItemId (ids differ per agent build: MX1actm / C13X1actm / ...)
        foreach (var axis in new Func<Sample, double?>[] { s => s.XAct, s => s.YAct, s => s.ZAct })
        {
            var axisRows = samples.Where(s => axis(s) != null).OrderBy(r => r.TsUtc).ToList();
            var winLast = axisRows.LastOrDefault(s => s.TsUtc >= winStart);
            if (winLast == null) continue;
            var beforeWin = axisRows.LastOrDefault(s => s.TsUtc < winStart);
            var a = axis(winLast) ?? 0;
            var b = beforeWin == null ? a : (axis(beforeWin) ?? 0);
            if (Math.Abs(a - b) > 0.1) return true;
        }
        return false;
    }

    // parse one var from a single CommonVars map string as a long (null = absent)
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

    // lookup a single common var value from the latest parsed map ("65=5600;...")
    private static string? CommonVarValue(List<Sample> samples, int varIndex)
    {
        var row = samples.LastOrDefault(s => s.CommonVars != null);
        if (row?.CommonVars is not string map) return null;
        var prefix = varIndex.ToString() + "=";
        foreach (var part in map.Split(';'))
        {
            if (part.StartsWith(prefix, StringComparison.Ordinal))
            {
                var v = part[prefix.Length..].Trim();
                return v.Length > 0 ? v : null;
            }
        }
        return null;
    }
}