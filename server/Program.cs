using CncDash.Server.Api;
using CncDash.Server.Data;
using CncDash.Server.Data.Entities;
using CncDash.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Data.Sqlite;
using System.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration["CncDash:DbPath"]
             ?? Environment.GetEnvironmentVariable("CNC_DASH_DB")
             ?? Path.Combine(builder.Environment.ContentRootPath, "data", "cncdash.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddSingleton<JsonOptionsProvider>();
builder.Services.AddSingleton<MachineResolver>();
builder.Services.AddDbContext<CncDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<MetricsEngine>();
builder.Services.AddHttpClient("alarmfeed");
builder.Services.AddSingleton<AlarmFeedService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlarmFeedService>());
builder.Services.AddSingleton<ShiftHistoryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ShiftHistoryService>());

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/dashboard/admin/login";
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services.AddRazorPages();
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CncDbContext>();
    db.Database.EnsureCreated();

    // additive schema patch for existing databases created before the new
    // columns existed (EnsureCreated only builds fresh DBs)
    var sqconn = (SqliteConnection)db.Database.GetDbConnection();
    if (sqconn.State != ConnectionState.Open) await sqconn.OpenAsync();
    await EnsureColumnAsync(sqconn, "MachineConfigs", "SpindleStopDelaySeconds", "INTEGER NOT NULL DEFAULT 30");
    await EnsureColumnAsync(sqconn, "MachineConfigs", "ProbeToolNumber", "INTEGER NOT NULL DEFAULT 1");
    await EnsureColumnAsync(sqconn, "MachineConfigs", "PartsThisShiftOverride", "INTEGER NULL");
    await EnsureColumnAsync(sqconn, "Samples", "TotalCuttingTime", "REAL NULL");
    await EnsureColumnAsync(sqconn, "Samples", "FeedCmd", "REAL NULL");
    await EnsureColumnAsync(sqconn, "Samples", "XAct", "REAL NULL");
    await EnsureColumnAsync(sqconn, "Samples", "YAct", "REAL NULL");
    await EnsureColumnAsync(sqconn, "Samples", "ZAct", "REAL NULL");
    await EnsureColumnAsync(sqconn, "Samples", "Line", "INTEGER NULL");
    await EnsureColumnAsync(sqconn, "Samples", "Block", "TEXT NULL");
    await EnsureColumnAsync(sqconn, "Samples", "CommonVars", "TEXT NULL");
    await EnsureColumnAsync(sqconn, "Samples", "StationsOn", "INTEGER NULL");
    await EnsureColumnAsync(sqconn, "Samples", "PartsTally", "REAL NULL");
    await EnsureColumnAsync(sqconn, "MachineConfigs", "PartsTallyVarIndex", "INTEGER NOT NULL DEFAULT 41");
    await EnsureColumnAsync(sqconn, "MachineConfigs", "CycleVarIndex", "INTEGER NOT NULL DEFAULT 0");
    await EnsureColumnAsync(sqconn, "MachineConfigs", "StationsVarIndex", "INTEGER NOT NULL DEFAULT 40");
    await EnsureColumnAsync(sqconn, "MachineConfigs", "PartNameVarIndex", "INTEGER NOT NULL DEFAULT 65");
    await EnsureColumnAsync(sqconn, "MachineConfigs", "QuotaParts", "INTEGER NOT NULL DEFAULT 48");
    if (sqconn.State == ConnectionState.Open) await sqconn.CloseAsync();

    if (!await db.Machines.AnyAsync())
    {
        // Seed only when explicitly requested (first run) so ingest is testable
        // immediately; real machines are added via /dashboard/admin.
        var seedUser = Environment.GetEnvironmentVariable("CNC_DASH_SEED_MACHINE");
        if (!string.IsNullOrEmpty(seedUser))
        {
            var key = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            db.Machines.Add(new Machine
            {
                Id = seedUser,
                ApiKey = key,
                DisplayName = seedUser.ToUpperInvariant(),
                CreatedAtUtc = DateTime.UtcNow,
                Config = new MachineConfig { MachineId = seedUser },
            });
            await db.SaveChangesAsync();
            Console.WriteLine($"[seed] machine '{seedUser}' created. API key: {key}");
        }
    }
}

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", async (CncDbContext db) =>
    await db.Database.CanConnectAsync()
        ? Results.Ok(new { status = "ok", utc = DateTime.UtcNow })
        : Results.StatusCode(503));

app.MapIngest();

app.MapGet("/api/status/{segment}", async (string segment, MachineResolver resolver, MetricsEngine engine) =>
{
    var resolved = await resolver.ResolveAsync(segment);
    if (resolved?.Machine is not { } machine)
    {
        return resolved is not null && resolved.AmbiguousCandidates is { Count: > 0 } list
            ? Results.Conflict(new { ambiguous = list })
            : Results.NotFound();
    }
    var status = await engine.ComputeAsync(machine.Id);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

// latest ntfy post on the machine's alarm topic (server-side subscription via
// AlarmFeedService); null body when the topic has nothing recent
app.MapGet("/api/alarmfeed", (AlarmFeedService feed) =>
{
    var snap = feed.Snapshot();
    return snap is null ? Results.NotFound() : Results.Ok(snap);
});

// captured shift history for a machine — newest first, optional limit
app.MapGet("/api/shifthistory/{machineId}", async (string machineId, int? limit, IServiceScopeFactory scopes) =>
{
    using var scope = scopes.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CncDbContext>();
    var rows = await db.ShiftHistories.AsNoTracking()
        .Where(h => h.MachineId == machineId)
        .OrderByDescending(h => h.StartUtc)
        .Take(limit is > 0 and < 100 ? limit.Value : 14)
        .Select(h => new
        {
            localDate = h.LocalDate.ToString("yyyy-MM-dd"),
            shift = h.ShiftNumber,
            startUtc = h.StartUtc,
            endUtc = h.EndUtc,
            parts = h.PartsCount,
        })
        .ToListAsync();
    return rows.Count == 0 ? Results.NotFound() : Results.Ok(rows);
});

app.MapRazorPages();

app.Run();

static async Task EnsureColumnAsync(SqliteConnection conn, string table, string column, string decl)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
    if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0)
    {
        cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {decl}";
        await cmd.ExecuteNonQueryAsync();
        Console.WriteLine($"[schema] {table}.{column} added ({decl})");
    }
}