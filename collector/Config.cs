using System.Text.Json;

namespace CncDash.Collector;

public class CollectorConfig
{
    // MTConnect
    public string AgentUrl { get; set; } = "http://localhost:5000";
    public string DevicePath { get; set; } = "CNC_12";
    public int PollSeconds { get; set; } = 2;
    public int AssetSeconds { get; set; } = 60;

    // Server
    public string ServerUrl { get; set; } = "http://localhost:5154";
    public string MachineId { get; set; } = "cnc12";
    public string ApiKey { get; set; } = "";
    public int UploadSeconds { get; set; } = 5;

    // Temporary instrumentation: observe every data item seen in /current
    // (including ones the parser drops) and dump itemId/count/first/last to a
    // log file every N seconds. 0 = off. Useful for discovering the agent's
    // real data item ids (e.g. the actual cumulative part counter).
    public int DiagnosticDumpSeconds { get; set; } = 0;
    public string DiagnosticLogPath { get; set; } = "datadump.log";

    // Data items to track: dataItemId -> null = any element, tracked generically.
    // Anything not listed is ignored.
    public string[] SampleDataItems { get; set; } = new[]
    {
        "MS1speed", "Mppartcount", "Mpprogram", "Mp1CurrentTool",
    };

    public string[] EventDataItems { get; set; } = new[]
    {
        "Mpexecution", "Mpmode", "Mfmode", "Mp1ToolAssetId", "Mp1SpindleRunTime",
    };

    public string[] AssetTriggerItems { get; set; } = new[]
    {
        "asset_chg", "asset_rem",
    };

    public static CollectorConfig Load(string exeDir)
    {
        var cfg = new CollectorConfig();
        var defaults = Path.Combine(exeDir, "config_default.json");
        if (File.Exists(defaults))
            Merge(cfg, ReadJson(defaults));
        var local = Path.Combine(exeDir, "config_local.json");
        if (File.Exists(local))
            Merge(cfg, ReadJson(local));
        return cfg;
    }

    private static JsonDocument? ReadJson(string path)
    {
        try
        {
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[config] failed to parse {path}: {ex.Message}");
            return null;
        }
    }

    private static void Merge(CollectorConfig cfg, JsonDocument? doc)
    {
        if (doc is null) return;
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var target = typeof(CollectorConfig).GetProperty(prop.Name,
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
            if (target is null || !target.CanWrite) continue;
            try
            {
                if (target.PropertyType == typeof(string))
                    target.SetValue(cfg, prop.Value.GetString());
                else if (target.PropertyType == typeof(int) && prop.Value.ValueKind == JsonValueKind.Number)
                    target.SetValue(cfg, prop.Value.GetInt32());
                else if (target.PropertyType == typeof(string[]))
                    target.SetValue(cfg, prop.Value.EnumerateArray().Select(v => v.GetString() ?? "").ToArray());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[config] bad value for {prop.Name}: {ex.Message}");
            }
        }
    }
}