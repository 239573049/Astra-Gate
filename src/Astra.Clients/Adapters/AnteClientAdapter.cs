using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Ante (antigma.ai, command <c>ante</c>; Coexist) takes a provider of one's own in <c>catalog.json</c> in <c>ANTE_HOME</c> (else
/// <c>~/.ante</c>; the catalog is always <c>$ANTE_HOME/catalog.json</c>), merged on top of the providers it ships:
/// <c>{"providers":{"&lt;id&gt;":{display_name, base_url, wire_style, auth, preferred_models:[{id, display_name, context_limit, max_tokens, …}]}}}</c>.
/// Astra adds <c>providers.astra</c> at the gateway's <c>/v1</c> on <c>OpenAiCompatible</c> (the wire style Ante speaks Chat Completions
/// with) and lists the bound provider's models in <c>preferred_models</c>, Ante's model picker; Ante skips an entry whose
/// <c>max_tokens</c> is not below its <c>context_limit</c>, so such a model is told one token less than its window. Which provider a session
/// starts on is the <c>provider</c> and <c>model</c> of <c>settings.json</c> beside it: when a model is chosen both are set (what they held
/// is recorded and put back). No <c>auth</c> block is written: Ante counts a provider with none as authenticated and sends no key, so
/// Astra's "require gateway key" setting must be off for local requests to be accepted, and they are then the default token's. Ante reads
/// the catalog once per process. Verified against magpie's wiring (Ante 0.2.9) only.
/// </summary>
public sealed class AnteClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private const string ProviderPath = "providers.astra";

    private string Dir => Env.GetEnvironmentVariable("ANTE_HOME") is { } home && home.Trim() is { Length: > 0 } trimmed ? trimmed : Env.Combine(".ante");

    private string CatalogFile => Path.Combine(Dir, "catalog.json");

    private string SettingsFile => Path.Combine(Dir, "settings.json");

    public override string Kind => ClientKinds.Ante;

    protected override string MarkerFile => CatalogFile;

    protected override string MarkerKey => ProviderPath;

    protected override IReadOnlyList<string> EnabledWarnings =>
    [
        "Ante sends no key to a provider without an auth block: turn off \"require gateway key\" in Astra's settings so local requests are accepted (they use the default token).",
        "Ante reads its catalog once per process: restart open ante sessions to use this.",
    ];

    public override ClientDetection Detect()
    {
        var dir = Dir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("ante");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no ante executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [CatalogFile, SettingsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var hints = ModelHints(ctx);
        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            var info = hints.GetValueOrDefault(id);
            var model = new JsonObject { ["id"] = id };
            if (name is not null && name != id) model["display_name"] = name;
            var context = PositiveLong(info?["contextWindow"]);
            if (context is { } window) model["context_limit"] = window;
            if (PositiveLong(info?["maxOutputTokens"]) is { } output)
            {
                // Ante skips an entry whose max_tokens is not less than its context_limit.
                if (context is { } limit && output >= limit) output = limit - 1;
                if (output > 0) model["max_tokens"] = output;
            }
            models.AddNode(model);
        }
        var provider = new JsonObject { ["display_name"] = "Astra", ["base_url"] = Gateway(ctx), ["wire_style"] = "OpenAiCompatible" };
        if (models.Count > 0) provider["preferred_models"] = models;
        var changes = new List<ConfigChange> { JsonSet(CatalogFile, ProviderPath, provider) };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            changes.Add(JsonSet(SettingsFile, "provider", ProviderId));
            changes.Add(JsonSet(SettingsFile, "model", ctx.Model));
        }
        return BuildPlan(changes);
    }
}
