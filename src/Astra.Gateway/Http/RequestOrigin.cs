using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Http;

/// <summary>
/// Where a request came from, as the host's security middleware decided it: "remote" means it reached Astra through a
/// Cloudflare Tunnel (Astra's own or any Cloudflare proxy in front of it) rather than from this machine or its network
/// directly. The gateway never serves a remote request without an Astra token, whatever <c>RequireGatewayKey</c> says.
/// </summary>
public static class RequestOrigin
{
    private const string RemoteKey = "astra.remote";

    public static void MarkRemote(HttpContext ctx) => ctx.Items[RemoteKey] = true;

    public static bool IsRemote(HttpContext ctx) => ctx.Items.TryGetValue(RemoteKey, out var v) && v is true;
}
