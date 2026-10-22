using System.Text.Json;
using System.Text.Json.Serialization;
using CncDash.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace CncDash.Server.Api;

public class IngestBatch
{
    [JsonPropertyName("samples")]
    public List<IngestSample> Samples { get; set; } = new();

    [JsonPropertyName("tools")]
    public List<IngestTool> Tools { get; set; } = new();

    [JsonPropertyName("collectorVersion")]
    public string? CollectorVersion { get; set; }

    // the agent's real data-item inventory (ids it published this poll) and
    // the ones the collector could not map to a column
    [JsonPropertyName("seenItems")]
    public List<string> SeenItems { get; set; } = new();

    [JsonPropertyName("unmappedItems")]
    public List<string> UnmappedItems { get; set; } = new();
}

public class IngestSample
{
    [JsonPropertyName("ts")]
    public DateTime TsUtc { get; set; }

    [JsonPropertyName("di")]
    public string DataItemId { get; set; } = default!;

    [JsonPropertyName("seq")]
    public long Sequence { get; set; }

    [JsonPropertyName("speed")]
    public double? SpindleSpeed { get; set; }

    [JsonPropertyName("exec")]
    public string? Execution { get; set; }

    [JsonPropertyName("mode")]
    public string? ControllerMode { get; set; }

    [JsonPropertyName("fmode")]
    public string? FunctionalMode { get; set; }

    [JsonPropertyName("count")]
    public long? PartCount { get; set; }

    [JsonPropertyName("prog")]
    public string? Program { get; set; }

    [JsonPropertyName("tool")]
    public int? CurrentTool { get; set; }

    [JsonPropertyName("totalCuttingTime")]
    public double? TotalCuttingTime { get; set; }

    [JsonPropertyName("runtime")]
    public double? Runtime { get; set; }

    [JsonPropertyName("totalRuntime")]
    public double? TotalRuntime { get; set; }

    [JsonPropertyName("feedCmd")]
    public double? FeedCmd { get; set; }

    [JsonPropertyName("xAct")]
    public double? XAct { get; set; }

    [JsonPropertyName("yAct")]
    public double? YAct { get; set; }

    [JsonPropertyName("zAct")]
    public double? ZAct { get; set; }

    [JsonPropertyName("line")]
    public long? Line { get; set; }

    [JsonPropertyName("block")]
    public string? Block { get; set; }

    [JsonPropertyName("commonVars")]
    public string? CommonVars { get; set; }

    [JsonPropertyName("stationsOn")]
    public int? StationsOn { get; set; }

    [JsonPropertyName("partsTally")]
    public double? PartsTally { get; set; }
}

public class IngestTool
{
    [JsonPropertyName("assetId")]
    public string AssetId { get; set; } = default!;

    [JsonPropertyName("serial")]
    public int? SerialNumber { get; set; }

    [JsonPropertyName("toolNumber")]
    public int? ToolNumber { get; set; }

    [JsonPropertyName("group")]
    public int? ProgramToolGroup { get; set; }

    [JsonPropertyName("pot")]
    public int? Pot { get; set; }

    [JsonPropertyName("initial")]
    public double? LifeInitial { get; set; }

    [JsonPropertyName("value")]
    public double? LifeValue { get; set; }

    [JsonPropertyName("lifeType")]
    public string? LifeType { get; set; }

    [JsonPropertyName("dir")]
    public string? CountDirection { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("ts")]
    public DateTime UpdatedAtUtc { get; set; }
}

public class IngestResult
{
    [JsonPropertyName("samplesAccepted")]
    public int SamplesAccepted { get; set; }

    [JsonPropertyName("samplesDuplicate")]
    public int SamplesDuplicate { get; set; }

