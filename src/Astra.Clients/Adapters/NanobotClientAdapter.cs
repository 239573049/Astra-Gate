using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// nanobot (HKUDS/nanobot, <c>pip install nanobot-ai</c>; Coexist) keeps its settings in <c>~/.nanobot/config.json</c>, plain JSON its
/// pydantic models read with camelCase or snake_case keys alike. The model it answers with is <c>agents.defaults.model</c>, asked of
/// <c>agents.defaults.provider</c>, unless <c>agents.defaults.modelPreset</c> names one of <c>modelPresets</c>, which then goes over both.
/// Any key under <c>providers</c> that is not one of its own is an OpenAI-compatible endpoint at its <c>apiBase</c> (sent <c>apiKey</c> and
/// <c>extraHeaders</c>), and a model <c>"&lt;key&gt;/&lt;id&gt;"</c> is sent to it as <c>&lt;id&gt;</c>. Astra adds <c>providers.astra</c> (the gateway's
/// <c>/v1</c>, the local key and a <c>User-Agent</c> of nanobot's name) and, when a model is chosen, sets <c>agents.defaults.provider</c>,
/// <c>model</c> (<c>astra/&lt;id&gt;</c>) and <c>contextWindowTokens</c>; a <c>modelPreset</c> that would go over them, and a snake_case
/// spelling of those keys, are taken out and put back on disable. nanobot's gateway and WebUI take a change at their next turn; <c>nanobot
/// agent</c> reads the file once. Verified against magpie's wiring (nanobot 0.4.1) only.
/// </summary>
public sealed class NanobotClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private const string ProviderPath = "providers.astra";

    private const string Defaults = "agents.defaults";

    private string ConfigFile => Env.Combine(".nanobot", "config.json");

    public override string Kind => ClientKinds.Nanobot;

    protected override string MarkerFile => ConfigFile;

    protected override string MarkerKey => ProviderPath;

    protected override IReadOnlyList<string> EnabledWarnings =>
        ["nanobot's gateway and WebUI take this at their next turn; restart `nanobot agent` sessions to use it."];

    public override ClientDetection Detect()
    {
        var dir = Env.Combine(".nanobot");
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("nanobot");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no nanobot executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var provider = new JsonObject
        {
            ["apiKey"] = ctx.LocalKey,
            ["apiBase"] = Gateway(ctx),
            ["extraHeaders"] = new JsonObject { ["User-Agent"] = "nanobot" },
        };
        var changes = new List<ConfigChange> { JsonSet(file, ProviderPath, provider) };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            // nanobot reads either spelling of a key; the one this writes must be the only one, and a preset would go over it.
            foreach (var path in new[] { $"{Defaults}.model_preset", $"{Defaults}.modelPreset", $"{Defaults}.context_window_tokens" })
                if (Drop(file, ConfigFileFormat.Json, path) is { } drop) changes.Add(drop);
            changes.Add(JsonSet(file, $"{Defaults}.provider", ProviderId));
            changes.Add(JsonSet(file, $"{Defaults}.model", $"{ProviderId}/{ctx.Model}"));
            if (PositiveLong(ModelHints(ctx).GetValueOrDefault(ctx.Model)?["contextWindow"]) is { } window)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Json, $"{Defaults}.contextWindowTokens",
                    CurrentValue(file, ConfigFileFormat.Json, $"{Defaults}.contextWindowTokens"), Num(window)));
            else if (Drop(file, ConfigFileFormat.Json, $"{Defaults}.contextWindowTokens") is { } dropWindow)
                changes.Add(dropWindow);
        }
        return BuildPlan(changes);
    }
}
