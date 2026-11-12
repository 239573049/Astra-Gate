namespace Astra.Core.Mcp;

/// <summary>An MCP server definition (row of <c>mcp_local_servers</c>), a local stdio command or a remote address: written into clients' own MCP lists, never run or proxied by Astra.</summary>
public sealed class LocalMcpServer
{
    public string Id { get; set; } = "";

    /// <summary>The entry name in the clients' files: <c>[a-z0-9][a-z0-9_-]{0,31}</c>, and not <c>astra</c> (the gateway's entry).</summary>
    public string Name { get; set; } = "";

    /// <summary><see cref="LocalMcpTransports.Stdio"/> or <see cref="LocalMcpTransports.Http"/>.</summary>
    public string Transport { get; set; } = LocalMcpTransports.Stdio;

    /// <summary>The command of a stdio server.</summary>
    public string Command { get; set; } = "";

    /// <summary>The address of an http server.</summary>
    public string Url { get; set; } = "";

    /// <summary>An http server's request headers as one protected JSON object (name → value); null = none. Never returned by the API.</summary>
    public string? HeadersEnc { get; set; }

    /// <summary>A JSON array of strings.</summary>
    public string ArgsJson { get; set; } = "[]";

    /// <summary>A stdio server's environment as one protected JSON object (name → value); null = none. Never returned by the API.</summary>
    public string? EnvEnc { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>A JSON array of client kinds the server should be written into.</summary>
    public string ClientsJson { get; set; } = "[]";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public static bool ValidName(string name) =>
        name.Length is >= 1 and <= 32 && name != "astra" && (char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0]))
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');
}

/// <summary>How a local definition reaches its server (<c>mcp_local_servers.transport</c>).</summary>
public static class LocalMcpTransports
{
    /// <summary>A command the client starts.</summary>
    public const string Stdio = "stdio";

    /// <summary>A remote streamable-HTTP server the client connects to directly (not through Astra's gateway).</summary>
    public const string Http = "http";
}
