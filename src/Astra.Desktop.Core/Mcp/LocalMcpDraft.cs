using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Mcp;

/// <summary>
/// The fields of the "your own MCP servers" sheet and what they become (web <c>LocalMcpServers.tsx</c>: <c>parseArgs</c>, <c>parseEnv</c>,
/// <c>save</c>). Stored environment and header values never reach the app, so an existing server's boxes start blank and blank means
/// "keep what is stored": the body then leaves <c>Env</c> / <c>Headers</c> out.
/// </summary>
public sealed class LocalMcpDraft
{
    /// <summary>The clients Astra writes MCP servers into: kind and display name.</summary>
    public static IReadOnlyList<(string Kind, string Name)> Clients { get; } =
    [
        ("claude-code", "Claude Code"), ("codex", "Codex"), ("gemini-cli", "Gemini CLI"), ("qwen-code", "Qwen Code"), ("opencode", "OpenCode"),
    ];

    /// <summary>The display name of a client kind (the kind itself when unknown).</summary>
    public static string ClientName(string kind) => Clients.FirstOrDefault(c => c.Kind == kind).Name ?? kind;

    /// <summary>A new definition's draft: stdio, every client chosen.</summary>
    public LocalMcpDraft()
    {
        ChosenClients = [.. Clients.Select(c => c.Kind)];
    }

    /// <summary>A draft of an existing definition (values blank, names known).</summary>
    public LocalMcpDraft(LocalMcpServerDto s)
    {
        Existing = s;
        Name = s.Name;
        Transport = s.Transport;
        Command = s.Command;
        Args = string.Join('\n', s.Args);
        Url = s.Url;
        ChosenClients = [.. s.Clients];
        Enabled = s.Enabled;
    }

    /// <summary>The definition being edited; null = a new one.</summary>
    public LocalMcpServerDto? Existing { get; }

    /// <summary>The entry name in the clients.</summary>
    public string Name { get; set; } = "";

    /// <summary><c>stdio</c> or <c>http</c>.</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>The command (stdio).</summary>
    public string Command { get; set; } = "";

    /// <summary>The arguments, one per line (stdio).</summary>
    public string Args { get; set; } = "";

    /// <summary><c>NAME=value</c> per line (stdio); blank keeps the stored variables on update.</summary>
    public string Env { get; set; } = "";

    /// <summary>The address (http).</summary>
    public string Url { get; set; } = "";

    /// <summary><c>Name: value</c> per line (http); blank keeps the stored headers on update.</summary>
    public string Headers { get; set; } = "";

    /// <summary>The client kinds chosen.</summary>
    public HashSet<string> ChosenClients { get; }

    /// <summary>Whether the definition is written at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>One argument per line; blank lines dropped.</summary>
    public static List<string> ParseArgs(string? text)
        => (text ?? "").Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();

    /// <summary><c>NAME=value</c> per line, keeping any "=" inside the value; null when a non-blank line has no name.</summary>
    public static Dictionary<string, string>? ParseEnv(string? text)
    {
        var env = new Dictionary<string, string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) return null;
            env[line[..eq].Trim()] = line[(eq + 1)..];
        }
        return env;
    }

    /// <summary><c>Name: value</c> per line (the value may hold ":"); null when a non-blank line has no name or no colon.</summary>
    public static Dictionary<string, string>? ParseHeaders(string? text)
    {
        var headers = new Dictionary<string, string>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) return null;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return headers;
    }

    /// <summary>Whether the boxes hold something Astra can save.</summary>
    public bool IsValid
    {
        get
        {
            if (Name.Trim().Length == 0) return false;
            return Transport == "http"
                ? Url.Trim().Length > 0 && ParseHeaders(Headers) is not null
                : Command.Trim().Length > 0 && ParseEnv(Env) is not null;
        }
    }

    /// <summary>The create / update body. Blank env / headers on an existing definition are left out, so the server keeps what it stores.</summary>
    public LocalMcpServerInputDto ToBody()
    {
        var http = Transport == "http";
        var keep = Existing is not null;
        var env = !http && (Env.Trim().Length > 0 || !keep) ? ParseEnv(Env) : null;
        var headers = http && (Headers.Trim().Length > 0 || !keep) ? ParseHeaders(Headers) : null;
        return new LocalMcpServerInputDto(
            Name.Trim(), Transport, http ? null : Command.Trim(), http ? null : ParseArgs(Args), env, http ? Url.Trim() : null, headers, Enabled,
            [.. Clients.Select(c => c.Kind).Where(ChosenClients.Contains)]);
    }

    /// <summary>The command line or address of a definition, for the list.</summary>
    public static string Summary(LocalMcpServerDto s) => s.Transport == "http" ? s.Url : string.Join(' ', [s.Command, .. s.Args]);

    /// <summary>Whether a definition has a client it should be in that is not (Astra offers "write again").</summary>
    public static bool NeedsAttention(LocalMcpServerDto s) => s.Placements.Any(p => p.Wanted && p.State != "applied");
}
