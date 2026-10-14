using System.Text.Json.Nodes;
using Astra.Core;
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
/// <para>
/// A provider entry runs one agent, so it speaks one protocol: models whose native protocol is Responses get a second entry,
/// <c>agents.providers.astra-codex</c>, that extends Paseo's <c>codex</c> provider — it launches the user's Codex CLI with
/// <c>OPENAI_BASE_URL</c> (the gateway root; Paseo adds <c>/v1</c> and <c>wire_api = "responses"</c>) and <c>OPENAI_API_KEY</c>
/// (getpaseo/paseo <c>codex-app-server-agent.ts</c>). That entry exists only while it has models; the Claude entry lists the
/// rest (all of them when every model is Responses-native, since Claude Code reaches those too, translated).
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

    private static string CodexProviderPath => $"agents.providers.{ProviderId}-codex";

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
        var protocols = ModelProtocols(ctx, ApiProtocol.Anthropic);
        var entries = ModelEntries(ctx);
        var codex = entries.Where(e => protocols[e.Id] == ApiProtocol.OpenAIResponses).ToList();
        var claude = entries.Where(e => protocols[e.Id] != ApiProtocol.OpenAIResponses).ToList();
        var models = ModelsJson(claude.Count > 0 ? claude : entries, ctx.Model);
        if (models.Count > 0) provider["models"] = models;
        var changes = new List<ConfigChange>
        {
            new(ConfigFile, ConfigFileFormat.Json, ProviderPath, CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath), provider.ToJsonString()),
        };
        var codexBefore = CurrentValue(ConfigFile, ConfigFileFormat.Json, CodexProviderPath);
        if (codex.Count > 0)
        {
            var codexProvider = new JsonObject
            {
                ["extends"] = "codex",
                ["label"] = "Astra (Codex)",
                ["description"] = "Codex through the Astra gateway, for models served over the Responses API",
                ["env"] = new JsonObject
                {
                    ["OPENAI_BASE_URL"] = ctx.GatewayBaseUrl.TrimEnd('/'),
                    ["OPENAI_API_KEY"] = ctx.LocalKey,
                },
                ["models"] = ModelsJson(codex, ctx.Model),
            };
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, CodexProviderPath, codexBefore, codexProvider.ToJsonString()));
        }
        else if (codexBefore is not null && Store.Get(Kind, ConfigFile, CodexProviderPath) is not null)
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, CodexProviderPath, codexBefore, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        if (CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath) is { } before)
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, ProviderPath, before, null));
        if (Store.Get(Kind, ConfigFile, CodexProviderPath) is not null && CurrentValue(ConfigFile, ConfigFileFormat.Json, CodexProviderPath) is { } codex)
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, CodexProviderPath, codex, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = StillOurs(ConfigFile, ConfigFileFormat.Json, ProviderPath);
        return BuildStatus(detection, enabled, []);
    }

    /// <summary>
    /// Models as Paseo profile models: <c>[{"id", "label", optional "isDefault"}]</c>. The selected model (else the first)
    /// is the default.
    /// </summary>
    private static JsonArray ModelsJson(IReadOnlyList<(string Id, string? Name)> entries, string? selected)
    {
        var defaultId = entries.Any(e => e.Id == selected) ? selected : entries.FirstOrDefault().Id;
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
