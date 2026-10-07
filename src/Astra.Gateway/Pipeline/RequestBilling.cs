using System.Globalization;
using System.Text.Json;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Requests;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Turns normalized usage + the effective model's resolved pricing into a bill and copies it onto a
/// <see cref="RequestRecord"/>. Subscription requests bill the same way; the caller passes
/// <paramref name="subscriptionEquivalent"/> to label the amount as an equivalent cost (plan §5.4).
/// </summary>
public static class RequestBilling
{
    private const string SubscriptionPrefix = "订阅额度，等效成本仅供参考｜";

    public static BillingResult Apply(
        RequestRecord record, NormalizedUsage? usage, ResolvedPricing pricing, string locale,
        bool subscriptionEquivalent = false)
    {
        usage ??= new NormalizedUsage();
        if (record.UsageSource == "missing") usage.Notes.Add(UsageNotes.UsageMissing);
        if (usage.ServiceTier is not null) record.ServiceTier = usage.ServiceTier;

        var ctx = BillingContext.From(pricing, record.StartedAtUtc,
            record.SystemModelId ?? record.UpstreamModel ?? record.RequestedModel ?? "?", record.ProviderName, locale);
        var bill = BillingEngine.Calculate(usage, pricing.Schedule, ctx);

        record.TotalInputTokens = usage.TotalInput;
        record.TotalOutputTokens = usage.TotalOutput;
        record.CacheReadTokens = usage.Get(TokenTypes.CacheRead);
        record.CacheWriteTokens = usage.Get(TokenTypes.CacheWrite5m) + usage.Get(TokenTypes.CacheWrite1h);
        record.ReasoningTokens = usage.Get(TokenTypes.Reasoning);
        record.CostNanoUsd = bill.TotalNanos;
        record.PricingSource = pricing.Source switch
        {
            PricingSource.ProviderOverride => "provider_override",
            PricingSource.ProviderPrice => "provider_price",
            PricingSource.SystemDefault => "system_default",
            _ => "none",
        };
        record.PriceKey = pricing.PriceKey;
        record.PricingSnapshotJson = bill.PricingSnapshot is null ? null : Json.Serialize(bill.PricingSnapshot);
        record.BillingTraceJson = JsonSerializer.Serialize(bill.Trace, Json.Storage);
        record.BillingDescription = subscriptionEquivalent ? SubscriptionPrefix + bill.Description : bill.Description;
        record.UsageItems = bill.Items.Select(i => new RequestUsageItem
        {
            RequestId = record.Id,
            TokenType = i.TokenType,
            Tokens = i.Tokens,
            IsPerCall = i.IsPerCall,
            UnitPrice = i.UnitPrice.ToString(CultureInfo.InvariantCulture),
            BaseUnitPrice = i.BaseUnitPrice.ToString(CultureInfo.InvariantCulture),
            TierApplied = i.Tier,
            PricedAs = i.PricedAs,
            MultipliersJson = i.MultiplierSources.Count == 0 ? null : JsonSerializer.Serialize(i.MultiplierSources, Json.Storage),
            CostNanoUsd = i.CostNanos,
            Note = i.Note,
        }).ToList();
        return bill;
    }
}
