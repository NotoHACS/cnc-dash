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

    // Quota gauge (independent of pace/learning): parts / QuotaParts; null when
    // the feature is off (QuotaParts = 0). Rendered neutral, no color grading.
    public int QuotaParts { get; set; }
    public double? PercentOfQuota { get; set; }

    // which signal produced PartsThisShift: partcount pulse | cutting time est |
    // manual seed — exposed so the dashboard/API can show provenance
    public string? PartsSource { get; set; }

    // stations on (common var VC40) when StationsVarIndex > 0 and data exists
    public int? StationsOn { get; set; }

    // part name resolved from the PartNameVarIndex common var (null = var off or
    // absent from the map; PartName then keeps its configured value)
    public string? PartNameFromVar { get; set; }

    // Elapsed seconds within the cycle in progress (since the last part-count
    // change); null when the server has no count-change timestamp to anchor to.
    public double? CurrentCycleSeconds { get; set; }

    // Learning
    public double? LearnedCycleSeconds { get; set; }
    public int CyclesCollected { get; set; }
    public double? MaxPartsPerHour { get; set; }

    // Spindle
    public double SpindleUptimePercent { get; set; }
    public bool SpindleRunning { get; set; }
    public double? LastSpindleSpeed { get; set; }

    // Motion (Mp1Fcmd > 0 or axis position change > 0.1 within 10s)
    public bool IsMoving { get; set; }
    public double MotionUpPercent { get; set; }
    public double? LastFeedCmd { get; set; }

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

    // Part-count based tool life remaining. Only set when the agent's tool life
    // type itself is part counts (PARTS / PART_COUNT); percent or minutes life
    // cannot be converted into cycles honestly and stays null (rendered as "—").
    public double? CyclesLeft { get; set; }
    public string? Status { get; set; }
}