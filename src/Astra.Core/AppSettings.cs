namespace Astra.Core;

/// <summary>Runtime-editable settings stored in the database (key <see cref="StorageKey"/>).</summary>
public sealed class AppSettings
{
    public const string StorageKey = "app";

    /// <summary>Default language for stored billing descriptions: "zh" | "en".</summary>
    public string Locale { get; set; } = "zh";

    /// <summary>Capture client/upstream request and response bodies under bodies/.</summary>
    public bool DebugBodies { get; set; }

    /// <summary>
    /// Record conversation content in the trace (链路追踪): each tool call's arguments, the start of the user's prompt and of
    /// the reply. Off by default; names, counts, timings and attempts are recorded either way.
    /// </summary>
    public bool TraceContent { get; set; }

    public int BodyRetentionDays { get; set; } = 7;

    /// <summary>Null = keep request logs forever.</summary>
    public int? RequestRetentionDays { get; set; }

    /// <summary>
    /// Whether gateway calls must present an Astra token. Off = a call without a (valid) token is served as a direct
    /// call through the default token; a valid token (with or without a client suffix) still routes as usual.
    /// </summary>
    public bool RequireGatewayKey { get; set; } = true;

    public EffortBudgets EffortBudgets { get; set; } = new();
    public int StreamIdleTimeoutSec { get; set; } = 300;

    /// <summary>
    /// Keep the machine from idle sleep while gateway requests are in flight (Astra.Server's KeepAwakeService; issue #14).
    /// Off by default; idle sleep only — the display may still turn off and closing the lid still sleeps.
    /// </summary>
    public bool PreventSleep { get; set; }

    /// <summary>Release feed channel for update checks: "stable" (default) | "preview" (opt-in previews; see UpdateChannels).</summary>
    public string UpdateChannel { get; set; } = "stable";

    /// <summary>Whether the server polls the update feed on a schedule (UpdateCheckWorker).</summary>
    public bool UpdateAutoCheck { get; set; } = true;

    /// <summary>Update feed base URL override; null = built-in default, empty = checks disabled.</summary>
    public string? UpdateFeedUrl { get; set; }

    /// <summary>
    /// Plugin market registry address override; null = the Astra site's (the default), empty = only the copy fetched
    /// last and the built-in one. The ASTRA_PLUGIN_MARKET environment variable wins over it.
    /// </summary>
    public string? PluginMarketUrl { get; set; }

    /// <summary>
    /// Background balance / quota refresh for API-key providers, in minutes (ProviderQuotaWorker).
    /// 0 turns the background refresh off; a provider's own <c>settings.quota.intervalMinutes</c> wins.
    /// </summary>
    public int QuotaAutoIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Background quota refresh for subscription accounts, in minutes (ProviderQuotaWorker via SubscriptionQuotaSync).
    /// 0 turns it off; otherwise at least <c>SubscriptionQuotaSync.MinIntervalMinutes</c> (the upstream usage endpoints rate-limit).
    /// </summary>
    public int SubscriptionQuotaIntervalMinutes { get; set; } = 30;

    /// <summary>
    /// Time zone (IANA / Windows id) whose local days, weeks and months token budgets reset on and token daily usage is
    /// counted in; null = the process's local zone (UTC in most containers).
    /// </summary>
    public string? UsageTimeZone { get; set; }

    /// <summary>
    /// Outbound proxy (<see cref="SystemProxy"/>): "system" = HTTPS_PROXY-style variables, then the OS setting;
    /// "custom" = <see cref="ProxyUrl"/>; "direct" = never proxy. A provider's own httpProxy still wins.
    /// </summary>
    public string ProxyMode { get; set; } = "system";

    /// <summary>Custom proxy, scheme + host + port only (http / https / socks5); credentials are kept apart.</summary>
    public string? ProxyUrl { get; set; }

    public string? ProxyUsername { get; set; }

    /// <summary>Proxy password protected with ISecretProtector; the API only reports whether one is set.</summary>
    public string? ProxyPasswordProtected { get; set; }

    /// <summary>NO_PROXY-style hosts (comma separated) that stay direct in custom mode; loopback always does.</summary>
    public string? ProxyBypass { get; set; }

    /// <summary>
    /// Request body coding for providers that compress (their <c>settings.request_compression</c>, e.g. the ChatGPT
    /// subscription's zstd): "default" = each provider's own; "zstd" | "gzip" | "br" | "deflate" = that one instead;
    /// "none" = every body goes upstream as is. Providers that do not compress are never affected.
    /// </summary>
    public string RequestCompression { get; set; } = "default";

    /// <summary>
    /// Largest request body the gateway accepts, in MiB (issue #37), as sent and once its Content-Encoding is undone; a
    /// bigger one is a 413. 1-1024, default 64. (The relay has its own, <c>ASTRA_RELAY_MAX_REQUEST_BODY_BYTES</c>.)
    /// </summary>
    public int MaxRequestBodyMb { get; set; } = 64;

    /// <summary>
    /// Provider fallback for clients (issue #28): a client bound to several providers retries a request that failed before
    /// anything reached it — rate limit, rejected credentials, 5xx, network error — on the next bound provider that serves the
    /// model, in binding order. On by default; off = the first provider serving the model answers, whatever it returns.
    /// </summary>
    public bool ClientProviderFailover { get; set; } = true;

