using CncDash.Server.Data;
using CncDash.Server.Data.Entities;
using CncDash.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace CncDash.Server.Pages.Admin;

[Authorize]
public class IndexModel : PageModel
{
    private readonly CncDbContext db;
    private readonly MachineResolver resolver;

    public IndexModel(CncDbContext db, MachineResolver resolver)
    {
        this.db = db;
        this.resolver = resolver;
    }

    public List<MachineRow> Machines { get; set; } = new();
    public string Message { get; set; } = "";

    [BindProperty] public string EditMachineId { get; set; } = "";
    [BindProperty] public bool Shift1Enabled { get; set; }
    [BindProperty] public string? Shift1Start { get; set; }
    [BindProperty] public string? Shift1End { get; set; }
    [BindProperty] public bool Shift2Enabled { get; set; }
    [BindProperty] public string? Shift2Start { get; set; }
    [BindProperty] public string? Shift2End { get; set; }
    [BindProperty] public bool Shift3Enabled { get; set; }
    [BindProperty] public string? Shift3Start { get; set; }
    [BindProperty] public string? Shift3End { get; set; }
    [BindProperty] public string? PartName { get; set; }
    [BindProperty] public int PartsPerCycle { get; set; }
    [BindProperty] public double GreenThresholdPercent { get; set; }
    [BindProperty] public double WarnThresholdPercent { get; set; }
    [BindProperty] public int CycleLearningWindow { get; set; }
    [BindProperty] public double SpindleRunningRpm { get; set; }
    [BindProperty] public int SpindleStopDelaySeconds { get; set; }
    [BindProperty] public int ProbeToolNumber { get; set; }
    [BindProperty] public double ExpectedCycleSeconds { get; set; }
    [BindProperty] public int? PartsThisShiftOverride { get; set; }
    [BindProperty] public string? TimeZone { get; set; }

    [BindProperty] public string? NewMachineId { get; set; }
    [BindProperty] public string? NewMachineName { get; set; }

    public async Task<IActionResult> OnGet(string? machine)
    {
        Machines = await LoadMachines();

        // route segment first, then legacy ?machine=, then the id posted back
        // from the form, then the first machine
        if (string.IsNullOrEmpty(machine) && Request.Query.TryGetValue("machine", out var q))
            machine = q.FirstOrDefault();
        if (string.IsNullOrEmpty(machine))
            machine = EditMachineId;

        Machine? target = null;
        if (!string.IsNullOrEmpty(machine))
        {
            var resolved = await resolver.ResolveAsync(machine);
            if (resolved?.Machine is not null)
                target = resolved.Machine;
            if (resolved?.AmbiguousCandidates is { Count: > 0 } amb)
                Message = $"machine '{machine}' is ambiguous: {string.Join(", ", amb)}";
        }
        target ??= await db.Machines.AsNoTracking().OrderBy(m => m.Id).FirstOrDefaultAsync();

        if (target != null)
            LoadForm(target.Id);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        var cfg = await db.MachineConfigs.FindAsync(EditMachineId);
        var machine = await db.Machines.FindAsync(EditMachineId);
        if (cfg is null || machine is null)
        {
            Message = $"machine '{EditMachineId}' not found";
            await OnGet(null);
            return Page();
        }

        if (Shift1Enabled) ApplyTime(cfg, 1, Shift1Start, Shift1End); else { cfg.Shift1Start = null; cfg.Shift1End = null; }
        if (Shift2Enabled) ApplyTime(cfg, 2, Shift2Start, Shift2End); else { cfg.Shift2Start = null; cfg.Shift2End = null; }
        if (Shift3Enabled) ApplyTime(cfg, 3, Shift3Start, Shift3End); else { cfg.Shift3Start = null; cfg.Shift3End = null; }

        cfg.PartName = PartName ?? "UNSET";
        cfg.PartsPerCycle = Math.Max(1, PartsPerCycle);
        cfg.GreenThresholdPercent = Math.Clamp(GreenThresholdPercent, 1, 200);
        cfg.WarnThresholdPercent = Math.Clamp(WarnThresholdPercent, 0, 100);
        cfg.CycleLearningWindow = Math.Max(3, CycleLearningWindow);
        cfg.SpindleRunningRpm = Math.Max(0, SpindleRunningRpm);
        cfg.SpindleStopDelaySeconds = Math.Max(0, SpindleStopDelaySeconds);
        cfg.ProbeToolNumber = Math.Max(0, ProbeToolNumber);
        cfg.ExpectedCycleSeconds = Math.Max(0, ExpectedCycleSeconds);
        cfg.PartsThisShiftOverride = PartsThisShiftOverride is < 0 ? null : PartsThisShiftOverride;
        cfg.TimeZone = string.IsNullOrWhiteSpace(TimeZone) ? cfg.TimeZone : TimeZone.Trim();

        await db.SaveChangesAsync();
        Message = $"saved config for {EditMachineId}";
        return RedirectToPage(new { machine = EditMachineId });
    }

