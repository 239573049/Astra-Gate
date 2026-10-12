using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Codex CLI (Switch): adds a <c>[model_providers.astra]</c> table and flips the top-level
/// <c>model_provider</c> (plus optional <c>model</c>). Disabling restores the file byte-identically:
/// a provider table Astra created is removed again, and fields refreshed inside a user's own
/// table are reset to their originals. <see cref="PlanPurge"/> additionally removes the table even
/// when it pre-existed. <c>auth.json</c> is never touched.
/// With the "modelCatalog" option it also writes <see cref="CatalogFileName"/> beside <c>config.toml</c> and points
/// <c>model_catalog_json</c> at it. Codex's catalog replaces its built-in model list, so the file keeps every model
/// Codex already had (the user's own catalog when one was set, else Codex's <c>models_cache.json</c>) and appends the
/// bound providers' models it lacks, each cloned from one of those entries (instructions included: an entry without
/// them fails the whole file). Without such a source nothing is written. Codex reads the catalog only at startup.
/// </summary>
public sealed class CodexClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    /// <summary>The model catalog file Astra owns in the Codex home.</summary>
    public const string CatalogFileName = "astra-models.json";

    private const string CatalogKey = "models";
    private const string CatalogPathKey = "model_catalog_json";

    /// <summary>Fields of a cloned entry that describe the OpenAI model it was cloned from, not the Astra model.</summary>
    private static readonly string[] TemplateOnlyFields =
    [
        "context_window", "max_context_window", "auto_compact_token_limit", "model_specialty", "available_access_programs",
        "default_service_tier", "comp_hash", "auto_review_model_override", "tool_mode", "multi_agent_version",
        "multi_agent_reasoning_effort",
    ];

    /// <summary>OpenAI-backend features a cloned entry must not claim for a model served through the gateway.</summary>
    private static readonly string[] BackendFeatureFlags =
    [
        "support_verbosity", "supports_search_tool", "use_responses_lite", "supports_experimental_context",
        "supports_reasoning_effort_updates", "supports_image_detail_original",
    ];

    private string ConfigDir =>
        Env.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir ? dir : Env.Combine(".codex");

    private string ConfigFile => Path.Combine(ConfigDir, "config.toml");

    private string CatalogFile => Path.Combine(ConfigDir, CatalogFileName);

    public override string Kind => ClientKinds.Codex;

    public override ClientMode Mode => ClientMode.Switch;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = ConfigDir;
        var exe = Env.FindOnPath("codex");
        if (Env.DirectoryExists(dir))
            return ClientDetection.Found(null, $"config directory {dir}");
        if (exe is not null)
            return ClientDetection.Found(null, $"executable {exe}");
        return ClientDetection.NotFound($"no {dir} and no codex executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() =>
        Store.Get(Kind, CatalogFile, CatalogKey) is null ? [ConfigFile] : [ConfigFile, CatalogFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        // Stored "after" values are JSON texts of RAW TOML source (a TOML string includes its quotes).
        var changes = new List<ConfigChange> { TopKey(file, "model_provider", JsonString(TomlRaw(ProviderId))) };
        if (!string.IsNullOrEmpty(ctx.Model)) changes.Add(TopKey(file, "model", JsonString(TomlRaw(ctx.Model))));
        if (ClientOptions.GetInt(ctx.Extras, ClientOptions.ContextWindow) is { } window)
            changes.Add(TopKey(file, "model_context_window", JsonString(window.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        if (ClientOptions.GetInt(ctx.Extras, ClientOptions.AutoCompactTokenLimit) is { } compact)
            changes.Add(TopKey(file, "model_auto_compact_token_limit", JsonString(compact.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        if (ClientOptions.GetChoice(ctx.Extras, ClientOptions.ReasoningEffort) is { } effort)
            changes.Add(TopKey(file, "model_reasoning_effort", JsonString(TomlRaw(effort))));
        changes.AddRange(ProviderTableChanges(file, ctx));
        if (ClientOptions.GetBool(ctx.Extras, ClientOptions.ModelCatalog) && CatalogModels(ctx) is { } catalog)
        {
            changes.Add(new ConfigChange(CatalogFile, ConfigFileFormat.Json, CatalogKey,
                CurrentValue(CatalogFile, ConfigFileFormat.Json, CatalogKey), catalog.ToJsonString()));
            changes.Add(TopKey(file, CatalogPathKey, JsonString(TomlRaw(CatalogFile))));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        foreach (var key in new[] { "model_provider", "model" }.Concat(ClientOptions.ConfigKeyPaths(Kind).Where(k => k != CatalogKey)))
        {
            var before = CurrentValue(file, ConfigFileFormat.Toml, key);
            if (before is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, key, before, null));
        }
        // The catalog file is Astra's own: restoring removes its key, and with it the file Astra created.
        if (Store.Get(Kind, CatalogFile, CatalogKey) is not null)
            changes.Add(new ConfigChange(CatalogFile, ConfigFileFormat.Json, CatalogKey,
                CurrentValue(CatalogFile, ConfigFileFormat.Json, CatalogKey), null));
        // Fields refreshed inside a pre-existing provider table were tracked individually; restore them.
        changes.AddRange(TrackedFieldRestores(file, "base_url", "experimental_bearer_token"));
        // When the provider table itself did not exist before the takeover, remove it again so the
        // file restores byte-identically.
        var tablePath = $"model_providers.{ProviderId}";
        if (Store.Get(Kind, file, tablePath) is { OriginalAbsent: true })
            changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, tablePath,
                CurrentValue(file, ConfigFileFormat.Toml, tablePath, ConfigChangeKind.Table), null,
                ConfigChangeKind.Table));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge()
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>(PlanDisable().Changes);
        changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, $"model_providers.{ProviderId}",
            CurrentValue(file, ConfigFileFormat.Toml, $"model_providers.{ProviderId}", ConfigChangeKind.Table), null,
            ConfigChangeKind.Table));
        return BuildPlan(changes);
    }

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var current = CurrentValue(ConfigFile, ConfigFileFormat.Toml, "model_provider");
        var entry = Store.Get(Kind, ConfigFile, "model_provider");
        var enabled = entry is not null
            && string.Equals(current, ConfigValueCodec.DecodeToSource(ConfigFileFormat.Toml, entry.AppliedValueJson), StringComparison.Ordinal);
        return BuildStatus(detection, enabled, []);
    }

    protected override bool IsTableKeyPath(string keyPath) => keyPath == $"model_providers.{ProviderId}";

    /// <summary>The provider table: created wholesale on first enable, otherwise only URL/key fields refreshed.</summary>
    private IEnumerable<ConfigChange> ProviderTableChanges(string file, EnableContext ctx)
    {
        var tablePath = $"model_providers.{ProviderId}";
        var baseUrl = TomlRaw(ctx.GatewayBaseUrl.TrimEnd('/') + "/v1");
        var token = TomlRaw(ctx.LocalKey);
        if (CurrentValue(file, ConfigFileFormat.Toml, tablePath, ConfigChangeKind.Table) is null)
        {
            var items = new JsonObject
            {
                ["name"] = TomlRaw("Astra"),
                ["base_url"] = baseUrl,
                ["wire_api"] = TomlRaw("responses"),
                ["experimental_bearer_token"] = token,
                ["requires_openai_auth"] = "false",
            };
            yield return new ConfigChange(file, ConfigFileFormat.Toml, tablePath,
                null, items.ToJsonString(), ConfigChangeKind.Table);
            yield break;
        }
        foreach (var (key, value) in new[] { ("base_url", baseUrl), ("experimental_bearer_token", token) })
        {
            var fieldPath = $"{tablePath}.{key}";
            var before = CurrentValue(file, ConfigFileFormat.Toml, fieldPath);
            if (!string.Equals(before, value, StringComparison.Ordinal))
                yield return new ConfigChange(file, ConfigFileFormat.Toml, fieldPath, before, JsonString(value));
        }
    }

    /// <summary>Restore changes for tracked fields of an existing provider table (absent when we created it).</summary>
    private IEnumerable<ConfigChange> TrackedFieldRestores(string file, params string[] fields)
    {
        var tablePath = $"model_providers.{ProviderId}";
        foreach (var field in fields)
        {
            var fieldPath = $"{tablePath}.{field}";
            if (Store.Get(Kind, file, fieldPath) is null) continue;
            var before = CurrentValue(file, ConfigFileFormat.Toml, fieldPath);
            if (before is not null) yield return new ConfigChange(file, ConfigFileFormat.Toml, fieldPath, before, null);
        }
    }

    // ---------------------------------------------------------------- model catalog

    /// <summary>
    /// The catalog's model list: the models Codex already has, then the bound providers' models it lacks. Null when
    /// there is no source of Codex entries with instructions to clone, or no model to add.
    /// </summary>
    private JsonArray? CatalogModels(EnableContext ctx)
    {
        var cache = CatalogEntries(Path.Combine(ConfigDir, "models_cache.json"));
        var existing = (UserCatalogPath() is { } userPath ? CatalogEntries(userPath) : null) ?? cache;
        if (existing is null) return null;
        var template = Template(existing) ?? (cache is null ? null : Template(cache));
        var models = ModelEntries(ctx);
        if (template is null || models.Count == 0) return null;
        var instructions = Instructions(template)!;

        var result = new JsonArray();
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        var priority = 0;
        foreach (var entry in existing)
        {
            // An entry without instructions would make Codex reject the whole file.
            if (Instructions(entry) is null) entry["base_instructions"] = instructions;
            if (entry["priority"] is JsonValue p && p.TryGetValue(out int value)) priority = Math.Max(priority, value);
            slugs.Add(Str(entry["slug"])!);
            result.AddNode(entry);
        }
        var hints = ModelHints(ctx);
        foreach (var (id, name) in models)
        {
            if (!slugs.Add(id)) continue; // Codex already knows this model: keep its own entry
            var entry = (JsonObject)template.DeepClone();
            foreach (var field in TemplateOnlyFields) entry.Remove(field);
            foreach (var flag in BackendFeatureFlags) entry[flag] = false;
            entry["slug"] = id;
            entry["display_name"] = name ?? id;
            entry["description"] = "Astra";
            entry["visibility"] = "list";
            entry["supported_in_api"] = true;
            entry["priority"] = ++priority;
            entry["upgrade"] = null;
            entry["availability_nux"] = null;
            entry["default_verbosity"] = null;
            entry["apply_patch_tool_type"] = null; // Codex's own choice for models it has no metadata for
            entry["experimental_supported_tools"] = new JsonArray();
            entry["additional_speed_tiers"] = new JsonArray();
            entry["service_tiers"] = new JsonArray();
            entry.Remove("input_modalities");
            if (hints.TryGetValue(id, out var hint))
            {
                if (PositiveLong(hint["contextWindow"]) is { } context) entry["context_window"] = context;
                if (BoolHint(hint["vision"]) is { } vision)
                    entry["input_modalities"] = vision ? new JsonArray("text", "image") : new JsonArray("text");
                if (BoolHint(hint["reasoning"]) == false)
                {
                    entry["supported_reasoning_levels"] = new JsonArray();
                    entry.Remove("default_reasoning_level");
                    entry["supports_reasoning_summary_parameter"] = false;
                }
            }
            result.AddNode(entry);
        }
        return result;
    }

    /// <summary>
    /// The catalog the user pointed <c>model_catalog_json</c> at: the current value, or — once it names Astra's
    /// file — the value Astra replaced. Null when there is none.
    /// </summary>
    private string? UserCatalogPath()
    {
        if (TomlString(CurrentValue(ConfigFile, ConfigFileFormat.Toml, CatalogPathKey)) is not { } current) return null;
        var path = ResolvePath(current);
        if (!SamePath(path, CatalogFile)) return path;
        return Store.Get(Kind, ConfigFile, CatalogPathKey) is { OriginalAbsent: false, OriginalValueJson: { } original }
               && TomlString(ConfigValueCodec.DecodeToSource(ConfigFileFormat.Toml, original)) is { } replaced
               && !SamePath(ResolvePath(replaced), CatalogFile)
            ? ResolvePath(replaced)
            : null;
    }

    /// <summary>The <c>models</c> entries (objects with a slug) of a catalog-shaped JSON file; null when unreadable.</summary>
    private List<JsonObject>? CatalogEntries(string path)
    {
        if (Env.ReadTextOrNull(path) is not { } text) return null;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject { } root || root["models"] is not JsonArray models) return null;
            return models.OfType<JsonObject>().Where(m => !string.IsNullOrEmpty(Str(m["slug"])))
                .Select(m => (JsonObject)m.DeepClone()).ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The entry new models are cloned from: a listed one with instructions, else any with instructions.</summary>
    private static JsonObject? Template(IReadOnlyList<JsonObject> entries) =>
        entries.FirstOrDefault(m => Instructions(m) is { Length: > 0 } && Str(m["visibility"]) == "list")
        ?? entries.FirstOrDefault(m => Instructions(m) is { Length: > 0 });

    /// <summary>An entry's instructions: <c>model_messages.instructions_template</c>, else the legacy <c>base_instructions</c>.</summary>
    private static string? Instructions(JsonObject entry) =>
        Str((entry["model_messages"] as JsonObject)?["instructions_template"]) ?? Str(entry["base_instructions"]);

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>The string a raw TOML value source holds, or null.</summary>
    private static string? TomlString(string? rawSource) =>
        rawSource is null ? null : Import.CodexConfig.Parse("v = " + rawSource)?.TopString("v");

    /// <summary>A path as Codex resolves it: <c>~/</c> from home, relative paths from the config.toml directory.</summary>
    private string ResolvePath(string path) =>
        Path.GetFullPath(path.StartsWith("~/", StringComparison.Ordinal) ? Env.Combine(path[2..])
            : Path.IsPathRooted(path) ? path : Path.Combine(ConfigDir, path));

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private ConfigChange TopKey(string file, string key, string afterJson)
    {
        var before = CurrentValue(file, ConfigFileFormat.Toml, key);
        return new ConfigChange(file, ConfigFileFormat.Toml, key, before, afterJson);
    }

    private static string TomlRaw(string value) => Editing.TomlValueText.ForString(value);
}
