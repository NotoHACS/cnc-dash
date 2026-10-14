using System.Text.Json;
using System.Xml.Linq;

namespace CncDash.Collector;

public class AssetClient
{
    private static readonly XNamespace Ns = "urn:mtconnect.org:MTConnectAssets:1.4";
    private static readonly XNamespace Okuma = "urn:okuma.com:OkumaToolAssets:1.4";

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
        XDocument doc;
        try
        {
            var uri = $"{cfg.AgentUrl.TrimEnd('/')}/asset?type=CuttingTool";
            var xml = await http.GetStringAsync(uri);
            doc = XDocument.Parse(xml);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] asset fetch failed: {ex.Message}");
            return null;
        }

        var tools = new List<ToolRow>();
        foreach (var asset in doc.Descendants(Ns + "CuttingTool"))
        {
            var assetId = asset.Attribute("assetId")?.Value;
            if (string.IsNullOrEmpty(assetId)) continue;

            int.TryParse(asset.Attribute("serialNumber")?.Value, out var serial);

            var lc = asset.Element(Ns + "CuttingToolLifeCycle");
            int? toolNumber = null, group = null, pot = null;
            double? initial = null, value = null;
            string? lifeType = null, dir = null, status = null;

            if (lc != null)
            {
                status = lc.Element(Ns + "CutterStatus")?.Element(Ns + "Status")?.Value;
                var tn = lc.Element(Ns + "ProgramToolNumber");
                if (tn != null && int.TryParse(tn.Value, out var tnVal)) toolNumber = tnVal;
                var tg = lc.Element(Ns + "ProgramToolGroup");
                if (tg != null && int.TryParse(tg.Value, out var tgVal)) group = tgVal;
                var loc = lc.Element(Ns + "Location");
                if (loc != null && int.TryParse(loc.Value, out var potVal)) pot = potVal;

                var life = lc.Element(Ns + "ToolLife");
                if (life != null)
                {
                    // Okuma extension elements/attributes live in x: ns; attributes are unprefixed here
                    lifeType = life.Attribute("type")?.Value;
                    dir = life.Attribute("countDirection")?.Value;
                    if (double.TryParse(life.Attribute("initial")?.Value, out var initVal)) initial = initVal;
                    if (double.TryParse(life.Value, out var valVal)) value = valVal;
                }
            }

            tools.Add(new ToolRow
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
            });
        }
        return tools;
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