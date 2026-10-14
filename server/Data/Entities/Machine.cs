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

    public string TimeZone { get; set; } = "America/New_York";

    // Expected cycle time in seconds (operator estimate). Used until the server has
    // learned enough cycles; 0 means no expectation (LEARNING until then).
    public double ExpectedCycleSeconds { get; set; } = 0;

    public Machine? Machine { get; set; }
}