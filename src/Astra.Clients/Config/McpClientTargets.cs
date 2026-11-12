using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core.Clients;
using Tomlyn.Model;

namespace Astra.Clients.Config;

/// <summary>An MCP server as a client launches or reaches it: a local command, or a remote address (streamable HTTP).</summary>
/// <param name="Url">Set for a remote server; then <paramref name="Command"/>, <paramref name="Args"/> and <paramref name="Env"/> are unused.</param>
/// <param name="Headers">A remote server's request headers (often an API key).</param>
public sealed record LocalMcpSpec(
    string Name, string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env,
    string? Url = null, IReadOnlyDictionary<string, string>? Headers = null)
{
    public bool IsRemote => Url is not null;
}

/// <summary>One MCP entry found in a client's file (<see cref="McpClientTargets.ReadEntries"/>).</summary>
/// <param name="Spec">What the entry says, or null when it uses something Astra cannot carry (<paramref name="Unsupported"/> says what).</param>
public sealed record FoundMcpEntry(string Name, LocalMcpSpec? Spec, string? Unsupported);

/// <summary>
/// Where each client keeps its MCP server list and how an entry looks there (issue #34). Verified against each client's
/// documentation: Claude Code <c>mcpServers</c> (<c>type</c> stdio | http), Codex <c>[mcp_servers.*]</c> (<c>url</c> +
/// <c>http_headers</c>), Gemini CLI and Qwen Code <c>mcpServers</c> (<c>httpUrl</c> for streamable HTTP, <c>url</c> is the
/// deprecated SSE transport), OpenCode <c>mcp</c> (<c>type</c> local | remote, <c>command</c> is one array, <c>environment</c>).
/// </summary>
public static class McpClientTargets
{
    public static IReadOnlyList<string> Kinds { get; } =
        [ClientKinds.ClaudeCode, ClientKinds.Codex, ClientKinds.GeminiCli, ClientKinds.QwenCode, ClientKinds.OpenCode];

    public static bool Supports(string kind) => Kinds.Contains(kind);

    public static ConfigFileFormat Format(string kind) => kind == ClientKinds.Codex ? ConfigFileFormat.Toml : ConfigFileFormat.Json;

    public static ConfigChangeKind ChangeKind(string kind) => kind == ClientKinds.Codex ? ConfigChangeKind.Table : ConfigChangeKind.Key;

    /// <summary>The object that holds the client's servers, by name.</summary>
    public static string Container(string kind) => kind switch
    {
        ClientKinds.Codex => "mcp_servers",
        ClientKinds.OpenCode => "mcp",
        _ => "mcpServers",
    };

