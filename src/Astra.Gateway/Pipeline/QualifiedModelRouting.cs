using Astra.Core;
using Astra.Core.Routing;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Provider-qualified model ids (issue #21, <see cref="ModelQualifier"/>) at the routing seam: a request for
/// <c>glm-5.3@zcode</c> is pointed at the provider whose alias is <c>zcode</c> and the model becomes <c>glm-5.3</c> —
/// through <see cref="GatewayRoute.Routed"/>, like smart routing, so the body's <c>model</c> is never rewritten here and the
/// request log keeps the id the client used. Only providers among the caller's own targets are considered: an alias cannot
/// carry a token past the providers it may reach. A request that does not end in a known alias — every request while the
/// setting is off, or a bare id — routes exactly as before.
/// </summary>
public static class QualifiedModelRouting
{
    /// <exception cref="GatewayException">The alias names a provider the caller has, but it does not serve the model.</exception>
    public static async Task<GatewayRoute> ApplyAsync(
        IGatewayRouter router, GatewayRoute route, string requested, AppSettings settings, bool isClaudeCode, CancellationToken ct)
    {
        if (ModelQualifier.From(settings) is not { } qualifier) return route;
        // The id after role mapping: Claude Desktop's role map may itself hold a qualified id.
        if (!qualifier.TrySplit(route.ClientModelFor(requested), route.Targets.Select(t => t.Provider), out var model, out var provider))
            return route;
        var (target, _) = await router.GradeUseAsync(route, provider.Id, model, isClaudeCode, ct);
        if (target is null)
            throw new GatewayException(404, "not_found_error", $"提供商 {provider.Name} 当前不能提供模型 {model}（未启用该模型，或不允许此调用方使用）。");
        return GatewayRouter.WithTarget(route with { Routed = new RoutedModel(requested, model, "provider-qualified") }, target);
    }
}
