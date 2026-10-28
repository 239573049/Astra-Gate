using System.Text.RegularExpressions;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Settings;

namespace Astra.Desktop.Core.Accounts;

/// <summary>Where an account stands for the gateway right now (web <c>AccountState</c>): drives the card's accent and the status filter.</summary>
public enum AccountState
{
    /// <summary>Enabled, signed in and not rate-limited.</summary>
    Usable,

    /// <summary>Rate-limited until <c>cooldownUntilUtc</c> (automatic failover skips it meanwhile).</summary>
    Cooling,

    /// <summary>The login lapsed (expired / revoked).</summary>
    Dead,

    /// <summary>Switched off by the user.</summary>
    Disabled,
}

/// <summary>A semantic text color the views map to a token (web <c>var(--warning)</c> / <c>var(--danger)</c> / muted).</summary>
public enum TextTone
{
    /// <summary>Muted text.</summary>
    Muted,

    /// <summary>The warning color.</summary>
    Warning,

    /// <summary>The danger color.</summary>
    Danger,

    /// <summary>The success color.</summary>
    Success,

    /// <summary>The accent color.</summary>
    Accent,
}

/// <summary>
/// The small rules the account views share (web SubscriptionAccountCard.tsx / SubscriptionAccountDetail.tsx / SubscriptionAccounts.tsx):
/// an account's state, its display name, its status text, the plan gradient of a card, and the request-error text.
/// </summary>
public static partial class AccountRules
{
    /// <summary>Web <c>accountState</c>.</summary>
    public static AccountState StateOf(ProviderAccountDto a, DateTimeOffset now)
    {
        if (!a.Enabled) return AccountState.Disabled;
        if (a.Status != "active") return AccountState.Dead;
        if (a.CooldownUntilUtc is { } until && until > now) return AccountState.Cooling;
        return AccountState.Usable;
    }

    /// <summary>The name a card and a toast show: display name, else e-mail, else the masked key, else the id (web <c>nameOf</c>).</summary>
    public static string NameOf(ProviderAccountDto a) => FirstText(a.DisplayName, a.AccountEmail, a.KeyMasked) ?? a.Id;

    /// <summary>The detail sheet's title: display name, else the masked key, else the id.</summary>
    public static string TitleOf(ProviderAccountDto a) => FirstText(a.DisplayName, a.KeyMasked) ?? a.Id;

    /// <summary>An API-key account (a static key, never refreshed).</summary>
    public static bool IsKey(ProviderAccountDto a) => a.Kind == "api_key";

    /// <summary>The catalog key of a login status (web <c>STATUS_KEY</c>).</summary>
    public static string StatusKey(string status) => status switch
    {
        "expired" => "providers.subscription.status.expired",
        "revoked" => "providers.subscription.status.revoked",
        _ => "providers.subscription.status.active",
    };

    /// <summary>The status text of a card / the detail's badge: "disabled" for a switched-off account, else its login status.</summary>
    public static string StatusText(ProviderAccountDto a, AccountState state)
        => state == AccountState.Disabled ? Loc.T("providers.subscription.disabled") : Loc.T(StatusKey(a.Status));

    /// <summary>"OAuth login" or "API key".</summary>
    public static string KindText(ProviderAccountDto a) => Loc.T(IsKey(a) ? "providers.accounts.kind.api_key" : "providers.accounts.kind.oauth");

    /// <summary>The text of how the account was added (<c>providers.accounts.source.*</c>); null when unknown.</summary>
    public static string? SourceText(ProviderAccountDto a) => string.IsNullOrEmpty(a.Source) ? null : Loc.T("providers.accounts.source." + a.Source);

    /// <summary>The tone of the status dot (web <c>STATE_DOT</c>): success / warning / danger / muted.</summary>
    public static TextTone DotTone(AccountState state) => state switch
    {
        AccountState.Usable => TextTone.Success,
        AccountState.Cooling => TextTone.Warning,
        AccountState.Dead => TextTone.Danger,
        _ => TextTone.Muted,
    };

