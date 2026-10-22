using System.Text.Json;
using CncDash.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace CncDash.Server.Services;

// Subscribes to an ntfy topic PER MACHINE. Each machine's topic comes from its
// admin config (MachineConfig.NtfyTopic); blank = the machine id itself
// (cnc8 -> topic "cnc8"). Every topic is polled independently - own cursor,
// own latest message - so the alarm bar on /dashboard/<machine> shows THAT
// machine's alarms only, never another machine's.
public class AlarmFeedService : BackgroundService
{
    private readonly IHttpClientFactory httpFactory;
    private readonly ILogger<AlarmFeedService> log;
    private readonly IServiceScopeFactory scopes;

    private readonly object gate = new();

    // topic -> live state (cursor + latest post + worker)
    private readonly Dictionary<string, TopicState> topicStates = new(StringComparer.OrdinalIgnoreCase);

    // machineId -> topic it listens on
    private readonly Dictionary<string, string> machineTopic = new(StringComparer.OrdinalIgnoreCase);

    public AlarmFeedService(
        IHttpClientFactory httpFactory,
        ILogger<AlarmFeedService> log,
        IServiceScopeFactory scopes)
    {
        this.httpFactory = httpFactory;
        this.log = log;
        this.scopes = scopes;
    }

    public record NtfyMessage(
        long Time, string Topic, string? Title, string? Message, int? Priority);

    private sealed class TopicState
    {
        public string Topic = "";
        public string Since = "";
        public NtfyMessage? Latest;
        public CancellationTokenSource? Cts;
    }

    // what the dashboard API serves for a machine: latest post on that
    // machine's topic (null fields when the topic has nothing yet) + server
    // receive time so clients can render "N ago"
    public object? Snapshot(string machineId)
    {
        lock (gate)
        {
            if (!machineTopic.TryGetValue(machineId, out var topic)) return null;
            if (!topicStates.TryGetValue(topic, out var st)) return null;
            var m = st.Latest;
            return new
            {
                ts = m?.Time ?? 0,
                topic = topic,
                title = m?.Title,
                message = m?.Message,
                priority = m?.Priority ?? 3,
                receivedUtc = DateTime.UtcNow,
            };
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // keep the topic set in sync with the admin config: a machine added or
        // re-pointed at another topic must start/stop its subscription
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncTopicsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "alarmfeed topic sync failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SyncTopicsAsync(CancellationToken ct)
    {
        var server = Environment.GetEnvironmentVariable("NTFY_SERVER_URL") ?? "https://ntfy.fehustn.cc";

        Dictionary<string, string> desired;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CncDbContext>();
            var machineIds = await db.Machines.AsNoTracking()
                .Select(m => m.Id).ToListAsync(ct);
            var configured = await db.MachineConfigs.AsNoTracking()
                .ToDictionaryAsync(c => c.MachineId, c => c.NtfyTopic, ct);

            desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in machineIds)
            {
                var topic = configured.TryGetValue(id, out var cfg) && !string.IsNullOrWhiteSpace(cfg)
                    ? cfg.Trim()
                    : id;
                if (!string.IsNullOrWhiteSpace(topic)) desired[id] = topic;
            }
        }

        List<TopicState> stopped;
        lock (gate)
        {
            // drop machines that no longer exist
            foreach (var key in machineTopic.Keys.ToList())
                if (!desired.ContainsKey(key)) machineTopic.Remove(key);

            foreach (var (machine, topic) in desired)
                machineTopic[machine] = topic;

            var wanted = desired.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // stop subscriptions for topics nobody listens to anymore
            stopped = new List<TopicState>();
            foreach (var key in topicStates.Keys.ToList())
            {
                if (wanted.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                stopped.Add(topicStates[key]);
                topicStates.Remove(key);
            }

            // start subscriptions for new topics
            foreach (var topic in wanted)
            {
                if (topicStates.ContainsKey(topic)) continue;
                var st = new TopicState
                {
                    Topic = topic,
                    // live-only: the retained message cache is NOT replayed
                    // (a probe/test post from hours ago must not show as
                    // current); only posts arriving after we start count
                    Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                };
                topicStates[topic] = st;
                st.Cts = new CancellationTokenSource();
                _ = Task.Run(() => PollTopicAsync(server, st, st.Cts.Token), st.Cts.Token);
                log.LogInformation("alarmfeed: subscribed topic {Topic}", topic);
            }
        }

        foreach (var st in stopped)
        {
            try { st.Cts?.Cancel(); st.Cts?.Dispose(); } catch { /* ignore */ }
            log.LogInformation("alarmfeed: unsubscribed topic {Topic}", st.Topic);
        }
    }

    private async Task PollTopicAsync(string server, TopicState st, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var http = httpFactory.CreateClient("alarmfeed");
                var reqUrl = $"{server}/{st.Topic}/json?poll=1&since={Uri.EscapeDataString(st.Since)}";
                using var resp = await http.GetAsync(reqUrl, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
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
                if (lastId != null) st.Since = lastId;
                if (newest != null) StoreIfNewer(st, newest);
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "ntfy feed poll failed for {Topic}", st.Topic);
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
                continue;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private void StoreIfNewer(TopicState st, NtfyMessage m)
    {
        lock (gate)
        {
            if (st.Latest is null || m.Time >= st.Latest.Time)
            {
                st.Latest = m;
                log.LogInformation("alarmfeed[{Topic}]: new post p{Prio} {Title}", st.Topic, m.Priority, m.Title ?? m.Message);
            }
        }
    }

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
