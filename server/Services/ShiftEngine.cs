using CncDash.Server.Data.Entities;

namespace CncDash.Server.Services;

public record ShiftWindowUtc(int Number, DateTime StartUtc, DateTime EndUtc);

public static class ShiftEngine
{
    /// <summary>Resolve all shifts (numbered windows) that contain `nowUtc` — overlap windows
    /// produce multiple results (a part counts into every shift containing it).</summary>
    public static List<ShiftWindowUtc> ActiveShifts(MachineConfig cfg, DateTime nowUtc)
    {
        var tz = GetTz(cfg.TimeZone);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var today = localNow.Date;

        var result = new List<ShiftWindowUtc>();
        var nowLocalMin = localNow.TimeOfDay.TotalMinutes;

        // Check today's windows plus yesterday's (for shifts wrapping past midnight).
        foreach (var dayOffset in new[] { -1, 0 })
        {
            foreach (var (number, start, end) in new[]
            {
                (1, cfg.Shift1Start, cfg.Shift1End),
                (2, cfg.Shift2Start, cfg.Shift2End),
                (3, cfg.Shift3Start, cfg.Shift3End),
            })
            {
                if (start is null || end is null) continue;
                var s = start.Value; var e = end.Value;
                var wraps = e <= s;

                // local window boundary minutes relative to `today`
                var startLocal = today.AddDays(dayOffset).AddMinutes(s);
                var endLocal = wraps
                    ? today.AddDays(dayOffset + 1).AddMinutes(e)
                    : today.AddDays(dayOffset).AddMinutes(e);

                if (localNow >= startLocal && localNow < endLocal)
                {
                    result.Add(new ShiftWindowUtc(
                        number,
                        TimeZoneInfo.ConvertTimeToUtc(startLocal, tz),
                        TimeZoneInfo.ConvertTimeToUtc(endLocal, tz)));
                }
            }
        }

        return result.OrderBy(r => r.Number).ToList();
    }

    /// <summary>Named shift windows that cover the half-open local day [day.date, day.date + 1).
    /// Used by history/report functions; overlap edges count into both shifts.</summary>
    public static ShiftWindowUtc? ShiftOnDate(MachineConfig cfg, int shiftNumber, DateTime localDate)
    {
        var tz = GetTz(cfg.TimeZone);
        var (start, end) = shiftNumber switch
        {
            1 => (cfg.Shift1Start, cfg.Shift1End),
            2 => (cfg.Shift2Start, cfg.Shift2End),
            3 => (cfg.Shift3Start, cfg.Shift3End),
            _ => (null, null),
        };
        if (start is null || end is null) return null;
        var s = start.Value; var e = end.Value;
        var startLocal = localDate.Date.AddMinutes(s);
        var endLocal = e <= s ? localDate.Date.AddDays(1).AddMinutes(e) : localDate.Date.AddMinutes(e);
        return new ShiftWindowUtc(
            shiftNumber,
            TimeZoneInfo.ConvertTimeToUtc(startLocal, tz),
            TimeZoneInfo.ConvertTimeToUtc(endLocal, tz));
    }

    public static TimeZoneInfo GetTz(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { }
        try { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); }
        catch { }
        return TimeZoneInfo.Utc;
    }
}