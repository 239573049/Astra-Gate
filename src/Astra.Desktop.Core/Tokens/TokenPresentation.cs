using Astra.Api.Contracts;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Traces;

namespace Astra.Desktop.Core.Tokens;

/// <summary>Color of a limit bar: the closer to the cap, the hotter.</summary>
public enum LimitTone
{
    /// <summary>Below 70%.</summary>
    Accent,

    /// <summary>70% and up.</summary>
    Warning,

    /// <summary>90% and up.</summary>
    Danger,
}

/// <summary>One budget's bar of a token row (web <c>LimitBar</c>).</summary>
public sealed record LimitBarModel(string Label, string UsedText, int Percent, LimitTone Tone, string? ResetText);

/// <summary>One cell group of the today / total table (web <c>StatsTable</c>).</summary>
public sealed record StatsTableRow(string Label, string Cost, string Requests, string Tokens, string InOut, string CacheHit, string CacheHitTip, string Tps);

/// <summary>A pool member of the detail page (web <c>TokenDetailPage</c> pool section).</summary>
public sealed record PoolMemberView(string ProviderId, string Name, HealthTone Tone, string HealthText);

/// <summary>One row of the provider breakdown (web <c>ProviderRow</c>).</summary>
public sealed record ProviderRow(string Id, string Name, long Requests, double Share, double Success, double? Ttft, decimal Cost, long Out, long In);

/// <summary>One row of the model breakdown (web <c>ModelRow</c>).</summary>
public sealed record ModelRow(string Model, long Requests, long Tokens, decimal Cost);

/// <summary>One row of the recent-requests table of a token (web <c>LogRow</c> of the detail page).</summary>
public sealed record TokenLogRow(string Id, DateTimeOffset Time, string Model, string Provider, string Status, decimal Cost, RequestSummaryDto Request);

/// <summary>
/// Text and numbers behind the tokens page and a token's detail page, ported from <c>TokensPage.tsx</c> and
/// <c>TokenDetailPage.tsx</c>: the views only draw what these return.
/// </summary>
public static class TokenPresentation
{
    /// <summary>The bar of one budget: label "period · metric", used / limit, a clamped percent and its tone (<c>LimitBar</c>).</summary>
    public static LimitBarModel LimitBar(TokenBudgetUsageDto u, TimeSpan zone, string language, Translate t)
    {
        var label = t(BudgetRules.PeriodKey(u.Period)) + " · " + t(BudgetRules.MetricKey(u.Metric));
        string Amount(decimal v) => u.Metric == "cost" ? StatsFormat.Usd(v) : StatsFormat.Tokens(v, true);
        var percent = (int)Math.Max(0, Math.Min(100, TraceFormat.JsRound(u.UsedPercent)));
        var tone = percent >= 90 ? LimitTone.Danger : percent >= 70 ? LimitTone.Warning : LimitTone.Accent;
        var reset = u.ResetsAt is { } at ? t("tokens.limitResets", ("time", StatsFormat.DateTime(at, zone, language))) : null;
        return new LimitBarModel(label, t("tokens.limitUsed", ("used", Amount(u.Used)), ("limit", Amount(u.Limit))), percent, tone, reset);
    }

    /// <summary>"RPM · 3 / 60" line of a token with a request-rate cap (null without one).</summary>
    public static string? RpmText(TokenDto token, Translate t)
        => token.Limits?.Rpm is { } rpm ? t("tokens.rpm") + " · " + t("tokens.rpmUsage", ("used", token.RecentRequests), ("limit", rpm)) : null;

    /// <summary>Whether the token has no caps at all ("unlimited" instead of bars).</summary>
    public static bool IsUnlimited(TokenDto token) => token.LimitUsage.Count == 0 && token.Limits?.Rpm is null;

    /// <summary>The collapsed row's pool badge text: "2 providers · failover" (<c>tokens.pool.summary</c>).</summary>
    public static string PoolBadge(TokenPoolDto pool, Translate t)
        => t("tokens.pool.summary", ("count", pool.Members.Count), ("strategy", t(PoolStrategies.NameKey(pool.Strategy))));

    /// <summary>The names of a token's clients joined for a tooltip, or "no clients" (<c>tokens.noClients</c>).</summary>
    public static string ClientsTitle(IEnumerable<string> kinds, Translate t)
    {
        var names = kinds.Select(k => ClientCatalog.Name(k)).ToList();
        return names.Count == 0 ? t("tokens.noClients") : string.Join(", ", names);
    }

