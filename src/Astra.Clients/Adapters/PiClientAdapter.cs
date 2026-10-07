using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Pi coding agent (Coexist): adds <c>providers.astra</c> (an <c>openai-completions</c> endpoint listing
/// the bound provider's models) to <c>&lt;agent-dir&gt;/models.json</c> and, when a model is chosen, sets
/// <c>defaultProvider</c>/<c>defaultModel</c> in <c>&lt;agent-dir&gt;/settings.json</c>. The agent directory
/// is <c>~/.pi/agent</c> unless PI_CODING_AGENT_DIR overrides it (pi.dev docs: configuration.md,
/// models.md, settings.md). Pi's own providers and logins stay untouched; <c>/model</c> reloads models.json.
/// </summary>
public sealed class PiClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private string AgentDir =>
        Env.GetEnvironmentVariable("PI_CODING_AGENT_DIR") is { Length: > 0 } dir ? dir : Env.Combine(".pi", "agent");

    private string ModelsFile => Path.Combine(AgentDir, "models.json");

    private string SettingsFile => Path.Combine(AgentDir, "settings.json");

    private static string ProviderPath => $"providers.{ProviderId}";

    public override string Kind => ClientKinds.Pi;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = AgentDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("pi");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no pi executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ModelsFile, SettingsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var provider = new JsonObject
        {
            ["baseUrl"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["api"] = "openai-completions",
            ["apiKey"] = ctx.LocalKey,
        };
        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            var model = new JsonObject { ["id"] = id };
            if (name is not null) model["name"] = name;
            models.Add(model);
        }
        if (models.Count > 0) provider["models"] = models;

        var changes = new List<ConfigChange>
        {
            new(ModelsFile, ConfigFileFormat.Json, ProviderPath, CurrentValue(ModelsFile, ConfigFileFormat.Json, ProviderPath),
                provider.ToJsonString()),
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            changes.Add(SettingsKey("defaultProvider", ProviderId));
            changes.Add(SettingsKey("defaultModel", ctx.Model));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        var providerBefore = CurrentValue(ModelsFile, ConfigFileFormat.Json, ProviderPath);
        if (providerBefore is not null)
            changes.Add(new ConfigChange(ModelsFile, ConfigFileFormat.Json, ProviderPath, providerBefore, null));
        // The startup selection is only ours while it still names the Astra provider.
        var defaultProvider = CurrentValue(SettingsFile, ConfigFileFormat.Json, "defaultProvider");
        if (defaultProvider is not null && StringOf(defaultProvider) == ProviderId)
        {
            changes.Add(new ConfigChange(SettingsFile, ConfigFileFormat.Json, "defaultProvider", defaultProvider, null));
            var defaultModel = CurrentValue(SettingsFile, ConfigFileFormat.Json, "defaultModel");
            if (defaultModel is not null && Store.Get(Kind, SettingsFile, "defaultModel") is not null)
                changes.Add(new ConfigChange(SettingsFile, ConfigFileFormat.Json, "defaultModel", defaultModel, null));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var entry = Store.Get(Kind, ModelsFile, ProviderPath);
        var enabled = entry is not null && ConfigValueCodec.EqualsValue(
            ConfigFileFormat.Json,
            CurrentValue(ModelsFile, ConfigFileFormat.Json, ProviderPath),
            entry.AppliedValueJson);
        return BuildStatus(detection, enabled, []);
    }

    private ConfigChange SettingsKey(string key, string value) =>
        new(SettingsFile, ConfigFileFormat.Json, key, CurrentValue(SettingsFile, ConfigFileFormat.Json, key), JsonString(value));
}
