using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Pi coding agent (Coexist): adds <c>providers.astra</c> (an <c>openai-completions</c> endpoint listing
/// the bound provider's models) to <c>&lt;agent-dir&gt;/models.json</c> and, when a model is chosen, sets
/// <c>defaultProvider</c>/<c>defaultModel</c> in <c>&lt;agent-dir&gt;/settings.json</c>. The agent directory
/// is <c>~/.pi/agent</c> unless PI_CODING_AGENT_DIR overrides it (pi.dev docs: configuration.md,
/// models.md, settings.md). Pi's own providers and logins stay untouched; <c>/model</c> reloads models.json.
/// <para>
/// Pi resolves <c>api</c> and <c>baseUrl</c> per model (<c>model.api ?? provider.api</c>), so a model whose native protocol is
/// Responses, Anthropic or Gemini carries its own <c>api</c> (and the base that protocol's SDK needs, see
/// <see cref="PiAiModels"/>), and every model carries the reasoning / input / limit hints Astra knows.
/// </para>
/// <para>
/// The same adapter serves Pi's forks through a <see cref="PiFlavour"/> (OmO, Prime Agent, mu, StepCode, Aside): they keep Pi's
/// <c>models.json</c> in an agent directory of their own; only where the start-up model is written differs (StepCode: top-level
/// keys of <c>config.toml</c>; Aside: a <c>defaultModel</c> object). The forks are verified against magpie's wiring only.
/// </para>
/// </summary>
public sealed class PiClientAdapter(ClientEnvironment env, IClientConfigStateStore store, PiFlavour flavour)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    /// <summary>Pi itself.</summary>
    public PiClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
        : this(env, store, PiFlavour.Pi)
    {
    }

    private string AgentDir => flavour.AgentDir(Env);

    private string ModelsFile => Path.Combine(AgentDir, "models.json");

    private bool TomlSettings => flavour.Settings == PiSettings.Toml;

    private ConfigFileFormat SettingsFormat => TomlSettings ? ConfigFileFormat.Toml : ConfigFileFormat.Json;

    private string SettingsFile => Path.Combine(AgentDir, TomlSettings ? "config.toml" : "settings.json");

    private static string ProviderPath => $"providers.{ProviderId}";

    public override string Kind => flavour.Kind;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = AgentDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        // A command name other tools use too (mu, step) is not looked up: the agent directory alone says it is installed.
        if (flavour.Executable is null) return ClientDetection.NotFound($"no {dir}");
        var exe = Env.FindOnPath(flavour.Executable);
        return exe is not null
            ? ClientDetection.Found(null, $"executable {exe}")
            : ClientDetection.NotFound($"no {dir} and no {flavour.Executable} executable on PATH");
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
        var hints = ModelHints(ctx);
        var protocols = ModelProtocols(ctx, ApiProtocol.OpenAIChat);
        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            var model = new JsonObject { ["id"] = id };
            if (name is not null) model["name"] = name;
            if (protocols[id] != ApiProtocol.OpenAIChat)
            {
                model["api"] = PiAiModels.ApiOf(protocols[id]);
                var baseUrl = PiAiModels.BaseUrlFor(ctx.GatewayBaseUrl, protocols[id]);
                if (baseUrl != (string?)provider["baseUrl"]) model["baseUrl"] = baseUrl;
            }
            PiAiModels.AddHints(model, hints.GetValueOrDefault(id));
            models.AddNode(model);
        }
        if (models.Count > 0) provider["models"] = models;

        var changes = new List<ConfigChange>
        {
            new(ModelsFile, ConfigFileFormat.Json, ProviderPath, CurrentValue(ModelsFile, ConfigFileFormat.Json, ProviderPath),
                provider.ToJsonString()),
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            if (flavour.Settings == PiSettings.AsideObject)
                changes.Add(AsideDefaultModel(ctx.Model));
            else
            {
                changes.Add(SettingsKey("defaultProvider", ProviderId));
                changes.Add(SettingsKey("defaultModel", ctx.Model));
            }
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
        if (flavour.Settings == PiSettings.AsideObject)
        {
            var current = CurrentValue(SettingsFile, ConfigFileFormat.Json, "defaultModel");
            if (current is not null && TryObject(current)?["provider"]?.GetValue<string>() == ProviderId)
                changes.Add(new ConfigChange(SettingsFile, ConfigFileFormat.Json, "defaultModel", current, null));
            return BuildPlan(changes);
        }
        var defaultProvider = CurrentValue(SettingsFile, SettingsFormat, "defaultProvider");
        if (defaultProvider is not null && IsProviderValue(defaultProvider))
        {
            changes.Add(new ConfigChange(SettingsFile, SettingsFormat, "defaultProvider", defaultProvider, null));
            var defaultModel = CurrentValue(SettingsFile, SettingsFormat, "defaultModel");
            if (defaultModel is not null && Store.Get(Kind, SettingsFile, "defaultModel") is not null)
                changes.Add(new ConfigChange(SettingsFile, SettingsFormat, "defaultModel", defaultModel, null));
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
        var warnings = new List<string>();
        if (enabled && flavour.Settings == PiSettings.AsideObject)
            warnings.Add("Aside keeps its settings in memory while it runs and writes them back: restart Aside so the model choice sticks.");
        return BuildStatus(detection, enabled, warnings);
    }

    private bool IsProviderValue(string source) =>
        TomlSettings ? source == Editing.TomlValueText.ForString(ProviderId) : StringOf(source) == ProviderId;

    private ConfigChange SettingsKey(string key, string value) =>
        new(SettingsFile, SettingsFormat, key, CurrentValue(SettingsFile, SettingsFormat, key),
            JsonString(TomlSettings ? Editing.TomlValueText.ForString(value) : value));

    /// <summary>Aside's start-up model is one object, <c>defaultModel: {provider, modelId, thinkingLevel}</c>; its other members stay.</summary>
    private ConfigChange AsideDefaultModel(string model)
    {
        var before = CurrentValue(SettingsFile, ConfigFileFormat.Json, "defaultModel");
        var selection = before is not null && TryObject(before) is { } existing ? existing : new JsonObject();
        selection["provider"] = ProviderId;
        selection["modelId"] = model;
        selection["thinkingLevel"] ??= "medium";
        return new ConfigChange(SettingsFile, ConfigFileFormat.Json, "defaultModel", before, selection.ToJsonString());
    }

    private static JsonObject? TryObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

/// <summary>Where a Pi fork keeps its start-up model.</summary>
public enum PiSettings
{
    /// <summary><c>settings.json</c> with top-level <c>defaultProvider</c> / <c>defaultModel</c> (Pi, OmO, Prime Agent, mu).</summary>
    Json,

    /// <summary>The same two keys at the top of <c>config.toml</c> (StepCode).</summary>
    Toml,

    /// <summary><c>settings.json</c> with a <c>defaultModel</c> object (Aside).</summary>
    AsideObject,
}

/// <summary>The Pi build a <see cref="PiClientAdapter"/> configures (Pi and its forks share <c>models.json</c>).</summary>
/// <param name="Kind">The client kind.</param>
/// <param name="Executable">The command on PATH, or null when its name is other tools' too (detection is the directory alone).</param>
/// <param name="AgentDir">The agent directory, honouring the client's environment overrides.</param>
/// <param name="Settings">Where the start-up model goes.</param>
public sealed record PiFlavour(string Kind, string? Executable, Func<ClientEnvironment, string> AgentDir, PiSettings Settings = PiSettings.Json)
{
    public static readonly PiFlavour Pi = new(ClientKinds.Pi, "pi",
        env => env.GetEnvironmentVariable("PI_CODING_AGENT_DIR") is { Length: > 0 } dir ? dir : env.Combine(".pi", "agent"));

    /// <summary>OmO (omo-ai, engine senpi): <c>~/.omo/agent</c>, or an absolute <c>OMO_CODING_AGENT_DIR</c> / <c>SENPI_CODING_AGENT_DIR</c>.</summary>
    public static readonly PiFlavour OmO = new(ClientKinds.OmO, "omo",
        env => FirstAbsolute(env, "OMO_CODING_AGENT_DIR", "SENPI_CODING_AGENT_DIR") ?? env.Combine(".omo", "agent"));

    /// <summary>Prime Agent (PrimeIntellect-ai/prime-agent): <c>~/.prime/agent</c>, or <c>PRIME_AGENT_CODING_AGENT_DIR</c>.</summary>
    public static readonly PiFlavour PrimeAgent = new(ClientKinds.PrimeAgent, "prime-agent",
        env => FirstAbsolute(env, "PRIME_AGENT_CODING_AGENT_DIR") ?? env.Combine(".prime", "agent"));

    /// <summary>mu (npm mu-agent, Pi with a judgment kernel): <c>~/.mu/agent</c> (<c>~/.kyrn/agent</c> while only that exists), or one of four variables.</summary>
    public static readonly PiFlavour Mu = new(ClientKinds.Mu, null,
        env => FirstAbsolute(env, "MU_AGENT_DIR", "KYRN_AGENT_DIR", "MU_CODING_AGENT_DIR", "KYRN_CODING_AGENT_DIR")
            ?? (!env.DirectoryExists(env.Combine(".mu")) && env.DirectoryExists(env.Combine(".kyrn"))
                ? env.Combine(".kyrn", "agent")
                : env.Combine(".mu", "agent")));

    /// <summary>StepCode: <c>~/.stepcode</c> (<c>STEPCODE_CONFIG_DIR</c> names another folder under home, an absolute <c>STEP_CODING_AGENT_DIR</c> its parent); <c>config.toml</c> + <c>models.json</c>.</summary>
    public static readonly PiFlavour StepCode = new(ClientKinds.StepCode, null, env =>
    {
        if (env.GetEnvironmentVariable("STEP_CODING_AGENT_DIR")?.Trim() is { Length: > 0 } agent && Path.IsPathRooted(agent))
            return Path.GetDirectoryName(agent.TrimEnd('/', '\\'))!;
        return env.GetEnvironmentVariable("STEPCODE_CONFIG_DIR")?.Trim() is { Length: > 0 } custom ? env.Combine(custom) : env.Combine(".stepcode");
    }, PiSettings.Toml);

    /// <summary>Aside: the first account's folder, <c>~/.aside/u/0</c>.</summary>
    public static readonly PiFlavour Aside = new(ClientKinds.Aside, "aside", env => env.Combine(".aside", "u", "0"), PiSettings.AsideObject);

    /// <summary>The first of the named variables that holds an absolute path (a leading <c>~</c> stands for the home directory).</summary>
    private static string? FirstAbsolute(ClientEnvironment env, params string[] names)
    {
        foreach (var name in names)
        {
            var value = env.GetEnvironmentVariable(name)?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
            if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
                value = env.HomeDirectory + value[1..];
            if (Path.IsPathRooted(value)) return value.TrimEnd('/', '\\');
        }
        return null;
    }
}
