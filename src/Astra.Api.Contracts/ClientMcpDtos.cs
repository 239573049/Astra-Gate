namespace Astra.Api.Contracts;

/// <summary>
/// Where a client (Claude Code, Codex) stands with the MCP gateway entry Astra writes into it (issue #34). Never carries the token's key.
/// </summary>
/// <param name="Kind">The client kind (<c>claude-code</c> or <c>codex</c>).</param>
/// <param name="State"><c>missing</c> (no <c>astra</c> entry), <c>applied</c>, <c>drifted</c> (Astra wrote it and it was changed since) or <c>conflict</c> (an entry Astra did not write, or a file the client cannot parse; never overwritten).</param>
/// <param name="File">The client file the entry lives in.</param>
/// <param name="Url">The address the entry points at, when it could be read.</param>
/// <param name="TokenId">The gateway token whose key the entry carries, when it is one of Astra's tokens.</param>
/// <param name="TokenName">That token's name.</param>
/// <param name="GatewayEnabled">Whether the MCP gateway answers at all; while it is off the entry points at a 404.</param>
/// <param name="GatewayUrl">The address a new entry would point at.</param>
/// <param name="Error">Why the client's file could not be read.</param>
public sealed record ClientMcpDto(
    string Kind, string State, string File, string? Url, string? TokenId, string? TokenName, bool GatewayEnabled, string GatewayUrl, string? Error);

/// <summary>Request body of "apply the MCP gateway to a client".</summary>
/// <param name="TokenId">The gateway token whose key the entry carries; it must have MCP servers bound.</param>
/// <param name="Force">Replace an entry that was changed since Astra wrote it. A foreign entry is never replaced.</param>
public sealed record ClientMcpApplyDto(string TokenId, bool Force = false);
