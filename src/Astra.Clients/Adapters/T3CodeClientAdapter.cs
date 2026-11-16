using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// T3 Code (pingdotgg/t3code, a GUI that drives Claude Code, Codex, Cursor and others; Coexist) keeps its settings in
/// <c>~/.t3/userdata/settings.json</c> (<c>$T3CODE_HOME/userdata</c>), a sparse JSON file its server watches and reloads on an outside edit.
/// Each provider it lists is an instance of a driver, and besides the built-in ones it takes instances of one's own. A
/// <c>claudeAgent</c> instance runs Claude Code with the instance's <c>environment</c> added and lists Claude's models plus its custom models,
/// each sent as Claude Code's model. So Astra is an instance of its own, <c>providerInstances.astra</c>: Claude Code pointed at the gateway
/// (<c>ANTHROPIC_BASE_URL</c> at the gateway root and <c>ANTHROPIC_AUTH_TOKEN</c> the local key, so its requests are Claude Code's, Anthropic
/// Messages, to the gateway) with every bound model as one of its <c>customModels</c>; a reasoning model gets T3's Reasoning pick and
/// Thinking switch (low / medium / high, the usual Claude Code levels, unverified). The user's own instances, their custom models and T3's other
/// keys stay; what the user set on Astra's instance in T3 (name, on/off, the environment variables besides Astra's, keys Astra does not write)
/// stays when it is rewritten and is not drift; a binary path or Claude home the user gave T3's own Claude is carried over. The model is picked in
/// T3's model picker, so Astra only decides whether its models are in it. magpie also writes a second instance for Codex (launch arguments and a
/// model catalog file); Astra does not. Verified against magpie's wiring only.
/// </summary>
public sealed class T3CodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : ConfigFileClientAdapter(env, store)
{
    public const string InstanceId = "astra";

    private static readonly string[] OwnedEnvironment = ["ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN"];

    /// <summary>Switches that take Claude Code to a cloud provider past the gateway: never carried over from what the user set on the instance.</summary>
    private static readonly string[] ProviderSwitches = ["CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY"];

    private const string SearchVariable = "ENABLE_TOOL_SEARCH";

    private static readonly string[] Known = ["driver", "displayName", "enabled", "environment", "config"];

    private string Home
    {
        get
        {
            var value = Env.GetEnvironmentVariable("T3CODE_HOME")?.Trim();
            if (string.IsNullOrEmpty(value)) return Env.Combine(".t3");
            if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
                return Env.HomeDirectory + value[1..];
            return value;
        }
    }

    private string SettingsFile => Path.Combine(Home, "userdata", "settings.json");

    private static string InstancePath => $"providerInstances.{InstanceId}";

    public override string Kind => ClientKinds.T3Code;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        if (Env.DirectoryExists(Home)) return ClientDetection.Found(null, $"config directory {Home}");
        // No command is looked up: `t3` is a common name. The Mac app is found by its bundle.
        var app = FindApp("T3 Code (Alpha).app", "T3 Code.app");
        return app is not null ? ClientDetection.Found(null, $"application {app}") : ClientDetection.NotFound($"no {Home}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [SettingsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = SettingsFile;
        var hints = ModelHints(ctx);
        var custom = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            var model = new JsonObject { ["slug"] = id, ["name"] = name ?? id };
            if (BoolHint(hints.GetValueOrDefault(id)?["reasoning"]) == true) model["capabilities"] = Capabilities();
            custom.AddNode(model);
        }
        var config = new JsonObject { ["customModels"] = custom };
        foreach (var key in new[] { "binaryPath", "homePath" })
        {
            // The user's own Claude instance, else the setting it was before T3 had instances.
            var value = TextAt(file, $"providerInstances.claudeAgent.config.{key}") ?? TextAt(file, $"providers.claudeAgent.{key}");
            if (!string.IsNullOrWhiteSpace(value)) config[key] = value.Trim();
        }
        var environment = new JsonArray(
            EnvironmentEntry("ANTHROPIC_BASE_URL", ctx.GatewayBaseUrl.TrimEnd('/')),
            EnvironmentEntry("ANTHROPIC_AUTH_TOKEN", ctx.LocalKey),
            // tool search on: the Claude Code T3 runs may be one that sends every MCP tool's definition on each request
            EnvironmentEntry(SearchVariable, "true"));
        var instance = new JsonObject
        {
            ["driver"] = "claudeAgent",
            ["displayName"] = "Astra",
            ["enabled"] = true,
            ["environment"] = environment,
            ["config"] = config,
        };

