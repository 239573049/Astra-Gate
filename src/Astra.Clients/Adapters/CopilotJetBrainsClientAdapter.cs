using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// GitHub Copilot in JetBrains IDEs (the <c>github-copilot-intellij</c> plugin; Coexist) takes models of other vendors through Bring
/// Your Own Key. The plugin keeps none of it itself: the Copilot language server it bundles keeps every provider in one JSON object,
/// <c>byok.json</c> in <c>$XDG_CONFIG_HOME/github-copilot</c> (when absolute), <c>%USERPROFILE%\AppData\Local\github-copilot</c> on
/// Windows and <c>~/.config/github-copilot</c> elsewhere (Xcode's and Eclipse's Copilot read the same file). A custom endpoint
/// provider <c>&lt;name&gt;</c> is three keys: <c>&lt;name&gt;-provider-config</c> (<c>{groupName, apiType: "chatCompletions"}</c>),
/// <c>&lt;name&gt;-api-key</c> (the key in plain text) and <c>&lt;name&gt;-models-config</c> (per model id: the full Chat Completions
/// <c>deploymentUrl</c>, <c>isRegistered</c>, <c>isCustomModel</c> and <c>modelCapabilities</c>). Astra adds the provider
/// <c>astra</c>; the user's other providers stay. The IDE lists the providers when Copilot signs in, so the models show after a restart,
/// in Copilot Chat's model picker (Astra selects none), and only on a Copilot plan with Bring Your Own Key. A model the user hid in
/// Manage Models (<c>isRegistered: false</c>) stays hidden through a re-apply, and that flag is not drift. Verified against magpie's
/// wiring (language server 1.545.1) only.
/// </summary>
public sealed class CopilotJetBrainsClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : ConfigFileClientAdapter(env, store)
{
    public const string ProviderName = "astra";

    private const long DefaultContextWindow = 128_000;
    private const long DefaultMaxOutputTokens = 16_000;

    private const string ConfigKey = ProviderName + "-provider-config";
    private const string ApiKeyKey = ProviderName + "-api-key";
    private const string ModelsKey = ProviderName + "-models-config";

    private const string PluginFolder = "github-copilot-intellij";

    private string ByokFile
    {
        get
        {
            if (Env.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg && Path.IsPathRooted(xdg))
                return Path.Combine(xdg, "github-copilot", "byok.json");
            return Env.Os == "windows"
                ? Env.Combine("AppData", "Local", "github-copilot", "byok.json")
                : Env.Combine(".config", "github-copilot", "byok.json");
        }
    }

    public override string Kind => ClientKinds.CopilotJetBrains;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        if (PluginInstalled() is { } plugin) return ClientDetection.Found(null, $"plugin {plugin}");
        return CurrentValue(ByokFile, ConfigFileFormat.Json, ConfigKey) is not null
            ? ClientDetection.Found(null, $"config {ByokFile}")
            : ClientDetection.NotFound($"no {PluginFolder} plugin in a JetBrains IDE");
    }

    /// <summary>The Copilot plugin among the IDEs' plugin folders (JetBrains and Android Studio), or null.</summary>
    private string? PluginInstalled()
    {
        // macOS and Windows keep an IDE's plugins in <config>/plugins, Linux in the data folder beside the IDE's own folder.
        var (root, sub) = Env.Os switch
        {
            "osx" => (Env.Combine("Library", "Application Support"), "plugins"),
            "windows" => (AppData, "plugins"),
            _ => (Env.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } data ? data : Env.Combine(".local", "share"), ""),
        };
        foreach (var vendor in new[] { "JetBrains", "Google" })
        {
            var dir = Path.Combine(root, vendor);
            if (!Directory.Exists(dir)) continue;
            foreach (var ide in Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var plugin = sub.Length == 0 ? Path.Combine(ide, PluginFolder) : Path.Combine(ide, sub, PluginFolder);
                if (Env.DirectoryExists(plugin)) return plugin;
            }
        }
        return null;
    }

    public override IReadOnlyList<string> ConfigPaths() => [ByokFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ByokFile;
        var url = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1/chat/completions";
        var hidden = HiddenModels(file);
        var hints = ModelHints(ctx);
        var models = new JsonObject();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            hints.TryGetValue(id, out var info);
            var context = PositiveLong(info?["contextWindow"]) ?? DefaultContextWindow;
            var output = PositiveLong(info?["maxOutputTokens"]) ?? DefaultMaxOutputTokens;
            output = Math.Min(output, Math.Max(1, context / 4));
            models[id] = new JsonObject
            {
                ["deploymentUrl"] = url,
                ["isRegistered"] = !hidden.Contains(id),
                ["isCustomModel"] = true,
                ["modelCapabilities"] = new JsonObject
                {
                    ["name"] = name ?? id,
                    ["maxInputTokens"] = context,
                    ["maxOutputTokens"] = output,
                    ["toolCalling"] = true,
                    ["vision"] = BoolHint(info?["vision"]) ?? false,
                },
            };
        }
        var config = new JsonObject { ["groupName"] = ProviderName, ["apiType"] = "chatCompletions" };
        return BuildPlan(
        [
            SetKey(file, ConfigKey, config.ToJsonString()),
            SetKey(file, ApiKeyKey, JsonString(ctx.LocalKey)),
            ModelsChange(file, models.ToJsonString()),
        ]);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = TrackedRemovals();
        for (var i = 0; i < changes.Count; i++)
        {
            if (changes[i].KeyPath == ModelsKey)
                changes[i] = new ConfigChange(changes[i].File, changes[i].Format, ModelsKey, changes[i].Before, null) { StillOurs = ModelsStillOurs };
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect() =>
        BuildStatus(Detect(), Applied(ByokFile, ApiKeyKey),
            ["Copilot lists the Astra models after the IDE restarts, and only while it is signed in to GitHub on a plan with Bring Your Own Key."]);

    protected override bool ValueStillOurs(string file, string keyPath, ConfigFileFormat format, string? current, string? appliedJson) =>
        keyPath == ModelsKey ? ModelsStillOurs(current, appliedJson) : base.ValueStillOurs(file, keyPath, format, current, appliedJson);

    private ConfigChange ModelsChange(string file, string modelsJson) =>
        new(file, ConfigFileFormat.Json, ModelsKey, CurrentValue(file, ConfigFileFormat.Json, ModelsKey), modelsJson) { StillOurs = ModelsStillOurs };

    /// <summary>The model ids the user hid in Manage Models (<c>isRegistered: false</c>) in the provider's current models config.</summary>
    private HashSet<string> HiddenModels(string file)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        if (Parse(CurrentValue(file, ConfigFileFormat.Json, ModelsKey)) is not { } models) return hidden;
        foreach (var (id, node) in models)
            if (node is JsonObject m && m["isRegistered"] is JsonValue v && v.TryGetValue(out bool registered) && !registered) hidden.Add(id);
        return hidden;
    }

    /// <summary>Whether the models config still holds what Astra wrote, apart from the per-model <c>isRegistered</c> flag Copilot's UI flips.</summary>
    private static bool ModelsStillOurs(string? current, string? applied)
    {
        if (current is null || applied is null) return current is null && applied is null;
        if (Parse(current) is not { } now || Parse(applied) is not { } written || now.Count != written.Count) return false;
        foreach (var (id, node) in written)
        {
            if (node is not JsonObject w || now[id] is not JsonObject n) return false;
            foreach (var (key, value) in w)
                if (key != "isRegistered" && !JsonNode.DeepEquals(value, n[key])) return false;
        }
        return true;
    }

    private static JsonObject? Parse(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
