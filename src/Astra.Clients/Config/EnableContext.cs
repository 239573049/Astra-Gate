using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Clients.Config;

/// <summary>The parameters an enable operation writes into a client's configuration.</summary>
public sealed class EnableContext
{
    public const string DefaultGatewayBaseUrl = "http://127.0.0.1:17321";

    /// <summary>Gateway base URL, e.g. "http://127.0.0.1:17321" (no trailing slash).</summary>
    public required string GatewayBaseUrl { get; init; }

    /// <summary>This client's local gateway key ("astra-&lt;client&gt;-&lt;random&gt;").</summary>
    public required string LocalKey { get; init; }

    /// <summary>Optional model to write into the client configuration.</summary>
    public string? Model { get; init; }

    /// <summary>
    /// Client-specific extras (JSON object). Known keys: claude-code "smallFastModel";
    /// claude-desktop "roleMap" ({"sonnet":…,"opus":…,"haiku":…}); opencode, pi, minimax-code, copilot-cli and
    /// vscode-copilot "models" ({"…": {"id":…,"name":…, optional "contextWindow", "maxOutputTokens", "vision",
    /// "reasoning"}}, the bound provider's enabled models).
    /// </summary>
    public JsonObject? Extras { get; init; }

    /// <summary>Returns a string extra value, or null.</summary>
    public string? ExtraString(string name)
    {
        if (Extras is null) return null;
        return Extras.TryGetPropertyValue(name, out var node) && node is JsonValue v && v.TryGetValue(out string? s) ? s : null;
    }

    /// <summary>Returns a nested JSON object extra value, or null.</summary>
    public JsonObject? ExtraObject(string name)
    {
        if (Extras is null) return null;
        return Extras.TryGetPropertyValue(name, out var node) && node is JsonObject o ? o : null;
    }
}

/// <summary>The file format of one client configuration file.</summary>
public enum ConfigFileFormat
{
    Toml,
    Json,
    Env,

    /// <summary>YAML block mapping; values are exchanged as JSON text, like <see cref="Json"/>.</summary>
    Yaml,
}

/// <summary>Whether a config change targets a plain key or a whole container (TOML table, owned JSON element).</summary>
public enum ConfigChangeKind
{
    Key,

    /// <summary>
    /// The key path names a container Astra creates and owns as a whole: a TOML table header such as
    /// [model_providers.astra], or a JSON array element such as a VS Code model group. Reads as "true"/null.
    /// </summary>
    Table,
}

/// <summary>One key (or table) Astra plans to write, restore or remove in one client config file.</summary>
public sealed class ConfigChange
{
    /// <param name="file">Absolute path of the config file.</param>
    /// <param name="format">File format.</param>
    /// <param name="keyPath">Dotted key path inside the file, e.g. "env.ANTHROPIC_BASE_URL".</param>
    /// <param name="before">Value before the change as JSON text, or null when absent. For TOML/.env this is the JSON-encoded raw source text of the value; for JSON and YAML it is the JSON value text itself.</param>
    /// <param name="after">Value after the change (same encoding), or null to remove the key.</param>
    public ConfigChange(string file, ConfigFileFormat format, string keyPath, string? before, string? after,
        ConfigChangeKind kind = ConfigChangeKind.Key)
    {
        File = file;
        Format = format;
        KeyPath = keyPath;
        Before = before;
        After = after;
        Kind = kind;
    }

    public string File { get; }
    public ConfigFileFormat Format { get; }
    public string KeyPath { get; }
    public ConfigChangeKind Kind { get; }
    public string? Before { get; }
    public string? After { get; }

    public override string ToString() => $"{Path.GetFileName(File)}:{KeyPath}: {Before ?? "<absent>"} -> {After ?? "<absent>"}";
}

/// <summary>A per-file unified diff for the UI change preview.</summary>
public sealed record FileDiff(string File, string UnifiedDiff);

/// <summary>The complete, human-reviewable set of file modifications for an enable/disable/purge.</summary>
public sealed class ConfigChangePlan
{
    public ConfigChangePlan(string clientKind, IReadOnlyList<ConfigChange> changes, IReadOnlyList<FileDiff> diffs)
    {
        ClientKind = clientKind;
        Changes = changes;
        Diffs = diffs;
    }

    public string ClientKind { get; }

    public IReadOnlyList<ConfigChange> Changes { get; }

    /// <summary>One unified diff per affected file, computed against the current file contents.</summary>
    public IReadOnlyList<FileDiff> Diffs { get; }

    public IEnumerable<IGrouping<string, ConfigChange>> ByFile => Changes.GroupBy(c => c.File);

    public bool IsEmpty => Changes.Count == 0;
}
