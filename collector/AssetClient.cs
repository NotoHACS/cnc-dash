using System.Text.Json;
using System.Xml.Linq;

namespace CncDash.Collector;

public class AssetClient
{
    // Asset XML namespaces differ per agent build: 1.4 agents serve
    // urn:mtconnect.org:MTConnectAssets:1.4, older 1.3 agents serve 1.3, and
    // Okuma's tool-asset extensions live in urn:okuma.com:OkumaToolAssets:*.
    // A hardcoded namespace makes every lookup miss silently (0 tools) on a
    // differently-versioned agent, so match by LOCAL element/attribute name.
    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string? Attr(XElement? el, string localName) =>
        el?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static IEnumerable<XElement> CuttingTools(XDocument doc) =>
        doc.Descendants().Where(e => e.Name.LocalName == "CuttingTool");

    private readonly HttpClient http;
    private readonly CollectorConfig cfg;

    public AssetClient(CollectorConfig cfg)
    {
        this.cfg = cfg;
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.ConnectionClose = true;
    }

    /// <summary>Fetch CuttingTool assets → list of tool rows for ingest. Null on failure.</summary>
    public async Task<List<ToolRow>?> FetchAsync()
    {
        // The standard MTConnect asset endpoint is /assets (plural). Okuma's
        // 1.4 adapter adds /asset (singular); 1.3-era agents expose only the
        // plural form, and some serve it device-scoped. Try every known form
        // and keep the first that yields tools - hardcoding one URL is how a
        // machine ends up reporting "0 tools" with no error anywhere.
        var tried = new List<string>();
        foreach (var uri in AssetUrls())
        {
            tried.Add(uri);
            var rows = await FetchFromAsync(uri);
            if (rows is { Count: > 0 })
            {
                if (tried.Count > 1)
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] assets served at {uri}");
                return rows;
            }
        }
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] no tool assets found at: {string.Join(", ", tried)}");
        return null;
    }

    // every asset endpoint shape a MTConnect/Okuma agent may serve
    private IEnumerable<string> AssetUrls()
    {
        var baseUrl = cfg.AgentUrl.TrimEnd('/');
        var device = (cfg.DevicePath ?? "").Trim('/');
        yield return $"{baseUrl}/asset?type=CuttingTool";
        yield return $"{baseUrl}/assets?type=CuttingTool";
        if (device.Length > 0)
        {
            yield return $"{baseUrl}/{device}/asset?type=CuttingTool";
            yield return $"{baseUrl}/{device}/assets?type=CuttingTool";
        }
    }

    private async Task<List<ToolRow>?> FetchFromAsync(string uri)
    {
        XDocument doc;
        try
        {
            var xml = await http.GetStringAsync(uri);
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] asset fetch failed ({uri}): {ex.Message}");
            return null;
        }

        var tools = new List<ToolRow>();
        foreach (var asset in CuttingTools(doc))
        {
            var row = ParseCuttingTool(asset);
            if (row != null) tools.Add(row);
        }
        return tools;
    }

    /// <summary>
    /// Targeted fetch of a single asset by id (after an asset-change trigger).
    /// Returns null when unreachable, or when the asset exists but is not a
    /// CuttingTool (caller falls back to a full type=CuttingTool refetch).
    /// </summary>
    public async Task<ToolRow?> FetchOneAsync(string assetId)
    {
        var baseUrl = cfg.AgentUrl.TrimEnd('/');
        var device = (cfg.DevicePath ?? "").Trim('/');
        var esc = Uri.EscapeDataString(assetId);
        var urls = new List<string>
        {
            $"{baseUrl}/asset/{esc}",
            $"{baseUrl}/assets/{esc}",
        };
        if (device.Length > 0)
        {
            urls.Add($"{baseUrl}/{device}/asset/{esc}");
            urls.Add($"{baseUrl}/{device}/assets/{esc}");
        }

        foreach (var uri in urls)
        {
            XDocument doc;
            try
            {
                var xml = await http.GetStringAsync(uri);
                doc = XDocument.Parse(xml);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] asset fetch ({assetId}) failed at {uri}: {ex.Message}");
                continue;
            }

            var asset = CuttingTools(doc).FirstOrDefault();
            if (asset != null) return ParseCuttingTool(asset);
        }
        return null;
    }

    private static ToolRow? ParseCuttingTool(XElement asset)
    {
        var assetId = Attr(asset, "assetId");
        if (string.IsNullOrEmpty(assetId)) return null;

        int.TryParse(Attr(asset, "serialNumber"), out var serial);

        var lc = Child(asset, "CuttingToolLifeCycle");
        int? toolNumber = null, group = null, pot = null;
        double? initial = null, value = null;
        string? lifeType = null, dir = null, status = null;

        if (lc != null)
        {
            status = Child(Child(lc, "CutterStatus"), "Status")?.Value;
            var tn = Child(lc, "ProgramToolNumber");
            if (tn != null && int.TryParse(tn.Value, out var tnVal)) toolNumber = tnVal;
            var tg = Child(lc, "ProgramToolGroup");
            if (tg != null && int.TryParse(tg.Value, out var tgVal)) group = tgVal;
            var loc = Child(lc, "Location");
            if (loc != null && int.TryParse(loc.Value, out var potVal)) pot = potVal;

            var life = Child(lc, "ToolLife");
            if (life != null)
            {
                // Okuma extension attributes (type/countDirection/initial) may be
                // prefixed (x:type) on some agent builds — match by local name
                lifeType = Attr(life, "type");
                dir = Attr(life, "countDirection");
                if (double.TryParse(Attr(life, "initial"), out var initVal)) initial = initVal;
                if (double.TryParse(life.Value, out var valVal)) value = valVal;
            }
        }

        var row = new ToolRow
        {
            assetId = assetId,
            serial = serial,
            toolNumber = toolNumber,
            group = group,
            pot = pot,
            initial = initial,
            value = value,
            lifeType = lifeType,
            dir = dir,
            status = status,
            ts = DateTime.UtcNow,
        };

        // raw tool-life provenance for field triage (used/set semantics depend
        // on exactly these attributes)
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [debug] tool {assetId}: type={lifeType ?? "-"} dir={dir ?? "-"} initial={initial?.ToString() ?? "-"} value={value?.ToString() ?? "-"}");

        return row;
    }
}

public class ToolRow
{
    public string assetId { get; set; } = default!;
    public int? serial { get; set; }
    public int? toolNumber { get; set; }
    public int? group { get; set; }
    public int? pot { get; set; }
    public double? initial { get; set; }
    public double? value { get; set; }
    public string? lifeType { get; set; }
    public string? dir { get; set; }
    public string? status { get; set; }
    public DateTime ts { get; set; }
}