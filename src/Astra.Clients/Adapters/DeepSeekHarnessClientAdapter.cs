using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// DeepSeek Harness (deepseek.com/harness, github.com/deepseek-ai/deepseek-harness; Coexist): adds a custom model route
/// to the profile's patch layer and the local key to the harness-home <c>.env</c>. Verified (Harness docs "Configure
/// models", the <c>dsh-llm-pi-ai</c> / <c>dsh-credentials-local</c> / <c>dsh-agent-default-model</c> package READMEs and
/// config catalog, and a real <c>~/.dsh/profiles/desktop/cordis.patch.yml</c>, 2026-10):
/// <list type="bullet">
/// <item><c>$DSH_HOME</c> (default <c>~/.dsh</c>) holds <c>profiles/&lt;profile&gt;/cordis.patch.yml</c>, a top-level YAML sequence of
/// rows <c>{id, name, config}</c>. The desktop app owns the <c>desktop</c> profile, <c>dsh web</c> uses <c>web</c>; Astra writes
/// to whichever of the two exists (a profile Harness never created is not invented).</item>
/// <item>Row <c>llm-pi-ai</c> (<c>@deepseek-ai/dsh-llm-pi-ai</c>): <c>config.providers.astra</c> = <c>{displayName, apiKeyEnv, api:
/// openai-completions, baseURL, models:[{id,name,contextWindow,maxTokens,input}]}</c> — the same entry the Models page's
/// "Custom model API" form writes; it is re-read on the next request. A patch replaces a row's whole config, so the existing
/// providers are kept and only <c>providers.astra</c> is touched.</item>
/// <item>Row <c>agent-default-model</c>: <c>config.provider</c> / <c>config.model</c> pick the default model for new sessions
/// when a model is chosen. Its optional <c>reasoningEffort</c> is dropped while enabled (a custom model declares no effort
/// levels, and an effort a model does not declare is refused); disabling puts it back.</item>
/// <item>Harness never keeps a literal key in the patch file (only <c>apiKeyEnv</c>, a credential reference). The reference
/// resolves from the launch environment, the stored <c>.credentials.yaml</c>, a project <c>.env</c> and finally
/// <c>$DSH_HOME/.env</c>; Astra writes <c>ASTRA_GATEWAY_API_KEY</c> to the last one, so nothing a user saved is overridden.</item>
/// </list>
/// Rows are addressed with array selectors on <c>id</c> (<see cref="JsoncEditor.Selector"/>; YAML sequences are supported by
/// <see cref="YamlEditor"/>). A row Astra creates is removed again on disable, but only while it holds nothing but Astra's
/// keys. A home-level <c>$DSH_HOME/cordis.patch.yml</c> row for <c>llm-pi-ai</c> would be applied after the profile's and is
/// warned about.
/// </summary>
public sealed class DeepSeekHarnessClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    public const string KeyVariable = "ASTRA_GATEWAY_API_KEY";

    public const long DefaultContextWindow = 128_000;

    public const long DefaultMaxTokens = 16_384;

    private const string LlmRowId = "llm-pi-ai";
    private const string LlmRowName = "@deepseek-ai/dsh-llm-pi-ai";
    private const string DefaultRowId = "agent-default-model";
    private const string DefaultRowName = "@deepseek-ai/dsh-agent-default-model";

    private static readonly string LlmRow = JsoncEditor.Selector(("id", LlmRowId));
    private static readonly string DefaultRow = JsoncEditor.Selector(("id", DefaultRowId));
    private static readonly string ProviderPath = $"{LlmRow}.config.providers.{ProviderId}";
    private static readonly string DefaultProviderPath = $"{DefaultRow}.config.provider";
    private static readonly string DefaultModelPath = $"{DefaultRow}.config.model";
    private static readonly string DefaultEffortPath = $"{DefaultRow}.config.reasoningEffort";

    private static readonly string[] Profiles = ["desktop", "web"];

    private string HomeDir =>
        Env.GetEnvironmentVariable("DSH_HOME") is { Length: > 0 } dir ? dir : Env.Combine(".dsh");

    private string EnvFile => Path.Combine(HomeDir, ".env");

    private string PatchFile(string profile) => Path.Combine(HomeDir, "profiles", profile, "cordis.patch.yml");

    /// <summary>The patch files of the profiles Harness created (desktop first).</summary>
    private List<string> ExistingPatchFiles() => Profiles
        .Where(p => Env.DirectoryExists(Path.Combine(HomeDir, "profiles", p)))
        .Select(PatchFile)
        .ToList();

    /// <summary>Existing profiles plus every patch file Astra wrote to earlier (the profile may be gone by now).</summary>
    private List<string> KnownPatchFiles() => ExistingPatchFiles()
        .Concat(Store.List(Kind).Where(e => e.FilePath.EndsWith("cordis.patch.yml", StringComparison.Ordinal)).Select(e => e.FilePath))
        .Distinct(StringComparer.Ordinal)
        .ToList();

    public override string Kind => ClientKinds.DeepSeekHarness;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = HomeDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("dsh");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no dsh executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [.. ExistingPatchFiles(), EnvFile];

    protected override bool IsTableKeyPath(string keyPath) => keyPath == LlmRow || keyPath == DefaultRow;

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var files = ExistingPatchFiles();
        if (files.Count == 0)
            throw new EditorException($"DeepSeek Harness has no desktop or web profile in {Path.Combine(HomeDir, "profiles")} yet; start it once, then enable it here.");
        var models = ModelsJson(ctx);
        if (models.Count == 0) throw new EditorException("The bound provider has no enabled models to offer to DeepSeek Harness.");
        var provider = new JsonObject
        {
            ["displayName"] = "Astra",
            ["apiKeyEnv"] = KeyVariable,
            ["api"] = "openai-completions",
            ["baseURL"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["models"] = models,
        };
        var selected = !string.IsNullOrEmpty(ctx.Model) && models.Any(m => m!["id"]!.GetValue<string>() == ctx.Model) ? ctx.Model : null;

        var changes = new List<ConfigChange>
        {
            new(EnvFile, ConfigFileFormat.Env, KeyVariable, CurrentValue(EnvFile, ConfigFileFormat.Env, KeyVariable), JsonString(ctx.LocalKey)),
        };
        foreach (var file in files)
        {
            changes.AddRange(EnsureRow(file, LlmRow, LlmRowId, LlmRowName));
            changes.Add(Key(file, ProviderPath, provider.ToJsonString()));
            if (selected is null) continue;
            changes.AddRange(EnsureRow(file, DefaultRow, DefaultRowId, DefaultRowName));
            changes.Add(Key(file, DefaultProviderPath, JsonString(ProviderId)));
            changes.Add(Key(file, DefaultModelPath, JsonString(selected)));
            // A custom model declares no effort levels; an effort it does not declare is refused.
            if (CurrentValue(file, ConfigFileFormat.Yaml, DefaultEffortPath) is { } effort)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Yaml, DefaultEffortPath, effort, null));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        foreach (var file in KnownPatchFiles())
        {
            if (!Env.FileExists(file)) continue;
            // A default model the user picked afterwards (another provider) is theirs and stays.
            var ownsDefault = StringOf(CurrentValue(file, ConfigFileFormat.Yaml, DefaultProviderPath)) == ProviderId;
            var owned = ownsDefault ? new[] { DefaultModelPath, DefaultProviderPath, ProviderPath } : [ProviderPath];
            foreach (var path in owned)
            {
                if (CurrentValue(file, ConfigFileFormat.Yaml, path) is { } current)
                    changes.Add(new ConfigChange(file, ConfigFileFormat.Yaml, path, current, null));
            }
            // The reasoning effort dropped on enable comes back from its recorded original.
            if (ownsDefault && Store.Get(Kind, file, DefaultEffortPath) is not null)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Yaml, DefaultEffortPath, null, null));
            AddRowRemoval(changes, file, LlmRow, ["config", "providers", ProviderId]);
            if (ownsDefault) AddRowRemoval(changes, file, DefaultRow, ["config", "provider"], ["config", "model"]);
        }
        if (CurrentValue(EnvFile, ConfigFileFormat.Env, KeyVariable) is { } key)
            changes.Add(new ConfigChange(EnvFile, ConfigFileFormat.Env, KeyVariable, key, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var warnings = new List<string>();
        var files = ExistingPatchFiles();
        if (files.Count == 0)
            warnings.Add($"No DeepSeek Harness profile (desktop or web) exists in {Path.Combine(HomeDir, "profiles")} yet; start Harness once before enabling.");
        var homePatch = Path.Combine(HomeDir, "cordis.patch.yml");
        if (Env.FileExists(homePatch) && CurrentValue(homePatch, ConfigFileFormat.Yaml, LlmRow) is not null)
            warnings.Add($"{homePatch} defines an llm-pi-ai row; Harness applies it after the profile's, so it replaces the Astra provider (not modified by Astra).");
        var enabled = KnownPatchFiles().Any(f => StillOurs(f, ConfigFileFormat.Yaml, ProviderPath));
        return BuildStatus(detection, enabled, warnings);
    }

    private ConfigChange Key(string file, string path, string valueJson) =>
        new(file, ConfigFileFormat.Yaml, path, CurrentValue(file, ConfigFileFormat.Yaml, path), valueJson);

    /// <summary>Creates the row (id and name only) when the patch file has none; its keys are added as separate tracked changes.</summary>
    private IEnumerable<ConfigChange> EnsureRow(string file, string rowPath, string id, string name)
    {
        if (CurrentValue(file, ConfigFileFormat.Yaml, rowPath, ConfigChangeKind.Table) is not null) yield break;
        var row = new JsonObject { ["id"] = id, ["name"] = name };
        yield return new ConfigChange(file, ConfigFileFormat.Yaml, rowPath, null, row.ToJsonString(), ConfigChangeKind.Table);
    }

    /// <summary>
    /// Removes a row Astra created, but only when taking Astra's keys out leaves nothing but <c>id</c> and <c>name</c> — a
    /// provider or setting the user added to it later keeps the row.
    /// </summary>
    private void AddRowRemoval(List<ConfigChange> changes, string file, string rowPath, params string[][] ownedPaths)
    {
        if (Store.Get(Kind, file, rowPath) is not { OriginalAbsent: true }) return;
        if (CurrentValue(file, ConfigFileFormat.Yaml, rowPath) is not { } json || JsonNode.Parse(json) is not JsonObject row) return;
        foreach (var path in ownedPaths) RemovePath(row, path);
        if (row["config"] is JsonObject { Count: 0 }) row.Remove("config");
        if (row.Count == 2 && row.ContainsKey("id") && row.ContainsKey("name"))
            changes.Add(new ConfigChange(file, ConfigFileFormat.Yaml, rowPath, "true", null, ConfigChangeKind.Table));
    }

    /// <summary>Removes the member at a path and the objects that only held it.</summary>
    private static void RemovePath(JsonObject root, string[] parts)
    {
        var chain = new List<JsonObject> { root };
        var current = root;
        foreach (var part in parts[..^1])
        {
            if (current[part] is not JsonObject next) return;
            chain.Add(next);
            current = next;
        }
        current.Remove(parts[^1]);
        for (var i = chain.Count - 1; i >= 1 && chain[i].Count == 0; i--) chain[i - 1].Remove(parts[i - 1]);
    }

    private static JsonArray ModelsJson(EnableContext ctx)
    {
        var hints = ModelHints(ctx);
        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            hints.TryGetValue(id, out var info);
            var context = PositiveLong(info?["contextWindow"]) ?? DefaultContextWindow;
            var maxTokens = PositiveLong(info?["maxOutputTokens"]) ?? DefaultMaxTokens;
            if (maxTokens >= context) maxTokens = Math.Max(1, context / 4);
            var model = new JsonObject { ["id"] = id, ["name"] = name ?? id, ["contextWindow"] = context, ["maxTokens"] = maxTokens };
            if (BoolHint(info?["vision"]) == true) model["input"] = new JsonArray("text", "image");
            models.AddNode(model);
        }
        return models;
    }
}
