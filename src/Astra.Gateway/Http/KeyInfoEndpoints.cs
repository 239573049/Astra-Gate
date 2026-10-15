using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using Astra.Core;
using Astra.Core.Requests;
using Astra.Core.Tokens;
using Astra.Data;
using Astra.Data.Repositories;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Astra.Gateway.Http;

/// <summary>
/// Self-service views of a gateway token, authenticated by the token itself (plan: alerts-webhooks-token-limits §2):
/// whoever holds a key — possibly someone it was shared with, over the network — sees that key's limits and usage
/// and nothing else: no provider, account, upstream model or upstream quota. <c>/v1/astra/key</c> is Astra's own shape;
/// <c>/v1/astra/key/requests</c> and <c>/v1/astra/key/stats</c> are the key's own request log and statistics — metadata
/// and the models its clients asked for only (.agents/memory/key-self-service-requested-model.md);
/// <c>/v1/dashboard/billing/*</c> is the legacy OpenAI balance API many clients poll to show a balance. A missing or
/// unknown key is always 401 here — <c>RequireGatewayKey=false</c> never lends these views the default token.
/// </summary>
public static class KeyInfoEndpoints
{
    /// <summary>Rate-limit policy (per client IP) of these endpoints; registered by the host.</summary>
    public const string RateLimitPolicy = "astra-key-info";

    public const int RequestsPerMinute = 30;

    /// <summary>Rate-limit policy of the request log and statistics, apart so browsing them never starves balance polls.</summary>
    public const string LogsRateLimitPolicy = "astra-key-logs";

    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    /// <summary>Days of the daily series in <c>/v1/astra/key</c>.</summary>
    public const int DailyDays = 30;

    /// <summary>The "no limit" hard_limit_usd of the OpenAI billing shape (one-api's convention).</summary>
    private const decimal UnlimitedUsd = 100_000_000m;

    public static void AddKeyInfoRateLimit(RateLimiterOptions options)
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        foreach (var policy in new[] { RateLimitPolicy, LogsRateLimitPolicy })
            options.AddPolicy(policy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = RequestsPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    }

