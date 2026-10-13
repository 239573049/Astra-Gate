using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Alerts;
using Astra.Core.Tokens;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Enforces a token's usage caps before a request goes upstream (plan: alerts-webhooks-token-limits §1.4). Soft: the
/// check reads usage settled so far, so requests already in flight may push a budget past its limit. A lifetime budget
/// used up answers 403 (it never resets, retrying is pointless); a periodic one 429 with Retry-After until the period
/// resets; the RPM window 429 with Retry-After until a slot frees up. Messages never name upstream providers or
/// accounts: the caller may be someone the token was shared with. A refusal is reported to the alert engine
/// (<c>token.limit.reached</c>) right away; budgets running high are picked up by the engine's periodic sweep.
/// </summary>
public sealed class TokenLimiter(TokenUsageLedger ledger, IAlertSignals? alerts = null)
{
    /// <summary>A token's alert observation: <c>{ budgets: [...], rpmLimited? }</c> (see <see cref="AlertSignal"/>).</summary>
    public static JsonObject Observation(IEnumerable<TokenBudgetStatus> statuses, bool? rpmLimited = null)
    {
        var node = new JsonObject
        {
            ["budgets"] = new JsonArray(statuses.Select(s => (JsonNode)new JsonObject
            {
                ["key"] = s.Budget.Key,
                ["metric"] = s.Budget.Metric,
                ["period"] = s.Budget.Period,
                ["used"] = Display(s.Budget.Metric, s.Used),
                ["limit"] = Display(s.Budget.Metric, s.Budget.Limit),
                ["usedPercent"] = s.UsedPercent,
                ["exhausted"] = s.Exhausted,
                ["resetsAt"] = s.ResetsAt?.ToString("o", CultureInfo.InvariantCulture),
            }).ToArray()),
        };
        if (rpmLimited is { } limited) node["rpmLimited"] = limited;
        return node;
    }

    private static JsonNode Display(string metric, long value) =>
        metric == TokenLimitMetrics.Cost ? JsonValue.Create(Money.FromNanos(value)) : JsonValue.Create(value);

    /// <exception cref="GatewayException">When a budget is used up or the RPM window is full.</exception>
    public async Task CheckAsync(TokenRecord token, CancellationToken ct)
    {
        if (token.Limits() is not { } limits) return;
        IReadOnlyList<TokenBudgetStatus> statuses = [];
        if (limits.Budgets.Count > 0)
        {
            statuses = await ledger.StatusAsync(token.Id, limits, ct);
            var exhausted = statuses.Where(s => s.Exhausted).ToList();
            if (exhausted.Count > 0)
            {
                alerts?.Report(new AlertSignal(AlertSubjectTypes.Token, token.Id, token.Name, Observation(statuses)));
                // A lifetime budget wins (nothing resets it); otherwise the one that resets last decides the wait.
                var worst = exhausted.FirstOrDefault(s => s.ResetsAt is null) ?? exhausted.MaxBy(s => s.ResetsAt)!;
                var what = $"{PeriodName(worst.Budget.Period)}{MetricName(worst.Budget.Metric)}额度已用完（已用 {Amount(worst.Budget.Metric, worst.Used)} / 上限 {Amount(worst.Budget.Metric, worst.Budget.Limit)}）";
                if (worst.ResetsAt is not { } resetsAt)
                    throw new GatewayException(403, "permission_error", $"令牌 {token.Name} 的{what}。")
                    {
                        RecordErrorType = TokenLimits.RefusedErrorType,
                    };
                throw new GatewayException(429, "rate_limit_error",
                    $"令牌 {token.Name} 的{what}，将于 {resetsAt.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)} 重置。")
                {
                    RecordErrorType = TokenLimits.RefusedErrorType,
                    RetryAfterSeconds = Seconds(resetsAt - DateTimeOffset.UtcNow),
                };
            }
        }
        if (limits.Rpm is { } rpm && !ledger.TryTakeMinuteSlot(token.Id, rpm, out var wait))
        {
            alerts?.Report(new AlertSignal(AlertSubjectTypes.Token, token.Id, token.Name, Observation(statuses, rpmLimited: true)));
            throw new GatewayException(429, "rate_limit_error", $"令牌 {token.Name} 每分钟最多 {rpm} 个请求，请稍后再试。")
            {
                RecordErrorType = TokenLimits.RefusedErrorType,
                RetryAfterSeconds = Seconds(wait),
            };
        }
    }

    private static int Seconds(TimeSpan wait) => (int)Math.Max(1, Math.Ceiling(wait.TotalSeconds));

    private static string PeriodName(string period) => period switch
    {
        TokenLimitPeriods.Day => "今日",
        TokenLimitPeriods.Week => "本周",
        TokenLimitPeriods.Month => "本月",
        _ => "总",
    };

    private static string MetricName(string metric) => metric switch
    {
        TokenLimitMetrics.Cost => "费用",
        TokenLimitMetrics.Requests => "请求数",
        _ => " token ",
    };

    private static string Amount(string metric, long value) => metric == TokenLimitMetrics.Cost
        ? Money.Format(Money.FromNanos(value), 2)
        : value.ToString("N0", CultureInfo.InvariantCulture);
}
