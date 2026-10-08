using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// GitHub Copilot Chat in VS Code (Coexist): adds a bring-your-own-key "Custom Endpoint" model group to
/// <c>chatLanguageModels.json</c> in the VS Code user directory (macOS <c>~/Library/Application Support/Code/User</c>,
/// Windows <c>%APPDATA%\Code\User</c>, Linux <c>$XDG_CONFIG_HOME/Code/User</c>). The group
/// <c>{"name":"Astra","vendor":"customendpoint","apiType":"chat-completions","models":[…]}</c> lists the bound
/// provider's models, each pointing at the gateway's <c>/v1/chat/completions</c> (VS Code docs: "Add a custom endpoint
/// model"). The local key travels as a per-model <c>requestHeaders.Authorization</c>: the group-level <c>apiKey</c> is a
/// secret VS Code only resolves from its secret storage (a literal value in the file is read as a storage reference),
/// while a user-supplied Authorization header replaces the inferred one (verified against the Copilot Chat extension
/// bundled with VS Code). The file is a JSON array; the group is addressed with a selector
/// (<see cref="JsoncEditor.Selector"/>) and owned as a whole (<see cref="ConfigChangeKind.Table"/>), so the user's
/// other groups and the per-model "settings" VS Code stores inside our group never count as drift. Models are
/// picked in VS Code's model picker; Astra selects none.
/// </summary>
public sealed class VsCodeCopilotClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string GroupName = "Astra";

    /// <summary>Context window and output limit used when the model catalog does not know them.</summary>
    public const long DefaultContextWindow = 128_000;

    public const long DefaultMaxOutputTokens = 16_384;

    private static readonly string GroupPath = JsoncEditor.Selector(("vendor", "customendpoint"), ("name", GroupName));

    private static readonly string[] ManagedKeys = ["apiType", "models"];

    private string UserDir => Env.Os switch
    {
        "osx" => Env.Combine("Library", "Application Support", "Code", "User"),
        "windows" => Path.Combine(
            Env.GetEnvironmentVariable("APPDATA") is { Length: > 0 } appData ? appData : Env.Combine("AppData", "Roaming"),
            "Code", "User"),
        _ => Path.Combine(
            Env.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Env.Combine(".config"),
            "Code", "User"),
    };

    private string ConfigFile => Path.Combine(UserDir, "chatLanguageModels.json");

    public override string Kind => ClientKinds.VsCodeCopilot;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = UserDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("code");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no code executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        if (CurrentValue(file, ConfigFileFormat.Json, GroupPath, ConfigChangeKind.Table) is null)
        {
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, GroupPath, null,
                new JsonObject { ["name"] = GroupName, ["vendor"] = "customendpoint" }.ToJsonString(), ConfigChangeKind.Table));
        }
        changes.Add(Key("apiType", JsonString("chat-completions")));
        changes.Add(Key("models", Models(ctx).ToJsonString()));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        foreach (var key in ManagedKeys)
        {
            var path = $"{GroupPath}.{key}";
            if (Store.Get(Kind, file, path) is null) continue;
            var current = CurrentValue(file, ConfigFileFormat.Json, path);
            if (current is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, current, null));
        }
        // The group Astra created goes last, together with whatever VS Code stored inside it.
        if (Store.Get(Kind, file, GroupPath) is { OriginalAbsent: true })
        {
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, GroupPath,
                CurrentValue(file, ConfigFileFormat.Json, GroupPath, ConfigChangeKind.Table), null, ConfigChangeKind.Table));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var path = $"{GroupPath}.models";
        var entry = Store.Get(Kind, ConfigFile, path);
        var enabled = entry is not null && ConfigValueCodec.EqualsValue(
            ConfigFileFormat.Json, CurrentValue(ConfigFile, ConfigFileFormat.Json, path), entry.AppliedValueJson);
        return BuildStatus(detection, enabled, []);
    }

    protected override bool IsTableKeyPath(string keyPath) => keyPath == GroupPath;

    private ConfigChange Key(string key, string valueJson)
    {
        var path = $"{GroupPath}.{key}";
        return new ConfigChange(ConfigFile, ConfigFileFormat.Json, path, CurrentValue(ConfigFile, ConfigFileFormat.Json, path), valueJson);
    }

    /// <summary>One Custom Endpoint model per bound model, with limits from the model catalog where known.</summary>
    private static JsonArray Models(EnableContext ctx)
    {
        var url = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1/chat/completions";
        var details = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var (_, value) in ctx.ExtraObject("models") ?? new JsonObject())
        {
            if (value is JsonObject m && m["id"] is JsonValue id && id.TryGetValue(out string? s) && !string.IsNullOrEmpty(s))
                details.TryAdd(s, m);
        }

        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            details.TryGetValue(id, out var info);
            var maxOutput = Long(info?["maxOutputTokens"]) ?? DefaultMaxOutputTokens;
            var context = Long(info?["contextWindow"]) ?? DefaultContextWindow;
            if (maxOutput >= context) maxOutput = Math.Max(1, context / 4);
            models.AddNode(new JsonObject
            {
                ["id"] = id,
                ["name"] = name ?? id,
                ["url"] = url,
                ["toolCalling"] = true,
                ["vision"] = Bool(info?["vision"]) ?? false,
                ["thinking"] = Bool(info?["reasoning"]) ?? false,
                ["maxInputTokens"] = context - maxOutput,
                ["maxOutputTokens"] = maxOutput,
                ["requestHeaders"] = new JsonObject { ["Authorization"] = "Bearer " + ctx.LocalKey },
            });
        }
        return models;
    }

    private static long? Long(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.Number
        && long.TryParse(v.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out var l) && l > 0 ? l : null;

    private static bool? Bool(JsonNode? node) => node is JsonValue v && v.TryGetValue(out bool b) ? b : null;
}