    [JsonPropertyName("toolsUpdated")]
    public int ToolsUpdated { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public static class IngestEndpoints
{
    public static void MapIngest(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ingest/{machineId}", async (
            string machineId,
            HttpRequest request,
            CncDbContext db,
            JsonOptionsProvider jsonOptions,
            ILoggerFactory loggerFactory) =>
        {
            var result = new IngestResult();
            var logger = loggerFactory.CreateLogger("Ingest");

            var machine = await db.Machines.FindAsync(machineId);
            if (machine is null)
            {
                result.Error = "unknown machine";
                return Results.NotFound(result);
            }

            var providedKey = request.Headers["X-Api-Key"].FirstOrDefault() ?? "";
            if (!ApiKeyHelper.IsValid(machine, providedKey))
            {
                result.Error = "unauthorized";
                return Results.Unauthorized();
            }

            IngestBatch? batch;
            try
            {
                batch = await JsonSerializer.DeserializeAsync<IngestBatch>(
                    request.Body, jsonOptions.Ingest);
            }
            catch (JsonException ex)
            {
                result.Error = $"bad json: {ex.Message}";
                return Results.BadRequest(result);
            }

            if (batch is null)
            {
                result.Error = "empty body";
                return Results.BadRequest(result);
            }

            await using var tx = await db.Database.BeginTransactionAsync();

            foreach (var s in batch.Samples)
            {
                var exists = await db.Samples.AnyAsync(x =>
                    x.MachineId == machineId &&
                    x.DataItemId == s.DataItemId &&
                    x.Sequence == s.Sequence);
                if (exists)
                {
                    result.SamplesDuplicate++;
                    continue;
                }

                db.Samples.Add(new Data.Entities.Sample
                {
                    MachineId = machineId,
                    TsUtc = s.TsUtc,
                    DataItemId = s.DataItemId,
                    Sequence = s.Sequence,
                    SpindleSpeed = s.SpindleSpeed,
                    Execution = s.Execution,
                    ControllerMode = s.ControllerMode,
                    FunctionalMode = s.FunctionalMode,
                    PartCount = s.PartCount,
                    Program = s.Program,
                    CurrentTool = s.CurrentTool,
                    TotalCuttingTime = s.TotalCuttingTime,
                    Runtime = s.Runtime,
                    TotalRuntime = s.TotalRuntime,
                    FeedCmd = s.FeedCmd,
                    XAct = s.XAct,
                    YAct = s.YAct,
                    ZAct = s.ZAct,
                    Line = s.Line,
                    Block = s.Block,
                    CommonVars = s.CommonVars,
                    StationsOn = s.StationsOn,
                    PartsTally = s.PartsTally,
                });
                result.SamplesAccepted++;
            }

            foreach (var t in batch.Tools)
            {
                var existing = await db.ToolAssets.FirstOrDefaultAsync(x =>
                    x.MachineId == machineId && x.AssetId == t.AssetId);
                if (existing is null)
                {
                    db.ToolAssets.Add(new Data.Entities.ToolAsset
                    {
                        MachineId = machineId,
                        AssetId = t.AssetId,
                        SerialNumber = t.SerialNumber,
                        ToolNumber = t.ToolNumber,
                        ProgramToolGroup = t.ProgramToolGroup,
                        Pot = t.Pot,
                        LifeInitial = t.LifeInitial,
                        LifeValue = t.LifeValue,
                        LifeType = t.LifeType,
                        CountDirection = t.CountDirection,
                        Status = t.Status,
                        UpdatedAtUtc = t.UpdatedAtUtc,
                    });
                }
                else
                {
                    existing.SerialNumber = t.SerialNumber;
                    existing.ToolNumber = t.ToolNumber;
                    existing.ProgramToolGroup = t.ProgramToolGroup;
                    existing.Pot = t.Pot;
                    existing.LifeInitial = t.LifeInitial;
                    existing.LifeValue = t.LifeValue;
                    existing.LifeType = t.LifeType;
                    existing.CountDirection = t.CountDirection;
                    existing.Status = t.Status;
                    existing.UpdatedAtUtc = t.UpdatedAtUtc;
                }
                result.ToolsUpdated++;
            }

            // ---- the agent's data-item inventory -------------------------------
            // Record every id the agent published and whether the collector
            // could map it. This is what makes "this machine shows no common
            // vars" diagnosable: the id is absent from the agent, or present
            // but unmapped by the collector.
            if (batch.SeenItems.Count > 0)
            {
                var unmapped = new HashSet<string>(batch.UnmappedItems, StringComparer.OrdinalIgnoreCase);
                var seen = new HashSet<string>(batch.SeenItems, StringComparer.OrdinalIgnoreCase);
                var known = await db.AgentItems
                    .Where(a => a.MachineId == machineId && seen.Contains(a.DataItemId))
                    .ToListAsync();
                var knownIds = known.Select(a => a.DataItemId).ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var di in seen)
                {
                    var mapped = !unmapped.Contains(di);
                    var row = known.FirstOrDefault(a => string.Equals(a.DataItemId, di, StringComparison.OrdinalIgnoreCase));
                    if (row is null)
                    {
                        db.AgentItems.Add(new Data.Entities.AgentItem
                        {
                            MachineId = machineId,
                            DataItemId = di,
                            Mapped = mapped,
                            LastSeenUtc = DateTime.UtcNow,
                        });
                    }
                    else
                    {
                        row.Mapped = mapped;
                        row.LastSeenUtc = DateTime.UtcNow;
                    }
                }
                // ids the agent no longer publishes
                var stale = knownIds.Except(seen, StringComparer.OrdinalIgnoreCase).ToList();
                if (stale.Count > 0)
                {
                    db.AgentItems.RemoveRange(db.AgentItems.Where(a => a.MachineId == machineId && stale.Contains(a.DataItemId)));
                }
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            logger.LogInformation(
                "ingest {Machine}: {A} samples ({D} dup), {T} tools, collector {V}",
                machineId, result.SamplesAccepted, result.SamplesDuplicate,
                result.ToolsUpdated, batch.CollectorVersion ?? "?");

            return Results.Ok(result);
        });
    }
}

public class JsonOptionsProvider
{
    public JsonSerializerOptions Ingest { get; }
    public JsonOptionsProvider()
    {
        Ingest = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
    }
}

public static class ApiKeyHelper
{
    public static bool IsValid(Data.Entities.Machine machine, string providedKey)
    {
        var expected = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(machine.ApiKey));
        var provided = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(providedKey));
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}