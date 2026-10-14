using CncDash.Server.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace CncDash.Server.Pages.Dashboard;

public class PanelModel : PageModel
{
    private readonly MetricsEngine engine;
    private readonly MachineResolver resolver;
    public PanelModel(MetricsEngine engine, MachineResolver resolver)
    {
        this.engine = engine;
        this.resolver = resolver;
    }

    public MachineStatus Status { get; set; } = new() { MachineId = "?" };
    public string? ErrorMessage { get; set; }

    public async Task OnGet(string? machine)
    {
        if (string.IsNullOrEmpty(machine) && Request.Query.TryGetValue("machine", out var q))
            machine = q.FirstOrDefault();

        var result = await resolver.ResolveAsync(machine);
        if (result?.Machine is null)
        {
            ErrorMessage = result?.AmbiguousCandidates is { Count: > 0 } list
                ? $"ambiguous machine: {string.Join(", ", list)}"
                : $"machine '{machine ?? "??"}' not found";
            return;
        }

        Status = await engine.ComputeAsync(result.Machine.Id)
                 ?? new MachineStatus { MachineId = result.Machine.Id, StatusReason = "config missing" };
    }
}