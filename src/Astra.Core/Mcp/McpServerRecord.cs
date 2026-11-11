namespace Astra.Core.Mcp;

/// <summary>How the gateway reaches an upstream MCP server (<c>mcp_servers.transport</c>). Local stdio servers are not offered (plan mcp-gateway §1).</summary>
public static class McpTransports
{
    /// <summary>Streamable HTTP; the gateway finds out whether the server is a modern (2026-07-28) or an initialize-based one itself.</summary>
    public const string Http = "http";

    /// <summary>The deprecated HTTP+SSE transport (2024-11-05); many older servers still only speak it.</summary>
    public const string Sse = "sse";

    public static readonly IReadOnlyList<string> All = [Http, Sse];
}

/// <summary>How the gateway authenticates to an upstream (<c>mcp_servers.auth_type</c>).</summary>
public static class McpAuthTypes
{
    public const string None = "none";

    /// <summary>Static request headers (an API key or a bearer token the admin typed in).</summary>
    public const string Header = "header";

    /// <summary>OAuth 2.1: the admin authorizes once in a browser, the gateway keeps and refreshes the tokens (plan §4.1).</summary>
    public const string OAuth = "oauth";

    public static readonly IReadOnlyList<string> All = [None, Header, OAuth];
}

/// <summary>What the last contact with an upstream showed (<c>mcp_servers.status</c>); a cache, not a setting.</summary>
public static class McpServerStatuses
{
    public const string Unknown = "unknown";
    public const string Ok = "ok";

    /// <summary>OAuth is needed (never granted, expired without a usable refresh token, or a wider scope is required): an admin has to authorize.</summary>
    public const string NeedsAuth = "needs_auth";

    /// <summary>The upstream refused the configured static credentials.</summary>
    public const string AuthError = "auth_error";

    public const string Unreachable = "unreachable";

    /// <summary>The upstream answered, but with a protocol version the gateway does not speak.</summary>
    public const string VersionUnsupported = "version_unsupported";
}

/// <summary>An upstream MCP server the gateway aggregates (row of <c>mcp_servers</c>).</summary>
public sealed class McpServerRecord
{
    public string Id { get; set; } = "";

    /// <summary>Unique, lower-case, no underscore (<see cref="McpNames.ValidateSlug"/>): it prefixes the server's tool names.</summary>
    public string Slug { get; set; } = "";

    public string Name { get; set; } = "";
    public string Transport { get; set; } = McpTransports.Http;
    public string Url { get; set; } = "";
    public string AuthType { get; set; } = McpAuthTypes.None;

    /// <summary>The static headers as one <see cref="Clients.ISecretProtector"/>-protected JSON object (name → value); null = none. Never returned by the API.</summary>
    public string? HeadersEnc { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Limit of one upstream call (list or tool call), in seconds.</summary>
    public int TimeoutSeconds { get; set; } = McpLimits.DefaultTimeoutSeconds;

    public string Status { get; set; } = McpServerStatuses.Unknown;

    /// <summary>A short reason for <see cref="Status"/>; never the upstream's address or a credential.</summary>
    public string? StatusMessage { get; set; }

    /// <summary>The protocol version the last connection settled on (e.g. <c>2026-07-28</c>, <c>2025-11-25</c>), or null.</summary>
    public string? ProtocolVersion { get; set; }

    public DateTimeOffset? CheckedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>An independent copy.</summary>
    public McpServerRecord Clone() => (McpServerRecord)MemberwiseClone();
}

/// <summary>Limits of the MCP gateway.</summary>
public static class McpLimits
{
    public const int DefaultTimeoutSeconds = 60;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 600;

    /// <summary>The largest request body the front end accepts (the MCP spec's gateway guidance; microsoft/mcp-gateway uses the same).</summary>
    public const int MaxRequestBytes = 4 * 1024 * 1024;

    /// <summary>How long a server's tool list is reused before it is asked again.</summary>
    public static readonly TimeSpan ToolListTtl = TimeSpan.FromMinutes(1);

    /// <summary>Days of <see cref="McpCallRecord"/> kept.</summary>
    public const int CallRetentionDays = 30;
}

/// <summary>One tool call through the gateway (row of <c>mcp_calls</c>): who, which tool, how long, how it ended. Never the arguments or the result.</summary>
public sealed class McpCallRecord
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string TokenId { get; set; } = "";
    public string ServerId { get; set; } = "";

    /// <summary>The slug when the call was made (the server may be renamed or deleted later).</summary>
    public string ServerSlug { get; set; } = "";

    /// <summary>The upstream's own tool name (no gateway prefix).</summary>
    public string Tool { get; set; } = "";

    public long DurationMs { get; set; }
    public bool Ok { get; set; }

    /// <summary>For a failed call: <see cref="McpErrorCategories"/>; null on success.</summary>
    public string? ErrorCategory { get; set; }

    /// <summary>The era of the client that called: <c>modern</c> or <c>legacy</c>.</summary>
    public string? FrontEra { get; set; }
}

/// <summary>Why a tool call failed, in terms safe to show to whoever holds the token.</summary>
public static class McpErrorCategories
{
    /// <summary>The upstream ran the tool and reported the tool itself failed (<c>isError</c>).</summary>
    public const string Tool = "tool";

    public const string NeedsAuth = "needs_auth";
    public const string Unreachable = "unreachable";
    public const string Timeout = "timeout";

    /// <summary>The upstream wanted something the stateless gateway cannot relay (a sampling or elicitation request, <c>input_required</c> for a legacy client).</summary>
    public const string NeedsInput = "needs_input";

    public const string Protocol = "protocol";
}

/// <summary>The OAuth state kept for one server (row of <c>mcp_oauth</c>). <see cref="TokensEnc"/> is a protected value; only the gateway reads it.</summary>
public sealed class McpOAuthRecord
{
    public string ServerId { get; set; } = "";
    public string TokensEnc { get; set; } = "";

    /// <summary>The authorization server that issued the tokens (what its credentials are bound to), for display and for spotting a change.</summary>
    public string? Issuer { get; set; }

    /// <summary>The scope the tokens were granted, for display.</summary>
    public string? Scope { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
