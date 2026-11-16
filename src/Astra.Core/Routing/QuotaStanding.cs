using System.Text.Json.Nodes;
using Astra.Core.Models;

namespace Astra.Core.Routing;

/// <summary>One usage window of an account or a provider's plan: how much of it is used and when it renews.</summary>
public sealed record QuotaWindow(string Kind, double UsedPercent, DateTimeOffset? ResetsAt)
{
    public const string Session = "session";
    public const string Weekly = "weekly";
    public const string Credits = "credits";
    public const string Plan = "plan";
}

/// <summary>
/// What is known about how much an account (or a provider's plan) has left, read from the snapshots Astra already
/// collects (<c>ProviderAccount.Extra["quota"]</c>, <c>Provider.Quota.plans</c>). Missing or stale data is an
/// <em>unknown</em> standing — routing treats it as "no information", never as an error. The ordering rules in
/// <see cref="QuotaRanking"/> and the thresholds follow magpie's account picker (MIT).
/// </summary>
public sealed class QuotaStanding(IReadOnlyList<QuotaWindow> windows, DateTimeOffset? fetchedAt)
{
    /// <summary>A window this full puts the account behind the others for new conversations.</summary>
    public const double SoftLimit = 90;

    /// <summary>A window this full takes the account out of the running (a conversation already on it stays no longer).</summary>
    public const double HardLimit = 98;

    /// <summary>A snapshot older than this says too little to route by.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    public static QuotaStanding Unknown { get; } = new([], null);

    public IReadOnlyList<QuotaWindow> Windows { get; } = windows;
    public DateTimeOffset? FetchedAt { get; } = fetchedAt;

    /// <summary>Whether any window has a figure to route by.</summary>
    public bool Known => Windows.Count > 0;

    /// <summary>The fullest window's used share (0 when unknown).</summary>
    public double Used => Windows.Count == 0 ? 0 : Windows.Max(w => w.UsedPercent);

    /// <summary>0 = under <see cref="SoftLimit"/>, 1 = from it, 2 = from <see cref="HardLimit"/>.</summary>
    public int Band => Used >= HardLimit ? 2 : Used >= SoftLimit ? 1 : 0;

    /// <summary>The window that decides when the allowance renews: the week, else the session, else anything with a reset.</summary>
    public QuotaWindow? Deciding =>
        Windows.FirstOrDefault(w => w.Kind == QuotaWindow.Weekly)
        ?? Windows.FirstOrDefault(w => w.Kind == QuotaWindow.Session)
        ?? Windows.FirstOrDefault(w => w.ResetsAt is not null)
        ?? Windows.FirstOrDefault();

    /// <summary>
    /// Share of the deciding window left per hour until it renews (at least one hour): 60% left with 20 hours to go is 3 per hour.
    /// Null when there is no window or no renewal time to pace against.
    /// </summary>
    public double? Pace(DateTimeOffset now)
    {
        if (Deciding is not { ResetsAt: { } reset } window) return null;
        var hours = Math.Max((reset - now).TotalHours, 1);
        return Math.Max(100 - window.UsedPercent, 0) / hours;
    }

    /// <summary>The standing of a subscription / key account from its stored quota snapshot.</summary>
    public static QuotaStanding Of(ProviderAccount account, DateTimeOffset now) => OfSnapshot(account.Extra["quota"] as JsonObject, now);

    /// <summary>The standing of an API-key provider from its balance / plan snapshot (<c>Provider.Quota</c>).</summary>
    public static QuotaStanding Of(Provider provider, DateTimeOffset now)
    {
        if (provider.Quota is not { } quota) return Unknown;
        if (quota["isValid"] is JsonValue ok && ok.TryGetValue<bool>(out var valid) && !valid) return Unknown;
        var fetched = Time(quota["fetchedAtUtc"]);
        if (fetched is null || now - fetched > StaleAfter) return Unknown;
        var windows = new List<QuotaWindow>();
        if (quota["plans"] is JsonArray plans)
            foreach (var plan in plans.OfType<JsonObject>())
                if (Number(plan["usedPercent"]) is { } used && Live(Time(plan["resetsAtUtc"]), now))
                    windows.Add(new QuotaWindow(QuotaWindow.Plan, used, Time(plan["resetsAtUtc"])));
        return windows.Count == 0 ? Unknown : new QuotaStanding(windows, fetched);
    }

    private static QuotaStanding OfSnapshot(JsonObject? quota, DateTimeOffset now)
    {
        if (quota is null) return Unknown;
        var fetched = Time(quota["fetchedAtUtc"]);
        if (fetched is null || now - fetched > StaleAfter) return Unknown;
        var windows = new List<QuotaWindow>();
        foreach (var kind in new[] { QuotaWindow.Session, QuotaWindow.Weekly, QuotaWindow.Credits })
        {
            if (quota[kind] is not JsonObject node || Number(node["usedPercent"]) is not { } used) continue;
            var reset = Time(node["resetsAtUtc"]);
            // A window that renewed since the snapshot was taken says nothing about now.
            if (!Live(reset, now)) continue;
            windows.Add(new QuotaWindow(kind, used, reset));
        }
        return windows.Count == 0 ? Unknown : new QuotaStanding(windows, fetched);
    }

    private static bool Live(DateTimeOffset? reset, DateTimeOffset now) => reset is null || reset > now;

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        double d;
        if (v.TryGetValue<double>(out var asDouble)) d = asDouble;
        else if (v.TryGetValue<int>(out var asInt)) d = asInt;
        else if (v.TryGetValue<long>(out var asLong)) d = asLong;
        else if (v.TryGetValue<decimal>(out var asDecimal)) d = (double)asDecimal;
        else return null;
        return double.IsFinite(d) ? Math.Clamp(d, 0, 100) : null;
    }

    // A stored snapshot is JSON text read back (a JsonElement), one just fetched may hold the DateTimeOffset itself or its ISO string.
    private static DateTimeOffset? Time(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<DateTimeOffset>(out var t)) return t;
        return v.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }
}
