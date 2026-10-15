using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace CncDash.Collector;

public class MtConnectClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly XNamespace Ns = "urn:mtconnect.org:MTConnectStreams:1.4";

    private readonly HttpClient http;
    private readonly CollectorConfig cfg;
    private readonly Diagnostics? diag;
    private bool cvLoggedOnce;

    public MtConnectClient(CollectorConfig cfg)
    {
        this.cfg = cfg;
        diag = cfg.DiagnosticDumpSeconds > 0 ? new Diagnostics(cfg) : null;
        http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            DefaultRequestHeaders = { { "Accept", "application/xml" } },
        };
        http.DefaultRequestHeaders.ConnectionClose = true;
    }

    /// <summary>Temporary per-poll data-item observer; null when diagnostics are off.</summary>
    public Diagnostics? Diagnostics => diag;

    /// <summary>Fetch /current and return changed samples + tool-change triggers. Null on unreachable/parse failure.</summary>
    public async Task<PollResult?> PollAsync(Dictionary<string, string> lastValues)
    {
        string? xml = null;
        try
        {
            var uri = $"{cfg.AgentUrl.TrimEnd('/')}/{cfg.DevicePath.Trim('/')}/current";
            xml = await http.GetStringAsync(uri);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] poll failed: {ex.Message}");
            return null;
        }

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] xml parse failed: {ex.Message}");
            return null;
        }

        var result = new PollResult
        {
            Samples = new List<SampleRow>(),
            AssetChangedTrigger = false,
        };

        // agent bookkeeping (MTConnectStreams Header): instanceId identifies the
        // agent's in-memory instance; first/nextSequence bound the replay window.
        // Crossing either boundary means the cached change-detection state is
        // stale — clear it so the remaining elements (and the next poll) re-post
        // a full snapshot.
        lastValues.TryGetValue("instanceId", out var prevInstStr);
        ulong.TryParse(prevInstStr, out var prevInst);
        ulong firstSeq = 0, nextSeq = 0;
        string? instAttr = null;
        var header = doc.Root?.Element(Ns + "Header");
        if (header != null)
        {
            instAttr = header.Attribute("instanceId")?.Value;
            ulong.TryParse(instAttr, out var curInst);
            ulong.TryParse(header.Attribute("firstSequence")?.Value, out firstSeq);
            ulong.TryParse(header.Attribute("nextSequence")?.Value, out nextSeq);

            if (curInst > 0 && prevInst > 0 && curInst != prevInst)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] agent restart detected (instanceId {prevInst} -> {curInst}) - resnapshotting");
                lastValues.Clear();
            }
        }

        var seqOutageLogged = false;
        var elements = doc.Descendants()
            .Where(e => e.Attribute("dataItemId") != null);

        foreach (var e in elements)
        {
            var di = e.Attribute("dataItemId")!.Value;
            var tsAttr = e.Attribute("timestamp")?.Value;
            var ts = tsAttr != null ? DateTime.Parse(tsAttr).ToUniversalTime() : DateTime.UtcNow;
            diag?.Observe(di, e.Value, tsAttr ?? "", e.Attribute("sequence")?.Value ?? "");

            var isAssetTrigger = cfg.AssetTriggerItems.Any(t =>
                di.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (isAssetTrigger)
            {
                result.AssetChangedTrigger = true;
                result.ChangedAssetId = e.Value;
                lastValues[di] = e.Value;
                continue;
            }

            var sample = SampleRow.From(di, ts, 0, e);
            if (sample is null) continue;

            // sequence: agent-provided when present. Okuma extension elements
            // (e.g. Mp1CommonVariable, subType x:COMMON) may omit timestamp/
            // sequence entirely — for those, fall back to a stable hash of the
            // payload so the server's (di, seq) dedupe key still changes when
            // the value does.
            var seqStr = e.Attribute("sequence")?.Value;
            if (seqStr != null && long.TryParse(seqStr, out var agentSeq))
            {
                sample.seq = agentSeq;

                if ((nextSeq > 0 && (ulong)agentSeq >= nextSeq) || (firstSeq > 0 && (ulong)agentSeq < firstSeq))
                {
                    // stale/unknown instance: still post this value (it is
                    // current reality), but reset the cache so re-detection
                    // starts clean
                    if (!seqOutageLogged)
                    {
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] sequence {agentSeq} outside agent window [{firstSeq}, {nextSeq}) - resnapshotting");
                        seqOutageLogged = true;
                        lastValues.Clear();
                    }
                }
            }
            else
            {
                sample.seq = HashPayload(sample.ValueCanonical());
            }

            // canonical value = only the payload fields we care about
            var value = sample.ValueCanonical();
            if (lastValues.TryGetValue(di, out var prev) && prev == value)
                continue;

            lastValues[di] = value;
            result.Samples.Add(sample);

            // first common-variable post visibility (subsequent change posts
            // stay silent by design)
            if (di.Equals("Mp1CommonVariable", StringComparison.OrdinalIgnoreCase) && !cvLoggedOnce)
            {
                cvLoggedOnce = true;
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [debug] common variables posted: stations={sample.stationsOn} tally={sample.partsTally} ({sample.commonVars?.Length ?? 0} chars)");
            }
        }

        // persist agent bookkeeping alongside the last values (state.json)
        if (instAttr != null && ulong.TryParse(instAttr, out var instOut))
            lastValues["instanceId"] = instOut.ToString();
        if (nextSeq > 0)
            lastValues["nextSequence"] = nextSeq.ToString();

        // Execution/mode context travels on the sample rows that carry them
        // (server merges by timestamp), so events map onto: exec + mode + fmode.
        return result;
    }
}

