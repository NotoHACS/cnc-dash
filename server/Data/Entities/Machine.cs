namespace CncDash.Server.Data.Entities;

public class Machine
{
    public string Id { get; set; } = default!;

    public string ApiKey { get; set; } = default!;

    public string DisplayName { get; set; } = default!;

    public DateTime CreatedAtUtc { get; set; }

    public MachineConfig? Config { get; set; }
}

public class MachineConfig
{
    public string MachineId { get; set; } = default!;

    // Shift windows in minutes from local midnight; Start >= End means window crosses
    // midnight. Use null start to disable a shift.
    public int? Shift1Start { get; set; } = 6 * 60;
    public int? Shift1End { get; set; } = 14 * 60 + 30;
    public int? Shift2Start { get; set; } = 14 * 60;
    public int? Shift2End { get; set; } = 22 * 60 + 30;
    public int? Shift3Start { get; set; } = 22 * 60;
    public int? Shift3End { get; set; } = 6 * 60 + 30;

    public string PartName { get; set; } = "UNSET";

    public int PartsPerCycle { get; set; } = 1;

    public double GreenThresholdPercent { get; set; } = 75;

    public double WarnThresholdPercent { get; set; } = 15;

    public int CycleLearningWindow { get; set; } = 20;

    public double SpindleRunningRpm { get; set; } = 500;

    // Spindle counts as stopped only after being below threshold this many
    // continuous seconds; shorter dips (tool changes) keep it "running".
    public int SpindleStopDelaySeconds { get; set; } = 30;

    // Tool number acting as the in-cycle touch probe: probe periods don't rotate
    // the spindle but count as running (exempt from spindle-down/RED). 0 disables.
    public int ProbeToolNumber { get; set; } = 1;

    // Okuma common var that flips when the pallet/table swaps (cnc12: VC3,
    // 1<->2 with a brief 0 during the swap). Cycle anchor source; 0 = disabled.
    public int CycleVarIndex { get; set; } = 0;

    public string TimeZone { get; set; } = "America/New_York";

    // Expected cycle time in seconds (operator estimate). Used until the server has
    // learned enough cycles; 0 means no expectation (LEARNING until then).
    public double ExpectedCycleSeconds { get; set; } = 0;

    // Manual seed for "parts this shift" (e.g. today's known count at deploy).
    // null = auto (partcount pulses × PartsPerCycle, floored against the
    // cutting-time estimate). Auto values higher than the seed win.
    public int? PartsThisShiftOverride { get; set; }

    // Common-variable indices for parts/stations (0 = feature off). The collector
    // extracts VC40 → StationsOn and VC41 → PartsTally: a PER-SHIFT tally the
    // machine's app resets, whose value IS the parts count (no diffing).
    public int PartsTallyVarIndex { get; set; } = 41;
    public int StationsVarIndex { get; set; } = 40;

    // Common var for the displayed part name (e.g. VC65 job number; 0 = off, →
    // PartName). Falls back to PartName when the var is absent from the map.
    public int PartNameVarIndex { get; set; } = 65;

    // Shift quota in parts (0 = off): quota gauge card on the dashboard.
    public int QuotaParts { get; set; } = 48;

    // ntfy topic this machine's alarm poller posts to. Blank/null = the
    // machine id (cnc8 -> topic "cnc8"). Each topic is polled separately so
    // the alarm bar only ever shows THIS machine's alarms.
    public string? NtfyTopic { get; set; }

    public Machine? Machine { get; set; }
}