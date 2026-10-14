namespace CncDash.Server.Data.Entities;

// One row per data-item change reported by a collector. Only compact values that
// the metrics engine needs; everything else stays in the collector.
public class Sample
{
    public long Id { get; set; }

    public string MachineId { get; set; } = default!;

    public DateTime TsUtc { get; set; }

    // MTConnect dataItemId, e.g. MS1speed, Mpexecution, Mppartcount
    public string DataItemId { get; set; } = default!;

    // MTConnect sequence number for dedup (unique per agent instance)
    public long Sequence { get; set; }

    // MS1speed ACTUAL
    public double? SpindleSpeed { get; set; }

    // Mpexecution: ACTIVE | READY | ...
    public string? Execution { get; set; }

    // Mpmode: AUTOMATIC | MANUAL | ...
    public string? ControllerMode { get; set; }

    // FunctionalMode: PRODUCTION | SETUP | ...
    public string? FunctionalMode { get; set; }

    // Mppartcount cumulative counter when it changes
    public long? PartCount { get; set; }

    public string? Program { get; set; }

    public int? CurrentTool { get; set; }
}

public class ToolAsset
{
    public long Id { get; set; }

    public string MachineId { get; set; } = default!;

    // MTConnect assetId, e.g. 123456.14
    public string AssetId { get; set; } = default!;

    public int? SerialNumber { get; set; }

    public int? ToolNumber { get; set; }

    public int? ProgramToolGroup { get; set; }

    public int? Pot { get; set; }

    public double? LifeInitial { get; set; }

    // Remaining life (countDirection DOWN) or accumulated use (UP)
    public double? LifeValue { get; set; }

    public string? LifeType { get; set; } // PART_COUNT | MINUTES | ...

    public string? CountDirection { get; set; } // DOWN | UP

    public string? Status { get; set; } // AVAILABLE | BROKEN | USED | NEW | EXPIRED

    public DateTime UpdatedAtUtc { get; set; }
}