public class PollResult
{
    public List<SampleRow> Samples { get; set; } = new();
    public bool AssetChangedTrigger { get; set; }

    // assetId from the asset-change trigger (e.g. asset_chg) for targeted fetch
    public string? ChangedAssetId { get; set; }
}

public class SampleRow
{
    public DateTime ts { get; set; }
    public string di { get; set; } = default!;
    public long seq { get; set; }
    public double? speed { get; set; }
    public string? exec { get; set; }
    public string? mode { get; set; }
    public string? fmode { get; set; }
    public long? count { get; set; }
    public string? prog { get; set; }
    public int? tool { get; set; }
    public double? runtime { get; set; }
    public double? totalCuttingTime { get; set; }
    public double? feedCmd { get; set; }
    public double? xAct { get; set; }
    public double? yAct { get; set; }
    public double? zAct { get; set; }
    public long? line { get; set; }
    public string? block { get; set; }
    public string? commonVars { get; set; }
    public int? stationsOn { get; set; }
    public double? partsTally { get; set; }

    public static SampleRow? From(string di, DateTime ts, long seq, XElement e)
    {
        var row = new SampleRow { ts = ts, di = di, seq = seq };
        switch (di)
        {
            case "MS1speed":
                if (double.TryParse(e.Value, out var s)) row.speed = s; else return null;
                break;
            case "Mppartcount":
                if (long.TryParse(e.Value, out var c)) row.count = c; else return null;
                break;
            case "Mpprogram":
                row.prog = e.Value; break;
            case "Mp1CurrentTool":
                if (int.TryParse(e.Value, out var t)) row.tool = t; else return null;
                break;
            case "Mpexecution":
                row.exec = e.Value; break;
            case "Mpmode":
                row.mode = e.Value; break;
            case "Mfmode":
                row.fmode = e.Value; break;
            case "Mp1SpindleRunTime":
                if (double.TryParse(e.Value, out var r)) row.runtime = r; else return null;
                break;
            case "Mp1TotalCuttingTime":
                if (double.TryParse(e.Value, out var tc)) row.totalCuttingTime = tc; else return null;
                break;
            case "Mp1Fcmd":
                if (double.TryParse(e.Value, out var f)) row.feedCmd = f; else return null;
                break;
            case "MX1actm":
                if (double.TryParse(e.Value, out var x)) row.xAct = x; else return null;
                break;
            case "MY1actm":
                if (double.TryParse(e.Value, out var y)) row.yAct = y; else return null;
                break;
            case "MZ1actm":
                if (double.TryParse(e.Value, out var z)) row.zAct = z; else return null;
                break;
            case "Mp1line":
                if (long.TryParse(e.Value, out var ln)) row.line = ln; else return null;
                break;
            case "Mp1block":
                row.block = e.Value; break;
            case "Mp1CommonVariable":
                var cv = ParseCommonVars(e.Value);
                if (cv == null) return null;
                row.commonVars = cv.Value.map;
                row.stationsOn = cv.Value.stations;
                row.partsTally = cv.Value.tally;
                break;
            default:
                return null;
        }
        return row;
    }

