using System.Globalization;
using Astra.Core;

namespace Astra.Server.Api;

/// <summary>When an account is warmed: the mode, the local times of day and the days of the week.</summary>
public sealed record WarmupPlan(string Mode, IReadOnlyList<TimeOnly> Times, IReadOnlyList<int> Weekdays);

/// <summary>
/// The warm-up's clock (magpie's daily warm-up, MIT: a Claude or ChatGPT window starts at the account's first request after it resets, so a tiny
/// request at the time of the user's choosing lines the day's windows up). Pure functions of the plan, the last run and the time, so the
/// schedule can be tested with a fake clock and a restart changes nothing: what is due is worked out from what was last done.
/// </summary>
public static class WarmupSchedule
{
    /// <summary>How late a slot may be caught up: after a restart or a sleeping machine a slot is still served within this, never after.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    /// <summary>The length of the usage window a warm-up starts.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(5);

    /// <summary>Two warm-ups of one account are never closer than this (a skipped or failed one is not retried every minute).</summary>
    public static readonly TimeSpan MinSpacing = TimeSpan.FromMinutes(30);

    public static bool TryParseTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static WarmupPlan PlanOf(string mode, IEnumerable<string> times, IEnumerable<int> weekdays) =>
        new(mode, times.Select(t => TryParseTime(t, out var parsed) ? (TimeOnly?)parsed : null).OfType<TimeOnly>().Distinct().Order().ToList(),
            weekdays.Where(d => d is >= 0 and <= 6).Distinct().Order().ToList());

    /// <summary>The zone the times are in: the usage time zone setting, else the machine's.</summary>
    public static TimeZoneInfo ZoneOf(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.UsageTimeZone))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(settings.UsageTimeZone);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.Local;
    }

    private static IEnumerable<DateTimeOffset> Slots(WarmupPlan plan, DateTimeOffset around, TimeZoneInfo zone, int fromDay, int toDay)
    {
        var today = TimeZoneInfo.ConvertTime(around, zone).Date;
        for (var offset = fromDay; offset <= toDay; offset++)
        {
            var day = today.AddDays(offset);
            if (plan.Weekdays.Count > 0 && !plan.Weekdays.Contains((int)day.DayOfWeek)) continue;
            foreach (var time in plan.Times)
            {
                var local = DateTime.SpecifyKind(day + time.ToTimeSpan(), DateTimeKind.Unspecified);
                // a time the clock skips (the spring change) is the first one after the gap
                if (zone.IsInvalidTime(local)) local = local.AddHours(1);
                yield return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
            }
        }
    }

    /// <summary>The latest slot at or before <paramref name="now"/> (today or yesterday); null when the plan has none.</summary>
    public static DateTimeOffset? LatestSlot(WarmupPlan plan, DateTimeOffset now, TimeZoneInfo zone) =>
        Slots(plan, now, zone, -7, 0).Where(s => s <= now).Select(s => (DateTimeOffset?)s).Max();

    /// <summary>The first slot after <paramref name="now"/>; null when the plan has none within the next week and a day.</summary>
    public static DateTimeOffset? NextSlot(WarmupPlan plan, DateTimeOffset now, TimeZoneInfo zone) =>
        Slots(plan, now, zone, 0, 8).Where(s => s > now).Select(s => (DateTimeOffset?)s).Min();

    /// <summary>
    /// Whether a daily warm-up is due: a slot has come, it is not older than <see cref="Grace"/>, and nothing was warmed since it (a restart
    /// that finds the slot already served does not serve it twice, and one that finds it long past does not catch up).
    /// </summary>
    public static bool DailyDue(WarmupPlan plan, DateTimeOffset? last, DateTimeOffset now, TimeZoneInfo zone) =>
        plan.Mode == WarmupSettings.ModeDaily && LatestSlot(plan, now, zone) is { } slot && now - slot <= Grace && (last is null || last < slot);

    /// <summary>
    /// Whether an "auto" warm-up is due, right after the previous window reset: the account's quota snapshot says its 5-hour window reset
    /// (<paramref name="windowResetsAt"/> has passed) and nothing was warmed since; with no window in the snapshot, one whose last warm-up is
    /// older than a window and a few minutes.
    /// </summary>
    public static bool AutoDue(DateTimeOffset? windowResetsAt, DateTimeOffset? last, DateTimeOffset now)
    {
        if (last is { } l && now - l < MinSpacing) return false;
        if (windowResetsAt is { } reset)
            return reset <= now && (last is null || last < reset);
        return last is null || now - last >= Window + TimeSpan.FromMinutes(5);
    }
}