    /// <summary>The detail sheet's status badge tone: green / orange / red / neutral (as text, the badge vocabulary).</summary>
    public static string BadgeTone(AccountState state) => state switch
    {
        AccountState.Usable => "green",
        AccountState.Cooling => "orange",
        AccountState.Dead => "red",
        _ => "neutral",
    };

    /// <summary>
    /// The RGB of the card's top band, by plan family (web <c>planGradient</c>): max / ultra blue, pro / plus / premium green,
    /// team / business / enterprise / allegro / vivace purple, an API key or anything else grey. The band runs from 34 % to 8 % alpha.
    /// </summary>
    public static (byte R, byte G, byte B) PlanRgb(string? plan, bool isKey)
    {
        var p = (plan ?? "").ToLowerInvariant();
        if (isKey) return (110, 115, 135);
        if (MaxRe().IsMatch(p)) return (47, 124, 246);
        if (ProRe().IsMatch(p)) return (52, 195, 143);
        if (TeamRe().IsMatch(p)) return (130, 100, 230);
        return (110, 115, 135);
    }

    [GeneratedRegex("max|ultra")]
    private static partial Regex MaxRe();

    [GeneratedRegex("pro|plus|premium")]
    private static partial Regex ProRe();

    [GeneratedRegex("team|business|enterprise|allegro|vivace")]
    private static partial Regex TeamRe();

    /// <summary>The text of a failure (web <c>errorText</c>): the API's own message.</summary>
    public static string ErrorText(Exception? e) => e switch
    {
        null => "",
        ApiException { Message.Length: > 0 } api => api.Message,
        _ => e.Message,
    };

    /// <summary>
    /// The zone account dates show in when a caller passes none (null = the machine's local zone, as the web's browser zone). The visual
    /// scenes set UTC: the web capture runs with <c>timezoneId: UTC</c>, and the machine's zone would shift every date.
    /// </summary>
    public static TimeZoneInfo? DisplayZone { get; set; }

    /// <summary>web <c>formatDateTime</c> in the current UI language.</summary>
    public static string When(DateTimeOffset? at, TimeZoneInfo? zone = null) => SettingsFormat.DateTime(at, Loc.Language, zone ?? DisplayZone);

    /// <summary>Nano-USD as web <c>formatUsd(usdFromNanos(n), {compact})</c>: compact above $1,000 ("$1.2K").</summary>
    public static string Nanos(long nanos, bool compact)
    {
        var usd = nanos / (decimal)StatsFormat.NanosPerUsd;
        if (compact && Math.Abs(usd) >= 1000) return "$" + StatsFormat.Compact(usd);
        return StatsFormat.Usd(usd);
    }

    /// <summary>web <c>formatTokens(n, compact)</c> for a count.</summary>
    public static string Count(long n, bool compact) => StatsFormat.Tokens(n, compact);

    /// <summary>JavaScript's <c>Math.round</c> (halves go up), so percentages match the web's.</summary>
    public static int JsRound(double v) => (int)Math.Floor(v + 0.5);

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrEmpty(v));
}

/// <summary>
/// What the client-policy group of a subscription shows (web SubscriptionPolicy.tsx, plan §5.4): the Claude subscription's two switches —
/// "Claude Code only" (default on) and "other clients present as Claude Code" (default off, a ban risk). The switching mode lives in the
/// accounts tab's pool line, so a provider with nothing left to set shows no group.
/// </summary>
public static class SubscriptionPolicyRules
{
    /// <summary>The "Claude Code only" row: on a Claude subscription, or wherever the policy was set to it explicitly.</summary>
    public static bool ShowClientPolicy(SubscriptionPolicyDto p) => p.ClaudeSubscription || p.ClientPolicy == "claude-code-only";

    /// <summary>The group shows at all.</summary>
    public static bool Visible(SubscriptionPolicyDto p) => ShowClientPolicy(p) || p.ClaudeSubscription;

    /// <summary>The warning under the rows (any client allowed): which one, or null for none.</summary>
    public static string? WarningKey(SubscriptionPolicyDto p) => ShowClientPolicy(p) && p.ClientPolicy == "any"
        ? p.MimicClaudeCode ? "providers.subscription.policy.mimicWarning" : "providers.subscription.policy.anyWarning"
        : null;
}
