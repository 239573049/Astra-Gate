using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;
using Astra.Clients.Editing;

namespace Astra.Clients.Adapters;

/// <summary>
/// AstrBot (AstrBotDevs/AstrBot, a chatbot framework for QQ, Telegram, Discord, Lark…; command <c>astrbot</c>; Coexist). Every setting is in
/// <c>&lt;root&gt;/data/cmd_config.json</c>, the root being <c>ASTRBOT_ROOT</c>, else the desktop app's <c>~/.astrbot</c>, else the folder
/// <c>astrbot init</c> was run in that Astra can know of (<c>~</c> with a <c>.astrbot</c> file, <c>~/astrbot</c>, <c>~/AstrBot</c>). Since 4.10 a chat
/// provider is two lists: <c>provider_sources</c> (an endpoint and its keys) and <c>provider</c> (one entry per model of a source). Astra owns the
/// element <c>id = astra</c> of the first (OpenAI Compatible, <c>api_base</c> the gateway's <c>/v1</c>, the local key) and one
/// <c>astra/&lt;model&gt;</c> element of the second per bound model, and makes the chosen one the default chat model
/// (<c>agent_runner.config.model.provider_id</c> when the file has <c>agent_runner</c>, else the older
/// <c>provider_settings.default_provider_id</c>). The user's own sources and models stay untouched; a field changed in one of Astra's entries
/// inside AstrBot's WebUI shows as drift (and a disable then leaves that entry alone). AstrBot reads the file once
/// at start and its WebUI writes the whole of it back on every save, so it must be restarted (before saving anything in the WebUI). The file
/// keeps its UTF-8 BOM. AstrBot in Docker is not wired: its data folder is a volume Astra cannot know. Model ids containing
/// <c>[ ] = &amp;</c> cannot be addressed and are left out. Verified against magpie's wiring (AstrBot 4.28) only.
/// </summary>
public sealed class AstrBotClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string SourceId = "astra";

    private const string SourcesKey = "provider_sources";

    private const string ModelsKey = "provider";

    private const string RunnerPick = "agent_runner.config.model.provider_id";

    private const string LegacyPick = "provider_settings.default_provider_id";

    private string Root
    {
        get
        {
            if (Env.GetEnvironmentVariable("ASTRBOT_ROOT")?.Trim() is { Length: > 0 } root) return root;
            var desktop = Env.Combine(".astrbot");
            if (Env.FileExists(Path.Combine(desktop, "data", "cmd_config.json"))) return desktop;
            if (Env.FileExists(desktop)) return Env.HomeDirectory;
            foreach (var name in new[] { "astrbot", "AstrBot" })
            {
                var dir = Env.Combine(name);
                if (Env.FileExists(Path.Combine(dir, "data", "cmd_config.json")) || Env.FileExists(Path.Combine(dir, ".astrbot"))) return dir;
            }
            return desktop;
        }
    }

    private string DataDir => Path.Combine(Root, "data");

    private string ConfigFile => Path.Combine(DataDir, "cmd_config.json");

    private static string SourcePath => $"{SourcesKey}." + JsoncEditor.Selector(("id", SourceId));

    private static string ModelPath(string model) => $"{ModelsKey}." + JsoncEditor.Selector(("id", $"{SourceId}/{model}"));

    private static bool Addressable(string id) => id.IndexOfAny(['[', ']', '=', '&']) < 0;

    public override string Kind => ClientKinds.AstrBot;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        if (Env.FileExists(ConfigFile)) return ClientDetection.Found(null, $"settings {ConfigFile}");
        if (Env.DirectoryExists(DataDir)) return ClientDetection.Found(null, $"data folder {DataDir}");
        var exe = Env.FindOnPath("astrbot");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {DataDir} and no astrbot executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    private string PickKey => CurrentValue(ConfigFile, ConfigFileFormat.Json, "agent_runner") is null ? LegacyPick : RunnerPick;

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var hints = ModelHints(ctx);
        var source = new JsonObject
        {
            ["id"] = SourceId,
            ["provider"] = "openai",
            ["type"] = "openai_chat_completion",
            ["provider_type"] = "chat_completion",
            ["enable"] = true,
            ["key"] = new JsonArray(ctx.LocalKey),
            ["api_base"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["timeout"] = 120,
            ["proxy"] = "",
            ["custom_headers"] = new JsonObject(),
        };
        var changes = new List<ConfigChange>
        {
            new(file, ConfigFileFormat.Json, SourcePath, CurrentValue(file, ConfigFileFormat.Json, SourcePath), source.ToJsonString()),
        };
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, _) in ModelEntries(ctx))
        {
            if (!Addressable(id)) continue;
            wanted.Add(ModelPath(id));
            var info = hints.GetValueOrDefault(id);
            // AstrBot drops a model's tools unless tool_use is in its modalities, and the gateway hands a tool call on whatever the model.
            var modalities = BoolHint(info?["vision"]) == true ? new JsonArray("text", "image", "tool_use") : new JsonArray("text", "tool_use");
            var model = new JsonObject
            {
                ["id"] = $"{SourceId}/{id}",
                ["enable"] = true,
                ["provider_source_id"] = SourceId,
                ["model"] = id,
                ["modalities"] = modalities,
                ["custom_extra_body"] = new JsonObject(),
                ["max_context_tokens"] = PositiveLong(info?["contextWindow"]) ?? 0,
            };
            var path = ModelPath(id);
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, CurrentValue(file, ConfigFileFormat.Json, path), model.ToJsonString()));
        }
        // A model the provider no longer offers goes out again (a list kept current).
        var modelPrefix = $"{ModelsKey}." + "[id=" + SourceId + "/";
        foreach (var entry in Store.List(Kind))
        {
            if (entry.FilePath == file && entry.KeyPath.StartsWith(modelPrefix, StringComparison.Ordinal) && !wanted.Contains(entry.KeyPath)
                && CurrentValue(file, ConfigFileFormat.Json, entry.KeyPath) is { } stale)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Json, entry.KeyPath, stale, null));
        }
        if (!string.IsNullOrEmpty(ctx.Model) && Addressable(ctx.Model))
        {
            var pick = PickKey;
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, pick, CurrentValue(file, ConfigFileFormat.Json, pick),
                JsonString($"{SourceId}/{ctx.Model}")));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        foreach (var entry in Store.List(Kind))
            if (CurrentValue(entry.FilePath, ConfigFileFormat.Json, entry.KeyPath) is { } current)
                changes.Add(new ConfigChange(entry.FilePath, ConfigFileFormat.Json, entry.KeyPath, current, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = StillOurs(ConfigFile, ConfigFileFormat.Json, SourcePath);
        var warnings = new List<string>();
        if (enabled)
        {
            warnings.Add("AstrBot reads cmd_config.json at start-up and writes it back whole when its WebUI saves settings: restart AstrBot to use this, before saving anything in its WebUI.");
            if (CurrentValue(ConfigFile, ConfigFileFormat.Json, "agent_runner.runner_type") is { } runner && StringOf(runner) is { } name && name != "local")
                warnings.Add($"AstrBot's agent runner is {name}, which does not ask its chat model, so it does not ask Astra.");
        }
        return BuildStatus(detection, enabled, warnings);
    }
}
