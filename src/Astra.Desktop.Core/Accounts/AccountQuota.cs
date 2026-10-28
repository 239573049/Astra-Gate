using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;

namespace Astra.Desktop.Core.Accounts;

/// <summary>One rate-limit window of an account's quota (web <c>QuotaWindow</c>).</summary>
/// <param name="UsedPercent">Share used, 0..100 (may be missing for balance-only plans).</param>
/// <param name="WindowMinutes">The window's length.</param>
/// <param name="ResetsAt">When it resets.</param>
public sealed record QuotaWindow(double? UsedPercent, double? WindowMinutes, DateTimeOffset? ResetsAt);

/// <summary>The credits part of a quota (Grok / Copilot premium requests).</summary>
/// <param name="UsedPercent">Share used.</param>
/// <param name="ResetsAt">When it resets.</param>
/// <param name="MonthlyLimit">The monthly limit.</param>
/// <param name="PrepaidBalance">A prepaid balance.</param>
public sealed record QuotaCredits(double? UsedPercent, DateTimeOffset? ResetsAt, double? MonthlyLimit, double? PrepaidBalance);

/// <summary>
/// An account's quota snapshot (web <c>AccountQuota</c>). The DTO carries it as a free JSON node (<c>ProviderAccountDto.Quota</c>), so it is
/// read here leniently: a field of the wrong type is treated as missing, never as an error.
/// </summary>
public sealed record AccountQuota(
    DateTimeOffset? FetchedAt, QuotaWindow? Session, QuotaWindow? Weekly, QuotaCredits? Credits, string? PlanLabel,
    double? Entitlement, double? Remaining, double? OverageUsed, bool? OveragePermitted)
{
    /// <summary>Reads a quota node; null for none (or a node that is not an object).</summary>
    public static AccountQuota? Parse(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        return new AccountQuota(
            Date(o["fetchedAtUtc"]), Window(o["session"]), Window(o["weekly"]), CreditsOf(o["credits"]), Str(o["planLabel"]),
            Num(o["entitlement"]), Num(o["remaining"]), Num(o["overageUsed"]), Bool(o["overagePermitted"]));
    }

    /// <summary>Some window reports a used share (web <c>hasQuota</c> of the card).</summary>
    public bool HasUsage => Session?.UsedPercent is not null || Weekly?.UsedPercent is not null || Credits?.UsedPercent is not null;

    private static QuotaWindow? Window(JsonNode? node) => node is JsonObject o
        ? new QuotaWindow(Num(o["usedPercent"]), Num(o["windowMinutes"]), Date(o["resetsAtUtc"]))
        : null;

    private static QuotaCredits? CreditsOf(JsonNode? node) => node is JsonObject o
        ? new QuotaCredits(Num(o["usedPercent"]), Date(o["resetsAtUtc"]), Num(o["monthlyLimit"]), Num(o["prepaidBalance"]))
        : null;

    /// <summary>A number of a JSON node (a numeric string counts); null otherwise.</summary>
    public static double? Num(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        // ToJsonString, not GetValue<double>: a value built in code may hold an int, which GetValue<double> refuses.
        if (v.GetValueKind() == JsonValueKind.Number) return double.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);
        if (v.GetValueKind() == JsonValueKind.String && double.TryParse(v.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return null;
    }

    /// <summary>A string of a JSON node; null otherwise.</summary>
    public static string? Str(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>A boolean of a JSON node; null otherwise.</summary>
    public static bool? Bool(JsonNode? node) => node is JsonValue v ? v.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    } : null;

    /// <summary>An ISO date of a JSON node (web <c>new Date(iso)</c>; an unparsable one is missing).</summary>
    public static DateTimeOffset? Date(JsonNode? node)
        => Str(node) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

/// <summary>One bar of a quota view: a label, the rounded used share, its color and the reset line.</summary>
/// <param name="Key">session / weekly / credits.</param>
/// <param name="Label">The bar's label.</param>
/// <param name="Percent">Used share, rounded and clamped to 0..100.</param>
/// <param name="Tone">Accent below 70 %, warning from 70 %, danger from 90 %.</param>
/// <param name="Reset">"resets at …", or null.</param>
/// <param name="Hours">The window's length in hours (the detail card's " · 5h"), or null.</param>
public sealed record QuotaBar(string Key, string Label, int Percent, TextTone Tone, string? Reset, int? Hours = null)
{
    /// <summary>Segments of the card's 10-segment strip that are lit.</summary>
    public int Lit => AccountRules.JsRound(Percent / 100.0 * QuotaViews.Segments);
}

/// <summary>What the detail sheet's quota card shows (web SubscriptionQuotaCard.tsx).</summary>
/// <param name="Caption">The plan label, else "fetched at …".</param>
/// <param name="Balance">"balance …" of a prepaid plan.</param>
/// <param name="Bars">One bar per window with a used share; empty shows <c>quotaNoWindows</c>.</param>
/// <param name="Counts">Copilot's "used / total · overage" line.</param>
public sealed record QuotaCardModel(string? Caption, string? Balance, IReadOnlyList<QuotaBar> Bars, string? Counts);

/// <summary>The two quota views of an account: the card's segmented strip and the detail sheet's full card.</summary>
public static class QuotaViews
{
    /// <summary>Segments of the card's strip (web <c>SEGMENTS</c>).</summary>
    public const int Segments = 10;

    /// <summary>Web <c>tone</c> of a quota bar.</summary>
    public static TextTone ToneOf(int percent) => percent >= 90 ? TextTone.Danger : percent >= 70 ? TextTone.Warning : TextTone.Accent;

    private static int Percent(double used) => Math.Clamp(AccountRules.JsRound(used), 0, 100);

    /// <summary>
    /// The card's strip (web <c>QuotaStrip</c>): session (labelled by its window in hours) and weekly, else credits, at most two bars.
    /// Empty when nothing reports a share.
    /// </summary>
    public static IReadOnlyList<QuotaBar> Strip(AccountQuota? quota, TimeZoneInfo? zone = null)
    {
        var bars = new List<QuotaBar>();
        string? Reset(DateTimeOffset? at) => at is null ? null : Loc.T("providers.subscription.quotaResets", ("when", AccountRules.When(at, zone)));
        if (quota?.Session is { UsedPercent: { } s } session)
        {
            var label = session.WindowMinutes is { } m && m != 0
                ? $"{AccountRules.JsRound(m / 60)}h"
                : Loc.T("providers.accounts.quotaShortSession");
            bars.Add(new QuotaBar("session", label, Percent(s), ToneOf(Percent(s)), Reset(session.ResetsAt)));
        }
        if (quota?.Weekly is { UsedPercent: { } w } weekly)
            bars.Add(new QuotaBar("weekly", Loc.T("providers.accounts.quotaShortWeekly"), Percent(w), ToneOf(Percent(w)), Reset(weekly.ResetsAt)));
        if (bars.Count < 2 && quota?.Credits is { UsedPercent: { } c } credits)
        {
            var label = Loc.T(quota.Entitlement is not null ? "providers.accounts.quotaShortPremium" : "providers.accounts.quotaShortCredits");
            bars.Add(new QuotaBar("credits", label, Percent(c), ToneOf(Percent(c)), Reset(credits.ResetsAt)));
        }
        return bars.Take(2).ToList();
    }

    /// <summary>The detail sheet's card (web <c>SubscriptionQuotaCard</c>); dates read like the web's <c>toLocaleString(locale)</c>.</summary>
    public static QuotaCardModel Card(AccountQuota quota, TimeZoneInfo? zone = null)
    {
        string? Reset(DateTimeOffset? at) => at is { } v ? Loc.T("providers.subscription.quotaResets", ("when", SettingsFormat.Full(v, Loc.Language, zone))) : null;
        int? Hours(double? minutes) => minutes is { } m && m != 0 ? AccountRules.JsRound(m / 60) : null;

        var bars = new List<QuotaBar>();
        if (quota.Session is { UsedPercent: { } s } session)
            bars.Add(new QuotaBar("session", Loc.T("providers.subscription.quotaWindowSession"), Percent(s), ToneOf(Percent(s)), Reset(session.ResetsAt), Hours(session.WindowMinutes)));
        if (quota.Weekly is { UsedPercent: { } w } weekly)
            bars.Add(new QuotaBar("weekly", Loc.T("providers.subscription.quotaWindowWeekly"), Percent(w), ToneOf(Percent(w)), Reset(weekly.ResetsAt), Hours(weekly.WindowMinutes)));
        if (quota.Credits is { UsedPercent: { } c } credits)
        {
            // Copilot's credits are its "premium requests" allowance: a more exact label.
            var label = Loc.T(quota.Entitlement is not null ? "providers.subscription.quotaWindowPremium" : "providers.subscription.quotaWindowCredits");
            bars.Add(new QuotaBar("credits", label, Percent(c), ToneOf(Percent(c)), Reset(credits.ResetsAt)));
        }

        var balance = quota.Credits?.PrepaidBalance is { } b ? Loc.T("providers.subscription.quotaBalance", ("balance", JsNumber(b))) : null;

        string? counts = null;
        if (quota.Entitlement is { } total && total >= 0)
        {
            var parts = new List<string>
            {
                Loc.T("providers.subscription.quotaPremiumCount", ("used", JsNumber(Math.Max(0, total - (quota.Remaining ?? 0)))), ("total", JsNumber(total))),
            };
            if (quota.OverageUsed is { } over && over != 0) parts.Add(Loc.T("providers.subscription.quotaOverage", ("count", JsNumber(over))));
            if (quota.OveragePermitted == false) parts.Add(Loc.T("providers.subscription.quotaOverageOff"));
            counts = string.Join(" · ", parts);
        }

        var caption = quota.PlanLabel
            ?? (quota.FetchedAt is { } at ? Loc.T("providers.subscription.quotaFetchedAt", ("when", SettingsFormat.Full(at, Loc.Language, zone))) : null);
        return new QuotaCardModel(caption, balance, bars, counts);
    }

    /// <summary>A number the way JavaScript prints it in a template (<c>12.5</c>, <c>3</c>).</summary>
    public static string JsNumber(double v) => v.ToString("R", CultureInfo.InvariantCulture);
}
