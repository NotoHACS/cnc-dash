using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using CncDash.Server.Data;

namespace CncDash.Server.Pages.Dashboard;

public class HistoryModel : PageModel
{
    private readonly IServiceScopeFactory scopes;

    public HistoryModel(IServiceScopeFactory scopes) => this.scopes = scopes;

    public string MachineId { get; set; } = "";
    public string DisplayName { get; set; } = "";

    public async Task OnGetAsync(string? machine)
    {
        MachineId = (machine ?? "").Trim().ToLowerInvariant();
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CncDbContext>();
        var m = await db.Machines.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == MachineId);
        if (m is null && MachineId.EndsWith("12") && MachineId.Length > 2)
            MachineId = "cnc12"; // tolerate /dashboard/12/history style links
        DisplayName = m?.DisplayName ?? MachineId;
    }
}