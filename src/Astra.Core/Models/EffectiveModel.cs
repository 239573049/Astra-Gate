using Astra.Core.Billing;

namespace Astra.Core.Models;

/// <summary>"inherited" from the system model, "overridden" by the provider model, or "unset".</summary>
public static class FieldOrigin
{
    public const string Inherited = "inherited";
    public const string Overridden = "overridden";
    public const string Unset = "unset";
}

/// <summary>System model fields overlaid per-field by the provider model's overrides; pricing resolved per provider.</summary>
public sealed class EffectiveModel
{
    public string ProviderId { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string? SystemModelId { get; init; }
    public string DisplayName { get; init; } = "";
    public string? Vendor { get; init; }
    public long? ContextWindow { get; init; }
    public long? MaxOutputTokens { get; init; }
    public ModelCapabilities Capabilities { get; init; } = new();
    public ResolvedPricing Pricing { get; init; } = ResolvedPricing.None;
    public bool Enabled { get; init; }

    /// <summary>Field name → <see cref="FieldOrigin"/>.</summary>
    public Dictionary<string, string> Origins { get; init; } = new();
}

public static class ModelMerger
{
    /// <param name="providerPrices">System prices of <paramref name="system"/>, keyed by price key.</param>
    public static EffectiveModel Merge(Provider provider, ProviderModel pm, SystemModel? system,
        IReadOnlyDictionary<string, PricingSchedule>? providerPrices)
    {
        var o = pm.Overrides;
        var origins = new Dictionary<string, string>();

        T? Pick<T>(string field, T? overrideValue, T? systemValue)
        {
            if (overrideValue is not null)
            {
                origins[field] = FieldOrigin.Overridden;
                return overrideValue;
            }
            origins[field] = systemValue is null ? FieldOrigin.Unset : FieldOrigin.Inherited;
            return systemValue;
        }

        var displayName = Pick("displayName", o.DisplayName, system?.DisplayName) ?? pm.ModelId;
        var context = Pick("contextWindow", o.ContextWindow, system?.ContextWindow);
        var maxOut = Pick("maxOutputTokens", o.MaxOutputTokens, system?.MaxOutputTokens);
        origins["capabilities"] = o.Capabilities is not null ? FieldOrigin.Overridden
            : system is not null ? FieldOrigin.Inherited : FieldOrigin.Unset;
        var caps = (system?.Capabilities ?? new ModelCapabilities()).Overlay(o.Capabilities);

        var pricing = PricingResolver.Resolve(o.Pricing, providerPrices, provider.PriceKey ?? provider.TemplateId,
            system?.Pricing, provider.PriceMultiplier);
        origins["pricing"] = pricing.Source switch
        {
            PricingSource.ProviderOverride => FieldOrigin.Overridden,
            PricingSource.None => FieldOrigin.Unset,
            _ => FieldOrigin.Inherited,
        };

        return new EffectiveModel
        {
            ProviderId = provider.Id,
            ModelId = pm.ModelId,
            SystemModelId = system?.Id,
            DisplayName = displayName,
            Vendor = system?.Vendor,
            ContextWindow = context,
            MaxOutputTokens = maxOut,
            Capabilities = caps,
            Pricing = pricing,
            Enabled = pm.Enabled && (system?.Enabled ?? true),
            Origins = origins,
        };
    }
}
