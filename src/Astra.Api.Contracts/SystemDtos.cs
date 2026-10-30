using Astra.Core;

namespace Astra.Api.Contracts;

/// <summary>Body of <c>GET /api/health</c>.</summary>
public sealed record StatusDto(string Status);

/// <summary>Body of <c>GET /api/version</c>: the server build and the admin API protocol version.</summary>
public sealed record VersionDto(string Version, string ApiVersion);

/// <summary>Payload of /api/update/status and /api/update/check (the UI caches it under one key).</summary>
public sealed record UpdateStatusDto(
    string Current, string? Available, DateTimeOffset? LastCheckAt, string? Notes, string? Error,
    string Channel, bool AutoCheck, bool FeedConfigured);

/// <summary>Error body of the admin API and the gateway: <c>{ "error", "details"? }</c>. <c>Details</c> is object-typed and written by its runtime type, so every details shape is a named record registered in <see cref="ContractsJsonContext"/> (an anonymous object has no generated metadata under Native AOT).</summary>
public sealed record ErrorBody(string Error, object? Details = null);

/// <summary><c>details</c> of an error caused by an upstream HTTP status.</summary>
public sealed record UpstreamStatusDetailsDto(int HttpStatus);

/// <summary><c>details</c> of a refusal because clients are still bound.</summary>
public sealed record BoundClientsDetailsDto(IReadOnlyList<string> BoundClients);

/// <summary>The plain <c>{ "error" }</c> envelope shared by several endpoints.</summary>
public sealed record ErrorOnlyDto(string Error);

/// <summary><c>{ "error", "status" }</c> envelope.</summary>
public sealed record ErrorStatusDto(string Error, string Status);

/// <summary><c>{ "error", "needsVerification" }</c> envelope.</summary>
public sealed record ErrorVerificationDto(string Error, bool NeedsVerification);

/// <summary><c>{ "removed" }</c> envelope of a delete.</summary>
public sealed record RemovedDto(bool Removed);

/// <summary>Kinds whose client configs were re-applied.</summary>
public sealed record ReappliedDto(IReadOnlyList<string> Updated);

/// <summary><c>{ "signedIn" }</c> envelope of the login endpoints.</summary>
public sealed record SignedInDto(bool SignedIn);

/// <param name="Required">Whether this request needs a signed-in session (not from this machine: from the network, or through a tunnel).</param>
/// <param name="Remote">Whether this request came through Cloudflare (a tunnel) rather than from this machine or network.</param>
/// <param name="AdminBlocked">Remote, and the tunnel settings keep the admin UI local: signing in would not help.</param>
/// <summary>Body of <c>GET /api/auth/status</c>.</summary>
public sealed record AuthStatusDto(bool Required, bool SignedIn, bool Remote = false, bool AdminBlocked = false);

/// <summary>Body of <c>POST /api/auth/login</c>.</summary>
public sealed record LoginRequest(string Password);

/// <summary>Body of <c>POST /api/auth/password</c>.</summary>
public sealed record SetPasswordRequest(string Password);

/// <summary>
/// The listen address. <paramref name="Host"/> is what this process is bound to and <paramref name="ConfiguredHost"/>
/// what config.json asks for at the next start (they differ until a restart); with <paramref name="HostFromEnvironment"/>
/// ASTRA_HOST decides and config.json does not apply. <paramref name="NetworkUrls"/> are the base URLs other machines
/// can use while listening beyond loopback (one per network interface for a wildcard host).
/// </summary>
public sealed record ListenDto(string Host, int Port, string ConfiguredHost, bool HostFromEnvironment, bool PasswordSet, List<string> NetworkUrls);

/// <summary>Body of <c>PUT /api/server/listen</c>.</summary>
public sealed record ListenRequest(string Host);

/// <param name="Supported">False in a container, on Linux without systemd, or where Astra has no login entry.</param>
/// <param name="Enabled">An entry starts the server at login (written here or by <c>astra autostart enable</c>).</param>
/// <param name="Mechanism">launchd | systemd | registry | none.</param>
/// <param name="Unsupported">container | no-systemd | platform; null when supported.</param>
/// <param name="Location">Where the entry lives (plist, unit file, Run key value, or the CLI's old scheduled task).</param>
/// <param name="Program">The program the entry starts, when it can be read back.</param>
/// <param name="CanChange">The caller is on this machine: only it may add or remove the entry.</param>
public sealed record AutostartDto(
    bool Supported, bool Enabled, string Mechanism, string? Unsupported, string? Location, string? Program, bool CanChange);

