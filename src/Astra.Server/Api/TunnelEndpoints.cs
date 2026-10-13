using Astra.Core;
using Astra.Core.Clients;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;
using Astra.Server.Tunnel;

namespace Astra.Server.Api;

/// <summary>/api/tunnel: remote access through a Cloudflare Tunnel (<see cref="TunnelService"/>).</summary>
public static class TunnelEndpoints
{
    /// <param name="Supported">False inside a container (cloudflared runs as a sidecar there).</param>
    /// <param name="Enabled">Whether remote access is switched on (and comes back after a restart).</param>
    /// <param name="State">off | downloading | starting | connected | reconnecting.</param>
    /// <param name="PublicUrl">The address remote clients use (the quick tunnel's once known, a named tunnel's hostname).</param>
    /// <param name="OriginUrl">What a named tunnel's public hostname must point at in the Cloudflare dashboard.</param>
    /// <param name="Checks">What starting needs; <see cref="TunnelChecksDto.Blocker"/> says why it cannot start now.</param>
    public sealed record TunnelStatusDto(
        bool Supported, bool Enabled, string State, string? PublicUrl, string? Error, DateTimeOffset? ConnectedAt,
        string Mode, string? Hostname, bool HasToken, bool AllowAdmin, string OriginUrl,
        CloudflaredStatus Cloudflared, TunnelChecksDto Checks, List<string> Log);

    /// <param name="PasswordSet">An admin password exists (needed only when the admin UI is opened remotely).</param>
    /// <param name="Blocker">Why the tunnel cannot start with the current settings; null when it can.</param>
    public sealed record TunnelChecksDto(bool PasswordSet, string? Blocker);

    /// <summary>Partial update; a null field is left as it is. <c>Token</c> is write-only and "" clears it.</summary>
    public sealed record TunnelConfigRequest(string? Mode, string? Hostname, string? Token, bool? AllowAdmin);

    public static void MapTunnelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tunnel").AddEndpointFilter<AdminApiErrorFilter>();

        group.MapGet("", (TunnelService tunnel, SettingsService settings, CloudflaredRuntime runtime, ServerOptions server) =>
            Results.Ok(ToDto(tunnel, settings.Current.Tunnel, runtime, server)));

        group.MapPut("", async (TunnelConfigRequest body, TunnelService tunnel, SettingsService settings, CloudflaredRuntime runtime,
            ServerOptions server, ISecretProtector secrets, CancellationToken ct) =>
        {
            if (body.Mode is not null && body.Mode is not (TunnelSettings.ModeQuick or TunnelSettings.ModeNamed))
                throw new AdminApiException(400, "mode must be quick or named");
            string? hostname = null;
            if (body.Hostname is not null && body.Hostname.Trim().Length > 0)
                hostname = NormalizeHostname(body.Hostname) ?? throw new AdminApiException(400, "hostname must be a domain name, such as astra.example.com");
            if (body.AllowAdmin == true && string.IsNullOrEmpty(server.AdminPasswordHash))
                throw new AdminApiException(409, "Set an admin password before opening the admin UI to remote access");

            var before = settings.Current.Tunnel;
            var next = await settings.UpdateAsync(s =>
            {
                if (body.Mode is not null) s.Tunnel.Mode = body.Mode;
                if (body.Hostname is not null) s.Tunnel.Hostname = hostname;
                if (body.Token is not null) s.Tunnel.TokenProtected = body.Token.Trim() is { Length: > 0 } token ? secrets.Protect(token) : null;
                if (body.AllowAdmin is { } allow) s.Tunnel.AllowAdmin = allow;
            }, ct);

            // What cloudflared runs with changed: start it again (AllowAdmin is read per request and needs no restart).
            var t = next.Tunnel;
            if (t.Mode != before.Mode || t.Hostname != before.Hostname || t.TokenProtected != before.TokenProtected)
            {
                if (t.Enabled && tunnel.Validate(t) is null) tunnel.RestartIfRunning();
                else if (t.Enabled) await tunnel.DisableAsync(ct);
            }
            return Results.Ok(ToDto(tunnel, settings.Current.Tunnel, runtime, server));
        });

        group.MapPost("/start", async (TunnelService tunnel, SettingsService settings, CloudflaredRuntime runtime, ServerOptions server,
            CancellationToken ct) =>
        {
            try
            {
                await tunnel.EnableAsync(ct);
            }
            catch (InvalidOperationException e)
            {
                throw new AdminApiException(409, e.Message);
            }
            return Results.Ok(ToDto(tunnel, settings.Current.Tunnel, runtime, server));
        });

        group.MapPost("/stop", async (TunnelService tunnel, SettingsService settings, CloudflaredRuntime runtime, ServerOptions server,
            CancellationToken ct) =>
        {
            await tunnel.DisableAsync(ct);
            return Results.Ok(ToDto(tunnel, settings.Current.Tunnel, runtime, server));
        });
    }

    /// <summary>A bare hostname from what was typed (a pasted https://host/path is accepted); null when it isn't one.</summary>
    internal static string? NormalizeHostname(string raw)
    {
        var text = raw.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains('.'))
            return null;
        return uri.IdnHost.ToLowerInvariant();
    }

    private static TunnelStatusDto ToDto(TunnelService tunnel, TunnelSettings s, CloudflaredRuntime runtime, ServerOptions server)
    {
        var state = tunnel.Snapshot();
        return new TunnelStatusDto(
            tunnel.Supported, s.Enabled, state.State, state.Url, state.Error, state.ConnectedAt,
            s.Mode, s.Hostname, !string.IsNullOrEmpty(s.TokenProtected), s.AllowAdmin, tunnel.OriginUrl,
            runtime.Status, new TunnelChecksDto(!string.IsNullOrEmpty(server.AdminPasswordHash), tunnel.Validate(s)), state.Log);
    }
}
