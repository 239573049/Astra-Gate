using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// LiveAgent (Stack-Cairn/LiveAgent, a Tauri app; Coexist). Its model calls run through K-brain, which keeps every provider in one
/// JSONC <c>config.json</c> in LiveAgent's data folder: <c>LIVEAGENT_HOME</c>, else <c>K_BRAIN_HOME</c> (each only an absolute path),
/// else the folder <c>~/.liveagent/data-dir.json</c> points to (<c>{"path": …}</c>), else <c>~/.liveagent</c>. Astra adds
/// <c>providers.astra</c> in the shape LiveAgent's own settings write for an OpenAI-compatible provider (<c>type: codex</c>,
/// <c>api</c> / <c>requestFormat: openai-completions</c>, <c>baseUrl</c> the gateway's <c>/v1</c>, a literal <c>apiKey</c>,
/// <c>activeModels: null</c> = every model active, the bound provider's models sorted by id as K-brain writes them) and, when a model is
/// chosen, <c>defaultProvider</c> / <c>defaultModel</c>. K-brain refuses unknown fields, so only the ones it knows are written. It reads
/// the file once, when its backend starts, and writes the whole of it back from memory whenever a setting changes, so Astra's change must
/// be made with LiveAgent closed (the status card says so). Verified against magpie's wiring (K-brain 0.107.12) only.
/// </summary>
public sealed class LiveAgentClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private string DefaultDir => Env.Combine(".liveagent");

    private string Dir
    {
        get
        {
            foreach (var name in new[] { "LIVEAGENT_HOME", "K_BRAIN_HOME" })
                if (Env.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } dir && Path.IsPathRooted(dir)) return dir;
            var def = DefaultDir;
            if (Env.ReadTextOrNull(Path.Combine(def, "data-dir.json")) is { } text)
            {
                try
                {
                    if (JsonNode.Parse(text) is JsonObject o && o["path"] is JsonValue v && v.TryGetValue(out string? moved)
                        && moved is not null && Path.IsPathRooted(moved)
                        && !string.Equals(moved.TrimEnd('/', '\\'), def.TrimEnd('/', '\\'), StringComparison.Ordinal))
                        return moved;
                }
                catch (JsonException)
                {
                }
            }
            return def;
        }
    }

    private string ConfigFile => Path.Combine(Dir, "config.json");

    private static string ProviderPath => $"providers.{ProviderId}";

    public override string Kind => ClientKinds.LiveAgent;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = Dir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"data folder {dir}");
        return Env.DirectoryExists(DefaultDir) ? ClientDetection.Found(null, $"data folder {DefaultDir}") : ClientDetection.NotFound($"no {dir}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var hints = ModelHints(ctx);
        var entries = ModelEntries(ctx);
        var models = new JsonArray();
        foreach (var (id, name) in entries.OrderBy(e => e.Id, StringComparer.Ordinal))
        {
            var info = hints.GetValueOrDefault(id);
            var model = new JsonObject { ["id"] = id };
            if (name is not null && name != id) model["displayName"] = name;
            if (BoolHint(info?["vision"]) == true) model["input"] = new JsonArray("text", "image");
            if (PositiveLong(info?["contextWindow"]) is { } context) model["contextWindow"] = context;
            if (PositiveLong(info?["maxOutputTokens"]) is { } maxTokens) model["maxTokens"] = maxTokens;
            models.AddNode(model);
        }
        var provider = new JsonObject
        {
            ["name"] = "Astra",
            ["type"] = "codex",
            ["baseUrl"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["api"] = "openai-completions",
            ["apiKey"] = ctx.LocalKey,
            ["activeModels"] = null,
            ["modelOrder"] = new JsonArray(entries.Select(e => (JsonNode?)JsonValue.Create(e.Id)).ToArray()),
            ["requestFormat"] = "openai-completions",
            ["models"] = models,
        };
        var changes = new List<ConfigChange>
        {
            new(file, ConfigFileFormat.Json, ProviderPath, CurrentValue(file, ConfigFileFormat.Json, ProviderPath), provider.ToJsonString()),
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            changes.Add(Key("defaultProvider", ProviderId));
            changes.Add(Key("defaultModel", ctx.Model));
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
        var enabled = StillOurs(ConfigFile, ConfigFileFormat.Json, ProviderPath);
        return BuildStatus(detection, enabled, enabled
            ? ["LiveAgent keeps its settings in memory and writes them all back when one changes: quit LiveAgent before changing this, then open it and pick an Astra model in its model picker."]
            : []);
    }

    private ConfigChange Key(string key, string value) =>
        new(ConfigFile, ConfigFileFormat.Json, key, CurrentValue(ConfigFile, ConfigFileFormat.Json, key), JsonString(value));
}
