using Astra.Clients.Editing;
using Tomlyn;
using Tomlyn.Model;

namespace Astra.Clients.Import;

/// <summary>A <c>[model_providers.*]</c> table of a Codex <c>config.toml</c>, reduced to what an import needs.</summary>
internal sealed record CodexProvider(
    string Key, string? Name, string? BaseUrl, string WireApi, string? BearerToken, string? EnvKey,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>Read-only view over Codex <c>config.toml</c> text (also the TOML string CC Switch stores per Codex provider).</summary>
internal sealed class CodexConfig
{
    private readonly TomlTable _root;

    private CodexConfig(TomlTable root) => _root = root;

    /// <summary>Null for blank or malformed TOML.</summary>
    public static CodexConfig? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return TomlSerializer.Deserialize(text, ClientTomlContext.Default.TomlTable) is { } table ? new CodexConfig(table) : null;
        }
        catch (Exception e) when (e is TomlException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public string? TopString(string key) => _root.TryGetValue(key, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    public IReadOnlyList<CodexProvider> Providers()
    {
        var result = new List<CodexProvider>();
        if (_root.TryGetValue("model_providers", out var node) && node is TomlTable tables)
            foreach (var (key, value) in tables)
                if (value is TomlTable t) result.Add(ToProvider(key, t));
        return result;
    }

    /// <summary>The provider named by the top-level <c>model_provider</c>, when it has its own table.</summary>
    public CodexProvider? SelectedProvider() =>
        TopString("model_provider") is { } name ? Providers().FirstOrDefault(p => p.Key == name) : null;

    /// <summary>Models a provider is used with: the top-level <c>model</c> and every profile pointing at it.</summary>
    public IEnumerable<string> ModelsFor(string providerKey)
    {
        if (TopString("model_provider") == providerKey && TopString("model") is { } top) yield return top;
        if (_root.TryGetValue("profiles", out var node) && node is TomlTable profiles)
            foreach (var (_, value) in profiles)
                if (value is TomlTable p && Str(p, "model_provider") == providerKey && Str(p, "model") is { } model) yield return model;
    }

    public string? ModelCatalogPath() => TopString("model_catalog_json");

    private static CodexProvider ToProvider(string key, TomlTable t)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (t.TryGetValue("http_headers", out var h) && h is TomlTable table)
            foreach (var (name, value) in table)
                if (value is string text && name.Length > 0) headers[name] = text;
        return new CodexProvider(key, Str(t, "name"), Str(t, "base_url"), Str(t, "wire_api") ?? "responses",
            Str(t, "experimental_bearer_token"), Str(t, "env_key"), headers);
    }

    private static string? Str(TomlTable t, string key) =>
        t.TryGetValue(key, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
}
