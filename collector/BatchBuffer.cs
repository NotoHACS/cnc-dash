using System.Text.Json;

namespace CncDash.Collector;

public class Batch
{
    public List<SampleRow> samples { get; set; } = new();
    public List<ToolRow> tools { get; set; } = new();
    public string? collectorVersion { get; set; }

    // every dataItemId the agent published this poll, and the ones the
    // collector could not map to a column. The server stores them per machine
    // so "this machine has no common vars" can be told apart from "the agent
    // publishes them under a name we do not map".
    public List<string> seenItems { get; set; } = new();
    public List<string> unmappedItems { get; set; } = new();
}

/// <summary>
/// Crash-safe outbox: every batch is written to a timestamp-named file, then
/// uploaded oldest-first and deleted on success. Network loss never loses data.
/// </summary>
public class BatchBuffer
{
    private readonly string dir;
    private readonly string deadDir;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public BatchBuffer(string exeDir)
    {
        dir = Path.Combine(exeDir, "buffer");
        deadDir = Path.Combine(dir, "dead");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(deadDir);
    }

    public void Enqueue(Batch batch)
    {
        if (batch.samples.Count == 0 && batch.tools.Count == 0) return;
        var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.json";
        File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(batch, JsonOpts));
    }

    public List<string> PendingFiles() =>
        Directory.GetFiles(dir, "*.json").OrderBy(f => Path.GetFileName(f)).ToList();

    public void Dead(string file, string reason)
    {
        try
        {
            var target = Path.Combine(deadDir, Path.GetFileName(file));
            File.Move(file, target, overwrite: true);
            File.WriteAllText(target + ".err", reason);
        }
        catch { /* best effort */ }
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] batch parked in dead/: {reason}");
    }

    public void Delete(string file) => File.Delete(file);
}