    /// <summary>Value used for change detection (payload only, ts/seq excluded).</summary>
    public string ValueCanonical()
    {
        if (speed.HasValue) return $"speed:{speed.Value:0.#}";
        if (exec != null) return $"exec:{exec}";
        if (mode != null) return $"mode:{mode}";
        if (fmode != null) return $"fmode:{fmode}";
        if (count.HasValue) return $"count:{count.Value}";
        if (prog != null) return $"prog:{prog}";
        if (tool.HasValue) return $"tool:{tool.Value}";
        if (runtime.HasValue) return $"runtime:{runtime.Value:0.#}";
        if (totalCuttingTime.HasValue) return $"tcut:{totalCuttingTime.Value:0.#}";
        if (feedCmd.HasValue) return $"feed:{feedCmd.Value:0.#}";
        if (xAct.HasValue) return $"x:{xAct.Value:0.#}";
        if (yAct.HasValue) return $"y:{yAct.Value:0.#}";
        if (zAct.HasValue) return $"z:{zAct.Value:0.#}";
        if (line.HasValue) return $"line:{line.Value}";
        if (block != null) return $"block:{block}";
        if (commonVars != null) return $"cvars:{commonVars}";
        return "";
    }

    /// <summary>
    /// Mp1CommonVariable carries all Okuma common variables as one space-separated
    /// text: "N:value N:value ..." (N = VC number). Values may be '&#x221E;'
    /// (infinity, unused) or otherwise unparsable — those tokens are ignored.
    /// Returns the compact map ("40=3;41=12;65=5733", truncated to 4000 chars)
    /// plus dedicated convenience values for VC40 (stations on) and VC41 (parts tally).
    /// </summary>
    private static (string map, int? stations, double? tally)? ParseCommonVars(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder();
        int? stations = null;
        double? tally = null;

        // whitespace-agnostic: the agent may emit tokens separated by plain
        // spaces, tabs, or newlines
        foreach (var tok in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ci = tok.IndexOf(':');
            if (ci <= 0) continue;
            if (!int.TryParse(tok[..ci], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx)) continue;
            var rawVal = tok[(ci + 1)..];
            if (!double.TryParse(rawVal, NumberStyles.Any, CultureInfo.InvariantCulture, out var val)) continue;
            if (sb.Length > 0) sb.Append(';');
            sb.Append(idx).Append('=').Append(val.ToString("0.####", CultureInfo.InvariantCulture));
            if (idx == 40) stations = (int)val;
            if (idx == 41) tally = val;
        }
        if (sb.Length == 0) return null;
        var map = sb.ToString();
        if (map.Length > 4000) map = map[..4000];
        return (map, stations, tally);
    }

    // FNV-1a 64 of the payload → stable positive sequence for items whose agent
    // element carries no sequence attribute (server dedupe key still value-bound)
    private static long HashPayload(string value)
    {
        unchecked
        {
            ulong h = 14695981039346656037;
            foreach (var ch in value)
            {
                h ^= ch;
                h *= 1099511628211;
            }
            return (long)(h & 0x7FFFFFFFFFFFFFFF);
        }
    }
}
/// <summary>
/// Temporary instrumentation for finding unmapped data items: observes every
/// dataItem element seen in /current and periodically appends a compact
/// itemId/count/first/last dump to a log file. Off unless DiagnosticDumpSeconds &gt; 0.
/// </summary>
public class Diagnostics
{
    private class Obs
    {
        public int Count;
        public string? FirstValue;
        public string? LastValue;
        public string? LastTs;
    }

    private readonly string path;
    private readonly Dictionary<string, Obs> items = new();

    public string LogPath { get; }

    public Diagnostics(CollectorConfig cfg)
    {
        var p = string.IsNullOrWhiteSpace(cfg.DiagnosticLogPath) ? "datadump.log" : cfg.DiagnosticLogPath!;
        path = Path.IsPathRooted(p) ? p : Path.Combine(AppContext.BaseDirectory, p);
        LogPath = path;
    }

    public void Observe(string id, string value, string tsRaw, string seqRaw)
    {
        var v = Trunc(value);
        if (!items.TryGetValue(id, out var o))
        {
            items[id] = new Obs { Count = 1, FirstValue = v, LastValue = v, LastTs = tsRaw };
        }
        else
        {
            o.Count = o.Count + 1;
            o.LastValue = v;
            o.LastTs = tsRaw;
        }
    }

    public void Dump()
    {
        if (items.Count == 0) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            foreach (var kv in items.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"{kv.Key} count={kv.Value.Count} first='{kv.Value.FirstValue}' last='{kv.Value.LastValue}'");
            }
            File.AppendAllText(path, sb.ToString());
            items.Clear(); // fresh accumulation window: count/first reset each dump
        }
        catch (Exception ex)
        {
            // a locked/missing file must never kill the collector; keep the dict
            // so a later successful dump still covers this window
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] diagnostics dump failed: {ex.Message}");
        }
    }

    private static string Trunc(string v) => v.Length <= 200 ? v : v[..200];
}
