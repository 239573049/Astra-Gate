using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Pipeline;

/// <summary>A gateway-level rejection, rendered in the inbound protocol's error format.</summary>
public sealed class GatewayException(int status, string type, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Type { get; } = type;
}

/// <summary>Who is calling and where the request goes.</summary>
public sealed record GatewayRoute(ClientRecord Client, ClientBinding Binding, Provider Provider)
{
    /// <summary>
    /// The model sent upstream. The gateway passes model names through, except for Claude Desktop, whose role ids
    /// are mapped to provider models (plan §7.5).
    /// </summary>
    public string UpstreamModelFor(string requested) =>
        Client.Kind == ClientKinds.ClaudeDesktop ? ClaudeDesktopRoles.Map(ClaudeDesktopRoles.Parse(Client.ExtraJson), requested) : requested;
}

/// <summary>
/// Authenticates the local client key (plan §6.1) and resolves the bound provider.
/// Keys are looked up by prefix and compared by hash in constant time.
/// </summary>
public sealed class GatewayRouter(AstraDatabase db)
{
    public async Task<GatewayRoute> ResolveAsync(HttpRequest request, CancellationToken ct)
    {
        var key = LocalKeys.Extract(request);
        if (string.IsNullOrEmpty(key))
            throw new GatewayException(401, "authentication_error", "缺少 Astra 客户端密钥（Authorization: Bearer / x-api-key / x-goog-api-key / ?key=）。请在 Astra 的「客户端」页面启用对应客户端。");
        if (!LocalKeys.LooksLikeLocalKey(key))
            throw new GatewayException(401, "authentication_error", "这不是 Astra 客户端密钥。请在 Astra 的「客户端」页面启用客户端，由 Astra 写入密钥。");

        var prefix = LocalKeys.PrefixOf(key);
        var client = (await db.Clients.ListAsync(ct)).FirstOrDefault(c => c.LocalKeyPrefix == prefix && LocalKeys.Verify(key, c.LocalKeyHash));
        if (client is null)
            throw new GatewayException(401, "authentication_error", "Astra 客户端密钥无效或已轮换。请在 Astra 中重新启用该客户端。");
        if (!client.Enabled)
            throw new GatewayException(403, "permission_error", $"客户端 {client.Kind} 在 Astra 中未启用。");

        var binding = await db.Clients.GetBindingAsync(client.Kind, ct)
                      ?? throw new GatewayException(503, "api_error", $"客户端 {client.Kind} 还没有选择提供商。请在 Astra 的「客户端」页面选择一个提供商。");
        var provider = await db.Providers.GetAsync(binding.ProviderId, ct)
                       ?? throw new GatewayException(503, "api_error", "绑定的提供商不存在，请在 Astra 中重新选择提供商。");
        if (!provider.Enabled)
            throw new GatewayException(503, "api_error", $"提供商 {provider.Name} 已停用。");
        if (provider.Endpoints.Count == 0)
            throw new GatewayException(503, "api_error", $"提供商 {provider.Name} 没有配置接口地址。");
        return new GatewayRoute(client, binding, provider);
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
