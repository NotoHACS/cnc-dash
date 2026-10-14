using System.Text;
using System.Text.Json;

namespace CncDash.Collector;

public class Uploader : IDisposable
{
    private readonly HttpClient http;
    private readonly CollectorConfig cfg;
    private readonly BatchBuffer buffer;

    public Uploader(CollectorConfig cfg, BatchBuffer buffer)
    {
        this.cfg = cfg;
        this.buffer = buffer;
        http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.Add("X-Api-Key", cfg.ApiKey);
        // api key is not in the URL so ConnectionClose default (keep-alive) is fine here
    }

    /// <summary>Flush pending buffered batches. Returns files pending after the run.</summary>
    public async Task<int> FlushAsync()
    {
        foreach (var file in buffer.PendingFiles())
        {
            if (!await TrySendAsync(file))
                return buffer.PendingFiles().Count; // stop on first failure, retry next tick
        }
        return 0;
    }

    private async Task<bool> TrySendAsync(string file)
    {
        string json;
        try { json = File.ReadAllText(file); }
        catch { return false; }

        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var uri = $"{cfg.ServerUrl.TrimEnd('/')}/api/ingest/{cfg.MachineId}";
            using var resp = await http.PostAsync(uri, content);

            if (resp.IsSuccessStatusCode)
            {
                buffer.Delete(file);
                return true;
            }

            // 4xx = our data or config is wrong; retrying forever is pointless.
            if ((int)resp.StatusCode is >= 400 and < 500)
            {
                var body = await resp.Content.ReadAsStringAsync();
                buffer.Dead(file, $"HTTP {(int)resp.StatusCode} {body}".Trim());
                return true; // continue flushing other files
            }

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] upload retry (HTTP {(int)resp.StatusCode})");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] upload failed (buffered): {ex.Message}");
            return false;
        }
    }

    public void Dispose() => http.Dispose();
}