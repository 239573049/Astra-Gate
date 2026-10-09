using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;

namespace Astra.Gateway.Pipeline;

/// <summary>One provider a request can go to, with the subscription account pinned for it (null = the provider's default).</summary>
public sealed record RouteTarget(Provider Provider, string? AccountId);

/// <summary>
/// A model id a client can use through its bindings: the id it asks for (<paramref name="Id"/>), the target that serves
/// it, and the provider model it reaches — the same model, or the one <see cref="Provider.ModelMap"/> maps it to.
/// </summary>
public sealed record RoutableModel(string Id, RouteTarget Target, ProviderModel Model, EffectiveModel Effective);

/// <summary>
/// Multi-provider routing over a client's ordered bindings (<see cref="ClientBinding.Priority"/>, drag order in the UI).
/// A request goes to the first usable provider that serves the requested model id — as one of its enabled models
/// (exact id match, no alias or system-model matching), or through its model mapping onto one of its enabled models —
/// else to the primary. A provider without a model and without a mapping for the id is skipped. The model list a client
/// sees (<c>/v1/models</c>, the list written into its config) is the union in the same order, de-duplicated by id.
/// </summary>
public sealed class ClientRouting(AstraDatabase db, EffectiveModelResolver models)
{
    /// <summary>Whether requests can be sent to the provider at all: enabled, with at least one endpoint.</summary>
    public static bool IsUsable(Provider provider) => provider.Enabled && provider.Endpoints.Count > 0;

    /// <summary>The usable providers of <paramref name="bindings"/>, in binding order; missing or unusable ones are skipped.</summary>
    public async Task<List<RouteTarget>> TargetsAsync(IReadOnlyList<ClientBinding> bindings, CancellationToken ct = default)
    {
        var result = new List<RouteTarget>();
        foreach (var binding in bindings)
            if (await db.Providers.GetAsync(binding.ProviderId, ct) is { } provider && IsUsable(provider))
                result.Add(new RouteTarget(provider, binding.AccountId));
        return result;
    }

    /// <summary>
    /// The first target that serves <paramref name="modelId"/> (after its model mapping) as an enabled model and
    /// accepts this caller (a Claude-Code-only subscription is skipped for other callers); null when none matches.
    /// </summary>
    public async Task<RouteTarget?> MatchAsync(IReadOnlyList<RouteTarget> targets, string modelId, bool isClaudeCode, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(modelId)) return null;
        foreach (var target in targets)
        {
            if (!isClaudeCode && SubscriptionSupport.ClientPolicyOf(target.Provider) == ClientPolicies.ClaudeCodeOnly) continue;
            var upstream = target.Provider.MapModel(modelId);
            if (await db.Providers.GetModelAsync(target.Provider.Id, upstream, ct) is not { Enabled: true } pm) continue;
            if ((await models.ResolveAsync(target.Provider, pm, ct)).Enabled) return target;
        }
        return null;
    }

    /// <summary>
    /// Model ids usable across <paramref name="targets"/>, in binding order: per provider its enabled models (in its
    /// model order), then its mapped ids whose target is one of those models. An id offered by several providers is
    /// listed once, for the first of them — the one <see cref="MatchAsync"/> picks.
    /// </summary>
    public async Task<List<RoutableModel>> ListModelsAsync(IReadOnlyList<RouteTarget> targets, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<RoutableModel>();
        foreach (var target in targets)
        {
            var map = target.Provider.ModelMap();
            var enabled = new Dictionary<string, (ProviderModel Model, EffectiveModel Effective)>(StringComparer.Ordinal);
            foreach (var pm in await db.Providers.ListModelsAsync(target.Provider.Id, ct))
            {
                var effective = await models.ResolveAsync(target.Provider, pm, ct);
                if (effective.Enabled) enabled[pm.ModelId] = (pm, effective);
            }
            // A provider model whose id is itself mapped elsewhere is reached under that id only through the mapping.
            foreach (var (id, (pm, effective)) in enabled)
                if (!map.ContainsKey(id) && seen.Add(id)) result.Add(new RoutableModel(id, target, pm, effective));
            foreach (var (alias, upstream) in map)
                if (enabled.TryGetValue(upstream, out var hit) && seen.Add(alias))
                    result.Add(new RoutableModel(alias, target, hit.Model, hit.Effective));
        }
        return result;
    }
}
