using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Paimon (aisk/paimon, <c>uv tool install paimon</c>, a pydantic-ai coding agent; Coexist) keeps its settings in one JSON file,
/// <c>config.json</c> in <c>PAIMON_CONFIG_HOME</c> (when it is an absolute path), else <c>~/.config/paimon</c> (Windows too). <c>model</c> is
/// <c>"provider:name"</c> and <c>providers</c> keeps each provider's <c>api_base</c> and <c>api_key</c>. Astra uses pydantic-ai's
/// <c>litellm</c> provider, an OpenAI-compatible proxy that profiles a <c>vendor/model</c> name by its vendor, which Paimon's own login
/// does not offer, so <c>providers.litellm</c> is almost never the user's: it is set to the gateway's <c>/v1</c> and the local key (the
/// user's own, if any, is recorded and put back). When a model is chosen, <c>model</c> becomes <c>litellm:&lt;id&gt;</c> and
/// <c>compaction.context_window</c> the model's window (Paimon does not compact at all without it; a model with no known window leaves
/// the user's value). Paimon reads the file at start-up. Verified against magpie's wiring (Paimon at 7f7fa51) only.
/// </summary>
public sealed class PaimonClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private const string ProviderPath = "providers.litellm";

    private string Dir => Env.GetEnvironmentVariable("PAIMON_CONFIG_HOME") is { Length: > 0 } home && Path.IsPathRooted(home)
        ? home
        : Env.Combine(".config", "paimon");

    private string ConfigFile => Path.Combine(Dir, "config.json");

    public override string Kind => ClientKinds.Paimon;

    protected override string MarkerFile => ConfigFile;

    protected override string MarkerKey => ProviderPath;

    protected override IReadOnlyList<string> EnabledWarnings =>
        ["Paimon reads its model at start-up: restart open paimon sessions to use this."];

    public override ClientDetection Detect()
    {
        var dir = Dir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("paimon");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no paimon executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var provider = new JsonObject { ["api_base"] = Gateway(ctx), ["api_key"] = ctx.LocalKey };
        var changes = new List<ConfigChange> { JsonSet(file, ProviderPath, provider) };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            changes.Add(JsonSet(file, "model", $"litellm:{ctx.Model}"));
            if (PositiveLong(ModelHints(ctx).GetValueOrDefault(ctx.Model)?["contextWindow"]) is { } window)
                changes.Add(JsonSet(file, "compaction.context_window", JsonValue.Create(window)!));
        }
        return BuildPlan(changes);
    }
}
