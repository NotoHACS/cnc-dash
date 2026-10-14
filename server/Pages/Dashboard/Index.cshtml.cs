using CncDash.Server.Data;
using CncDash.Server.Data.Entities;
using CncDash.Server.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace CncDash.Server.Pages.Dashboard;

public class IndexModel : PageModel
{
    private readonly MetricsEngine engine;
    private readonly MachineResolver resolver;
    private readonly IServiceScopeFactory scopes;

    public IndexModel(MetricsEngine engine, MachineResolver resolver, IServiceScopeFactory scopes)
    {
        this.engine = engine;
        this.resolver = resolver;
        this.scopes = scopes;
    }

    public MachineStatus? Status { get; set; }
    public List<MachineCard> MachineCards { get; set; } = new();
    public string? Notice { get; set; }
    public bool ShowPicker { get; set; }

    public async Task OnGet(string? machine)
    {
        if (string.IsNullOrEmpty(machine) && Request.Query.TryGetValue("machine", out var q))
            machine = q.FirstOrDefault();

        var result = await resolver.ResolveAsync(machine);

        if (result is null)
        {
            ShowPicker = true;
            await LoadCardsAsync();
            return;
        }

        if (result.NotFound)
        {
            ShowPicker = true;
            Notice = $"machine '{machine}' not found";
            await LoadCardsAsync();
            return;
        }

        if (result.AmbiguousCandidates is { Count: > 0 } list)
        {
            ShowPicker = true;
            Notice = $"'{machine}' is ambiguous: {string.Join(", ", list)} — pick one";
            await LoadCardsAsync();
            return;
        }

        Status = await engine.ComputeAsync(result.Machine!.Id)
                 ?? new MachineStatus { MachineId = result.Machine.Id, StatusReason = "config missing" };
    }

    private async Task LoadCardsAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CncDbContext>();
        var machines = await db.Machines.AsNoTracking().OrderBy(m => m.Id).ToListAsync();

        var cards = new List<MachineCard>();
        foreach (var m in machines)
        {
            var s = await engine.ComputeAsync(m.Id);
            cards.Add(new MachineCard
            {
                Id = m.Id,
                Short = MachineCard.ShortLabel(m.Id),
                DisplayName = s?.DisplayName ?? m.DisplayName,
                Color = s?.Color ?? StatusColor.Off,
                PartsThisShift = s?.PartsThisShift ?? 0,
                PercentOfExpected = s?.PercentOfExpected ?? 0,
            });
        }
        MachineCards = cards;
    }
}

public class MachineCard
{
    public string Id { get; set; } = default!;
    public string Short { get; set; } = default!;
    public string DisplayName { get; set; } = default!;
    public StatusColor Color { get; set; }
    public long PartsThisShift { get; set; }
    public double PercentOfExpected { get; set; }

    private static readonly Regex TrailingDigits = new(@"(\d+)$", RegexOptions.Compiled);

    public static string ShortLabel(string machineId)
    {
        var m = TrailingDigits.Match(machineId);
        return m.Success ? m.Groups[1].Value : machineId;
    }
}