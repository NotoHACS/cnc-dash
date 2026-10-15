using System.Text.Json;
namespace CncDash.Collector;

public static class Program
{
    private const string Version = "0.1.0";

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var exeDir = AppContext.BaseDirectory;
        var cfg = CollectorConfig.Load(exeDir);

        Console.WriteLine($"CncDash collector v{Version}");
        Console.WriteLine($"  agent:  {cfg.AgentUrl}/{cfg.DevicePath}/current");
        Console.WriteLine($"  server: {cfg.ServerUrl} (machine {cfg.MachineId})");

        if (string.IsNullOrEmpty(cfg.ApiKey))
            Console.WriteLine("  WARNING: no API key configured (config_local.json). Uploads will be rejected.");

        var buffer = new BatchBuffer(exeDir);
        using var uploader = new Uploader(cfg, buffer);
        var mtconnect = new MtConnectClient(cfg);
        var assets = new AssetClient(cfg);

        if (mtconnect.Diagnostics is { } diag)
            Console.WriteLine($"  diagnostics: every {cfg.DiagnosticDumpSeconds}s -> {diag.LogPath}");

        // Persisted last-seen values so a restart doesn't re-send an unchanged snapshot.
        var statePath = Path.Combine(exeDir, "state.json");
        var lastValues = LoadState(statePath);

        var pendingTools = new List<ToolRow>();
        DateTime lastAssetFetch = DateTime.MinValue;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Kick off initial flush of anything buffered from previous runs.
        _ = uploader.FlushAsync();

        var nextPoll = DateTime.UtcNow;
        var nextUpload = DateTime.UtcNow.AddSeconds(cfg.UploadSeconds);
        var nextDump = mtconnect.Diagnostics is null
            ? DateTime.MaxValue
            : DateTime.UtcNow.AddSeconds(cfg.DiagnosticDumpSeconds);

        while (!cts.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            try
            {
                if (now >= nextPoll)
                {
                    nextPoll = now.AddSeconds(cfg.PollSeconds);
                    var poll = await mtconnect.PollAsync(lastValues);
                    if (poll is not null)
                    {
                        // change detection in PollAsync already suppresses unchanged
                        // items; with empty state it emits a full snapshot.
                        buffer.Enqueue(new Batch
                        {
                            samples = poll.Samples,
                            tools = new List<ToolRow>(),
                            collectorVersion = Version,
                        });

                        if (poll.AssetChangedTrigger)
                        {
                            // targeted fetch of the changed asset (server upserts
                            // by assetId); full refetch as fallback
                            var targetId = poll.ChangedAssetId?.Trim();
                            ToolRow? targeted = null;
                            if (!string.IsNullOrWhiteSpace(targetId) &&
                                !targetId.Equals("UNAVAILABLE", StringComparison.OrdinalIgnoreCase))
                                targeted = await assets.FetchOneAsync(targetId);

                            if (targeted != null)
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [debug] asset change: targeted fetch {targetId}");
                                pendingTools.Add(targeted);
                                lastAssetFetch = now;
                            }
                            else
                            {
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [debug] asset change: full refetch fallback");
                                lastAssetFetch = DateTime.MinValue;
                            }
                        }

                        SaveState(statePath, lastValues);
                    }

                    var dueAsset = (now - lastAssetFetch).TotalSeconds >= cfg.AssetSeconds;
                    if (lastAssetFetch == DateTime.MinValue || dueAsset)
                    {
                        var tools = await assets.FetchAsync();
                        if (tools is not null)
                        {
                            lastAssetFetch = now;
                            pendingTools = tools;
                        }
                    }

                    if (pendingTools.Count > 0)
                    {
                        buffer.Enqueue(new Batch
                        {
                            samples = new List<SampleRow>(),
                            tools = pendingTools,
                            collectorVersion = Version,
                        });
                        pendingTools = new List<ToolRow>();
                    }
                }

                if (now >= nextUpload)
                {
                    var remaining = await uploader.FlushAsync();
                    nextUpload = DateTime.UtcNow.AddSeconds(cfg.UploadSeconds);
                    if (remaining > 0)
                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {remaining} batch(es) buffered");
                }

                if (now >= nextDump)
                {
                    mtconnect.Diagnostics?.Dump();
                    nextDump = DateTime.UtcNow.AddSeconds(cfg.DiagnosticDumpSeconds);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] loop error: {ex.Message}");
            }

            await Task.Delay(250, cts.Token).ContinueWith(_ => { });
        }

        SaveState(statePath, lastValues);
        Console.WriteLine("shutdown");
        return 0;
    }

    private static Dictionary<string, string> LoadState(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static void SaveState(string path, Dictionary<string, string> state)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"state save failed: {ex.Message}");
        }
    }
}