using System.Text.Json;
using System.Xml.Linq;

namespace CncDash.Collector;

public class MtConnectClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly XNamespace Ns = "urn:mtconnect.org:MTConnectStreams:1.4";

    private readonly HttpClient http;
    private readonly CollectorConfig cfg;

    public MtConnectClient(CollectorConfig cfg)
    {
        this.cfg = cfg;
        http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10),
            DefaultRequestHeaders = { { "Accept", "application/xml" } },
        };
        http.DefaultRequestHeaders.ConnectionClose = true;
    }

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
        return "";
    }
}