        var before = CurrentValue(file, ConfigFileFormat.Json, InstancePath);
        if (Store.Get(Kind, file, InstancePath) is not null && Parse(before) is { } current) CarryOver(current, instance);
        return BuildPlan([new ConfigChange(file, ConfigFileFormat.Json, InstancePath, before, instance.ToJsonString()) { StillOurs = OwnedPartsEqual }]);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = TrackedRemovals();
        for (var i = 0; i < changes.Count; i++)
        {
            if (changes[i].KeyPath == InstancePath)
                changes[i] = new ConfigChange(changes[i].File, changes[i].Format, InstancePath, changes[i].Before, null) { StillOurs = OwnedPartsEqual };
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect() =>
        BuildStatus(Detect(), Applied(SettingsFile, InstancePath),
            ["Pick an Astra model in T3 Code's model picker: the Astra instance runs Claude Code on the gateway."]);

    protected override bool ValueStillOurs(string file, string keyPath, ConfigFileFormat format, string? current, string? appliedJson) =>
        keyPath == InstancePath ? OwnedPartsEqual(current, appliedJson) : base.ValueStillOurs(file, keyPath, format, current, appliedJson);

    private static JsonObject EnvironmentEntry(string name, string value) =>
        new() { ["name"] = name, ["value"] = value, ["sensitive"] = false };

    /// <summary>The picks T3 shows beside a custom model: without them it shows none and sends no effort.</summary>
    private static JsonObject Capabilities()
    {
        var options = new JsonArray(
            new JsonObject { ["id"] = "low", ["label"] = "Low" },
            new JsonObject { ["id"] = "medium", ["label"] = "Medium", ["isDefault"] = true },
            new JsonObject { ["id"] = "high", ["label"] = "High" });
        return new JsonObject
        {
            ["optionDescriptors"] = new JsonArray(
                new JsonObject { ["id"] = "effort", ["label"] = "Reasoning", ["type"] = "select", ["options"] = options },
                new JsonObject { ["id"] = "thinking", ["label"] = "Thinking", ["type"] = "boolean", ["currentValue"] = true }),
        };
    }

    private string? TextAt(string file, string keyPath) => StringOf(CurrentValue(file, ConfigFileFormat.Json, keyPath));

    /// <summary>What the user set on the instance in T3 stays: its name, whether it is on, other environment variables, other config and top-level keys.</summary>
    private static void CarryOver(JsonObject current, JsonObject instance)
    {
        if (current["displayName"] is JsonValue name && name.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)) instance["displayName"] = text;
        if (current["enabled"] is JsonValue on && on.TryGetValue(out bool enabled)) instance["enabled"] = enabled;
        if (current["environment"] is JsonArray theirs && instance["environment"] is JsonArray environment)
        {
            foreach (var entry in theirs)
            {
                if (entry is not JsonObject e || e["name"] is not JsonValue n || !n.TryGetValue(out string? variable) || string.IsNullOrEmpty(variable)) continue;
                if (OwnedEnvironment.Contains(variable) || ProviderSwitches.Contains(variable)) continue;
                if (variable == SearchVariable)
                    for (var i = environment.Count - 1; i >= 0; i--)
                        if (environment[i]?["name"]?.GetValue<string>() == SearchVariable) environment.RemoveAt(i);
                environment.AddNode(e.DeepClone());
            }
        }
        if (current["config"] is JsonObject theirConfig && instance["config"] is JsonObject config)
        {
            foreach (var (key, value) in theirConfig)
                if (key != "customModels" && !config.ContainsKey(key)) config[key] = value?.DeepClone();
        }
        foreach (var (key, value) in current)
            if (!Known.Contains(key) && !instance.ContainsKey(key)) instance[key] = value?.DeepClone();
    }

    /// <summary>Whether the instance still holds what Astra wrote: the driver, Astra's gateway variables and the custom models.</summary>
    private static bool OwnedPartsEqual(string? currentText, string? appliedJson)
    {
        if (currentText is null || appliedJson is null) return currentText is null && appliedJson is null;
        if (Parse(currentText) is not { } current || Parse(appliedJson) is not { } applied) return false;
        if (!JsonNode.DeepEquals(applied["driver"], current["driver"])) return false;
        foreach (var variable in OwnedEnvironment)
            if (EnvironmentValue(applied, variable) != EnvironmentValue(current, variable)) return false;
        return JsonNode.DeepEquals(applied["config"]?["customModels"], current["config"]?["customModels"]);
    }

    private static string? EnvironmentValue(JsonObject instance, string name)
    {
        if (instance["environment"] is not JsonArray environment) return null;
        foreach (var entry in environment)
            if (entry?["name"] is JsonValue n && n.TryGetValue(out string? s) && s == name)
                return entry["value"] is JsonValue v && v.TryGetValue(out string? value) ? value : null;
        return null;
    }

    private static JsonObject? Parse(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
