using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Paseo (paseo.sh, a daemon + desktop/mobile app that runs Claude Code, Codex, OpenCode … side by side; Coexist): adds one
/// custom provider, <c>agents.providers.astra</c>, to <c>$PASEO_HOME/config.json</c> (default <c>~/.paseo</c>). The entry
/// extends Paseo's built-in <c>claude</c> provider — it still launches the user's Claude Code — with
/// <c>ANTHROPIC_BASE_URL</c> / <c>ANTHROPIC_AUTH_TOKEN</c> pointing at the gateway in its <c>env</c>, and lists the bound
/// provider's models (Paseo's <c>models</c> replaces the first-party Claude list). Paseo's own providers and profiles are
/// left alone, so "Astra" appears as one more provider next to them.
/// <para>
/// Paseo validates config.json with strict schemas (an unknown key rejects the file), so the entry only uses documented
/// fields: <c>extends</c>, <c>label</c>, <c>description</c>, <c>env</c>, <c>disallowedTools</c> and <c>models</c>
/// (<c>{"id","label",optional "isDefault"}</c>; <c>label</c> is required). <c>WebSearch</c> is disallowed because it is an
/// Anthropic server-side tool a gateway upstream does not serve. The whole provider is one state key, so a refreshed model
/// list replaces the old one wholesale. Paseo picks direct edits up on <c>paseo reload</c> or a daemon restart.
/// </para>
/// </summary>
public sealed class PaseoClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private string HomeDir =>
        Env.GetEnvironmentVariable("PASEO_HOME") is { Length: > 0 } dir ? dir : Env.Combine(".paseo");

    private string ConfigFile => Path.Combine(HomeDir, "config.json");

    private static string ProviderPath => $"agents.providers.{ProviderId}";

    public override string Kind => ClientKinds.Paseo;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = HomeDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("paseo");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no paseo executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var provider = new JsonObject
        {
            ["extends"] = "claude",
            ["label"] = "Astra",
            ["description"] = "Claude Code through the Astra gateway",
            ["env"] = new JsonObject
            {
                ["ANTHROPIC_BASE_URL"] = ctx.GatewayBaseUrl.TrimEnd('/'),
                ["ANTHROPIC_AUTH_TOKEN"] = ctx.LocalKey,
            },
            ["disallowedTools"] = new JsonArray("WebSearch"),
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
        var enabled = StillOurs(ConfigFile, ConfigFileFormat.Json, ProviderPath);
        return BuildStatus(detection, enabled, []);
    }

    /// <summary>
    /// The bound provider's models as Paseo profile models: <c>[{"id", "label", optional "isDefault"}]</c>. The selected
    /// model (else the first) is the default.
    /// </summary>
    private static JsonArray ModelsJson(EnableContext ctx)
    {
        var entries = ModelEntries(ctx);
        var defaultId = entries.Any(e => e.Id == ctx.Model) ? ctx.Model : entries.FirstOrDefault().Id;
        var models = new JsonArray();
        foreach (var (id, name) in entries)
        {
            var model = new JsonObject { ["id"] = id, ["label"] = name ?? id };
            if (id == defaultId) model["isDefault"] = true;
            models.Add((JsonNode)model);
        }
        return models;
    }
}