    /// <summary>The "allowed clients" line of the expanded row: any, direct only, or the names.</summary>
    public static string AllowedClientsText(TokenDto token, Translate t)
        => token.AllowedClients is null ? t("tokens.allowedClientsAny")
            : token.AllowedClients.Count == 0 ? t("tokens.directOnly")
            : string.Join(", ", token.AllowedClients.Select(k => ClientCatalog.Name(k)));

    /// <summary>The direct provider line of the expanded row: its name, else its id, else "none".</summary>
    public static string DirectProviderText(TokenDto token, IEnumerable<ProviderDto> providers, Translate t)
        => providers.FirstOrDefault(p => p.Id == token.ProviderId)?.Name
            ?? (string.IsNullOrEmpty(token.ProviderId) ? t("tokens.directNone") : token.ProviderId);

    /// <summary>Today and lifetime usage side by side, one row per period (web <c>StatsTable</c>).</summary>
    public static List<StatsTableRow> StatsTable(TokenDto token, Translate t)
        => [Row(t("tokens.today"), token.Today, t), Row(t("tokens.total"), token.Total, t)];

    private static StatsTableRow Row(string label, TokenStatsDto s, Translate t) => new(
        label, StatsFormat.Usd(s.CostUsd), StatsFormat.Tokens(s.Requests), StatsFormat.Tokens(s.TotalTokens, true),
        StatsFormat.Tokens(s.InputTokens, true) + " / " + StatsFormat.Tokens(s.OutputTokens, true),
        s.CacheHitRate is null ? StatsFormat.Dash : StatsFormat.Percent(s.CacheHitRate),
        t("usage.cacheAmounts", ("read", StatsFormat.Tokens(s.CacheReadTokens, true)), ("write", StatsFormat.Tokens(s.CacheWriteTokens, true))),
        s.Tps is null ? StatsFormat.Dash : StatsFormat.Tps(s.Tps));

    /// <summary>The pool of a token with each member's name and live health (the detail page's pool section).</summary>
    public static List<PoolMemberView> PoolMembers(TokenPoolDto pool, IEnumerable<ProviderDto> providers, DateTimeOffset now, TimeSpan zone, string language, Translate t)
    {
        var names = providers.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        return pool.Members.Select(m => new PoolMemberView(
            m.ProviderId, names.GetValueOrDefault(m.ProviderId, m.ProviderId), PoolDrafts.ToneOf(m.Health.State),
            PoolDrafts.HealthLabel(m.Health.State, m.Health.Until, now, zone, language, t))).ToList();
    }

    /// <summary>The by-provider rows: name from the provider list, else the logged one, else the id; share of the token's requests.</summary>
    public static List<ProviderRow> ProviderRows(IReadOnlyList<ProviderStatDto> stats, IEnumerable<ProviderDto> providers)
    {
        var names = providers.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        var total = stats.Sum(p => p.Requests);
        return stats.Select(p => new ProviderRow(
            p.ProviderId, names.GetValueOrDefault(p.ProviderId) ?? p.Name ?? p.ProviderId, p.Requests,
            total > 0 ? (double)p.Requests / total : 0, p.SuccessRate, p.AvgTtftMs, p.CostUsd, p.FailoversOut, p.FailoversIn)).ToList();
    }

    /// <summary>The by-model rows.</summary>
    public static List<ModelRow> ModelRows(IEnumerable<TopModelDto> models)
        => models.Select(m => new ModelRow(m.Model, m.Requests, m.Tokens, m.CostUsd)).ToList();

    /// <summary>The ten latest requests of a token.</summary>
    public static List<TokenLogRow> LogRows(IEnumerable<RequestSummaryDto> items)
        => items.Select(r => new TokenLogRow(
            r.Id, r.StartedAtUtc, r.RequestedModel ?? StatsFormat.Dash, r.ProviderName ?? r.ProviderId ?? StatsFormat.Dash, r.Status,
            r.CostNanoUsd / (decimal)StatsFormat.NanosPerUsd, r)).ToList();

    /// <summary>The unique "metric:period" key of a usage row.</summary>
    public static string LimitKey(TokenBudgetUsageDto u) => u.Metric + ":" + u.Period;

    /// <summary>The URL the token settings hint shows and the copy button copies: the OpenAI-compatible base.</summary>
    public static string V1Url(string gatewayBaseUrl) => gatewayBaseUrl + "/v1";

    /// <summary>The employee-side join command (<c>npx @aidotnet/astra-connect join &lt;gateway&gt;</c>): it carries the gateway address, never the key.</summary>
    public static string JoinCommand(string gatewayBaseUrl) => "npx @aidotnet/astra-connect join " + gatewayBaseUrl;
}
