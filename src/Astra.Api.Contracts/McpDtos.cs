using System.Text.Json.Serialization;

namespace Astra.Api.Contracts;

/// <summary>The OAuth state of an upstream MCP server (never the tokens themselves).</summary>
/// <param name="Authorized">Astra holds tokens for the server.</param>
/// <param name="Issuer">The authorization server that issued them.</param>
/// <param name="Scope">The scope that was granted.</param>
public sealed record McpOAuthDto(bool Authorized, string? Issuer, string? Scope, DateTimeOffset? UpdatedAt);

/// <summary>An upstream MCP server in the admin API. The static headers' values are never returned, only their names.</summary>
/// <param name="Slug">Prefixes the server's tool names on the aggregate endpoint (<c>&lt;slug&gt;__&lt;tool&gt;</c>).</param>
/// <param name="Transport"><c>http</c> (Streamable HTTP, of either era) or <c>sse</c> (the deprecated HTTP+SSE).</param>
/// <param name="AuthType"><c>none</c>, <c>header</c> or <c>oauth</c>.</param>
/// <param name="Status">What the last contact showed: <c>unknown</c>, <c>ok</c>, <c>needs_auth</c>, <c>auth_error</c>, <c>unreachable</c> or <c>version_unsupported</c>.</param>
/// <param name="ProtocolVersion">The protocol version the last connection settled on.</param>
/// <param name="OAuth">The OAuth state (JSON name <c>oauth</c>); null unless <paramref name="AuthType"/> is <c>oauth</c>.</param>
/// <param name="BoundTokens">How many gateway tokens may use the server.</param>
public sealed record McpServerDto(
    string Id, string Slug, string Name, string Transport, string Url, string AuthType, List<string> HeaderNames, bool Enabled,
    int TimeoutSeconds, string Status, string? StatusMessage, string? ProtocolVersion, DateTimeOffset? CheckedAt, [property: JsonPropertyName("oauth")] McpOAuthDto? OAuth,
    int BoundTokens, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>The outcome of "test connection": a fresh connection, a protocol handshake of whatever era the server speaks, and a tool list.</summary>
public sealed record McpTestResultDto(bool Ok, string Status, string? Message, string? ProtocolVersion, int ToolCount, List<string> Tools);

/// <summary>Settings of the MCP gateway.</summary>
/// <param name="Enabled">Whether <c>/mcp</c> answers (it is a 404 otherwise).</param>
/// <param name="AllowedOrigins">Browser origins allowed to call it; a request without an <c>Origin</c> header is not affected.</param>
/// <param name="Endpoint">The address clients use for the aggregate endpoint.</param>
public sealed record McpSettingsDto(bool Enabled, List<string> AllowedOrigins, string Endpoint);

/// <summary>Where the admin goes to authorize Astra at an upstream (open it in a browser).</summary>
/// <param name="AuthorizationUrl">The address to open; null when no authorization is needed (the upstream accepted the tokens Astra already holds, or asks for none).</param>
/// <param name="Message">A sentence for the admin when there is no address to open.</param>
public sealed record McpOAuthStartDto(string? AuthorizationUrl, string? Message);

/// <summary>One tool call through the MCP gateway: who, which tool, how long, how it ended. Never the arguments or the result.</summary>
/// <param name="ErrorCategory"><c>tool</c>, <c>needs_auth</c>, <c>unreachable</c>, <c>timeout</c>, <c>needs_input</c> or <c>protocol</c>; null on success.</param>
/// <param name="FrontEra"><c>modern</c> or <c>legacy</c>: the protocol era of the calling client.</param>
public sealed record McpCallDto(
    long Id, DateTimeOffset At, string TokenId, string? TokenName, string ServerId, string ServerSlug, string Tool, long DurationMs, bool Ok,
    string? ErrorCategory, string? FrontEra);
