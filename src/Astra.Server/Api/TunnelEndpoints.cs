using Astra.Core;
using Astra.Core.Clients;
using Astra.Gateway.Http;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;
using Astra.Server.Tunnel;

namespace Astra.Server.Api;

/// <summary>/api/tunnel: remote access through a Cloudflare Tunnel (<see cref="TunnelService"/>).</summary>
public static class TunnelEndpoints
{





    public static void MapTunnelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tunnel").AddEndpointFilter<AdminApiErrorFilter>();

        group.MapGet("", (TunnelService tunnel, SettingsService settings, CloudflaredRuntime runtime, ServerOptions server) =>
            Results.Ok(ToDto(tunnel, settings.Current.Tunnel, runtime, server)));

        group.MapPut("", async (TunnelConfigRequest body, HttpContext http, TunnelService tunnel, SettingsService settings,
            CloudflaredRuntime runtime, ServerOptions server, ISecretProtector secrets, CancellationToken ct) =>
        {
            if (body.Mode is not null && body.Mode is not (TunnelSettings.ModeQuick or TunnelSettings.ModeNamed))
                throw new AdminApiException(400, "mode must be quick or named");
            string? hostname = null;
            if (body.Hostname is not null && body.Hostname.Trim().Length > 0)
                hostname = NormalizeHostname(body.Hostname) ?? throw new AdminApiException(400, "hostname must be a domain name, such as astra.example.com");
            if (body.AllowAdmin == true && string.IsNullOrEmpty(server.AdminPasswordHash))
                throw new AdminApiException(409, "Set an admin password before opening the admin UI to remote access");
            if (body.DownloadSource is not null && body.DownloadSource is not (TunnelSettings.SourceAuto or TunnelSettings.SourceGitHub
                    or TunnelSettings.SourceAstra or TunnelSettings.SourceCustom))
                throw new AdminApiException(400, "downloadSource must be auto, github, astra or custom");
            string? baseUrl = null;
            if (body.DownloadBaseUrl is { } rawBase && rawBase.Trim().Length > 0)
            {
                // Any http(s) address will do: what it serves is checked against the pinned checksums.
                if (!Uri.TryCreate(rawBase.Trim(), UriKind.Absolute, out var b) || b.Scheme is not ("http" or "https"))
                    throw new AdminApiException(400, "downloadBaseUrl must be an http(s) address");
                baseUrl = rawBase.Trim().TrimEnd('/');
            }
            string? installed = null;
            if (body.CloudflaredPath is { } rawPath)
            {
                // Choosing a program for Astra to run is for this machine only, never through a tunnel.
                if (!RequestOrigin.IsLocal(http)) throw new AdminApiException(403, "The cloudflared program can only be chosen on this machine");
                if (rawPath.Trim().Length > 0)
                {
                    installed = rawPath.Trim();
                    if (!File.Exists(installed)) throw new AdminApiException(400, $"{installed} does not exist");
                    if (runtime.ProbeVersion(installed) is null) throw new AdminApiException(400, $"{installed} does not run as cloudflared (cloudflared --version)");
                }
            }

            var before = settings.Current.Tunnel;
            var next = await settings.UpdateAsync(s =>
            {
                if (body.Mode is not null) s.Tunnel.Mode = body.Mode;
                if (body.Hostname is not null) s.Tunnel.Hostname = hostname;
                if (body.Token is not null) s.Tunnel.TokenProtected = body.Token.Trim() is { Length: > 0 } token ? secrets.Protect(token) : null;
                if (body.AllowAdmin is { } allow) s.Tunnel.AllowAdmin = allow;
                if (body.DownloadSource is not null) s.Tunnel.DownloadSource = body.DownloadSource;
                if (body.DownloadBaseUrl is not null) s.Tunnel.DownloadBaseUrl = baseUrl;
                if (body.CloudflaredPath is not null) s.Tunnel.CloudflaredPath = installed;
            }, ct);
            if (next.Tunnel.DownloadSource == TunnelSettings.SourceCustom && next.Tunnel.DownloadBaseUrl is null)
            {
                await settings.UpdateAsync(s => s.Tunnel.DownloadSource = before.DownloadSource, ct);
                throw new AdminApiException(400, "A custom download source needs its address");
            }

            // What cloudflared runs with changed: start it again (AllowAdmin is read per request and needs no restart).
            var t = next.Tunnel;
            if (t.Mode != before.Mode || t.Hostname != before.Hostname || t.TokenProtected != before.TokenProtected
                || t.CloudflaredPath != before.CloudflaredPath)
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

        group.MapPost("/cloudflared/detect", (CloudflaredRuntime runtime) =>
            Results.Ok(runtime.Detect() is { } found ? new DetectedCloudflaredDto(found.Path, found.Version) : new DetectedCloudflaredDto(null, null)));

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
            runtime.Status, new TunnelDownloadDto(s.DownloadSource, s.DownloadBaseUrl, s.CloudflaredPath), new TunnelChecksDto(!string.IsNullOrEmpty(server.AdminPasswordHash), tunnel.Validate(s)), state.Log);
    }
}
