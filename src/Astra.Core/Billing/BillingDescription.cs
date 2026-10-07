using System.Globalization;
using System.Text;

namespace Astra.Core.Billing;

/// <summary>Renders a billing trace + items into a human readable, archived description (zh / en).</summary>
public static class BillingDescription
{
    public static string Render(BillingResult result, string locale)
    {
        var zh = !locale.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var step in result.Trace.Where(s => s.Code != "total"))
        {
            var line = RenderStep(step, zh);
            if (line is not null) sb.AppendLine(line);
        }
        foreach (var item in result.Items)
        {
            sb.AppendLine(RenderItem(item, zh));
        }
        sb.Append(zh ? "合计 " : "Total ").Append(Money.Format(result.TotalUsd));
        if (!result.Priced) sb.Append(zh ? "（未配置价格）" : " (no pricing configured)");
        return sb.ToString();
    }

    public static string RenderItem(BillingItem item, bool zh)
    {
        var count = item.Tokens.ToString("N0", CultureInfo.InvariantCulture);
        var unit = item.IsPerCall
            ? $"{Money.Format(item.UnitPrice, 4)}/{(zh ? "次" : "call")}"
            : $"{Money.Format(item.UnitPrice, 4)}/M";
        var sb = new StringBuilder();
        sb.Append(item.TokenType).Append(' ').Append(count).Append(" × ").Append(unit)
          .Append(" = ").Append(Money.Format(Money.FromNanos(item.CostNanos)));
        var extras = new List<string>();
        if (item.Tier != "base") extras.Add((zh ? "档位 " : "tier ") + item.Tier);
        if (item.Multiplier != 1m)
            extras.Add($"{Money.Format(item.BaseUnitPrice, 4)} × {BillingEngine.Fmt(item.Multiplier)} ({string.Join(", ", item.MultiplierSources)})");
        if (item.Note is { } note)
        {
            if (note.StartsWith("fallback:", StringComparison.Ordinal))
                extras.Add((zh ? "按 " : "priced as ") + note["fallback:".Length..] + (zh ? " 计价" : ""));
            else if (note == "missing_price") extras.Add(zh ? "未配置单价" : "no unit price");
            else if (note == "no_pricing") extras.Add(zh ? "未配置价格" : "no pricing");
        }
        if (extras.Count > 0) sb.Append(" [").Append(string.Join("; ", extras)).Append(']');
        return sb.ToString();
    }

    private static string? RenderStep(BillingTraceStep s, bool zh)
    {
        string A(string key) => s.Args.GetValueOrDefault(key) ?? "";
        string N(string key) => long.TryParse(A(key), out var v) ? v.ToString("N0", CultureInfo.InvariantCulture) : A(key);
        return s.Code switch
        {
            "pricing_source" when A("source") == "provider_override" => zh
                ? $"模型 {A("model")} @ {A("provider")}：使用提供商模型覆盖的价格（不乘提供商倍率）"
                : $"Model {A("model")} @ {A("provider")}: provider-model override pricing (provider multiplier not applied)",
            "pricing_source" when A("source") == "provider_price" => zh
                ? $"模型 {A("model")} @ {A("provider")}：使用系统模型在提供商 \"{A("price_key")}\" 下的价格"
                : $"Model {A("model")} @ {A("provider")}: system pricing for provider \"{A("price_key")}\"",
            "pricing_source" => zh
                ? $"模型 {A("model")} @ {A("provider")}：使用系统模型官方默认价格"
                : $"Model {A("model")} @ {A("provider")}: system default (official) pricing",
            "no_pricing" => zh ? $"模型 {A("model")} 未配置价格，费用记为 0" : $"Model {A("model")} has no pricing; cost recorded as 0",
            "service_tier_applied" => zh
                ? $"服务等级 {A("tier")}：" + (A("replaced") == "True" ? "替换基础价" : "") + (A("multiplier") != "" ? $" 倍率 ×{A("multiplier")}" : "")
                : $"Service tier {A("tier")}: " + (A("replaced") == "True" ? "base prices replaced" : "") + (A("multiplier") != "" ? $" multiplier ×{A("multiplier")}" : ""),
            "service_tier_unmatched" => zh
                ? $"服务等级 {A("tier")} 未配置价格，按默认价计"
                : $"Service tier {A("tier")} has no pricing; billed at default",
            "time_window" => zh
                ? $"时段 {A("name")}（{A("timezone")} {A("range")}，请求时间 {A("local_time")}）" + (A("multiplier") != "" ? $" 倍率 ×{A("multiplier")}" : "") + (A("replaced") == "True" ? " 替换单价" : "")
                : $"Time window {A("name")} ({A("timezone")} {A("range")}, request at {A("local_time")})" + (A("multiplier") != "" ? $" multiplier ×{A("multiplier")}" : "") + (A("replaced") == "True" ? " prices replaced" : ""),
            "provider_multiplier" => zh ? $"提供商倍率 ×{A("multiplier")}" : $"Provider multiplier ×{A("multiplier")}",
            "context_tier_none" => zh
                ? $"输入总量 {N("total_input")} ≤ {N("lowest_threshold")}，按基础档计"
                : $"Total input {N("total_input")} ≤ {N("lowest_threshold")}: base tier",
            "context_tier_whole" => zh
                ? $"输入总量 {N("total_input")} > {N("threshold")} → 整单套用 \"{A("tier")}\" 档"
                : $"Total input {N("total_input")} > {N("threshold")} → whole request billed at \"{A("tier")}\"",
            "context_tier_progressive" => zh
                ? $"输入总量 {N("total_input")} > {N("threshold")} → 累进分段计费（输出按{(A("output_policy") == "base" ? "基础档" : "最高档")}）"
                : $"Total input {N("total_input")} > {N("threshold")} → progressive tiers (outputs at {(A("output_policy") == "base" ? "base" : "highest")} tier)",
            "fallback_price" => zh
                ? $"{A("type")} 未配置单价，按 {A("priced_as")} 价计"
                : $"{A("type")} has no price; priced as {A("priced_as")}",
            "missing_price" => zh ? $"{A("type")} 未配置单价，记为 0" : $"{A("type")} has no price; billed at 0",
            "usage_note" => (zh ? "用量说明：" : "Usage note: ") + UsageNotes.Describe(A("text"), zh),
            _ => null,
        };
    }
}
