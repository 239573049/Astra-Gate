using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Mistral Vibe (mistralai/mistral-vibe, command <c>vibe</c>; Coexist) keeps its settings in <c>config.toml</c> in <c>VIBE_HOME</c> (a
/// leading <c>~</c> expanded), else <c>~/.vibe</c>. A provider is a <c>[[providers]]</c> entry, a model a <c>[[models]]</c> entry naming
/// it, and the model in use is the top-level <c>active_model</c> (an alias). Astra owns the provider entry named <c>astra</c>
/// (<c>api_base</c> the gateway's <c>/v1</c>, <c>api_style = "openai"</c>, <c>backend = "generic"</c>) and every <c>[[models]]</c> entry
/// whose <c>provider</c> is <c>astra</c>, aliased <c>astra/&lt;id&gt;</c> so the catalog is in Vibe's <c>/model</c> picker; <c>active_model</c>
/// points at the chosen one. Vibe reads a provider's key only from an environment variable named by <c>api_key_env_var</c> (or the
/// keyring), never from <c>config.toml</c>, so the local key goes into the provider's <c>extra_headers</c> as an <c>Authorization</c>
/// header and nothing is put in the user's environment. Avibe's <c>vibe</c> command is not Mistral Vibe's: an executable under an
/// <c>avibe</c> folder does not count. Verified against magpie's wiring only.
/// </summary>
public sealed class VibeClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private const string ProviderSelector = "providers[name=astra]";

    private const string ModelsSelector = "models[provider=astra]";

    /// <summary>The window Vibe is told for a model whose context is unknown (it warns and takes a default otherwise).</summary>
    public const long DefaultContext = 128_000;

    private string Dir => Env.GetEnvironmentVariable("VIBE_HOME") is { } home && home.Trim() is { Length: > 0 } trimmed
        ? ExpandHome(trimmed)
        : Env.Combine(".vibe");

    private string ConfigFile => Path.Combine(Dir, "config.toml");

    public override string Kind => ClientKinds.Vibe;

    protected override string MarkerFile => ConfigFile;

    protected override string MarkerKey => ProviderSelector;

    protected override bool IsTableKeyPath(string keyPath) => keyPath is ProviderSelector or ModelsSelector;

    protected override IReadOnlyList<string> EnabledWarnings =>
        ["Vibe reads its settings at start-up: restart open vibe sessions to use this."];

    public override ClientDetection Detect()
    {
        var dir = Dir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("vibe");
        if (exe is not null && !exe.Contains("avibe", StringComparison.OrdinalIgnoreCase)) return ClientDetection.Found(null, $"executable {exe}");
        return ClientDetection.NotFound($"no {dir} and no vibe executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var provider = new JsonObject
        {
            ["name"] = Raw(ProviderId),
            ["api_base"] = Raw(Gateway(ctx)),
            ["api_style"] = Raw("openai"),
            ["backend"] = Raw("generic"),
            ["extra_headers"] = "{ Authorization = " + Raw("Bearer " + ctx.LocalKey) + " }",
        };
        var hints = ModelHints(ctx);
        var models = new JsonArray();
        foreach (var (id, _) in ModelEntries(ctx))
        {
            var info = hints.GetValueOrDefault(id);
            var entry = new JsonObject
            {
                ["name"] = Raw(id),
                ["provider"] = Raw(ProviderId),
                ["alias"] = Raw($"{ProviderId}/{id}"),
                ["thinking_levels"] = BoolHint(info?["reasoning"]) == true ? "[\"off\", \"low\", \"medium\", \"high\"]" : "[\"off\"]",
                ["supports_images"] = BoolHint(info?["vision"]) == true ? "true" : "false",
                ["max_context_length"] = Num(PositiveLong(info?["contextWindow"]) ?? DefaultContext),
            };
            models.AddNode(entry);
        }
        var changes = new List<ConfigChange> { TomlEntries(file, ProviderSelector, new JsonArray(provider)), TomlEntries(file, ModelsSelector, models) };
        if (!string.IsNullOrEmpty(ctx.Model)) changes.Add(TomlSet(file, "active_model", Raw($"{ProviderId}/{ctx.Model}")));
        return BuildPlan(changes);
    }
}
