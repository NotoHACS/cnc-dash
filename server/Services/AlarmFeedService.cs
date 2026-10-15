using System.Text.Json;

namespace CncDash.Server.Services;

// Subscribes to an ntfy topic exactly like a phone with the notify app: polls
// the ntfy JSON endpoint (since=all returns the retained message cache) and
// keeps the LATEST message. The alarm poller on the CNC box pushes here on
// alarm/clear; the dashboard bar displays whatever the topic's last post is.
public class AlarmFeedService : BackgroundService
{
    private readonly IHttpClientFactory httpFactory;
    private readonly ILogger<AlarmFeedService> log;

    private readonly object gate = new();
    private NtfyMessage? latest;

    public AlarmFeedService(IHttpClientFactory httpFactory, ILogger<AlarmFeedService> log)
    {
        this.httpFactory = httpFactory;
        this.log = log;
    }

    public record NtfyMessage(
        long Time, string Topic, string? Title, string? Message, int? Priority);

    // what the dashboard API serves: latest post (null fields when the topic
    // is empty) + server receive time so clients can render "N ago"
    public object? Snapshot()
    {
        lock (gate)
        {
            if (latest is null) return null;
            return new
            {
                ts = latest.Time,
                topic = latest.Topic,
                title = latest.Title,
                message = latest.Message,
                priority = latest.Priority ?? 3,
                receivedUtc = DateTime.UtcNow,
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var server = Environment.GetEnvironmentVariable("NTFY_SERVER_URL") ?? "https://ntfy.fehustn.cc";
        var topic = Environment.GetEnvironmentVariable("NTFY_TOPIC") ?? "cnc12";
        var url = $"{server}/{topic}/json?poll=1&since=all";

        // initial: replay the retained cache, keep only the newest; then tail
        // with since=<id of last seen> — the same session the notify app shows.
        string? since = null;
        var backoff = TimeSpan.FromSeconds(2);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var http = httpFactory.CreateClient("alarmfeed");
                var reqUrl = since == null ? url : $"{server}/{topic}/json?poll=1&since={Uri.EscapeDataString(since)}";
                using var resp = await http.GetAsync(reqUrl, stoppingToken);
                var body = await resp.Content.ReadAsStringAsync(stoppingToken);
                NtfyMessage? newest = null;
                string? lastId = null;
                foreach (var line in body.Split('\n'))
                {
                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith('{')) continue;
                    try
                    {
                        var m = JsonSerializer.Deserialize<NtfyMessage>(line,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (m is null || string.IsNullOrEmpty(m.Topic)) continue;
                        lastId = ExtractId(line);
                        if (newest is null || m.Time > newest.Time) newest = m;
                    }
                    catch { /* skip bad line */ }
                }
                if (since == null && newest != null)
                {
                    // only adopt history as "latest" if it is recent — a stale
                    // cached alarm from days ago must not show as current
                    if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - newest.Time < 3600)
                        Store(newest);
                }
                if (lastId != null) since = lastId;
                if (newest != null && since != null) StoreIfNewer(newest);
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "ntfy feed poll failed, retrying");
                await Task.Delay(backoff, stoppingToken);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void StoreIfNewer(NtfyMessage m)
    {
        lock (gate)
        {
            if (latest is null || m.Time >= latest.Time)
            {
                latest = m;
                log.LogInformation("alarmfeed: new post p{Prio} {Title}", m.Priority, m.Title ?? m.Message);
            }
        }
    }

    private void Store(NtfyMessage m) => StoreIfNewer(m);

    private static string? ExtractId(string line)
    {
        // cheap id extraction without double-deserializing a wrapper type
        const string key = "\"id\":\"";
        var i = line.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        var start = i + key.Length;
        var end = line.IndexOf('"', start);
        return end < 0 ? null : line[start..end];
    }
}