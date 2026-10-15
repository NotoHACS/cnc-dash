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

        var lastCuttingRow = await db.Samples.AsNoTracking()
            .Where(s => s.MachineId == machine.Id && s.DataItemId == "Mp1TotalCuttingTime" && s.TsUtc < shift.StartUtc)
            .OrderByDescending(s => s.TsUtc)
            .FirstOrDefaultAsync();

        var lastFeedCmdRow = await db.Samples.AsNoTracking()
            .Where(s => s.MachineId == machine.Id && s.DataItemId == "Mp1Fcmd" && s.TsUtc < shift.StartUtc)
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
            .Where(s => s.DataItemId == "Mp1CurrentTool" && s.CurrentTool != null)
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
        var feedRows = samples.Where(s => s.DataItemId == "Mp1Fcmd" && s.FeedCmd != null).ToList();
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
        // Mppartcount is a 0/1 cycle-complete pulse (NOT cumulative): each 0->1
        // transition completes one cycle → PartsPerCycle parts. Cycle learning uses
        // the pulse cadence (the flag is a change-only stream, so pulse-to-pulse
        // dt ≈ one full cycle).
        var countRows = samples.Where(s => s.DataItemId == "Mppartcount").OrderBy(r => r.TsUtc).ToList();
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
                    if (dt > 5 && dt < 24 * 3600)
                        cycleSeconds.Add(dt);
                }
                flagPulses++;
                pulseTss.Add(row.TsUtc);
            }
            prevFlag = cur;
        }

        // learned cycle: rolling median of pulse cadence, blended with seeded expectation
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
        var tcutRows = samples.Where(s => s.DataItemId == "Mp1TotalCuttingTime" && s.TotalCuttingTime != null).ToList();
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

        long partsNow = Math.Max(flagParts, cuttingParts);
        status.PartsSource = flagParts >= cuttingParts ? "partcount pulse" : "cutting time est";
        if (cfg.PartsThisShiftOverride is int seed && seed > partsNow)
        {
            partsNow = seed;
            status.PartsSource = "manual seed";
        }
        status.PartsThisShift = partsNow;
        status.CyclesCollected = flagPulses;

        // cycle progress anchor: cutting time at the last cycle start (latest 0->1
        // pulse or a program-line wrap, whichever is later) — elapsed-in-cycle is
        // cutting seconds, which excludes probe pauses. Mp1line dropping = restart.
        var lineRows = samples.Where(s => s.DataItemId == "Mp1line" && s.Line != null).OrderBy(r => r.TsUtc).ToList();
        DateTime? resetEvent = pulseTss.Count > 0 ? pulseTss[^1] : null;
        long? prevLine = null;
        foreach (var row in lineRows)
        {
            var cur = row.Line!.Value;
            if (prevLine != null && cur < prevLine && (resetEvent == null || row.TsUtc > resetEvent))
                resetEvent = row.TsUtc;
            prevLine = cur;
        }
        double? cycleStartCut = null;
        if (tcutRows.Count > 0)
        {
            if (resetEvent != null)
            {
                var anchor = tcutRows.OrderBy(r => Math.Abs((r.TsUtc - resetEvent.Value).TotalSeconds)).First();
                if (Math.Abs((anchor.TsUtc - resetEvent.Value).TotalSeconds) <= 120)
                    cycleStartCut = anchor.TotalCuttingTime;
            }
            else
                cycleStartCut = tcutShiftStart; // no pulse/wrap yet this shift: assume shift start
        }
        var tcutLatest = tcutRows.LastOrDefault()?.TotalCuttingTime;
        if (tcutLatest != null && cycleStartCut != null)
            status.CurrentCycleSeconds = Math.Max(0, tcutLatest.Value - cycleStartCut.Value);
        else if (pulseTss.Count > 0)
            status.CurrentCycleSeconds = Math.Max(0, (nowUtc - pulseTss[^1]).TotalSeconds);
        // else: leave null → view renders plain "in cycle" (nothing to anchor to)

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

            // cycles left: only REAL count-based life is honest to expose. For
            // percent-based types (PARTS_PERCENT / MINUTES_PERCENT) there is no
            // cycle base value on hand, so leave null (view renders an em-dash).
            double? cyclesLeft = null;
            var lt = (t.LifeType ?? "").ToUpperInvariant();
            if (t.LifeValue.HasValue && lt is "PARTS" or "PART_COUNT")
            {
                if (t.CountDirection == "UP")
                {
                    if (t.LifeInitial is > 0)
                        cyclesLeft = Math.Max(0, t.LifeInitial.Value - t.LifeValue.Value);
                }
                else
                    cyclesLeft = Math.Max(0, t.LifeValue.Value);
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
        foreach (var di in new[] { "MX1actm", "MY1actm", "MZ1actm" })
        {
            var axisRows = samples.Where(s => s.DataItemId == di).OrderBy(r => r.TsUtc).ToList();
            var winLast = axisRows.LastOrDefault(s => s.TsUtc >= winStart);
            if (winLast == null) continue;
            var beforeWin = axisRows.LastOrDefault(s => s.TsUtc < winStart);
            var a = AxisVal(winLast, di);
            var b = beforeWin == null ? a : AxisVal(beforeWin, di);
            if (Math.Abs(a - b) > 0.1) return true;
        }
        return false;
    }

    private static double AxisVal(Sample row, string di) => di switch
    {
        "MX1actm" => row.XAct ?? 0,
        "MY1actm" => row.YAct ?? 0,
        _ => row.ZAct ?? 0,
    };
}