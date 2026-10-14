namespace CncDash.Server.Services;

public enum StatusColor
{
    Off,
    Red,
    Yellow,
    Green,
    Learning,
}

public class MachineStatus
{
    public string MachineId { get; set; } = default!;
    public string DisplayName { get; set; } = default!;

    public bool Online { get; set; }
    public DateTime? LastSampleUtc { get; set; }

    public StatusColor Color { get; set; }
    public string StatusReason { get; set; } = "";

    // Shift
    public int ShiftNumber { get; set; }
    public string ShiftName { get; set; } = "";
    public DateTime ShiftStartUtc { get; set; }
    public DateTime? ShiftEndUtcNext { get; set; }
    public double ShiftElapsedMinutes { get; set; }

    // Production
    public string PartName { get; set; } = "";
    public string? Program { get; set; }
    public int? CurrentTool { get; set; }
    public long PartsThisShift { get; set; }
    public double ExpectedParts { get; set; }
    public double PercentOfExpected { get; set; }

    // Learning
    public double? LearnedCycleSeconds { get; set; }
    public int CyclesCollected { get; set; }
    public double? MaxPartsPerHour { get; set; }

    // Spindle
    public double SpindleUptimePercent { get; set; }
    public bool SpindleRunning { get; set; }

    // Tools
    public List<ToolStatus> Tools { get; set; } = new();
}

public class ToolStatus
{
    public string AssetId { get; set; } = default!;
    public int? ToolNumber { get; set; }
    public int? Pot { get; set; }
    public string Grade { get; set; } = "na"; // ok | warn | ng | na
    public string? LifeType { get; set; }
    public double? PercentRemaining { get; set; }
    public string? Status { get; set; }
}