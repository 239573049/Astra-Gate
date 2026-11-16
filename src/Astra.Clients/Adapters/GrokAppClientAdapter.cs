using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Grok App (RongleCat/grok-app, a Tauri front end for the grok CLI; Coexist): its composer lists only Grok Build's official catalog,
/// and a model of the user's own is a "provider channel" of its Settings, a <c>[model.&lt;id&gt;]</c> table with the App's own keys in
/// <c>agent-home/config.toml</c> under its data folder: <c>GROK_APP_HOME</c>, else (the first of these that exists, else the first)
/// <c>~/Library/Application Support/com.grokapp.grok-app</c> / <c>~/.grok-app</c> on macOS, <c>%APPDATA%\grokapp\grok-app\data</c> /
/// <c>%APPDATA%\grokapp\grok-app</c> / <c>%APPDATA%\grok-app</c> on Windows, <c>$XDG_DATA_HOME/grok-app</c> (<c>~/.local/share</c>) /
/// <c>~/.grok-app</c> elsewhere. Astra adds the channel <c>[model.astra]</c> as the App writes one (the gateway as <c>base_url</c> with the
/// local key, chat completions, <c>app_models</c> a JSON list of the bound provider's models with window and vision), the chosen model
/// (else the first) as its <c>model</c>. It never sets <c>[models] default</c>: that key is the route, and making it astra takes the App
/// over (it then drops its own sign-in and the session no longer runs), so which route runs stays the user's choice — press Use on the
/// provider in the App. Effort levels are not written (the catalog has no per-model list). Verified against magpie's wiring only.
/// </summary>
public sealed class GrokAppClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private const string ChannelTable = "model.astra";

    private string Dir
    {
        get
        {
            if (Env.GetEnvironmentVariable("GROK_APP_HOME") is { } home && home.Trim() is { Length: > 0 } trimmed) return trimmed;
            string[] dirs;
            switch (Env.Os)
            {
                case "osx":
                    dirs = [Env.Combine("Library", "Application Support", "com.grokapp.grok-app"), Env.Combine(".grok-app")];
                    break;
                case "windows":
                    var app = Env.GetEnvironmentVariable("APPDATA") is { Length: > 0 } a ? a : Env.Combine("AppData", "Roaming");
                    dirs = [Path.Combine(app, "grokapp", "grok-app", "data"), Path.Combine(app, "grokapp", "grok-app"), Path.Combine(app, "grok-app")];
                    break;
                default:
                    var data = Env.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } x ? x : Env.Combine(".local", "share");
                    dirs = [Path.Combine(data, "grok-app"), Env.Combine(".grok-app")];
                    break;
            }
            return dirs.FirstOrDefault(Env.DirectoryExists) ?? dirs[0];
        }
    }

    private string ConfigFile => Path.Combine(Dir, "agent-home", "config.toml");

    public override string Kind => ClientKinds.GrokApp;

    protected override string MarkerFile => ConfigFile;

    protected override string MarkerKey => ChannelTable;

    protected override bool IsTableKeyPath(string keyPath) => keyPath == ChannelTable;

    protected override IReadOnlyList<string> EnabledWarnings =>
        ["Astra is a custom provider in Grok App's Settings > Account. Press Use on it there: Astra never changes the App's route ([models] default)."];

    public override ClientDetection Detect()
    {
        var dir = Dir;
        return Env.DirectoryExists(dir) ? ClientDetection.Found(null, $"data directory {dir}") : ClientDetection.NotFound($"no {dir}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var entries = ModelEntries(ctx);
        // Grok App needs a model for the channel: with none there is nothing to write.
        if (entries.Count == 0) return BuildPlan([]);
        var hints = ModelHints(ctx);
        var list = new JsonArray();
        foreach (var (id, name) in entries)
        {
            var info = hints.GetValueOrDefault(id);
            var entry = new JsonObject { ["id"] = id, ["name"] = name ?? id };
            if (PositiveLong(info?["contextWindow"]) is { } context) entry["contextWindow"] = context;
            if (BoolHint(info?["vision"]) == true) entry["supportsVision"] = true;
            list.AddNode(entry);
        }
        var picked = !string.IsNullOrEmpty(ctx.Model) && entries.Any(e => e.Id == ctx.Model) ? ctx.Model : entries[0].Id;
        var pickedInfo = hints.GetValueOrDefault(picked);
        var fields = new List<(string Key, string? Raw)>
        {
            ("model", Raw(picked)),
            ("base_url", Raw(Gateway(ctx))),
            ("name", Raw("Astra")),
            ("api_key", Raw(ctx.LocalKey)),
            ("api_backend", Raw("chat_completions")),
            ("app_provider_mode", Raw("generic")),
            ("app_models", Raw(list.ToJsonString())),
            ("app_efforts", Raw("[]")),
            ("supports_reasoning_effort", "false"),
            ("app_supports_vision", BoolHint(pickedInfo?["vision"]) == true ? "true" : null),
            ("context_window", PositiveLong(pickedInfo?["contextWindow"]) is { } window ? Num(window) : null),
        };
        return BuildPlan([.. TableChanges(ConfigFile, ChannelTable, fields)]);
    }
}
