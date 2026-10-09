using System.Text;
using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Kimi Code (Moonshot, npm <c>@moonshot-ai/kimi-code</c>, bin <c>kimi</c>, Node 22.19+; Coexist): adds a
/// <c>[providers.astra]</c> table (<c>type = "openai"</c>, <c>base_url</c>, a literal <c>api_key</c>) and one
/// <c>[models.astra-&lt;id&gt;]</c> table per bound model (<c>provider</c>, <c>model</c>, <c>max_context_size</c>,
/// <c>capabilities</c>) to <c>~/.kimi-code/config.toml</c> (<c>KIMI_CODE_HOME</c> relocates the data root) and, when a model is
/// chosen, sets the top-level <c>default_model</c> to its alias. Verified against Kimi Code's configuration docs
/// (moonshotai.github.io/kimi-code, 2026-10): provider <c>type</c> "openai" is OpenAI Chat Completions, model aliases are
/// free-form table keys, and shell environment variables are not read automatically. Aliases are bare TOML keys (the TOML editor
/// does not write quoted keys, so a model id such as "gpt-5.1" becomes the alias "astra-gpt-5-1"; the real id stays in
/// <c>model</c>). Tables Astra created are owned whole (<see cref="ConfigChangeKind.Table"/>); fields inside a table that
/// already existed are refreshed one by one and restored on disable. The archived Python CLI's <c>~/.kimi</c> directory is never
/// touched.
/// </summary>
public sealed class KimiCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    public const string AliasPrefix = "astra-";

    public const long DefaultContextSize = 131_072;

    private const string DefaultModelPath = "default_model";

    private string HomeDir =>
        Env.GetEnvironmentVariable("KIMI_CODE_HOME") is { Length: > 0 } dir ? dir : Env.Combine(".kimi-code");

    private string ConfigFile => Path.Combine(HomeDir, "config.toml");

    private static string ProviderTable => $"providers.{ProviderId}";

    private static string ModelTable(string alias) => $"models.{alias}";

    public override string Kind => ClientKinds.KimiCode;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = HomeDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("kimi");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no kimi executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    protected override bool IsTableKeyPath(string keyPath) => keyPath == ProviderTable || IsModelTable(keyPath);

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        changes.AddRange(TableChanges(file, ProviderTable,
        [
            ("type", Raw("openai")),
            ("base_url", Raw(ctx.GatewayBaseUrl.TrimEnd('/') + "/v1")),
            ("api_key", Raw(ctx.LocalKey)),
        ]));

        var hints = ModelHints(ctx);
        var aliases = Aliases(ModelEntries(ctx).Select(e => e.Id));
        foreach (var (id, alias) in aliases)
        {
            hints.TryGetValue(id, out var info);
            var capabilities = new List<string> { "\"tool_use\"" };
            if (BoolHint(info?["reasoning"]) == true) capabilities.Add("\"thinking\"");
            if (BoolHint(info?["vision"]) == true) capabilities.Add("\"image_in\"");
            changes.AddRange(TableChanges(file, ModelTable(alias),
            [
                ("provider", Raw(ProviderId)),
                ("model", Raw(id)),
                ("max_context_size", (PositiveLong(info?["contextWindow"]) ?? DefaultContextSize).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("capabilities", "[" + string.Join(", ", capabilities) + "]"),
            ]));
        }

        // Models Astra registered earlier that the bound provider no longer offers are taken out again.
        foreach (var path in TrackedModelTables(file))
        {
            if (aliases.ContainsValue(path[7..])) continue;
            if (CurrentValue(file, ConfigFileFormat.Toml, path, ConfigChangeKind.Table) is { } current)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, path, current, null, ConfigChangeKind.Table));
        }

        if (!string.IsNullOrEmpty(ctx.Model) && aliases.TryGetValue(ctx.Model, out var selected))
        {
            changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, DefaultModelPath,
                CurrentValue(file, ConfigFileFormat.Toml, DefaultModelPath), JsonString(Raw(selected))));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        // A model the user chose afterwards is theirs and stays.
        if (StillOurs(file, ConfigFileFormat.Toml, DefaultModelPath))
            changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, DefaultModelPath, CurrentValue(file, ConfigFileFormat.Toml, DefaultModelPath), null));

        // Fields refreshed inside a table that pre-existed were tracked one by one; put them back first.
        foreach (var entry in Store.List(Kind).Where(e => e.FilePath == file && !IsTableKeyPath(e.KeyPath) && e.KeyPath != DefaultModelPath))
        {
            if (CurrentValue(file, ConfigFileFormat.Toml, entry.KeyPath) is { } current)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, entry.KeyPath, current, null));
        }
        // Tables Astra created go away whole.
        foreach (var entry in Store.List(Kind).Where(e => e.FilePath == file && IsTableKeyPath(e.KeyPath) && e.OriginalAbsent))
        {
            if (CurrentValue(file, ConfigFileFormat.Toml, entry.KeyPath, ConfigChangeKind.Table) is { } current)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, entry.KeyPath, current, null, ConfigChangeKind.Table));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge()
    {
        // Purge also removes a provider table that pre-existed under Astra's own name.
        var file = ConfigFile;
        var changes = new List<ConfigChange>(PlanDisable().Changes);
        foreach (var path in new[] { ProviderTable }.Concat(TrackedModelTables(file)))
        {
            if (changes.Any(c => c.KeyPath == path)) continue;
            if (CurrentValue(file, ConfigFileFormat.Toml, path, ConfigChangeKind.Table) is { } current)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, path, current, null, ConfigChangeKind.Table));
        }
        return BuildPlan(changes);
    }

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var warnings = new List<string>();
        var legacy = Env.Combine(".kimi");
        if (!Env.DirectoryExists(HomeDir) && Env.DirectoryExists(legacy))
            warnings.Add($"{legacy} belongs to the archived Python Kimi CLI; Kimi Code reads {ConfigFile} instead (the old directory is not modified).");
        var enabled = Store.Get(Kind, ConfigFile, ProviderTable) is not null
            && CurrentValue(ConfigFile, ConfigFileFormat.Toml, ProviderTable, ConfigChangeKind.Table) is not null;
        return BuildStatus(detection, enabled, warnings);
    }

    /// <summary>Creates the table with all fields, or — when it already exists — refreshes only the differing fields.</summary>
    private IEnumerable<ConfigChange> TableChanges(string file, string tablePath, (string Key, string Raw)[] fields)
    {
        if (CurrentValue(file, ConfigFileFormat.Toml, tablePath, ConfigChangeKind.Table) is null)
        {
            var items = new JsonObject();
            foreach (var (key, raw) in fields) items[key] = raw;
            yield return new ConfigChange(file, ConfigFileFormat.Toml, tablePath, null, items.ToJsonString(), ConfigChangeKind.Table);
            yield break;
        }
        foreach (var (key, raw) in fields)
        {
            var path = $"{tablePath}.{key}";
            var before = CurrentValue(file, ConfigFileFormat.Toml, path);
            if (!string.Equals(before, raw, StringComparison.Ordinal))
                yield return new ConfigChange(file, ConfigFileFormat.Toml, path, before, JsonString(raw));
        }
    }

    /// <summary>The alias of every model id: <c>astra-</c> plus the id with everything but letters, digits, '_' and '-' turned into '-'.</summary>
    private static Dictionary<string, string> Aliases(IEnumerable<string> ids)
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var sb = new StringBuilder(AliasPrefix);
            foreach (var c in id) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '-');
            var alias = sb.ToString();
            for (var n = 2; !taken.Add(alias); n++) alias = $"{sb}-{n}";
            aliases[id] = alias;
        }
        return aliases;
    }

    private static bool IsModelTable(string keyPath) =>
        keyPath.StartsWith("models." + AliasPrefix, StringComparison.Ordinal) && keyPath.IndexOf('.', 7) < 0;

    private IEnumerable<string> TrackedModelTables(string file) => Store.List(Kind)
        .Where(e => e.FilePath == file && IsModelTable(e.KeyPath))
        .Select(e => e.KeyPath)
        .ToList();

    private static string Raw(string value) => Editing.TomlValueText.ForString(value);
}
