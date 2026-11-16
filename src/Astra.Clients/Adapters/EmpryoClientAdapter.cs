using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Empryo (formerly SoulForge, command <c>empryo</c>; Coexist) keeps its settings in <c>~/.empryo/config.json</c> (a project's
/// <c>.empryo/config.json</c> goes over it). Its own providers are a <c>providers</c> array of <c>{id, name, baseURL, envVar, modelsAPI}</c>
/// OpenAI-compatible endpoints it lists models from and asks <c>/chat/completions</c> of; the model sessions start on is
/// <c>defaultModel</c>, <c>"&lt;provider id&gt;/&lt;model id&gt;"</c>. Astra adds the element with <c>id: "astra"</c> at the gateway and, when a
/// model is chosen, sets <c>defaultModel</c> to <c>astra/&lt;id&gt;</c>. With no <c>envVar</c> Empryo sends no key (it reads one only from
/// an environment variable), so requests carry no token: Astra's "require gateway key" setting must be off for local requests to be
/// accepted, and they are then the default token's. Verified against magpie's wiring only.
/// </summary>
public sealed class EmpryoClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private static readonly string ProviderPath = "providers." + JsoncEditor.Selector(("id", ProviderId));

    private string ConfigFile => Env.Combine(".empryo", "config.json");

    public override string Kind => ClientKinds.Empryo;

    protected override string MarkerFile => ConfigFile;

    protected override string MarkerKey => ProviderPath;

    protected override IReadOnlyList<string> EnabledWarnings =>
    [
        "Empryo cannot hold a key for a provider (it reads one only from an environment variable): turn off \"require gateway key\" in Astra's settings so local requests are accepted (they use the default token).",
        "Empryo reads its config at start-up: restart open empryo sessions to use this.",
    ];

    public override ClientDetection Detect()
    {
        var dir = Env.Combine(".empryo");
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("empryo");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no empryo executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var v1 = Gateway(ctx);
        var provider = new JsonObject { ["id"] = ProviderId, ["name"] = "Astra", ["baseURL"] = v1, ["modelsAPI"] = v1 + "/models" };
        var changes = new List<ConfigChange> { JsonSet(file, ProviderPath, provider) };
        if (!string.IsNullOrEmpty(ctx.Model)) changes.Add(JsonSet(file, "defaultModel", $"{ProviderId}/{ctx.Model}"));
        return BuildPlan(changes);
    }
}
