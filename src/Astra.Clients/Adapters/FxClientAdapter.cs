using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// fx (fx.sh, Vercel Labs; Coexist): settings live in <c>~/.fx/settings.json</c> — the provider it talks to under <c>provider</c>
/// (<c>gateway</c>, <c>codex</c>, <c>grok</c> or one of the user's own), each provider's model under <c>models</c>, user providers under
/// <c>providers</c>. Astra adds <c>providers.astra</c> (<c>protocol: openai-chat-completions</c>, <c>base_url</c> the gateway's <c>/v1</c>,
/// <c>tool_choice_mode: send</c>, the bound provider's models as <c>model_metadata</c>) and, when a model is chosen, sets <c>provider</c>
/// to <c>astra</c> and <c>models.astra</c> to the model id. fx takes at most 256 models per provider (it refuses the whole file past
/// that), so the chosen model goes first and the rest are cut. A provider's <c>auth</c> can only be <c>none</c> or a bearer read from an
/// environment variable, never a literal key, so requests carry no token: Astra's "require gateway key" setting must be off for local
/// requests to be accepted, and they are then the default token's. Reasoning levels are not written (fx 0.0.17+ only, and a key an older fx
/// does not know costs it the whole file). <c>fx</c> is also the name of a JSON viewer, so only <c>~/.fx</c> counts as installed.
/// Verified against magpie's wiring only.
/// </summary>
public sealed class FxClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    /// <summary>How many models fx takes in a provider's <c>model_metadata</c>.</summary>
    public const int MaxModels = 256;

    private string Dir => Env.Combine(".fx");

    private string SettingsFile => Path.Combine(Dir, "settings.json");

    private static string ProviderPath => $"providers.{ProviderId}";

    private static string ModelPath => $"models.{ProviderId}";

    public override string Kind => ClientKinds.Fx;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = Dir;
        return Env.DirectoryExists(dir) ? ClientDetection.Found(null, $"config directory {dir}") : ClientDetection.NotFound($"no {dir}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [SettingsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = SettingsFile;
        var hints = ModelHints(ctx);
        // The model in use first, so it is never the one cut at fx's limit.
        var entries = ModelEntries(ctx).OrderBy(e => e.Id == ctx.Model ? 0 : 1).ToList();
        var metadata = new JsonObject();
        foreach (var (id, _) in entries)
        {
            if (metadata.Count == MaxModels) break;
            var info = hints.GetValueOrDefault(id);
            var model = new JsonObject { ["supports_tool_use"] = true, ["supports_vision"] = BoolHint(info?["vision"]) == true };
            var context = PositiveLong(info?["contextWindow"]);
            if (context is { } window) model["context_window"] = window;
            // fx refuses an output limit that is not below the context window.
            if (PositiveLong(info?["maxOutputTokens"]) is { } output && (context is null || output < context)) model["max_output_tokens"] = output;
            metadata[id] = model;
        }
        var provider = new JsonObject
        {
            ["protocol"] = "openai-chat-completions",
            ["base_url"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["auth"] = new JsonObject { ["type"] = "none" },
            ["tool_choice_mode"] = "send",
            ["model_metadata"] = metadata,
        };
        var changes = new List<ConfigChange>
        {
            new(file, ConfigFileFormat.Json, ProviderPath, CurrentValue(file, ConfigFileFormat.Json, ProviderPath), provider.ToJsonString()),
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            changes.Add(Key("provider", ProviderId));
            changes.Add(Key(ModelPath, ctx.Model));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = SettingsFile;
        var changes = new List<ConfigChange>();
        if (CurrentValue(file, ConfigFileFormat.Json, ProviderPath) is { } provider)
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, ProviderPath, provider, null));
        // The active provider and its model are only ours while the provider is still Astra's.
        var active = CurrentValue(file, ConfigFileFormat.Json, "provider");
        if (active is not null && StringOf(active) == ProviderId)
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, "provider", active, null));
        if (Store.Get(Kind, file, ModelPath) is not null && CurrentValue(file, ConfigFileFormat.Json, ModelPath) is { } model)
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, ModelPath, model, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = StillOurs(SettingsFile, ConfigFileFormat.Json, ProviderPath);
        var warnings = new List<string>();
        if (enabled)
        {
            warnings.Add("fx cannot hold a literal key for a provider: turn off \"require gateway key\" in Astra's settings so local requests are accepted (they use the default token).");
            warnings.Add("fx reads its settings at start-up: restart open fx sessions to use this.");
        }
        return BuildStatus(detection, enabled, warnings);
    }

    private ConfigChange Key(string key, string value) =>
        new(SettingsFile, ConfigFileFormat.Json, key, CurrentValue(SettingsFile, ConfigFileFormat.Json, key), JsonString(value));
}
