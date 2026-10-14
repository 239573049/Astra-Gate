using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Http;

/// <summary>
/// Where a request came from, as the host's security middleware decided it: "remote" means it reached Astra through a
/// Cloudflare Tunnel (Astra's own or any Cloudflare proxy in front of it); "network" means it came straight from another
/// machine (the server listens beyond loopback, e.g. 0.0.0.0 for the LAN, or a container's published port). Anything
/// else is this machine. The gateway never serves a remote or network request without an Astra token, whatever
/// <c>RequireGatewayKey</c> says.
/// </summary>
public static class RequestOrigin
{
    private const string RemoteKey = "astra.remote";
    private const string NetworkKey = "astra.network";

    public static void MarkRemote(HttpContext ctx) => ctx.Items[RemoteKey] = true;

    public static bool IsRemote(HttpContext ctx) => ctx.Items.TryGetValue(RemoteKey, out var v) && v is true;

    public static void MarkNetwork(HttpContext ctx) => ctx.Items[NetworkKey] = true;

    public static bool IsNetwork(HttpContext ctx) => ctx.Items.TryGetValue(NetworkKey, out var v) && v is true;

    /// <summary>From this machine: neither through a tunnel nor from another machine on the network.</summary>
    public static bool IsLocal(HttpContext ctx) => !IsRemote(ctx) && !IsNetwork(ctx);
}
