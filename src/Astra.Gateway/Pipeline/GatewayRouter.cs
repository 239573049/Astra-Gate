using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Tokens;
using Astra.Data;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Pipeline;

/// <summary>A gateway-level rejection, rendered in the inbound protocol's error format.</summary>
public sealed class GatewayException(int status, string type, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Type { get; } = type;
}

/// <summary>
/// Who is calling and where the request goes: the authenticating token, the client named by the key suffix (null for a
/// direct call with a bare token), the pinned subscription account (null = the provider's default) and the provider.
/// </summary>
public sealed record GatewayRoute(TokenRecord Token, ClientRecord? Client, string? AccountId, Provider Provider)
{
    /// <summary>
    /// Every usable provider the request may go to, in binding order (the primary first): the client's bound
    /// providers, or just the token's own provider for a direct call. See <see cref="GatewayRouter.ForModelAsync"/>.
    /// </summary>
    public IReadOnlyList<RouteTarget> Targets { get; init; } = [];

    /// <summary>The calling client's kind; null for a direct call.</summary>
    public string? ClientKind => Client?.Kind;

    /// <summary>
    /// The model the client means, before any provider is involved: model names pass through, except for Claude
    /// Desktop, whose role ids are mapped to provider models (plan §7.5). This is what routing matches on.
    /// </summary>
    public string ClientModelFor(string requested) =>
        Client?.Kind == ClientKinds.ClaudeDesktop ? ClaudeDesktopRoles.Map(ClaudeDesktopRoles.Parse(Client.ExtraJson), requested) : requested;

    /// <summary>
    /// The model sent upstream: <see cref="ClientModelFor"/>, then the chosen provider's model mapping
    /// (<see cref="Provider.ModelMap"/>). Otherwise the gateway passes model names through.
    /// </summary>
    public string UpstreamModelFor(string requested) => Provider.MapModel(ClientModelFor(requested));
}

/// <summary>Resolves a model's supported upstream protocols, discovering missing capabilities when needed.</summary>
public interface IModelProtocolResolver
{
    Task<IReadOnlyList<ApiProtocol>> ResolveAsync(Provider provider, ProviderModel model, string? accountId, CancellationToken ct);
}

/// <summary>
/// Authenticates the gateway token (plan §6.1, tokens) and resolves the provider: "&lt;token&gt;.&lt;kind&gt;" routes through
/// that client's bindings, a bare token is a direct call to the token's own default provider.
/// Tokens are looked up by prefix and compared by hash in constant time.
/// </summary>
public sealed class GatewayRouter(AstraDatabase db, ClientRouting routing)
{
    public async Task<GatewayRoute> ResolveAsync(HttpRequest request, CancellationToken ct)
    {
        var key = GatewayTokens.Extract(request);
        if (string.IsNullOrEmpty(key))
            throw new GatewayException(401, "authentication_error", "缺少 Astra 令牌（Authorization: Bearer / x-api-key / x-goog-api-key / ?key=）。请在 Astra 的「客户端」页面启用对应客户端，或在「令牌」页面复制令牌。");
        if (GatewayTokens.IsLegacyKey(key))
            throw new GatewayException(401, "authentication_error", "旧版客户端密钥已失效（Astra 已改用令牌）。请在 Astra 的「客户端」页面重新启用该客户端。");
        if (!GatewayTokens.TryParse(key, out var secret, out var kind))
            throw new GatewayException(401, "authentication_error", "这不是 Astra 令牌。请在 Astra 的「客户端」页面启用客户端，由 Astra 写入令牌，或在「令牌」页面复制令牌。");

        var prefix = GatewayTokens.PrefixOf(secret);
        var token = (await db.Tokens.FindByPrefixAsync(prefix, ct)).FirstOrDefault(t => GatewayTokens.Verify(secret, t.KeyHash));
        if (token is null)
            throw new GatewayException(401, "authentication_error", "Astra 令牌无效或已重置。请在 Astra 中重新启用该客户端，或在「令牌」页面复制最新的令牌。");
        if (!token.Enabled)
            throw new GatewayException(403, "permission_error", $"令牌 {token.Name} 已停用。");

        if (kind is null)
        {
            if (token.ProviderId is null)
                throw new GatewayException(503, "api_error", $"令牌 {token.Name} 没有设置默认提供商，无法直接调用。请在 Astra 的「令牌」页面为它选择提供商，或通过已启用的客户端使用。");
            var direct = await db.Providers.GetAsync(token.ProviderId, ct)
                         ?? throw new GatewayException(503, "api_error", $"令牌 {token.Name} 的默认提供商不存在，请在 Astra 的「令牌」页面重新选择。");
            RequireUsable(direct);
            return new GatewayRoute(token, null, token.AccountId, direct) { Targets = [new RouteTarget(direct, token.AccountId)] };
        }

        var client = await db.Clients.GetAsync(kind, ct);
        if (client is not { Enabled: true })
            throw new GatewayException(403, "permission_error", $"客户端 {kind} 在 Astra 中未启用。");
        var bindings = await db.Clients.ListBindingsAsync(client.Kind, ct);
        if (bindings.Count == 0)
            throw new GatewayException(503, "api_error", $"客户端 {client.Kind} 还没有选择提供商。请在 Astra 的「客户端」页面选择一个提供商。");
        var targets = await routing.TargetsAsync(bindings, ct);
        if (targets.Count == 0)
        {
            // Nothing usable: report the primary's problem, exactly as with a single binding.
            var primary = await db.Providers.GetAsync(bindings[0].ProviderId, ct)
                          ?? throw new GatewayException(503, "api_error", "绑定的提供商不存在，请在 Astra 中重新选择提供商。");
            RequireUsable(primary);
            throw new GatewayException(503, "api_error", $"客户端 {client.Kind} 绑定的提供商都不可用，请在 Astra 的「客户端」页面检查提供商。");
        }
        return new GatewayRoute(token, client, targets[0].AccountId, targets[0].Provider) { Targets = targets };
    }

