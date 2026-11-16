using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Command Code (commandcode.ai, command <c>command-code</c> / <c>cmd</c>; Coexist): settings live in <c>~/.commandcode/settings.json</c>
/// (the model as <c>provider/model</c>, plus <c>modelProvider</c>) and providers of the user's own in <c>~/.commandcode/providers.json</c>.
/// Astra adds <c>provider.astra</c> there (<c>api: openai-completions</c>, <c>baseURL</c> the gateway's <c>/v1</c>, the bound provider's
/// models with the window and reply limit Astra knows — Command Code reads them from this file alone) and, when a model is chosen, sets
/// <c>model</c> to <c>astra/&lt;id&gt;</c> and <c>modelProvider</c> to <c>astra</c>. A custom provider's <c>apiKey</c> is the literal
/// <c>false</c> (Command Code refuses a key written out), so requests carry no token: Astra's "require gateway key" setting must be off
/// for local requests to be accepted, and they are then the default token's. Command Code still wants its own sign-in. Verified against
/// magpie's wiring only.
/// </summary>
public sealed class CommandCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private string Dir => Env.Combine(".commandcode");

    private string SettingsFile => Path.Combine(Dir, "settings.json");

    private string ProvidersFile => Path.Combine(Dir, "providers.json");

    private static string ProviderPath => $"provider.{ProviderId}";

    public override string Kind => ClientKinds.CommandCode;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = Dir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("command-code") ?? Env.FindOnPath("cmd");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no command-code executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ProvidersFile, SettingsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var hints = ModelHints(ctx);
        var models = new JsonObject();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            var info = hints.GetValueOrDefault(id);
            var model = new JsonObject { ["name"] = name ?? id };
            if (BoolHint(info?["reasoning"]) == true) model["reasoning"] = true;
            if (PositiveLong(info?["contextWindow"]) is { } context) model["contextWindow"] = context;
            if (PositiveLong(info?["maxOutputTokens"]) is { } output) model["maxOutput"] = output;
            models[id] = model;
        }
        var provider = new JsonObject
        {
            ["name"] = "Astra",
            ["api"] = "openai-completions",
            ["baseURL"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["apiKey"] = false,
            ["models"] = models,
        };
        var changes = new List<ConfigChange>
        {
            new(ProvidersFile, ConfigFileFormat.Json, ProviderPath, CurrentValue(ProvidersFile, ConfigFileFormat.Json, ProviderPath), provider.ToJsonString()),
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            changes.Add(Key("model", $"{ProviderId}/{ctx.Model}"));
            changes.Add(Key("modelProvider", ProviderId));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        if (CurrentValue(ProvidersFile, ConfigFileFormat.Json, ProviderPath) is { } provider)
            changes.Add(new ConfigChange(ProvidersFile, ConfigFileFormat.Json, ProviderPath, provider, null));
        // The selection is only ours while it still names the Astra provider.
        var modelProvider = CurrentValue(SettingsFile, ConfigFileFormat.Json, "modelProvider");
        if (modelProvider is not null && StringOf(modelProvider) == ProviderId)
        {
            changes.Add(new ConfigChange(SettingsFile, ConfigFileFormat.Json, "modelProvider", modelProvider, null));
            var model = CurrentValue(SettingsFile, ConfigFileFormat.Json, "model");
            if (model is not null && Store.Get(Kind, SettingsFile, "model") is not null)
                changes.Add(new ConfigChange(SettingsFile, ConfigFileFormat.Json, "model", model, null));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = StillOurs(ProvidersFile, ConfigFileFormat.Json, ProviderPath);
        var warnings = new List<string>();
        if (enabled)
        {
            warnings.Add("Command Code sends no API key to a custom provider: turn off \"require gateway key\" in Astra's settings so local requests are accepted (they use the default token).");
            warnings.Add("Command Code reads its settings at start-up and still wants its own sign-in (cmd login): restart open sessions to use this.");
        }
        return BuildStatus(detection, enabled, warnings);
    }

    private ConfigChange Key(string key, string value) =>
        new(SettingsFile, ConfigFileFormat.Json, key, CurrentValue(SettingsFile, ConfigFileFormat.Json, key), JsonString(value));
}
