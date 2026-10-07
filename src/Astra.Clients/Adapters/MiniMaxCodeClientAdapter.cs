using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// MiniMax Code (<c>mcode</c>, npm <c>@minimax-ai/code</c>; Coexist): adds a custom provider
/// <c>custom_provider.astra</c> (<c>api: openai-completions</c>, <c>options.baseURL</c>/<c>options.apiKey</c>,
/// the bound provider's models keyed by id) to <c>&lt;data-dir&gt;/config.yaml</c> and, when a model is
/// chosen, selects it with <c>defaultModel: custom_provider:astra/&lt;id&gt;</c>. The data directory is
/// <c>~/.minimax</c> unless MINIMAX_DATA_DIR (or MAVIS_DATA_DIR) overrides it. The layout follows the
/// package's README and the config schema in its bundle (no separate reference docs yet). MiniMax's own
/// login and providers stay untouched.
/// </summary>
public sealed class MiniMaxCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    /// <summary>Prefix of model references that select the Astra provider.</summary>
    public const string ModelRefPrefix = "custom_provider:" + ProviderId + "/";

    private string DataDir =>
        Env.GetEnvironmentVariable("MINIMAX_DATA_DIR") is { Length: > 0 } dir ? dir
        : Env.GetEnvironmentVariable("MAVIS_DATA_DIR") is { Length: > 0 } legacy ? legacy
        : Env.Combine(".minimax");

    private string ConfigFile => Path.Combine(DataDir, "config.yaml");

    private static string ProviderPath => $"custom_provider.{ProviderId}";

    private const string DefaultModelPath = "defaultModel";

    public override string Kind => ClientKinds.MiniMaxCode;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = DataDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("mcode");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no mcode executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var models = new JsonObject();
        foreach (var (id, name) in ModelEntries(ctx))
            models[id] = name is null ? new JsonObject() : new JsonObject { ["name"] = name };
        var provider = new JsonObject
        {
            ["name"] = "Astra",
            ["api"] = "openai-completions",
            ["options"] = new JsonObject
            {
                ["baseURL"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
                ["apiKey"] = ctx.LocalKey,
            },
            ["models"] = models,
        };
        var changes = new List<ConfigChange>
        {
            new(ConfigFile, ConfigFileFormat.Yaml, ProviderPath, CurrentValue(ConfigFile, ConfigFileFormat.Yaml, ProviderPath),
                provider.ToJsonString()),
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Yaml, DefaultModelPath,
                CurrentValue(ConfigFile, ConfigFileFormat.Yaml, DefaultModelPath), JsonString(ModelRefPrefix + ctx.Model)));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        var providerBefore = CurrentValue(ConfigFile, ConfigFileFormat.Yaml, ProviderPath);
        if (providerBefore is not null)
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Yaml, ProviderPath, providerBefore, null));
        // A model the user picked afterwards (another provider) is theirs and stays.
        var model = CurrentValue(ConfigFile, ConfigFileFormat.Yaml, DefaultModelPath);
        if (StringOf(model)?.StartsWith(ModelRefPrefix, StringComparison.Ordinal) == true)
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Yaml, DefaultModelPath, model, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var entry = Store.Get(Kind, ConfigFile, ProviderPath);
        var enabled = entry is not null && ConfigValueCodec.EqualsValue(
            ConfigFileFormat.Yaml,
            CurrentValue(ConfigFile, ConfigFileFormat.Yaml, ProviderPath),
            entry.AppliedValueJson);
        return BuildStatus(detection, enabled, []);
    }
}
