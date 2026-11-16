using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;
using Astra.Clients.Editing;

namespace Astra.Clients.Adapters;

/// <summary>
/// OpenMausBot (milind-soni/OpenMausBot, an Electron chat app whose bots run the CLIs installed on the computer; Coexist). Its settings are
/// <c>config.json</c> in <c>OMB_DATA_DIR</c> (only an absolute path), else <c>~/.openmausbot</c>; every engine a bot can run on is an entry of its
/// <c>instances</c> map. Astra adds the engine <c>astra</c> the way OpenMausBot's own "Claude endpoint" writes one: a <c>claudeAgent</c> instance with
/// <c>access: custom</c>, a Claude home of its own (<c>providers/astra</c> under the data folder), <c>requireApiKey</c>, the gateway's root address and the
/// local key as <c>ANTHROPIC_BASE_URL</c> / <c>ANTHROPIC_AUTH_TOKEN</c> / <c>ANTHROPIC_API_KEY</c>, and every bound model in <c>endpointModels</c> (its
/// picker; one id it does not accept makes it refuse the engine, so only ids it takes are listed). A Claude Code the user gave it
/// (<c>config.cli</c> of an engine that is no endpoint) is kept for the new engine. A saved <c>instances</c> map is the whole fleet, so with none
/// in the file Astra writes OpenMausBot's default fleet (its <c>DEFAULT_FLEET</c>, commit 516ca75) beside the engine as OpenMausBot does when a user adds an
/// engine; disabling puts the original (no map) back byte for byte. OpenMausBot reads the file at start-up and writes its map back when
/// the user changes an engine in it, so it must be closed first. Verified against magpie's wiring only.
/// </summary>
public sealed partial class OpenMausBotClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string InstanceId = "astra";

    /// <summary>OpenMausBot's default fleet (server/instance-fleets.ts at 516ca75): the engines it runs while its file saves none.</summary>
    public const string DefaultFleet = """
        {
          "grok": {"driver": "grokAgent"},
          "kimi": {"driver": "kimiAgent"},
          "droid": {"driver": "droidAgent"},
          "cursor": {"driver": "cursorAgent"},
          "githubCopilot": {"driver": "copilotAgent"},
          "commandCode": {"driver": "commandCodeAgent"},
          "claude": {"driver": "claudeAgent"},
          "codex": {"driver": "codex"},
          "chatgpt": {"driver": "codex", "displayName": "ChatGPT plan", "config": {"authMode": "chatgpt-plan"}},
          "antigravity": {"driver": "antigravityAgent"},
          "opencodeGo": {"driver": "opencodeGo"},
          "openaiCompat": {"driver": "openai-compat"},
          "mistral": {"driver": "mistral"},
          "cerebras": {"driver": "cerebras"},
          "groq": {"driver": "groq"},
          "greenference": {"driver": "greenference"},
          "meta": {"driver": "meta"},
          "deepseek": {"driver": "deepseekAgent"},
          "museCode": {"driver": "museCode"},
          "openai": {"driver": "openai-compat", "displayName": "OpenAI", "access": "api", "icon": {"kind": "preset", "preset": "openai"},
            "config": {"url": "https://api.openai.com/v1", "apiKeyEnv": "OMB_OPENAI_API_KEY", "catalog": "openai"}},
          "claudeApi": {"driver": "claudeAgent", "displayName": "Claude (API key)", "access": "api", "icon": {"kind": "preset", "preset": "anthropic"},
            "config": {"requireApiKey": true}},
          "xaiApi": {"driver": "grok", "displayName": "xAI", "access": "api", "icon": {"kind": "preset", "preset": "xai"}},
          "openrouter": {"driver": "openai-compat", "displayName": "OpenRouter", "access": "api", "icon": {"kind": "preset", "preset": "openrouter"},
            "config": {"url": "https://openrouter.ai/api/v1", "apiKeyEnv": "OMB_OPENROUTER_API_KEY"}},
          "qwen": {"driver": "qwenAgent"},
          "hermes": {"driver": "hermesAgent"},
          "cline": {"driver": "clineAgent"},
          "pi": {"driver": "piAgent"}
        }
        """;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._:/-]*(?:\[1m\])?$", RegexOptions.IgnoreCase)]
    private static partial Regex ModelIdPattern();

    private string Dir =>
        Env.GetEnvironmentVariable("OMB_DATA_DIR")?.Trim() is { Length: > 0 } dir && Path.IsPathRooted(dir) ? dir : Env.Combine(".openmausbot");

    private string ConfigFile => Path.Combine(Dir, "config.json");

    private static string InstancePath => $"instances.{InstanceId}";

    public override string Kind => ClientKinds.OpenMausBot;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = Dir;
        return Env.DirectoryExists(dir) ? ClientDetection.Found(null, $"data folder {dir}") : ClientDetection.NotFound($"no {dir}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var models = new JsonArray();
        var ids = new List<string>();
        foreach (var (id, _) in ModelEntries(ctx))
            if (ModelIdPattern().IsMatch(id) && !ids.Contains(id))
            {
                ids.Add(id);
                models.AddNode(JsonValue.Create(id));
            }
        if (ids.Count == 0) throw new EditorException("OpenMausBot takes no model id Astra could list: pick models for it first.");
        var key = ctx.LocalKey;
        var config = new JsonObject
        {
            ["configDir"] = Path.Combine(Dir, "providers", InstanceId),
            ["requireApiKey"] = true,
            ["endpointModels"] = models,
        };
        var instances = CurrentValue(file, ConfigFileFormat.Json, "instances") is { } text ? JsonNode.Parse(text) : null;
        if (instances is not null and not JsonObject)
            throw new EditorException($"{file}: instances is not a map of engines, so Astra leaves it alone.");
        // The Claude Code the user gave OpenMausBot (the first claudeAgent with a binary that is no endpoint) carries over.
        if (instances is JsonObject map)
        {
            foreach (var (id, node) in map.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (id == InstanceId || node is not JsonObject inst || (string?)inst["driver"] != "claudeAgent") continue;
                if (inst["config"] is not JsonObject cfg || cfg.ContainsKey("endpointModels")) continue;
                if (cfg["cli"] is JsonValue cli && cli.TryGetValue(out string? path) && !string.IsNullOrEmpty(path))
                {
                    config["cli"] = path;
                    break;
                }
            }
        }
        var model = !string.IsNullOrEmpty(ctx.Model) && ids.Contains(ctx.Model) ? ctx.Model : ids[0];
        var instance = new JsonObject
        {
            ["driver"] = "claudeAgent",
            ["displayName"] = "Astra",
            ["access"] = "custom",
            ["config"] = config,
            ["environment"] = new JsonObject
            {
                ["ANTHROPIC_BASE_URL"] = ctx.GatewayBaseUrl.TrimEnd('/'),
                ["ANTHROPIC_AUTH_TOKEN"] = key,
                // Claude Code's own sign-in check needs a key it knows, as on OpenMausBot's endpoints.
                ["ANTHROPIC_API_KEY"] = key,
                ["ANTHROPIC_MODEL"] = model,
            },
        };
        // Once Astra has written the fleet itself, the whole map stays its change (the fleet is one recorded value).
        var wroteFleet = Store.Get(Kind, file, "instances") is not null;
        if (instances is JsonObject { Count: > 0 } && !wroteFleet)
        {
            return BuildPlan([new ConfigChange(file, ConfigFileFormat.Json, InstancePath, CurrentValue(file, ConfigFileFormat.Json, InstancePath), instance.ToJsonString())]);
        }
        // No saved map: the map is the whole fleet, so it starts as OpenMausBot's default one.
        var fleet = wroteFleet && instances is JsonObject { Count: > 0 } saved ? saved.DeepClone().AsObject() : JsonNode.Parse(DefaultFleet)!.AsObject();
        fleet[InstanceId] = instance;
        return BuildPlan([new ConfigChange(file, ConfigFileFormat.Json, "instances", CurrentValue(file, ConfigFileFormat.Json, "instances"), fleet.ToJsonString())]);
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
        var enabled = StillOurs(ConfigFile, ConfigFileFormat.Json, InstancePath) || StillOurs(ConfigFile, ConfigFileFormat.Json, "instances");
        return BuildStatus(detection, enabled, enabled
            ? ["OpenMausBot reads its engines when it starts and writes back the ones it holds when you change one in it: quit OpenMausBot before changing this, then open it and pick Astra as a bot's engine."]
            : []);
    }
}
