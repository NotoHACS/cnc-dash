using CncDash.Server.Data;
using CncDash.Server.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace CncDash.Server.Pages.Dashboard;

public class IndexModel : PageModel
{
    private readonly MetricsEngine engine;
    private readonly IServiceScopeFactory scopes;
    public IndexModel(MetricsEngine engine, IServiceScopeFactory scopes)
    {
        this.engine = engine;
        this.scopes = scopes;
    }

    public MachineStatus Status { get; set; } = new() { MachineId = "?" };
    public List<string> MachineIds { get; set; } = new();

    public async Task OnGet(string? machine)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CncDbContext>();
        MachineIds = await db.Machines.Select(m => m.Id).OrderBy(x => x).ToListAsync();

        var id = machine ?? MachineIds.FirstOrDefault();
        if (id is null)
        {
            Status = new MachineStatus { MachineId = "?", StatusReason = "No machines configured — visit /dashboard/admin" };
            return;
        }
        Status = await engine.ComputeAsync(id)
                 ?? new MachineStatus { MachineId = id, StatusReason = "Not found" };
    }
}

public class PanelModel : PageModel
{
    private readonly MetricsEngine engine;
    public PanelModel(MetricsEngine engine) => this.engine = engine;

    public MachineStatus Status { get; set; } = new() { MachineId = "?" };

    public async Task OnGet(string machine)
    {
        Status = await engine.ComputeAsync(machine)
                 ?? new MachineStatus { MachineId = machine, StatusReason = "Not found" };
    }
}