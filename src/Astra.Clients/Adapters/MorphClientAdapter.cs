using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Mister Morph (Coexist) keeps its settings in <c>~/.morph/config.yaml</c>, or the file <c>MISTER_MORPH_CONFIG</c> names: the main
/// model is <c>llm.model</c>, asked of the vendor <c>llm.inference_provider</c> names at <c>llm.endpoint</c> with <c>llm.api_key</c>.
/// Morph has no provider list to add to, so Astra takes <c>llm</c> over key by key (what the user had is recorded and put back):
/// <c>inference_provider: openai_response_compatible</c> (OpenAI's Responses API, Morph's own default, which the gateway serves for
/// every upstream), <c>endpoint</c> the gateway's <c>/v1</c> (Morph adds no second <c>/v1</c>), the local key, and, when a model is
/// chosen, <c>model</c> plus its <c>context_window_tokens</c> (Morph only knows windows from a list of its own) and a
/// <c>User-Agent</c> header. <c>llm.profiles</c> and <c>llm.routes</c> are the user's and stay. Only <c>~/.morph</c> counts as installed
/// (<c>morph</c> is other tools' command too). Verified against magpie's wiring only.
/// </summary>
public sealed class MorphClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private const string UserAgent = "mistermorph";

    private string ConfigFile =>
        Env.GetEnvironmentVariable("MISTER_MORPH_CONFIG") is { Length: > 0 } file ? file : Env.Combine(".morph", "config.yaml");

    public override string Kind => ClientKinds.Morph;

    protected override string MarkerFile => ConfigFile;

    protected override string MarkerKey => "llm.endpoint";

    protected override IReadOnlyList<string> EnabledWarnings =>
        ["Mister Morph's Console uses this for new tasks: restart open morph chats to use it there."];

    public override ClientDetection Detect()
    {
        var file = ConfigFile;
        var dir = Path.GetDirectoryName(file) ?? file;
        return Env.FileExists(file) || Env.DirectoryExists(dir) ? ClientDetection.Found(null, $"config {file}") : ClientDetection.NotFound($"no {file}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>
        {
            YamlSet(file, "llm.inference_provider", JsonValue.Create("openai_response_compatible")!),
            YamlSet(file, "llm.endpoint", JsonValue.Create(Gateway(ctx))!),
            YamlSet(file, "llm.api_key", JsonValue.Create(ctx.LocalKey)!),
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            changes.Add(YamlSet(file, "llm.model", JsonValue.Create(ctx.Model)!));
            if (PositiveLong(ModelHints(ctx).GetValueOrDefault(ctx.Model)?["contextWindow"]) is { } window)
                changes.Add(YamlSet(file, "llm.context_window_tokens", JsonValue.Create(window)!));
            else if (Drop(file, ConfigFileFormat.Yaml, "llm.context_window_tokens") is { } drop)
                changes.Add(drop);
        }
        changes.Add(YamlSet(file, "llm.headers.User-Agent", JsonValue.Create(UserAgent)!));
        return BuildPlan(changes);
    }
}
