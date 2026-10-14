using System.Text.Json.Nodes;
using Astra.Core;
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
/// <para>
/// <c>api</c> is read per provider only (<c>openai-completions</c> | <c>openai-responses</c> | <c>anthropic-messages</c>; anything
/// else silently becomes Anthropic, so Gemini is never written), so models whose native protocol is Responses or Anthropic
/// are listed under <c>custom_provider.astra-responses</c> / <c>astra-anthropic</c>, which exist only while they have models.
/// MiniMax trims the base for each protocol itself, so all three share <c>…/v1</c>. Models carry the limits, image input and
/// reasoning flag Astra knows (MiniMax otherwise assumes 200k / 16k, text only, thinking off).
/// </para>
/// </summary>
public sealed class MiniMaxCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    /// <summary>Prefix of model references that select the Astra provider.</summary>
    public const string ModelRefPrefix = "custom_provider:" + ProviderId + "/";

    /// <summary>The providers Astra writes, one per protocol: key, <c>api</c> and display name.</summary>
    private static readonly (ApiProtocol Protocol, string Key, string Name)[] Providers =
    [
        (ApiProtocol.OpenAIChat, ProviderId, "Astra"),
        (ApiProtocol.OpenAIResponses, ProviderId + "-responses", "Astra (Responses)"),
        (ApiProtocol.Anthropic, ProviderId + "-anthropic", "Astra (Anthropic)"),
    ];

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
        var hints = ModelHints(ctx);
        var protocols = ModelProtocols(ctx, ApiProtocol.OpenAIChat);
        var entries = ModelEntries(ctx);
        var changes = new List<ConfigChange>();
        foreach (var (protocol, key, displayName) in Providers)
        {
            var models = new JsonObject();
            foreach (var (id, name) in entries.Where(e => protocols[e.Id] == protocol))
                models[id] = ModelJson(name, hints.GetValueOrDefault(id));
            var path = $"custom_provider.{key}";
            var before = CurrentValue(ConfigFile, ConfigFileFormat.Yaml, path);
            if (protocol != ApiProtocol.OpenAIChat && models.Count == 0)
            {
                if (before is not null && Store.Get(Kind, ConfigFile, path) is not null)
                    changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Yaml, path, before, null));
                continue;
            }
            var provider = new JsonObject
            {
                ["name"] = displayName,
                ["api"] = PiAiModels.ApiOf(protocol),
                ["options"] = new JsonObject
                {
                    ["baseURL"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
                    ["apiKey"] = ctx.LocalKey,
                },
                ["models"] = models,
            };
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Yaml, path, before, provider.ToJsonString()));
        }
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            var key = Providers.First(p => p.Protocol == protocols.GetValueOrDefault(ctx.Model, ApiProtocol.OpenAIChat)).Key;
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Yaml, DefaultModelPath,
                CurrentValue(ConfigFile, ConfigFileFormat.Yaml, DefaultModelPath), JsonString($"custom_provider:{key}/{ctx.Model}")));
        }
        return BuildPlan(changes);
    }

    /// <summary>A model entry (models.dev field names): name, and the limits / image input / reasoning flag Astra knows.</summary>
    private static JsonObject ModelJson(string? name, JsonObject? info)
    {
        var model = new JsonObject();
        if (name is not null) model["name"] = name;
        var limit = new JsonObject();
        if (PositiveLong(info?["contextWindow"]) is { } context) limit["context"] = context;
        if (PositiveLong(info?["maxOutputTokens"]) is { } output) limit["output"] = output;
        if (limit.Count > 0) model["limit"] = limit;
        if (BoolHint(info?["vision"]) is { } vision)
            model["modalities"] = new JsonObject { ["input"] = vision ? new JsonArray("text", "image") : new JsonArray("text") };
        if (BoolHint(info?["reasoning"]) is { } reasoning) model["reasoning"] = reasoning;
        return model;
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        var providerBefore = CurrentValue(ConfigFile, ConfigFileFormat.Yaml, ProviderPath);
        if (providerBefore is not null)
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Yaml, ProviderPath, providerBefore, null));
        foreach (var (_, key, _) in Providers.Skip(1))
        {
            var path = $"custom_provider.{key}";
            if (Store.Get(Kind, ConfigFile, path) is not null && CurrentValue(ConfigFile, ConfigFileFormat.Yaml, path) is { } before)
                changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Yaml, path, before, null));
        }
        // A model the user picked afterwards (another provider) is theirs and stays.
        var model = CurrentValue(ConfigFile, ConfigFileFormat.Yaml, DefaultModelPath);
        if (Providers.Any(p => StringOf(model)?.StartsWith($"custom_provider:{p.Key}/", StringComparison.Ordinal) == true))
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