    public static IEndpointRouteBuilder MapKeyInfoEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var prefix in new[] { "/v1", "" })
        {
            app.MapGet($"{prefix}/astra/key", KeyAsync).RequireRateLimiting(RateLimitPolicy).ExcludeFromDescription();
            app.MapGet($"{prefix}/dashboard/billing/subscription", SubscriptionAsync).RequireRateLimiting(RateLimitPolicy).ExcludeFromDescription();
            app.MapGet($"{prefix}/dashboard/billing/usage", UsageAsync).RequireRateLimiting(RateLimitPolicy).ExcludeFromDescription();
            app.MapGet($"{prefix}/astra/key/requests", RequestsAsync).RequireRateLimiting(LogsRateLimitPolicy).ExcludeFromDescription();
            app.MapGet($"{prefix}/astra/key/stats", StatsAsync).RequireRateLimiting(LogsRateLimitPolicy).ExcludeFromDescription();
        }
        return app;
    }

    private static async Task<IResult> KeyAsync(HttpContext ctx, AstraDatabase db, TokenUsageLedger ledger, CancellationToken ct)
    {
        if (await AuthenticateAsync(ctx, db, ct) is not { } token) return Unauthorized();
        var limits = token.Limits();
        var statuses = limits is null ? [] : await ledger.StatusAsync(token.Id, limits, ct);
        var daily = await ledger.DailyAsync(token.Id, DailyDays, ct);
        var allowed = token.AllowedClients();
        var body = new JsonObject
        {
            ["name"] = token.Name,
            ["keyPrefix"] = GatewayTokens.Mask(token.KeyPrefix),
            ["enabled"] = token.Enabled,
            ["allowedClients"] = allowed is null ? null : new JsonArray(allowed.Select(k => (JsonNode)k).ToArray()),
            ["directCall"] = token.HasDirectRoute(),
            ["timeZone"] = ledger.Zone.Id,
            ["limits"] = new JsonArray(statuses.Select(s => (JsonNode)BudgetNode(s)).ToArray()),
            ["rpm"] = limits?.Rpm is { } rpm ? new JsonObject { ["limit"] = rpm, ["used"] = ledger.RecentRequests(token.Id) } : null,
            ["usage"] = new JsonObject
            {
                ["today"] = TallyNode(await ledger.PeriodAsync(token.Id, TokenLimitPeriods.Day, ct)),
                ["week"] = TallyNode(await ledger.PeriodAsync(token.Id, TokenLimitPeriods.Week, ct)),
                ["month"] = TallyNode(await ledger.PeriodAsync(token.Id, TokenLimitPeriods.Month, ct)),
                ["total"] = TallyNode(await ledger.TotalAsync(token.Id, ct)),
            },
            ["daily"] = new JsonArray(daily.Select(d =>
            {
                var node = TallyNode(d.Usage);
                node.Insert(0, "day", UsagePeriods.DayKey(d.Day));
                return (JsonNode)node;
            }).ToArray()),
        };
        return Json(body);
    }

    private static async Task<IResult> SubscriptionAsync(HttpContext ctx, AstraDatabase db, TokenUsageLedger ledger, CancellationToken ct)
    {
        if (await AuthenticateAsync(ctx, db, ct) is not { } token) return Unauthorized();
        var (limit, _) = await BillingAsync(token, ledger, ct);
        return Json(new JsonObject
        {
            ["object"] = "billing_subscription",
            ["has_payment_method"] = true,
            ["soft_limit_usd"] = limit,
            ["hard_limit_usd"] = limit,
            ["system_hard_limit_usd"] = limit,
            ["access_until"] = 0,
        });
    }

    /// <summary>
    /// <c>total_usage</c> (US cents) is the usage of the same budget <c>/subscription</c> reports — start_date and
    /// end_date are ignored — so a client's "hard_limit_usd − total_usage" is the real remaining balance.
    /// </summary>
    private static async Task<IResult> UsageAsync(HttpContext ctx, AstraDatabase db, TokenUsageLedger ledger, CancellationToken ct)
    {
        if (await AuthenticateAsync(ctx, db, ct) is not { } token) return Unauthorized();
        var (_, used) = await BillingAsync(token, ledger, ct);
        return Json(new JsonObject { ["object"] = "list", ["total_usage"] = decimal.Round(used * 100, 4) });
    }

    /// <summary>
    /// The cost budget closest to running out (limit and usage in USD); without one, "unlimited" and this month's cost.
    /// </summary>
    private static async Task<(decimal LimitUsd, decimal UsedUsd)> BillingAsync(TokenRecord token, TokenUsageLedger ledger, CancellationToken ct)
    {
        var limits = token.Limits();
        var costBudgets = limits is null
            ? []
            : (await ledger.StatusAsync(token.Id, limits, ct)).Where(s => s.Budget.Metric == TokenLimitMetrics.Cost).ToList();
        if (costBudgets.MinBy(s => s.Remaining) is { } binding)
            return (Money.FromNanos(binding.Budget.Limit), Money.FromNanos(binding.Used));
        return (UnlimitedUsd, Money.FromNanos((await ledger.PeriodAsync(token.Id, TokenLimitPeriods.Month, ct)).CostNanoUsd));
    }

    /// <summary>
    /// The holder's request log: newest first, <c>limit</c> (≤ <see cref="MaxPageSize"/>) at a time, the next page asked
    /// for with <c>before</c> = the last id; optional <c>status</c> (comma list), <c>model</c>, <c>client</c>,
    /// <c>from</c> / <c>to</c>. Metadata only (.agents/memory/key-self-service-requested-model.md).
    /// </summary>
    private static async Task<IResult> RequestsAsync(HttpContext ctx, AstraDatabase db, CancellationToken ct)
    {
        if (await AuthenticateAsync(ctx, db, ct) is not { } token) return Unauthorized();
        var q = ctx.Request.Query;
        var limit = int.TryParse(q["limit"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? Math.Clamp(l, 1, MaxPageSize) : DefaultPageSize;
        // One more than the page tells whether another page follows.
        var rows = await db.Requests.ListForKeyAsync(new KeyRequestQuery(token.Id)
        {
            From = Time(q["from"]),
            To = Time(q["to"]),
            Status = Text(q["status"]),
            Model = Text(q["model"]),
            Client = Text(q["client"]),
            Before = Text(q["before"]),
            Limit = limit + 1,
        }, ct);
        var page = rows.Take(limit).ToList();
        return Json(new JsonObject
        {
            ["items"] = new JsonArray(page.Select(r => (JsonNode)RequestNode(r)).ToArray()),
            ["nextBefore"] = rows.Count > limit ? page[^1].Id : null,
        });
    }

    /// <summary>
    /// The holder's statistics for <c>range</c> (today | 7d | 30d | 90d, in the usage time zone, like the budgets):
    /// totals, a series (hourly today, daily otherwise), usage per requested model and per client, and failures per
    /// error category.
    /// </summary>
    private static async Task<IResult> StatsAsync(
        HttpContext ctx, AstraDatabase db, TokenUsageLedger ledger, SettingsService settings, CancellationToken ct)
    {
        if (await AuthenticateAsync(ctx, db, ct) is not { } token) return Unauthorized();
        var asked = ctx.Request.Query["range"].ToString();
        var range = asked is "today" or "30d" or "90d" ? asked : "7d";
        var zone = ledger.Zone;
        var now = DateTimeOffset.UtcNow;
        var back = range switch { "today" => 0, "30d" => 29, "90d" => 89, _ => 6 };
        var from = UsagePeriods.StartOf(UsagePeriods.LocalDay(now, zone).AddDays(-back), zone).ToUniversalTime();
        var offsetMinutes = (int)zone.GetUtcOffset(now).TotalMinutes;

        var summary = await db.Requests.SummaryAsync(from, now, null, token.Id, ct);
        var series = await db.Requests.TimeseriesAsync(from, now, range == "today" ? "hour" : "day", "token", null, offsetMinutes, token.Id, ct);
        var models = await db.Requests.UsageByAsync(token.Id, from, now, "model", ct);
        var clients = await db.Requests.UsageByAsync(token.Id, from, now, "client", ct);
        var errors = (await db.Requests.ErrorBreakdownAsync(token.Id, from, now, ct))
            .GroupBy(e => RequestErrorCategories.Of(e.Status, e.HttpStatus, e.ErrorType) ?? RequestErrorCategories.Other)
            .Select(g => (Category: g.Key, Count: g.Sum(e => e.Count)))
            .OrderByDescending(e => e.Count);
        return Json(new JsonObject
        {
            ["range"] = range,
            ["timeZone"] = zone.Id,
            ["from"] = from.ToString("o", CultureInfo.InvariantCulture),
            // Older requests may have been removed from the log (null = kept forever); the budgets are unaffected.
            ["retentionDays"] = settings.Current.RequestRetentionDays,
            ["summary"] = new JsonObject
            {
                ["requests"] = summary.TotalRequests,
                ["successRequests"] = summary.SuccessRequests,
                ["successRate"] = summary.SuccessRate,
                ["costUsd"] = Money.FromNanos(summary.TotalCostNanoUsd),
                ["inputTokens"] = summary.TotalInputTokens,
                ["outputTokens"] = summary.TotalOutputTokens,
                ["cacheReadTokens"] = summary.TotalCacheReadTokens,
                ["cacheWriteTokens"] = summary.TotalCacheWriteTokens,
                ["reasoningTokens"] = summary.TotalReasoningTokens,
                ["avgTtftMs"] = summary.AvgTtftMs,
            },
            ["series"] = new JsonArray(series.Select(p => (JsonNode)new JsonObject
            {
                ["bucket"] = p.Bucket,
                ["requests"] = p.Requests,
                ["costUsd"] = Money.FromNanos(p.TotalCostNanoUsd),
                ["inputTokens"] = p.TotalInputTokens,
                ["outputTokens"] = p.TotalOutputTokens,
            }).ToArray()),
            ["models"] = new JsonArray(models.Select(m => (JsonNode)UsageNode("model", m)).ToArray()),
            ["clients"] = new JsonArray(clients.Select(c => (JsonNode)UsageNode("client", c)).ToArray()),
            ["errors"] = new JsonArray(errors.Select(e => (JsonNode)new JsonObject { ["category"] = e.Category, ["count"] = e.Count }).ToArray()),
        });
    }

    private static JsonObject RequestNode(KeyRequestRow r) => new()
    {
        ["id"] = r.Id,
        ["time"] = r.StartedAtUtc.ToString("o", CultureInfo.InvariantCulture),
        ["model"] = r.RequestedModel,
        ["client"] = r.ClientKind,
        ["stream"] = r.Stream,
        ["status"] = r.Status,
        ["httpStatus"] = r.HttpStatus,
        ["errorCategory"] = RequestErrorCategories.Of(r.Status, r.HttpStatus, r.ErrorType),
        ["inputTokens"] = r.InputTokens,
        ["outputTokens"] = r.OutputTokens,
        ["cacheReadTokens"] = r.CacheReadTokens,
        ["cacheWriteTokens"] = r.CacheWriteTokens,
        ["reasoningTokens"] = r.ReasoningTokens,
        ["costUsd"] = Money.FromNanos(r.CostNanoUsd),
        ["ttftMs"] = r.TtftMs,
        ["totalMs"] = r.TotalMs,
    };

    private static JsonObject UsageNode(string keyName, KeyUsageRow u) => new()
    {
        [keyName] = u.Key,
        ["requests"] = u.Requests,
        ["successRequests"] = u.SuccessRequests,
        ["costUsd"] = Money.FromNanos(u.CostNanoUsd),
        ["inputTokens"] = u.InputTokens,
        ["outputTokens"] = u.OutputTokens,
        ["avgTtftMs"] = u.AvgTtftMs,
    };

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTimeOffset? Time(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;

    /// <summary>The token a request presents (a client suffix is allowed); null when there is none or it is unknown.</summary>
    private static async Task<TokenRecord?> AuthenticateAsync(HttpContext ctx, AstraDatabase db, CancellationToken ct)
    {
        var key = GatewayTokens.Extract(ctx.Request);
        if (string.IsNullOrEmpty(key) || !GatewayTokens.TryParse(key, out var secret, out _)) return null;
        return (await db.Tokens.FindByPrefixAsync(GatewayTokens.PrefixOf(secret), ct))
            .FirstOrDefault(t => GatewayTokens.Verify(secret, t.KeyHash));
    }

    private static JsonObject BudgetNode(TokenBudgetStatus s)
    {
        var cost = s.Budget.Metric == TokenLimitMetrics.Cost;
        JsonNode Amount(long v) => cost ? JsonValue.Create(Money.FromNanos(v)) : JsonValue.Create(v);
        return new JsonObject
        {
            ["metric"] = s.Budget.Metric,
            ["period"] = s.Budget.Period,
            ["unit"] = cost ? "usd" : s.Budget.Metric,
            ["limit"] = Amount(s.Budget.Limit),
            ["used"] = Amount(s.Used),
            ["remaining"] = Amount(s.Remaining),
            ["usedPercent"] = s.UsedPercent,
            ["exhausted"] = s.Exhausted,
            ["resetsAt"] = s.ResetsAt?.ToString("o", CultureInfo.InvariantCulture),
        };
    }

    private static JsonObject TallyNode(TokenTally t) => new()
    {
        ["requests"] = t.Requests,
        ["successRequests"] = t.SuccessRequests,
        ["costUsd"] = Money.FromNanos(t.CostNanoUsd),
        ["inputTokens"] = t.InputTokens,
        ["outputTokens"] = t.OutputTokens,
    };

    private static IResult Unauthorized() => Json(new JsonObject
    {
        ["error"] = new JsonObject
        {
            ["message"] = "缺少或无效的 Astra 令牌（Authorization: Bearer / x-api-key / ?key=）。",
            ["type"] = "authentication_error",
        },
    }, StatusCodes.Status401Unauthorized);

    private static IResult Json(JsonObject body, int status = StatusCodes.Status200OK) =>
        Results.Text(body.ToJsonString(GatewayJson.Options), "application/json", System.Text.Encoding.UTF8, status);
}
