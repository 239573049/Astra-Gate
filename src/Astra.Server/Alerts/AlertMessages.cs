using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Core.Alerts;

namespace Astra.Server.Alerts;

/// <summary>
/// The human-readable <c>message</c> of a webhook payload, in <c>AppSettings.Locale</c>. Written in C# like the gateway's
/// own messages (the web i18n catalog is not available here); keep zh and en in step.
/// </summary>
public static class AlertMessages
{
    public static string Format(string locale, string eventId, string status, string subject, JsonObject observed, JsonObject condition)
    {
        var en = locale == "en";
        var resolved = status == "resolved";
        string O(string key) => Text(observed[key]);
        var value = Text(condition["value"]);
        switch (eventId)
        {
            case AlertEventIds.ProviderBalanceLow:
            {
                var unit = O("unit") is { Length: > 0 } u ? " " + u : "";
                var percent = AlertConditions.Str(condition["metric"]) == "usedPercent";
                if (resolved)
                    return en ? $"Provider {subject} balance is back ({O("remaining")}{unit})." : $"提供商 {subject} 余额已恢复（{O("remaining")}{unit}）。";
                return percent
                    ? en ? $"Provider {subject} has used {O("usedPercent")}% (threshold {value}%)." : $"提供商 {subject} 已用 {O("usedPercent")}%，达到阈值 {value}%。"
                    : en ? $"Provider {subject} balance is {O("remaining")}{unit}, below {value}{unit}." : $"提供商 {subject} 余额 {O("remaining")}{unit}，低于阈值 {value}{unit}。";
            }
            case AlertEventIds.ProviderQuotaFailed:
                return resolved
                    ? en ? $"Provider {subject} balance query works again." : $"提供商 {subject} 余额查询已恢复。"
                    : en ? $"Provider {subject} balance query failed {O("failures")} times in a row: {O("error")}" : $"提供商 {subject} 余额查询连续失败 {O("failures")} 次：{O("error")}";
            case AlertEventIds.AccountQuotaHigh:
            {
                var window = WindowName(O("window"), en);
                return resolved
                    ? en ? $"Subscription account {subject}: {window} usage is down to {O("usedPercent")}%." : $"订阅账号 {subject} 的{window}额度已回落到 {O("usedPercent")}%。"
                    : en ? $"Subscription account {subject} has used {O("usedPercent")}% of its {window} quota (threshold {value}%)." : $"订阅账号 {subject} 的{window}额度已用 {O("usedPercent")}%（阈值 {value}%）。";
            }
            case AlertEventIds.AccountAuthInvalid:
                return resolved
                    ? en ? $"Subscription account {subject} works again." : $"订阅账号 {subject} 已恢复可用。"
                    : en ? $"Subscription account {subject} is {O("status")} and needs to sign in again." : $"订阅账号 {subject} 已失效（{O("status")}），需要重新登录。";
            case AlertEventIds.AccountQuotaFailed:
                return resolved
                    ? en ? $"Subscription account {subject} quota query works again." : $"订阅账号 {subject} 额度查询已恢复。"
                    : en ? $"Subscription account {subject} quota query failed {O("failures")} times in a row: {O("error")}" : $"订阅账号 {subject} 额度查询连续失败 {O("failures")} 次：{O("error")}";
            case AlertEventIds.TokenUsageHigh:
            {
                var what = Budget(O("period"), O("metric"), en);
                return resolved
                    ? en ? $"Token {subject}: {what} is down to {O("usedPercent")}%." : $"令牌 {subject} 的{what}额度已回落到 {O("usedPercent")}%。"
                    : en ? $"Token {subject} has used {O("usedPercent")}% of its {what} limit ({O("used")} / {O("limit")})." : $"令牌 {subject} 的{what}额度已用 {O("usedPercent")}%（{O("used")} / {O("limit")}）。";
            }
            case AlertEventIds.TokenLimitReached:
                if (resolved) return en ? $"Token {subject} is within its limits again." : $"令牌 {subject} 的额度已恢复。";
                if (O("key") == "rpm") return en ? $"Token {subject} hit its requests-per-minute limit." : $"令牌 {subject} 达到每分钟请求上限。";
                return en
                    ? $"Token {subject} used up its {Budget(O("period"), O("metric"), en)} limit ({O("used")} / {O("limit")})."
                    : $"令牌 {subject} 的{Budget(O("period"), O("metric"), en)}额度已用完（{O("used")} / {O("limit")}）。";
            default:
                return en ? "Test notification from Astra." : "这是一条来自 Astra 的测试通知。";
        }
    }

    private static string WindowName(string window, bool en) => window switch
    {
        "session" => en ? "5-hour" : " 5 小时",
        "weekly" => en ? "weekly" : "每周",
        _ => en ? "credits" : "点数",
    };

    private static string Budget(string period, string metric, bool en)
    {
        var p = period switch
        {
            "day" => en ? "daily" : "今日",
            "week" => en ? "weekly" : "本周",
            "month" => en ? "monthly" : "本月",
            _ => en ? "total" : "总",
        };
        var m = metric switch
        {
            "cost" => en ? "cost" : "费用",
            "requests" => en ? "request" : "请求数",
            _ => en ? "token" : " token ",
        };
        return en ? $"{p} {m}" : p + m;
    }

    private static string Text(JsonNode? node) => AlertConditions.Num(node) is { } n
        ? Math.Round(n, 2).ToString(CultureInfo.InvariantCulture)
        : AlertConditions.Str(node) ?? "—";
}