/// <summary>Body of <c>PUT /api/server/autostart</c>.</summary>
public sealed record AutostartRequest(bool Enabled);

/// <param name="Supported">False inside a container (cloudflared runs as a sidecar there).</param>
/// <param name="Enabled">Whether remote access is switched on (and comes back after a restart).</param>
/// <param name="State">off | downloading | starting | connected | reconnecting.</param>
/// <param name="PublicUrl">The address remote clients use (the quick tunnel's once known, a named tunnel's hostname).</param>
/// <param name="OriginUrl">What a named tunnel's public hostname must point at in the Cloudflare dashboard.</param>
/// <param name="Checks">What starting needs; <see cref="TunnelChecksDto.Blocker"/> says why it cannot start now.</param>
public sealed record TunnelStatusDto(
    bool Supported, bool Enabled, string State, string? PublicUrl, string? Error, DateTimeOffset? ConnectedAt,
    string Mode, string? Hostname, bool HasToken, bool AllowAdmin, string OriginUrl,
    CloudflaredStatus Cloudflared, TunnelDownloadDto Download, TunnelChecksDto Checks, List<string> Log);

/// <param name="PasswordSet">An admin password exists (needed only when the admin UI is opened remotely).</param>
/// <param name="Blocker">Why the tunnel cannot start with the current settings; null when it can.</param>
public sealed record TunnelChecksDto(bool PasswordSet, string? Blocker);

/// <param name="DownloadSource">auto | github | astra | custom.</param>
/// <param name="DownloadBaseUrl">The custom source's base ({base}/{version}/{asset}); used with DownloadSource "custom".</param>
/// <param name="CloudflaredPath">An installed cloudflared to run; "" goes back to the downloaded one.</param>
public sealed record TunnelDownloadDto(string DownloadSource, string? DownloadBaseUrl, string? CloudflaredPath);

/// <summary>Partial update; a null field is left as it is. <c>Token</c> is write-only and "" clears it.</summary>
public sealed record TunnelConfigRequest(string? Mode, string? Hostname, string? Token, bool? AllowAdmin,
    string? DownloadSource = null, string? DownloadBaseUrl = null, string? CloudflaredPath = null);

/// <summary>A cloudflared found on PATH (POST /api/tunnel/cloudflared/detect); nothing is saved.</summary>
public sealed record DetectedCloudflaredDto(string? Path, string? Version);

/// <summary>cloudflared as the remote access page shows it.</summary>
/// <param name="Path">The cloudflared in use, null when none is at hand yet.</param>
/// <param name="Version">Its version: the pinned one for the downloaded copy, what an installed one reports (null if it doesn't).</param>
/// <param name="Downloading">Whether it is being downloaded now.</param>
/// <param name="Progress">0–1 while downloading with a known size.</param>
/// <param name="Error">Why the last download failed (every source tried, "host: reason; …").</param>
/// <param name="Asset">This machine's release asset; null when cloudflared has none.</param>
/// <param name="Source">The host being downloaded from right now.</param>
/// <param name="Installed">Whether <see cref="Path"/> is the user's own cloudflared rather than the downloaded copy.</param>
/// <param name="FromEnvironment">Whether ASTRA_CLOUDFLARED chose it (the setting then has no effect).</param>
public sealed record CloudflaredStatus(string? Path, string? Version, bool Downloading, double? Progress, string? Error, string? Asset,
    string? Source, bool Installed, bool FromEnvironment);

/// <summary>Admin API payload: settings.</summary>
public sealed record SettingsDto(
    string Locale, bool DebugBodies, int BodyRetentionDays, int? RequestRetentionDays, EffortBudgets EffortBudgets,
    int StreamIdleTimeoutSec, string UpdateChannel, bool UpdateAutoCheck,
    int Port, string Host, string DataDir, string GatewayBaseUrl, int QuotaAutoIntervalMinutes, int SubscriptionQuotaIntervalMinutes,
    string ProxyMode, string? ProxyUrl, string? ProxyUsername, bool HasProxyPassword, string? ProxyBypass,
    bool RequireGatewayKey, string? PluginMarketUrl, string PluginMarketDefaultUrl, string? UsageTimeZone,
    string EffectiveUsageTimeZone, string RequestCompression, bool TraceContent = false);