    /// <summary>
    /// Provider-qualified model ids (issue #21): the model lists clients see carry each provider's alias after the
    /// <see cref="ModelProviderSeparator"/> (<c>glm-5.3@zcode</c>), so providers serving the same model id can be told apart, and a
    /// request for such an id goes to that provider. Off = ids as they are, the first provider serving one wins.
    /// </summary>
    public bool ProviderQualifiedModels { get; set; }

    /// <summary>Between a model id and its provider's alias while <see cref="ProviderQualifiedModels"/> is on (default "@").</summary>
    public string ModelProviderSeparator { get; set; } = "@";

    /// <summary>
    /// Which ids carry the alias while <see cref="ProviderQualifiedModels"/> is on (<see cref="Routing.ModelQualifier"/> scopes):
    /// "shared" (the default) = only ids two or more of the caller's providers serve, the rest stay bare; "all" = every id.
    /// </summary>
    public string ModelQualifyScope { get; set; } = "shared";

    /// <summary>
    /// Sidebar entries of the web console the user hid (ids from <see cref="NavItems.Hideable"/>). Only the menu changes: a hidden
    /// page still opens by its address. Empty = every entry shown.
    /// </summary>
    public List<string> HiddenNav { get; set; } = [];

    /// <summary>Remote access through a Cloudflare Tunnel (Astra.Server's TunnelService).</summary>
    public TunnelSettings Tunnel { get; set; } = new();

    /// <summary>The MCP gateway (<c>/mcp</c>, plan mcp-gateway, GitHub issue #29).</summary>
    public McpGatewaySettings McpGateway { get; set; } = new();

    public AppSettings Clone() => Json.Deserialize<AppSettings>(Json.Serialize(this))!;
}

/// <summary>
/// The MCP gateway: one endpoint (<c>/mcp</c>) in front of the upstream MCP servers an admin registered. Off by default: with
/// <see cref="Enabled"/> unset the endpoint answers 404, so a fresh install exposes nothing new.
/// </summary>
public sealed class McpGatewaySettings
{
    /// <summary>Whether <c>/mcp</c> and <c>/mcp/{slug}</c> answer.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Browser origins allowed to call the endpoint (a request with an <c>Origin</c> header that is not listed, <c>null</c> included,
    /// gets 403: the DNS-rebinding defence the MCP spec asks of an HTTP server). A request without <c>Origin</c> — every non-browser
    /// client — is not affected. Empty = no browser origin is allowed.
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];
}

/// <summary>
/// Remote access through a Cloudflare Tunnel run by Astra (cloudflared as a child process). Remote requests always need
/// an Astra token on the gateway; the admin UI is reachable remotely only with <see cref="AllowAdmin"/> and a signed-in
/// session.
/// </summary>
public sealed class TunnelSettings
{
    public const string ModeQuick = "quick";
    public const string ModeNamed = "named";

    /// <summary>Whether the tunnel should run; the server starts it again on startup while this is set.</summary>
    public bool Enabled { get; set; }

    /// <summary>"quick" = a temporary *.trycloudflare.com address (no account, no SSE); "named" = the user's tunnel token.</summary>
    public string Mode { get; set; } = ModeQuick;

    /// <summary>The public hostname a named tunnel routes to this server (configured in the Cloudflare dashboard).</summary>
    public string? Hostname { get; set; }

    /// <summary>The named tunnel's token, protected with ISecretProtector; the API only reports whether one is set.</summary>
    public string? TokenProtected { get; set; }

    /// <summary>Whether the admin UI / API answers remote requests (behind the admin password); off = gateway only.</summary>
    public bool AllowAdmin { get; set; }

    public const string SourceAuto = "auto";
    public const string SourceGitHub = "github";
    public const string SourceAstra = "astra";
    public const string SourceCustom = "custom";

    /// <summary>
    /// Where cloudflared is downloaded from: "auto" = GitHub, then the Astra site's mirror when GitHub fails or stalls;
    /// "github" | "astra" = only that one; "custom" = <see cref="DownloadBaseUrl"/>. Every source is checked against the
    /// same pinned checksums.
    /// </summary>
    public string DownloadSource { get; set; } = SourceAuto;

    /// <summary>Custom download base: files are fetched from <c>{base}/{version}/{asset}</c> (GitHub's release layout).</summary>
    public string? DownloadBaseUrl { get; set; }

    /// <summary>An installed cloudflared to run instead of downloading one; ASTRA_CLOUDFLARED still wins.</summary>
    public string? CloudflaredPath { get; set; }
}

/// <summary>reasoning effort → thinking budget tokens, used when translating between protocols.</summary>
public sealed class EffortBudgets
{
    public int Low { get; set; } = 1024;
    public int Medium { get; set; } = 4096;
    public int High { get; set; } = 16384;
}

/// <summary>The web console's sidebar entries (the ids of the web's <c>NavTarget</c>).</summary>
public static class NavItems
{
    /// <summary>Entries the user may hide; Settings is not one of them, it is where they come back.</summary>
    public static readonly IReadOnlyList<string> Hideable =
        ["overview", "requests", "traces", "tokens", "clients", "providers", "models", "plugins", "privacy", "smartRouting"];
}