    public static string File(ClientEnvironment env, string kind)
    {
        switch (kind)
        {
            case ClientKinds.Codex:
                return Path.Combine(env.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } codex ? codex : env.Combine(".codex"), "config.toml");
            case ClientKinds.ClaudeCode:
                return env.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } claude ? Path.Combine(claude, ".claude.json") : env.Combine(".claude.json");
            case ClientKinds.GeminiCli:
                return env.Combine(".gemini", "settings.json");
            case ClientKinds.QwenCode:
                return env.Combine(".qwen", "settings.json");
            case ClientKinds.OpenCode:
                var xdg = env.GetEnvironmentVariable("XDG_CONFIG_HOME");
                var dir = string.IsNullOrEmpty(xdg) ? env.Combine(".config", "opencode") : Path.Combine(xdg, "opencode");
                var jsonc = Path.Combine(dir, "opencode.jsonc");
                return env.FileExists(jsonc) ? jsonc : Path.Combine(dir, "opencode.json");
            default:
                throw new ArgumentException($"{kind} has no MCP server list Astra writes", nameof(kind));
        }
    }

    // ---------------------------------------------------------------- writing

    /// <summary>The value written, as a plan carries it: JSON text for a JSON file, a JSON object of raw TOML values for a table.</summary>
    public static string Entry(string kind, LocalMcpSpec s) => kind switch
    {
        ClientKinds.Codex => CodexEntry(s),
        ClientKinds.OpenCode => OpenCodeEntry(s),
        ClientKinds.ClaudeCode => ClaudeEntry(s),
        _ => GeminiEntry(s),
    };

    private static JsonObject Strings(IEnumerable<KeyValuePair<string, string>> items) =>
        new(items.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)JsonValue.Create(p.Value))));

    private static JsonArray Array(IEnumerable<string> items) => new([.. items.Select(a => (JsonNode)JsonValue.Create(a)!)]);

    private static string ClaudeEntry(LocalMcpSpec s)
    {
        if (s.IsRemote)
        {
            var remote = new JsonObject { ["type"] = "http", ["url"] = s.Url };
            if (s.Headers is { Count: > 0 }) remote["headers"] = Strings(s.Headers);
            return remote.ToJsonString();
        }
        var entry = new JsonObject { ["type"] = "stdio", ["command"] = s.Command, ["args"] = Array(s.Args) };
        if (s.Env.Count > 0) entry["env"] = Strings(s.Env);
        return entry.ToJsonString();
    }

    private static string GeminiEntry(LocalMcpSpec s)
    {
        if (s.IsRemote)
        {
            var remote = new JsonObject { ["httpUrl"] = s.Url };
            if (s.Headers is { Count: > 0 }) remote["headers"] = Strings(s.Headers);
            return remote.ToJsonString();
        }
        var entry = new JsonObject { ["command"] = s.Command, ["args"] = Array(s.Args) };
        if (s.Env.Count > 0) entry["env"] = Strings(s.Env);
        return entry.ToJsonString();
    }

    private static string OpenCodeEntry(LocalMcpSpec s)
    {
        if (s.IsRemote)
        {
            var remote = new JsonObject { ["type"] = "remote", ["url"] = s.Url, ["enabled"] = true };
            if (s.Headers is { Count: > 0 }) remote["headers"] = Strings(s.Headers);
            return remote.ToJsonString();
        }
        var entry = new JsonObject { ["type"] = "local", ["command"] = Array([s.Command, .. s.Args]), ["enabled"] = true };
        if (s.Env.Count > 0) entry["environment"] = Strings(s.Env);
        return entry.ToJsonString();
    }

    private static string CodexEntry(LocalMcpSpec s)
    {
        static string Inline(IEnumerable<KeyValuePair<string, string>> items) =>
            "{ " + string.Join(", ", items.Select(p => TomlValueText.ForString(p.Key) + " = " + TomlValueText.ForString(p.Value))) + " }";
        if (s.IsRemote)
        {
            var remote = new JsonObject { ["url"] = TomlValueText.ForString(s.Url!) };
            if (s.Headers is { Count: > 0 }) remote["http_headers"] = Inline(s.Headers);
            return remote.ToJsonString();
        }
        var table = new JsonObject
        {
            ["command"] = TomlValueText.ForString(s.Command),
            ["args"] = "[" + string.Join(", ", s.Args.Select(TomlValueText.ForString)) + "]",
        };
        if (s.Env.Count > 0) table["env"] = Inline(s.Env);
        return table.ToJsonString();
    }

    /// <summary>The key whose value stands in for a Codex table's content when its state is compared (a table reads as just "present").</summary>
    public static string CodexIdentityKey(LocalMcpSpec s) => s.IsRemote ? "url" : "command";

    // ---------------------------------------------------------------- reading (import)

    /// <summary>Every entry in the client's file, with what it says when Astra can carry it.</summary>
    public static IReadOnlyList<FoundMcpEntry> ReadEntries(ClientEnvironment env, string kind)
    {
        var text = env.ReadTextOrNull(File(env, kind));
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (kind == ClientKinds.Codex) return ReadCodex(text);
        if (new JsoncEditor(text).Get(Container(kind)) is not JsonObject servers) return [];
        return [.. servers.Select(p => p.Value is JsonObject o ? ParseJson(kind, p.Key, o) : new FoundMcpEntry(p.Key, null, "not an object"))];
    }

    private static FoundMcpEntry ParseJson(string kind, string name, JsonObject o)
    {
        string? Str(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        Dictionary<string, string> Map(string key) => o[key] is JsonObject m
            ? m.Where(p => p.Value is JsonValue).ToDictionary(p => p.Key, p => p.Value!.ToString())
            : [];
        var type = Str("type");
        if (kind == ClientKinds.OpenCode)
        {
            if (type == "remote" && Str("url") is { } remoteUrl) return new(name, new LocalMcpSpec(name, "", [], new Dictionary<string, string>(), remoteUrl, Map("headers")), null);
            if (type == "local" && o["command"] is JsonArray cmd && cmd.Count > 0 && cmd.All(c => c is JsonValue))
            {
                var parts = cmd.Select(c => c!.ToString()).ToList();
                return new(name, new LocalMcpSpec(name, parts[0], parts[1..], Map("environment")), null);
            }
            return new(name, null, "unsupported shape");
        }
        if (kind == ClientKinds.ClaudeCode)
        {
            if (type is "sse" or "ws") return new(name, null, "sse transport");
            if (Str("url") is { } url) return new(name, new LocalMcpSpec(name, "", [], new Dictionary<string, string>(), url, Map("headers")), null);
        }
        else if (Str("httpUrl") is { } httpUrl)
        {
            return new(name, new LocalMcpSpec(name, "", [], new Dictionary<string, string>(), httpUrl, Map("headers")), null);
        }
        else if (Str("url") is not null || Str("tcp") is not null)
        {
            return new(name, null, "sse or tcp transport");
        }
        if (Str("command") is { Length: > 0 } command)
        {
            var args = o["args"] is JsonArray a ? a.Where(x => x is JsonValue).Select(x => x!.ToString()).ToList() : [];
            return new(name, new LocalMcpSpec(name, command, args, Map("env")), null);
        }
        return new(name, null, "unsupported shape");
    }

    private static IReadOnlyList<FoundMcpEntry> ReadCodex(string text)
    {
        TomlTable? doc;
        try
        {
            doc = Tomlyn.TomlSerializer.Deserialize(text, ClientTomlContext.Default.TomlTable);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new EditorException($"Invalid TOML document: {e.Message}");
        }
        if (doc is null || !doc.TryGetValue("mcp_servers", out var raw) || raw is not TomlTable servers) return [];
        var found = new List<FoundMcpEntry>();
        foreach (var (name, value) in servers)
        {
            if (value is not TomlTable t) { found.Add(new(name, null, "not a table")); continue; }
            Dictionary<string, string> Map(string key) => t.TryGetValue(key, out var m) && m is TomlTable mt
                ? mt.Where(p => p.Value is not null && p.Value is not TomlTable).ToDictionary(p => p.Key, p => p.Value!.ToString()!)
                : [];
            if (t.TryGetValue("bearer_token_env_var", out _) || t.ContainsKey("env_http_headers"))
                found.Add(new(name, null, "headers from environment variables"));
            else if (t.TryGetValue("url", out var url) && url is string u)
                found.Add(new(name, new LocalMcpSpec(name, "", [], new Dictionary<string, string>(), u, Map("http_headers")), null));
            else if (t.TryGetValue("command", out var cmd) && cmd is string c && c.Length > 0)
            {
                var args = t.TryGetValue("args", out var a) && a is TomlArray arr ? arr.Where(x => x is string).Select(x => (string)x!).ToList() : [];
                found.Add(new(name, new LocalMcpSpec(name, c, args, Map("env")), null));
            }
            else found.Add(new(name, null, "unsupported shape"));
        }
        return found;
    }
}
