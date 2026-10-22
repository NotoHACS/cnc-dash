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

    // Mp1TotalCuttingTime [AccumulatedTime] seconds, monotonic while cutting —
    // primary cycle/part anchor (Mppartcount is a 0/1 cycle-complete pulse).
    public double? TotalCuttingTime { get; set; }

    // Mp1Fcmd [PathFeedrate] commanded feed; > 0 while axes are moving.
    public double? FeedCmd { get; set; }

    // MX1actm / MY1actm / MZ1actm [Position] axis positions.
    public double? XAct { get; set; }
    public double? YAct { get; set; }
    public double? ZAct { get; set; }

    // Mp1line [Line] / Mp1block [Block]: program position — line wrap detects
    // a program restart (new cycle).
    public long? Line { get; set; }
    public string? Block { get; set; }

    // Mp1SpindleRunTime [AccumulatedTime] seconds — cumulative spindle run
    // time. Carried through ingest (the collector parses it) so the dashboard
    // can show real spindle runtime instead of only a speed-derived estimate.
    public double? Runtime { get; set; }

    // Mp1TotalSpindleRunTime [AccumulatedTime] seconds — cumulative spindle run
    // time (kept apart from Runtime: an agent that publishes both would
    // otherwise alternate the two series in one column).
    public double? TotalRuntime { get; set; }

    // Mp1CommonVariable [subType COMMON]: the full Okuma common-variable map as a
    // compact "N=v;N=v" string (4000-char cap); StationsOn = VC40, PartsTally =
    // VC41 (cumulative parts tally — primary parts source; resets on job change).
    public string? CommonVars { get; set; }
    public int? StationsOn { get; set; }
    public double? PartsTally { get; set; }
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
// One row per data item id the agent published for a machine. Records what the
// AGENT actually exposes, so a missing dashboard feature can be attributed to
// the agent (item never published) vs the collector (published but unmapped).
public class AgentItem
{
    public long Id { get; set; }

    public string MachineId { get; set; } = default!;

    public string DataItemId { get; set; } = default!;

    // true = the collector maps this id to a column; false = published but unmapped
    public bool Mapped { get; set; }

    public DateTime LastSeenUtc { get; set; }
}
