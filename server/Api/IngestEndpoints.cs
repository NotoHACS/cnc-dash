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