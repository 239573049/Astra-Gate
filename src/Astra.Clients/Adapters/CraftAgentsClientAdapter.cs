using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Craft Agents (lukilabs/craft-agents-oss, an Electron app running Claude and Pi sessions; Coexist) keeps its model connections in
/// <c>config.json</c>'s <c>llmConnections</c>, in <c>CRAFT_CONFIG_DIR</c> else <c>~/.craft-agent</c>; the connection new sessions start on is
/// the one <c>defaultLlmConnection</c> names by slug. A custom endpoint is a connection of <c>providerType: "pi_compat"</c>. Astra adds
/// the element with <c>slug: "astra"</c> (<c>authType: "none"</c>, <c>baseUrl</c> the gateway's <c>/v1</c>, <c>customEndpoint.api:
/// "openai-completions"</c>, the bound provider's models and the chosen one as <c>defaultModel</c>) and, when a model is chosen, makes it the
/// default connection (the user's default is recorded and put back). Craft keeps keys apart in a sealed <c>credentials.enc</c> Astra
/// cannot open; <c>authType: none</c> is what Craft itself saves for a local endpoint with no key, and its Pi server then sends a
/// placeholder key, so requests carry no token: Astra's "require gateway key" setting must be off for local requests to be accepted, and
/// they are then the default token's (magpie names the client by an <c>/agent/craft-agents</c> path, which Astra's gateway has no
/// equivalent of). Craft will not load a <c>config.json</c> without its <c>workspaces</c>, so Astra never makes one: open Craft Agents
/// once first. <c>createdAt</c> is not written (adapters have no clock). Craft watches the file; new sessions take the change. Verified
/// against magpie's wiring (Craft Agents 0.15.8) only.
/// </summary>
public sealed class CraftAgentsClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private static readonly string ConnectionPath = "llmConnections." + JsoncEditor.Selector(("slug", ProviderId));

    private string Dir => Env.GetEnvironmentVariable("CRAFT_CONFIG_DIR") is { Length: > 0 } dir ? dir : Env.Combine(".craft-agent");

    private string ConfigFile => Path.Combine(Dir, "config.json");

    public override string Kind => ClientKinds.CraftAgents;

    protected override string MarkerFile => ConfigFile;

    protected override string MarkerKey => ConnectionPath;

    protected override IReadOnlyList<string> EnabledWarnings =>
    [
        "Craft Agents sends no key to a keyless connection: turn off \"require gateway key\" in Astra's settings so local requests are accepted (they use the default token).",
        "Craft Agents starts new sessions on this; a session already open stays on the connection it started on.",
    ];

    public override ClientDetection Detect()
    {
        var dir = Dir;
        return Env.DirectoryExists(dir) ? ClientDetection.Found(null, $"config directory {dir}") : ClientDetection.NotFound($"no {dir}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var workspaces = CurrentValue(file, ConfigFileFormat.Json, "workspaces");
        if (workspaces is null || !workspaces.StartsWith('['))
            throw new EditorException($"Craft Agents has no {file} with its workspaces yet; open Craft Agents once, then enable it here.");
        var hints = ModelHints(ctx);
        var entries = ModelEntries(ctx);
        var models = new JsonArray();
        foreach (var (id, name) in entries)
        {
            var info = hints.GetValueOrDefault(id);
            var model = new JsonObject
            {
                ["id"] = id,
                ["name"] = name ?? id,
                ["shortName"] = name ?? id,
                ["description"] = "",
                ["provider"] = "pi",
            };
            if (PositiveLong(info?["contextWindow"]) is { } context) model["contextWindow"] = context;
            if (BoolHint(info?["vision"]) == true) model["supportsImages"] = true;
            models.AddNode(model);
        }
        var defaultModel = !string.IsNullOrEmpty(ctx.Model) ? ctx.Model : entries.FirstOrDefault().Id;
        if (string.IsNullOrEmpty(defaultModel)) return BuildPlan([]);
        var connection = new JsonObject
        {
            ["slug"] = ProviderId,
            ["name"] = "Astra",
            ["providerType"] = "pi_compat",
            ["authType"] = "none",
            ["baseUrl"] = Gateway(ctx),
            ["customEndpoint"] = new JsonObject { ["api"] = "openai-completions" },
            ["models"] = models,
            ["defaultModel"] = defaultModel,
            ["modelSelectionMode"] = "userDefined3Tier",
        };
        var changes = new List<ConfigChange> { JsonSet(file, ConnectionPath, connection) };
        if (!string.IsNullOrEmpty(ctx.Model)) changes.Add(JsonSet(file, "defaultLlmConnection", ProviderId));
        return BuildPlan(changes);
    }
}
