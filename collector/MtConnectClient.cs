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

        var elements = doc.Descendants()
            .Where(e => e.Attribute("dataItemId") != null && e.Attribute("timestamp") != null);

        foreach (var e in elements)
        {
            var di = e.Attribute("dataItemId")!.Value;
            var ts = DateTime.Parse(e.Attribute("timestamp")!.Value).ToUniversalTime();
            diag?.Observe(di, e.Value, e.Attribute("timestamp")!.Value, e.Attribute("sequence")?.Value ?? "");
            var seqOk = long.TryParse(e.Attribute("sequence")!.Value, out var seq);
            if (!seqOk) continue;

            var isAssetTrigger = cfg.AssetTriggerItems.Any(t =>
                di.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (isAssetTrigger)
            {
                result.AssetChangedTrigger = true;
                lastValues[di] = e.Value;
                continue;
            }

            var sample = SampleRow.From(di, ts, seq, e);
            if (sample is null) continue;

            // canonical value = only the payload fields we care about
            var value = sample.ValueCanonical();
            if (lastValues.TryGetValue(di, out var prev) && prev == value)
                continue;

            lastValues[di] = value;
            result.Samples.Add(sample);
        }

        // Execution/mode context travels on the sample rows that carry them
        // (server merges by timestamp), so events map onto: exec + mode + fmode.
        return result;
    }
}

public class PollResult
{
    public List<SampleRow> Samples { get; set; } = new();
    public bool AssetChangedTrigger { get; set; }
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
        return "";
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
