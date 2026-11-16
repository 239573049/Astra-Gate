using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;
using Astra.Clients.Editing;

namespace Astra.Clients.Adapters;

/// <summary>
/// PenguinHarness (Prism-Shadow/penguin-harness, npm <c>@prismshadow/penguin-cli</c>, command <c>penguin</c>; Coexist): each Project's
/// models live in <c>&lt;root&gt;/&lt;project&gt;/.project_config.toml</c>, the root being <c>PENGUIN_HOME</c> else <c>~/.penguin/data</c> (on
/// every system) and the project <c>PENGUIN_PROJECT_ID</c> else <c>default_project</c>. Astra adds <c>[providers.astra]</c>
/// (<c>base_url</c> the gateway's <c>/v1</c>, <c>client_type = "openai-responses"</c> — the one of Penguin's OpenAI clients that carries a
/// thinking level as <c>reasoning.effort</c> — and a literal <c>api_key</c>), one <c>[[models]]</c> entry per bound model (<c>provider</c>,
/// <c>model_id</c>, <c>display_name</c>, <c>context_window</c>, <c>vision</c>, <c>max_tokens</c>; the entries are the array-of-tables rows whose
/// <c>provider</c> is <c>"astra"</c>, see <see cref="TomlArrayEntries"/>) and, when a model is chosen, points <c>default_model</c> at it
/// (<c>{ provider, model_id }</c>; written in the inline form, or into a <c>[default_model]</c> table that already exists). Penguin refuses a
/// <c>default_model</c> naming no entry, so the entries are always written with it. The server re-reads the file when it changes: no restart.
/// Verified against magpie's wiring (penguin 0.2.17) only.
/// </summary>
public sealed class PenguinHarnessClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private const string ModelsSelector = "models[provider=" + ProviderId + "]";

    private const string ProviderTable = "providers." + ProviderId;

    private string Root => Env.GetEnvironmentVariable("PENGUIN_HOME") is { Length: > 0 } home ? home : Env.Combine(".penguin", "data");

    private string Project
    {
        get
        {
            var id = Env.GetEnvironmentVariable("PENGUIN_PROJECT_ID")?.Trim();
            return !string.IsNullOrEmpty(id) && id is not ("." or "..") && id.IndexOfAny(['/', '\\']) < 0 ? id : "default_project";
        }
    }

    private string ConfigFile => Path.Combine(Root, Project, ".project_config.toml");

    public override string Kind => ClientKinds.PenguinHarness;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    protected override bool IsTableKeyPath(string keyPath) => keyPath == ProviderTable || keyPath == ModelsSelector;

    public override ClientDetection Detect()
    {
        var dir = Root;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"data directory {dir}");
        var exe = Env.FindOnPath("penguin");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no penguin executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        changes.AddRange(ProviderChanges(file, ctx));

        var hints = ModelHints(ctx);
        var entries = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            var info = hints.GetValueOrDefault(id);
            var entry = new JsonObject { ["provider"] = Raw(ProviderId), ["model_id"] = Raw(id) };
            if (name is not null) entry["display_name"] = Raw(name);
            if (PositiveLong(info?["contextWindow"]) is { } context) entry["context_window"] = Num(context);
            if (BoolHint(info?["vision"]) is { } vision) entry["vision"] = vision ? "true" : "false";
            if (PositiveLong(info?["maxOutputTokens"]) is { } output) entry["max_tokens"] = Num(output);
            entries.AddNode(entry);
        }
        changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, ModelsSelector,
            CurrentValue(file, ConfigFileFormat.Toml, ModelsSelector, ConfigChangeKind.Table), entries.ToJsonString(), ConfigChangeKind.Table));

        if (!string.IsNullOrEmpty(ctx.Model)) changes.AddRange(DefaultModelChanges(file, ctx.Model));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        foreach (var entry in Store.List(Kind).Where(e => e.FilePath == file).ToList())
        {
            var isTable = IsTableKeyPath(entry.KeyPath);
            var kind = isTable ? ConfigChangeKind.Table : ConfigChangeKind.Key;
            // A pre-existing provider table only had fields refreshed (tracked one by one); only a table Astra created goes whole.
            if (isTable && !entry.OriginalAbsent) continue;
            if (CurrentValue(file, ConfigFileFormat.Toml, entry.KeyPath, kind) is { } current)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, entry.KeyPath, current, null, kind));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge()
    {
        // Purge also removes a provider table and entries that pre-existed under Astra's own name.
        var file = ConfigFile;
        var changes = new List<ConfigChange>(PlanDisable().Changes);
        foreach (var path in new[] { ModelsSelector, ProviderTable })
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
        var enabled = Store.Get(Kind, ConfigFile, ModelsSelector) is not null
            && CurrentValue(ConfigFile, ConfigFileFormat.Toml, ModelsSelector, ConfigChangeKind.Table) is not null;
        return BuildStatus(detection, enabled, []);
    }

    /// <summary>Creates <c>[providers.astra]</c>, or — when it already exists — refreshes only the differing fields.</summary>
    private IEnumerable<ConfigChange> ProviderChanges(string file, EnableContext ctx)
    {
        (string Key, string Raw)[] fields =
        [
            ("base_url", Raw(ctx.GatewayBaseUrl.TrimEnd('/') + "/v1")),
            ("client_type", Raw("openai-responses")),
            ("api_key", Raw(ctx.LocalKey)),
        ];
        if (CurrentValue(file, ConfigFileFormat.Toml, ProviderTable, ConfigChangeKind.Table) is null)
        {
            var items = new JsonObject();
            foreach (var (key, raw) in fields) items[key] = raw;
            yield return new ConfigChange(file, ConfigFileFormat.Toml, ProviderTable, null, items.ToJsonString(), ConfigChangeKind.Table);
            yield break;
        }
        foreach (var (key, raw) in fields)
        {
            var path = $"{ProviderTable}.{key}";
            var before = CurrentValue(file, ConfigFileFormat.Toml, path);
            if (!string.Equals(before, raw, StringComparison.Ordinal)) yield return new ConfigChange(file, ConfigFileFormat.Toml, path, before, JsonString(raw));
        }
    }

    /// <summary><c>default_model</c> in the form the file has it: a key (inline table), or the keys of a <c>[default_model]</c> table; a key when it is absent.</summary>
    private IEnumerable<ConfigChange> DefaultModelChanges(string file, string model)
    {
        ConfigChange Set(string path, string raw) => new(file, ConfigFileFormat.Toml, path, CurrentValue(file, ConfigFileFormat.Toml, path), JsonString(raw));
        if (CurrentValue(file, ConfigFileFormat.Toml, "default_model") is null
            && CurrentValue(file, ConfigFileFormat.Toml, "default_model", ConfigChangeKind.Table) is not null)
        {
            yield return Set("default_model.provider", Raw(ProviderId));
            yield return Set("default_model.model_id", Raw(model));
            yield break;
        }
        yield return Set("default_model", $"{{ provider = {Raw(ProviderId)}, model_id = {Raw(model)} }}");
    }

    private static string Raw(string value) => TomlValueText.ForString(value);

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);
}
