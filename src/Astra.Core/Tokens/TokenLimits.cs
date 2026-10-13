using System.Globalization;

namespace Astra.Core.Tokens;

/// <summary>What a <see cref="TokenBudget"/> counts.</summary>
public static class TokenLimitMetrics
{
    /// <summary>Cost in nanodollars.</summary>
    public const string Cost = "cost";

    public const string Requests = "requests";

    /// <summary>Input + output tokens (input already includes cache reads).</summary>
    public const string Tokens = "tokens";

    public static readonly IReadOnlyList<string> All = [Cost, Requests, Tokens];
}

/// <summary>The period a <see cref="TokenBudget"/> resets on, in the usage time zone.</summary>
public static class TokenLimitPeriods
{
    public const string Day = "day";

    /// <summary>ISO week (starts on Monday).</summary>
    public const string Week = "week";

    /// <summary>Calendar month.</summary>
    public const string Month = "month";

    /// <summary>Never resets (lifetime).</summary>
    public const string Total = "total";

    public static readonly IReadOnlyList<string> All = [Day, Week, Month, Total];
}

/// <summary>One cap of a token: at most <see cref="Limit"/> of <see cref="Metric"/> per <see cref="Period"/>.</summary>
public sealed class TokenBudget
{
    public string Metric { get; set; } = TokenLimitMetrics.Cost;
    public string Period { get; set; } = TokenLimitPeriods.Month;

    /// <summary>Nanodollars for <see cref="TokenLimitMetrics.Cost"/>, a count otherwise. Always &gt; 0.</summary>
    public long Limit { get; set; }

    /// <summary>"metric:period" — unique within a token.</summary>
    public string Key => $"{Metric}:{Period}";
}

/// <summary>
/// The usage caps of a token (column <c>tokens.limits_json</c>, plan: alerts-webhooks-token-limits §1). Null / empty =
/// unlimited. Enforced softly: a request is refused once a budget is used up, so requests in flight may overshoot it.
/// </summary>
public sealed class TokenLimits
{
    /// <summary>
    /// <c>requests.error_type</c> of a request refused by a token limit: logged, but counted in no usage counter (a
    /// refused request must not fill the very budget that refused it).
    /// </summary>
    public const string RefusedErrorType = "token_limit";

    /// <summary>Whether a finished request adds to its token's usage counters.</summary>
    public static bool Counts(Requests.RequestRecord record) => record.ErrorType != RefusedErrorType;

    public List<TokenBudget> Budgets { get; set; } = [];

    /// <summary>Requests per rolling minute; null = no limit.</summary>
    public int? Rpm { get; set; }

    public bool IsEmpty => Budgets.Count == 0 && Rpm is null;

    public static TokenLimits? Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : Json.Deserialize<TokenLimits>(json) is { IsEmpty: false } limits ? limits : null;

    /// <summary>Problems with this value (empty = valid).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        foreach (var b in Budgets)
        {
            if (!TokenLimitMetrics.All.Contains(b.Metric)) errors.Add($"unknown metric {b.Metric}");
            if (!TokenLimitPeriods.All.Contains(b.Period)) errors.Add($"unknown period {b.Period}");
            if (b.Limit <= 0) errors.Add($"limit of {b.Key} must be greater than 0");
        }
        foreach (var dup in Budgets.GroupBy(b => b.Key).Where(g => g.Count() > 1)) errors.Add($"{dup.Key} is set more than once");
        if (Rpm is <= 0) errors.Add("rpm must be greater than 0");
        return errors;
    }
}

/// <summary>Period arithmetic for token usage: local days of the usage time zone.</summary>
public static class UsagePeriods
{
    /// <summary>The usage time zone: the IANA / Windows id, or the process's local zone when unset or unknown.</summary>
    public static TimeZoneInfo Zone(string? id) =>
        string.IsNullOrWhiteSpace(id) ? TimeZoneInfo.Local
        : Billing.TimeZoneHelper.TryFind(id, out var tz) ? tz
        : TimeZoneInfo.Local;

    public static DateOnly LocalDay(DateTimeOffset utc, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);

    /// <summary>The key of a <c>token_usage_daily</c> row: "yyyy-MM-dd".</summary>
    public static string DayKey(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string DayKey(DateTimeOffset utc, TimeZoneInfo zone) => DayKey(LocalDay(utc, zone));

    /// <summary>
    /// First local day of the period containing <paramref name="now"/> and the moment it ends (the next period's start,
    /// with the zone's offset at that moment). <see cref="TokenLimitPeriods.Total"/> has neither.
    /// </summary>
    public static (DateOnly? From, DateTimeOffset? ResetsAt) Window(string period, DateTimeOffset now, TimeZoneInfo zone)
    {
        var today = LocalDay(now, zone);
        (DateOnly from, DateOnly next) = period switch
        {
            TokenLimitPeriods.Day => (today, today.AddDays(1)),
            TokenLimitPeriods.Week => WeekOf(today),
            TokenLimitPeriods.Month => (new DateOnly(today.Year, today.Month, 1), new DateOnly(today.Year, today.Month, 1).AddMonths(1)),
            _ => (default, default),
        };
        if (from == default) return (null, null);
        return (from, StartOf(next, zone));
    }

    private static (DateOnly, DateOnly) WeekOf(DateOnly day)
    {
        var monday = day.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        return (monday, monday.AddDays(7));
    }

    /// <summary>Local midnight of <paramref name="day"/> as an absolute time (a skipped midnight moves to the first valid minute).</summary>
    public static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