    public async Task<IActionResult> OnPostAddMachineAsync()
    {
        var id = NewMachineId?.Trim().ToLowerInvariant().Replace(' ', '-');
        if (string.IsNullOrWhiteSpace(id) || await db.Machines.AnyAsync(m => m.Id == id))
        {
            Message = "machine id empty or already exists";
            await OnGet(null);
            return Page();
        }
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        db.Machines.Add(new Machine
        {
            Id = id,
            ApiKey = key,
            DisplayName = string.IsNullOrWhiteSpace(NewMachineName) ? id.ToUpperInvariant() : NewMachineName.Trim(),
            CreatedAtUtc = DateTime.UtcNow,
            Config = new MachineConfig { MachineId = id },
        });
        await db.SaveChangesAsync();
        Message = $"machine '{id}' created, API key generated";
        return RedirectToPage(new { machine = id });
    }

    private void LoadForm(string machineId)
    {
        var cfg = db.MachineConfigs.AsNoTracking().First(c => c.MachineId == machineId);
        EditMachineId = machineId;
        PartName = cfg.PartName;
        PartsPerCycle = cfg.PartsPerCycle;
        GreenThresholdPercent = cfg.GreenThresholdPercent;
        WarnThresholdPercent = cfg.WarnThresholdPercent;
        CycleLearningWindow = cfg.CycleLearningWindow;
        SpindleRunningRpm = cfg.SpindleRunningRpm;
        SpindleStopDelaySeconds = cfg.SpindleStopDelaySeconds;
        ProbeToolNumber = cfg.ProbeToolNumber;
        ExpectedCycleSeconds = cfg.ExpectedCycleSeconds;
        PartsThisShiftOverride = cfg.PartsThisShiftOverride;
        TimeZone = cfg.TimeZone;

        Shift1Enabled = cfg.Shift1Start != null;
        Shift1Start = FormatMinutes(cfg.Shift1Start);
        Shift1End = FormatMinutes(cfg.Shift1End);
        Shift2Enabled = cfg.Shift2Start != null;
        Shift2Start = FormatMinutes(cfg.Shift2Start);
        Shift2End = FormatMinutes(cfg.Shift2End);
        Shift3Enabled = cfg.Shift3Start != null;
        Shift3Start = FormatMinutes(cfg.Shift3Start);
        Shift3End = FormatMinutes(cfg.Shift3End);
    }

    private static string? FormatMinutes(int? minutes) =>
        minutes is null ? null : TimeSpan.FromMinutes(minutes.Value).ToString(@"hh\:mm");

    private static void ApplyTime(MachineConfig cfg, int shift, string? start, string? end)
    {
        if (!TryParseTime(start, out var s) || !TryParseTime(end, out var e))
            return; // invalid input ignored (form enforces type=time normally)
        switch (shift)
        {
            case 1: cfg.Shift1Start = s; cfg.Shift1End = e; break;
            case 2: cfg.Shift2Start = s; cfg.Shift2End = e; break;
            case 3: cfg.Shift3Start = s; cfg.Shift3End = e; break;
        }
    }

    private static bool TryParseTime(string? text, out int minutes)
    {
        minutes = 0;
        return TimeSpan.TryParseExact(text, @"hh\:mm", null, out var ts) && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1)
            && int.TryParse((((int)ts.TotalMinutes)).ToString(), out minutes);
    }

    private async Task<List<MachineRow>> LoadMachines() =>
        await db.Machines.AsNoTracking()
            .OrderBy(m => m.Id)
            .Select(m => new MachineRow { Id = m.Id, DisplayName = m.DisplayName, ApiKey = m.ApiKey })
            .ToListAsync();

    public class MachineRow
    {
        public string Id { get; set; } = default!;
        public string DisplayName { get; set; } = default!;
        public string ApiKey { get; set; } = default!;
    }
}