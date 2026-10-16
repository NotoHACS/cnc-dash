using System.Text.Json.Serialization;

namespace CncDash.Server.Data.Entities;

// One row per (machine, actual) shift: captured when the engine first sees
// local time pass a configured shift end (or on demand). Parts = max PartsTally
// observed inside the window minus the baseline at window start (the machine's
// tally app resets at shift start, so baseline is usually 0 — but a baseline
// makes late resets correct too).
public class ShiftHistory
{
    public long Id { get; set; }

    public string MachineId { get; set; } = default!;

    // which configured shift (1..3)
    public int ShiftNumber { get; set; }

    // the LOCAL calendar date this shift ran on (e.g. 2026-10-15T00:00:00 local)
    public DateTime LocalDate { get; set; }

    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }

    // tally value at window start (usually 0 — the app resets) and peak inside
    public long PartsAtStart { get; set; }
    public long PartsAtEnd { get; set; }

    // PartsAtEnd - PartsAtStart — the number that matters
    public long PartsCount { get; set; }

    // when we captured it
    public DateTime CapturedUtc { get; set; }

    // UTC timestamps of the anchor var changes inside the window (VC3 flips)
    // — lets the dashboard render batch count without re-scanning Samples
    [JsonIgnore]
    public string? AnchorsJson { get; set; }
}