    /// <summary>
    /// Picks the provider for the requested model among the route's targets (see <see cref="ClientRouting"/>): the
    /// first serving <paramref name="modelId"/> (a <see cref="GatewayRoute.ClientModelFor"/> id) as an enabled model or
    /// through its model mapping, else the primary. A route with a single target is returned unchanged.
    /// </summary>
    public async Task<GatewayRoute> ForModelAsync(GatewayRoute route, string modelId, bool isClaudeCode, CancellationToken ct)
    {
        if (route.Targets.Count < 2) return route;
        var match = await routing.MatchAsync(route.Targets, modelId, isClaudeCode, ct);
        return match is null ? route : route with { Provider = match.Provider, AccountId = match.AccountId };
    }

    private static Provider RequireUsable(Provider provider)
    {
        if (!provider.Enabled)
            throw new GatewayException(503, "api_error", $"提供商 {provider.Name} 已停用。");
        if (provider.Endpoints.Count == 0)
            throw new GatewayException(503, "api_error", $"提供商 {provider.Name} 没有配置接口地址。");
        return provider;
    }

    /// <summary>
    /// Plan §6.2: the endpoint speaking the inbound protocol (pass-through), else the first preferred protocol the
    /// provider has an address for, else its first endpoint. A preferred protocol without a configured address
    /// cannot be used: it is skipped, so a request in that protocol gets translated instead.
    /// </summary>
    public static ProviderEndpoint SelectEndpoint(Provider provider, ApiProtocol inbound) =>
        provider.EndpointFor(inbound)
        ?? provider.PreferredUpstreamProtocols.Select(provider.EndpointFor).FirstOrDefault(e => e is not null)
        ?? provider.Endpoints[0];

    /// <summary>
    /// Endpoint choice for one model, honouring per-model upstream constraints. Some upstreams serve a model
    /// over only one of their APIs — GitHub Copilot's Claude models are Messages-only, so a Responses request
    /// for them must be translated rather than passed through (the Responses endpoint answers
    /// <c>model_not_supported</c>). <paramref name="supportedProtocols"/> comes from the provider model row
    /// (<c>upstream_protocols</c>, learned from the upstream model list); null/empty means "no constraint".
    /// Explicit constraints never fall back to an unsupported protocol when its endpoint is missing.
    /// </summary>
    public static ProviderEndpoint SelectEndpoint(Provider provider, ApiProtocol inbound, IReadOnlyList<ApiProtocol>? supportedProtocols)
    {
        if (supportedProtocols is not { Count: > 0 }) return SelectEndpoint(provider, inbound);
        // The inbound protocol works as-is when the model allows it and the provider has an address for it.
        if (supportedProtocols.Contains(inbound) && provider.EndpointFor(inbound) is { } direct) return direct;
        // Otherwise translate into the model's protocol: its own order first, then the provider's preference.
        foreach (var protocol in supportedProtocols)
            if (provider.EndpointFor(protocol) is { } endpoint) return endpoint;
        foreach (var protocol in provider.PreferredUpstreamProtocols)
            if (supportedProtocols.Contains(protocol) && provider.EndpointFor(protocol) is { } preferred) return preferred;
        throw new GatewayException(503, "api_error",
            $"提供商 {provider.Name} 未配置该模型支持的上游协议端点（{string.Join(", ", supportedProtocols.Select(p => p.ToId()))}）。请在提供商设置中添加对应端点。");
    }

    /// <summary>
    /// The provider's preferred protocols without an address, in priority order. None of them can ever be selected,
    /// so a request in one of them is translated into a lower-preference protocol instead of passing through.
    /// </summary>
    public static IReadOnlyList<ApiProtocol> PreferredProtocolsWithoutEndpoint(Provider provider) =>
        provider.PreferredUpstreamProtocols.Where(p => provider.EndpointFor(p) is null).ToList();

    /// <summary>
    /// Same for this inbound protocol only: it is preferred by the provider, yet arrives with no address to send it
    /// to. Null when there is nothing to report for <paramref name="inbound"/>.
    /// </summary>
    public static ApiProtocol? MissingEndpointFor(Provider provider, ApiProtocol inbound) =>
        PreferredProtocolsWithoutEndpoint(provider).Contains(inbound) ? inbound : null;
}
