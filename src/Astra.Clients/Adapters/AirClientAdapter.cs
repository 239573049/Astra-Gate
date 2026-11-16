using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// JetBrains Air (JetBrains' agentic development environment) and the Air plugin of JetBrains IDEs (Coexist). Air runs its own
/// agents on the accounts signed in under its AI Providers settings, with no base URL of its own, but it takes any agent that
/// speaks the Agent Client Protocol, listed in <c>acp.json</c> as <c>{"agent_servers": {"&lt;name in Air's agent menu&gt;": {"command",
/// "args", "env"}}}</c>. Astra adds an agent of its own there, <c>Astra</c>: OpenCode's ACP server (<c>opencode acp</c>) handed, as
/// <c>OPENCODE_CONFIG</c>, a config file Astra keeps beside <c>acp.json</c> (<c>astra-opencode.json</c>) that holds Astra's provider, its
/// models, the model a task starts on, and <c>enabled_providers</c> narrowed to Astra, so Air's model menu lists Astra's models and
/// nothing of OpenCode's own. OpenCode merges that file over the user's own config. Air's own agents and accounts are not touched.
/// The standalone app reads <c>acp.json</c> in its config folder (<c>~/Library/Application Support/JetBrains/Air</c>,
/// <c>$XDG_CONFIG_HOME/JetBrains/Air</c>, <c>%APPDATA%\JetBrains\Air</c>); the plugin (bundled with 2026.3 IDEs, from the Marketplace
/// in 2026.2) reads <c>~/.jetbrains/acp.json</c> on every system, the file AI Assistant's custom agents use too. OpenCode must be
/// installed; its path is written in full since Air, an app, may not have the shell's PATH. The WSL variant magpie adds for
/// the plugin is not written. Verified against magpie's wiring (OpenCode 2.0.20's <c>opencode acp</c>) only, not with Air itself.
/// </summary>
public sealed partial class AirClientAdapter(ClientEnvironment env, IClientConfigStateStore store, AirFlavour flavour)
    : ConfigFileClientAdapter(env, store)
{
    public const string AgentName = "Astra";
    public const string ProviderId = "astra";

    private string Dir => flavour.Ide ? Env.Combine(".jetbrains") : AppSupportDir("JetBrains", "Air");

    private string AcpFile => Path.Combine(Dir, "acp.json");

    private string OpenCodeConfigFile => Path.Combine(Dir, "astra-opencode.json");

    private static string EntryPath => $"agent_servers.{AgentName}";

    public override string Kind => flavour.Kind;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        if (!flavour.Ide)
        {
            if (Env.DirectoryExists(Dir)) return ClientDetection.Found(null, $"config directory {Dir}");
            var app = FindApp("Air.app", "JetBrains Air.app");
            return app is not null ? ClientDetection.Found(null, $"application {app}") : ClientDetection.NotFound($"no {Dir}");
        }
        // ~/.jetbrains is there for AI Assistant's agents too: an IDE of 2026.2 or later, or Astra's agent already in acp.json, says Air can be.
        if (CurrentValue(AcpFile, ConfigFileFormat.Json, EntryPath) is not null) return ClientDetection.Found(null, $"agent in {AcpFile}");
        return IdeSince(2026, 2) is { } ide
            ? ClientDetection.Found(null, $"JetBrains IDE {ide}")
            : ClientDetection.NotFound("no JetBrains IDE of 2026.2 or later");
    }

    public override IReadOnlyList<string> ConfigPaths() => [AcpFile, OpenCodeConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var entries = ModelEntries(ctx);
        var model = !string.IsNullOrEmpty(ctx.Model) ? ctx.Model : entries.FirstOrDefault().Id;
        if (model is null) return BuildPlan([]);
        var hints = ModelHints(ctx);
        var models = new JsonObject();
        foreach (var (id, name) in entries)
        {
            hints.TryGetValue(id, out var info);
            var entry = new JsonObject();
            if (name is not null) entry["name"] = name;
            if (PositiveLong(info?["contextWindow"]) is { } context)
                entry["limit"] = new JsonObject { ["context"] = context, ["output"] = PositiveLong(info?["maxOutputTokens"]) ?? 0 };
            var vision = BoolHint(info?["vision"]);
            var pdf = BoolHint(info?["pdf"]);
            if (vision is not null || pdf is not null)
            {
                var input = new JsonArray("text");
                if (vision == true) input.Add((JsonNode)"image");
                if (pdf == true) input.Add((JsonNode)"pdf");
                entry["modalities"] = new JsonObject { ["input"] = input, ["output"] = new JsonArray("text") };
            }
            if (BoolHint(info?["reasoning"]) is { } reasoning) entry["reasoning"] = reasoning;
            models[id] = entry;
        }
        var provider = new JsonObject
        {
            ["npm"] = "@ai-sdk/openai-compatible",
            ["name"] = "Astra",
            ["options"] = new JsonObject { ["baseURL"] = V1(ctx), ["apiKey"] = ctx.LocalKey },
            ["models"] = models,
        };
        var reference = $"{ProviderId}/{model}";
        var agent = new JsonObject
        {
            ["command"] = OpenCodePath(),
            ["args"] = new JsonArray("acp"),
            ["env"] = new JsonObject { ["OPENCODE_CONFIG"] = OpenCodeConfigFile },
        };
        var file = OpenCodeConfigFile;
        return BuildPlan(
        [
            SetKey(file, "$schema", JsonString("https://opencode.ai/config.json")),
            SetKey(file, "model", JsonString(reference)),
            // titles and summaries too, as the user's own small model may be a provider left out here
            SetKey(file, "small_model", JsonString(reference)),
            SetKey(file, "enabled_providers", new JsonArray(ProviderId).ToJsonString()),
            SetKey(file, $"provider.{ProviderId}", provider.ToJsonString()),
            SetKey(AcpFile, EntryPath, agent.ToJsonString()),
        ]);
    }

    public override ConfigChangePlan PlanDisable() => BuildPlan(TrackedRemovals());

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var enabled = Applied(AcpFile, EntryPath);
        var warnings = new List<string>();
        if (enabled && OpenCodeOnMachine() is null)
            warnings.Add("Air runs Astra's models through OpenCode's ACP server and OpenCode is not installed here: install OpenCode, then pick Astra in a new Air task.");
        else if (enabled)
            warnings.Add(flavour.Ide
                ? "In your JetBrains IDE (2026.3, or 2026.2 with the Air plugin), pick Astra in Air's agent menu; restart the IDE if it is not listed yet."
                : "In Air, start a new task and pick Astra in its agent menu: Astra's models are in its model menu.");
        return BuildStatus(Detect(), enabled, warnings);
    }

    /// <summary>The OpenCode Air is to start: the one on PATH, else the one OpenCode's installer puts in <c>~/.opencode/bin</c>, else the bare command.</summary>
    private string OpenCodePath() => OpenCodeOnMachine() ?? "opencode";

    private string? OpenCodeOnMachine()
    {
        if (Env.FindOnPath("opencode") is { } onPath) return onPath;
        var name = Env.Os == "windows" ? "opencode.exe" : "opencode";
        var installed = Env.Combine(".opencode", "bin", name);
        return Env.FileExists(installed) ? installed : null;
    }

    /// <summary>A JetBrains IDE config folder (<c>IntelliJIdea2026.3</c>, <c>PyCharm2026.2</c>, …) of year.minor or later, or null.</summary>
    private string? IdeSince(int year, int minor)
    {
        var root = AppSupportDir("JetBrains");
        if (!Directory.Exists(root)) return null;
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            var m = IdeFolder().Match(name);
            if (!m.Success) continue;
            var y = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var n = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (y > year || (y == year && n >= minor)) return name;
        }
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z]+(\d{4})\.(\d+)$")]
    private static partial Regex IdeFolder();
}

/// <summary>Which Air an <see cref="AirClientAdapter"/> configures.</summary>
/// <param name="Kind">The client kind.</param>
/// <param name="Ide">The plugin in JetBrains IDEs (<c>~/.jetbrains/acp.json</c>) rather than the standalone app.</param>
public sealed record AirFlavour(string Kind, bool Ide)
{
    public static readonly AirFlavour App = new(ClientKinds.JetBrainsAir, false);

    public static readonly AirFlavour Plugin = new(ClientKinds.AirIde, true);
}
