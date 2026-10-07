using Astra.Core.Models;
using Astra.Data;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Builds the effective model (system model + provider overrides + provider-specific price) for a provider's model.
/// Shared by the gateway (billing a request) and the admin API (provider model views, billing simulator).
/// </summary>
public sealed class EffectiveModelResolver(AstraDatabase db)
{
    public async Task<EffectiveModel> ResolveAsync(Provider provider, ProviderModel pm, CancellationToken ct = default)
    {
        var system = pm.SystemModelId is null ? null : await db.Models.GetAsync(pm.SystemModelId, ct);
        var prices = system is null ? null : await db.Models.GetPriceTableAsync(system.Id, ct);
        return ModelMerger.Merge(provider, pm, system, prices);
    }

    /// <summary>
    /// Resolves the model a client asked for at a provider. The gateway forwards model names verbatim, so a model
    /// that is not in the provider's list is still billed: it is auto-linked to a system model on the fly.
    /// </summary>
    public async Task<(ProviderModel Model, EffectiveModel Effective)> ResolveByModelIdAsync(
        Provider provider, string modelId, CancellationToken ct = default)
    {
        var pm = await db.Providers.GetModelAsync(provider.Id, modelId, ct)
                 ?? new ProviderModel
                 {
                     ProviderId = provider.Id,
                     ModelId = modelId,
                     SystemModelId = await LinkAsync(provider, modelId, ct),
                     Enabled = true,
                 };
        return (pm, await ResolveAsync(provider, pm, ct));
    }

    /// <summary>Auto-link an upstream model id to a system model (exact → provider upstream id → alias → normalized).</summary>
    public async Task<string?> LinkAsync(Provider provider, string modelId, CancellationToken ct = default)
    {
        var candidates = await db.Models.GetMatcherCandidatesAsync(ct);
        var priceKey = provider.PriceKey ?? provider.TemplateId;
        var index = priceKey is null ? null : await db.Models.GetUpstreamIdIndexAsync(priceKey, ct);
        return ModelIdMatcher.Match(modelId, candidates, index);
    }
}
