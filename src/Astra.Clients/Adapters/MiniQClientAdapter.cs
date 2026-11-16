using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// miniQ (LeadingThink/miniQ, command <c>miniq</c>; Coexist): the desktop app and the terminal client are both clients of one
/// <c>miniq-daemon</c>, whose provider is one OpenAI- or Anthropic-compatible endpoint, <c>settings.json</c>'s <c>provider</c>
/// (<c>{baseUrl, apiKey, model, apiProtocol, reasoningEffort?}</c>). <c>settings.json</c> sits in the daemon's data folder:
/// <c>MINIQ_DATA_DIR</c>, else the folder <c>miniq-data-dir.json</c> (<c>{"data_dir": …}</c>) names beside the default, else
/// <c>%LOCALAPPDATA%\miniq</c> on Windows and <c>~/.local/share/miniq</c> elsewhere (macOS too). Astra owns the <c>provider</c> key whole:
/// the gateway's <c>/v1</c>, the local key, the chosen model (else the bound provider's first) and <c>apiProtocol: chat_completions</c>
/// (<c>auto</c> would guess Responses or Anthropic Messages from a model's name). The user's provider is recorded and put back; the rest
/// of <c>settings.json</c> stays. The daemon reads the file once and writes its own copy back on every settings change, so a running
/// daemon must be restarted (magpie also tells it over its local WebSocket, which an adapter cannot do). Verified against magpie's
/// wiring (miniQ 0.1.213) only.
/// </summary>
public sealed class MiniQClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private string DataDir
    {
        get
        {
            if (Env.GetEnvironmentVariable("MINIQ_DATA_DIR") is { Length: > 0 } dir) return dir;
            var fallback = Env.Combine(".local", "share", "miniq");
            if (Env.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local) fallback = Path.Combine(local, "miniq");
            // miniq-data-dir.json, beside the default, names a folder the user moved the data to in Settings.
            if (Env.ReadTextOrNull(Path.Combine(Path.GetDirectoryName(fallback)!, "miniq-data-dir.json")) is { } pointer)
            {
                try
                {
                    if (JsonNode.Parse(pointer) is JsonObject o && o["data_dir"] is JsonValue v && v.TryGetValue(out string? moved) && !string.IsNullOrEmpty(moved))
                        return moved;
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }
            return fallback;
        }
    }

    private string SettingsFile => Path.Combine(DataDir, "settings.json");

    public override string Kind => ClientKinds.MiniQ;

    protected override string MarkerFile => SettingsFile;

    protected override string MarkerKey => "provider";

    protected override IReadOnlyList<string> EnabledWarnings =>
        ["Restart miniQ to use this: its running daemon keeps the provider it started with and writes it back over settings.json."];

    public override ClientDetection Detect()
    {
        var dir = DataDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"data directory {dir}");
        var exe = Env.FindOnPath("miniq");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no miniq executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [SettingsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var model = !string.IsNullOrEmpty(ctx.Model) ? ctx.Model : ModelEntries(ctx).FirstOrDefault().Id;
        // miniQ needs a model to start on: with none there is nothing to write.
        if (string.IsNullOrEmpty(model)) return BuildPlan([]);
        var provider = new JsonObject
        {
            ["baseUrl"] = Gateway(ctx),
            ["apiKey"] = ctx.LocalKey,
            ["model"] = model,
            ["apiProtocol"] = "chat_completions",
        };
        return BuildPlan([JsonSet(SettingsFile, "provider", provider)]);
    }
}
