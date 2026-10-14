using CncDash.Server.Api;
using CncDash.Server.Data;
using CncDash.Server.Data.Entities;
using CncDash.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration["CncDash:DbPath"]
             ?? Environment.GetEnvironmentVariable("CNC_DASH_DB")
             ?? Path.Combine(builder.Environment.ContentRootPath, "data", "cncdash.db");
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddSingleton<JsonOptionsProvider>();
builder.Services.AddDbContext<CncDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddSingleton<MetricsEngine>();

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

app.MapGet("/api/status/{machineId}", async (string machineId, MetricsEngine engine) =>
{
    var status = await engine.ComputeAsync(machineId);
    return status is null ? Results.NotFound() : Results.Ok(status);
});

app.MapRazorPages();

app.Run();