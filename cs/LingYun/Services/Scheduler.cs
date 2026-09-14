using LingYun.Config;

namespace LingYun.Services;

public static class Scheduler
{
    public static bool IsActiveDate(AppConfig cfg, DateOnly date)
    {
        if (!cfg.Enabled || !cfg.HasRules) return false;
        // ISO weekday: Monday=1 .. Sunday=7
        int iso = ((int)date.DayOfWeek + 6) % 7 + 1;
        if (cfg.Weekdays.Contains(iso)) return true;
        return cfg.Dates.Contains(date.ToString("yyyy-MM-dd"));
    }

    public static DateTime? NextTarget(AppConfig cfg, DateTime now)
    {
        if (!cfg.Enabled || !cfg.HasRules) return null;
        for (int offset = 0; offset < 366; offset++)
        {
            var day = DateOnly.FromDateTime(now.Date.AddDays(offset));
            if (!IsActiveDate(cfg, day)) continue;
            var candidate = day.ToDateTime(new TimeOnly(cfg.Hour, cfg.Minute));
            if (candidate > now) return candidate;
        }
        return null;
    }
}
