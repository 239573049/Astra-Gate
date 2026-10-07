namespace Astra.Core.Billing;

/// <summary>Where the pricing used for a request came from.</summary>
public enum PricingSource
{
    /// <summary>No pricing configured anywhere; cost is recorded as 0.</summary>
    None,

    /// <summary>The system model's official default pricing (× provider multiplier).</summary>
    SystemDefault,

    /// <summary>The system model's pricing for this provider's price key (× provider multiplier).</summary>
    ProviderPrice,

    /// <summary>The provider instance's own model override (multiplier not applied).</summary>
    ProviderOverride,
}

/// <summary>The pricing chosen for one request plus how it should be scaled.</summary>
public sealed record ResolvedPricing(PricingSchedule? Schedule, PricingSource Source, string? PriceKey, decimal Multiplier)
{
    public static readonly ResolvedPricing None = new(null, PricingSource.None, null, 1m);
}

/// <summary>
/// Picks the price schedule for (model, provider). Pricing differs per AI provider:
/// ① provider-instance override → ② system model price for the provider's price key → ③ system default → ④ none.
/// The provider multiplier only scales inherited (system) prices, never an instance override.
/// </summary>
public static class PricingResolver
{
    public static ResolvedPricing Resolve(
        PricingSchedule? providerOverride,
        IReadOnlyDictionary<string, PricingSchedule>? providerPrices,
        string? priceKey,
        PricingSchedule? systemDefault,
        decimal providerMultiplier)
    {
        if (providerOverride is not null)
            return new ResolvedPricing(providerOverride, PricingSource.ProviderOverride, null, 1m);
        if (!string.IsNullOrWhiteSpace(priceKey) && providerPrices is not null &&
            providerPrices.TryGetValue(priceKey, out var keyed))
            return new ResolvedPricing(keyed, PricingSource.ProviderPrice, priceKey, providerMultiplier);
        if (systemDefault is not null)
            return new ResolvedPricing(systemDefault, PricingSource.SystemDefault, null, providerMultiplier);
        return ResolvedPricing.None;
    }
}
