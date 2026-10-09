using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// NextCoWork (nextco.work, Electron desktop agent; Coexist): adds one provider, <c>providers.astra</c>, to the user-level
/// <c>~/.next-cowork/providers.json</c>. That file holds the providers the user added themselves (plus their models and
/// plaintext keys) and NextCoWork hot-reloads it, so the entry appears in its settings without a restart; NextCoWork's
/// own providers are left alone. NextCoWork builds before the file existed ignore it and keep providers in their database.
/// <para>
/// The entry is <c>{"name":"Astra","protocol":"openai-chat","baseUrl":"&lt;gateway&gt;/v1","apiKey":…,"models":{"&lt;id&gt;":
/// {"upstreamModel":…}}}</c>: for <c>openai-chat</c> NextCoWork expects the version segment inside <c>baseUrl</c> and appends
/// <c>/chat/completions</c> itself. <c>priority</c> and <c>enabled</c> are left out so they take NextCoWork's defaults (50,
/// enabled) and the entry stays exactly what Astra wrote until the user edits it in NextCoWork. The whole provider is one
/// state key, so a refreshed model list replaces the old one wholesale. NextCoWork keeps the default model in its
/// database, so Astra cannot select one — the selected model is only guaranteed to be in the list.
/// </para>
/// </summary>
public sealed class NextCoWorkClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private string ConfigDir => Env.Combine(".next-cowork");

    private string ConfigFile => Path.Combine(ConfigDir, "providers.json");

    private static string ProviderPath => $"providers.{ProviderId}";

    public override string Kind => ClientKinds.NextCoWork;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect() => Env.DirectoryExists(ConfigDir)
        ? ClientDetection.Found(null, $"config directory {ConfigDir}")
        : ClientDetection.NotFound($"no {ConfigDir}");

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var provider = new JsonObject
        {
            ["name"] = "Astra",
            ["protocol"] = "openai-chat",
            ["baseUrl"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["apiKey"] = ctx.LocalKey,
        };
        var models = ModelsJson(ctx);
        if (models.Count > 0) provider["models"] = models;
        var before = CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath);
        return BuildPlan([new ConfigChange(ConfigFile, ConfigFileFormat.Json, ProviderPath, before, provider.ToJsonString())]);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var before = CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath);
        return BuildPlan(before is null ? [] : [new ConfigChange(ConfigFile, ConfigFileFormat.Json, ProviderPath, before, null)]);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var entry = Store.Get(Kind, ConfigFile, ProviderPath);
        var enabled = entry is not null && ConfigValueCodec.EqualsValue(
            ConfigFileFormat.Json,
            CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath),
            entry.AppliedValueJson);
        return BuildStatus(detection, enabled, []);
    }

    /// <summary>The bound provider's models as NextCoWork aliases: <c>{"&lt;id&gt;": {"upstreamModel", optional "displayName", "contextWindow", "maxOutputTokens", "capabilities"}}</c>.</summary>
    private static JsonObject ModelsJson(EnableContext ctx)
    {
        var hints = ModelHints(ctx);
        var models = new JsonObject();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            hints.TryGetValue(id, out var info);
            var model = new JsonObject { ["upstreamModel"] = id };
            if (name is not null) model["displayName"] = name;
            if (PositiveLong(info?["contextWindow"]) is { } context) model["contextWindow"] = context;
            if (PositiveLong(info?["maxOutputTokens"]) is { } maxOutput) model["maxOutputTokens"] = maxOutput;
            // Only the hints Astra actually knows are written; NextCoWork fills the rest with its own defaults.
            // "thinking" is deliberately not mapped: when set for a model that rejects thinking parameters it makes every request fail.
            if (BoolHint(info?["vision"]) is { } vision) model["capabilities"] = new JsonObject { ["vision"] = vision, ["visionInput"] = vision };
            models[id] = model;
        }
        return models;
    }
}
