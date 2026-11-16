using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Qoder's CLI (1.1; npm <c>@qoder-ai/qodercli</c>, command <c>qodercli</c>; Coexist) keeps its settings in
/// <c>$QODER_CONFIG_DIR/settings.json</c>, <c>~/.qoder</c> by default. A provider of one's own is an entry of <c>providers</c>, its
/// models picked as <c>&lt;provider&gt;/&lt;model&gt;</c>: Astra adds <c>providers.astra</c> (<c>protocol: openai</c> at the gateway's
/// <c>/v1</c>, a literal key, the bound provider's models with their window, output limit and capabilities) and, when a model is
/// chosen, sets <c>model.name</c> to <c>astra/&lt;id&gt;</c>. An openai provider is asked at <c>baseUrl + /chat/completions</c>.
/// Qoder offers custom providers only to a signed-in account whose plan has BYOK. What Qoder or the user adds to the provider
/// (routing, timeouts, a display name) stays when Astra rewrites it and is not drift. Qoder CN (npm <c>@qodercn-ai/qoderclicn</c>,
/// command <c>qoderclicn</c>) is the same CLI for the China site with its own accounts and settings in <c>$QODERCN_CONFIG_DIR</c>,
/// <c>~/.qoder-cn</c> by default; the same adapter serves it through a <see cref="QoderFlavour"/>. A reasoning model gets Qoder's
/// thinking capability with the usual low / medium / high levels (Astra does not know each model's own levels), unverified. Verified
/// against magpie's wiring (Qoder 1.1.70) only.
/// </summary>
public sealed class QoderClientAdapter(ClientEnvironment env, IClientConfigStateStore store, QoderFlavour flavour)
    : ConfigFileClientAdapter(env, store)
{
    public const string ProviderId = "astra";

    private static readonly string[] OwnedFields = ["protocol", "baseUrl", "apiKey", "model", "models"];

    private string Dir => AbsoluteEnv(flavour.EnvVariable) ?? Env.Combine(flavour.DirName);

    private string SettingsFile => Path.Combine(Dir, "settings.json");

    private static string ProviderPath => $"providers.{ProviderId}";

    public override string Kind => flavour.Kind;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var exe = Env.FindOnPath(flavour.Executable);
        if (exe is not null) return ClientDetection.Found(null, $"executable {exe}");
        // Qoder's JetBrains plugin keeps its indexer and plugins in the same folder and writes a settings.json of enabledPlugins
        // alone, with no CLI installed: only a settings file with another key, or the app's status file, says Qoder is here.
        if (HasCliSettings()) return ClientDetection.Found(null, $"config {SettingsFile}");
        foreach (var status in new[] { ".qoder-app-status.json", ".qoder-app-canary-status.json", ".qoder-app-dev-status.json" })
            if (Env.FileExists(Path.Combine(Dir, status))) return ClientDetection.Found(null, $"app status in {Dir}");
        return ClientDetection.NotFound($"no {SettingsFile} and no {flavour.Executable} executable on PATH");
    }

    private bool HasCliSettings() =>
        Env.ReadTextOrNull(SettingsFile) is { } text && Parse(text) is { } settings && settings.Any(p => p.Key != "enabledPlugins");

    public override IReadOnlyList<string> ConfigPaths() => [SettingsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = SettingsFile;
        var hints = ModelHints(ctx);
        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            hints.TryGetValue(id, out var info);
            var capabilities = new JsonObject { ["tools"] = true, ["vision"] = BoolHint(info?["vision"]) ?? false };
            if (BoolHint(info?["reasoning"]) == true)
            {
                capabilities["thinking"] = new JsonObject
                {
                    ["modes"] = new JsonArray("enabled"),
                    ["supportsEffort"] = true,
                    ["supportedEffortLevels"] = new JsonArray("low", "medium", "high"),
                    ["requiresBudgetForEnabled"] = false,
                };
            }
            var model = new JsonObject { ["model"] = id, ["displayName"] = name ?? id, ["capabilities"] = capabilities };
            if (PositiveLong(info?["contextWindow"]) is { } context) model["contextWindow"] = context;
            if (PositiveLong(info?["maxOutputTokens"]) is { } output) model["maxOutputTokens"] = output;
            models.AddNode(model);
        }
        var defaultModel = !string.IsNullOrEmpty(ctx.Model) ? ctx.Model : ModelEntries(ctx).FirstOrDefault().Id;
        var provider = new JsonObject
        {
            ["displayName"] = "Astra",
            ["protocol"] = "openai",
            ["baseUrl"] = V1(ctx),
            ["apiKey"] = ctx.LocalKey,
        };
        if (defaultModel is not null) provider["model"] = defaultModel;
        provider["models"] = models;

        var before = CurrentValue(file, ConfigFileFormat.Json, ProviderPath);
        // What the user or Qoder added to the provider Astra wrote stays; a provider Astra never wrote is replaced.
        if (Store.Get(Kind, file, ProviderPath) is not null && Parse(before) is { } current) CarryOver(current, provider);
        var changes = new List<ConfigChange>
        {
            new(file, ConfigFileFormat.Json, ProviderPath, before, provider.ToJsonString()) { StillOurs = OwnedPartsEqual },
        };
        if (!string.IsNullOrEmpty(ctx.Model)) changes.Add(SetKey(file, "model.name", JsonString($"{ProviderId}/{ctx.Model}")));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = TrackedRemovals();
        for (var i = 0; i < changes.Count; i++)
        {
            if (changes[i].KeyPath == ProviderPath)
                changes[i] = new ConfigChange(changes[i].File, changes[i].Format, ProviderPath, changes[i].Before, null) { StillOurs = OwnedPartsEqual };
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect() =>
        BuildStatus(Detect(), Applied(SettingsFile, ProviderPath), ["Qoder reads its settings as a session starts, and offers custom providers only on a plan with BYOK."]);

    protected override bool ValueStillOurs(string file, string keyPath, ConfigFileFormat format, string? current, string? appliedJson) =>
        keyPath == ProviderPath ? OwnedPartsEqual(current, appliedJson) : base.ValueStillOurs(file, keyPath, format, current, appliedJson);

    /// <summary>Members the user or Qoder added to the provider, and a display name they gave it, carry over to the rewritten entry.</summary>
    private static void CarryOver(JsonObject current, JsonObject provider)
    {
        if (current["displayName"] is JsonValue name && name.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
            provider["displayName"] = text;
        foreach (var (key, value) in current)
            if (!OwnedFields.Contains(key) && key != "displayName" && !provider.ContainsKey(key)) provider[key] = value?.DeepClone();
    }

    /// <summary>Whether the provider still holds what Astra wrote in the fields Astra owns; extras and the display name are not compared.</summary>
    private static bool OwnedPartsEqual(string? currentText, string? appliedJson)
    {
        if (currentText is null || appliedJson is null) return currentText is null && appliedJson is null;
        if (Parse(currentText) is not { } current || Parse(appliedJson) is not { } applied) return false;
        foreach (var field in OwnedFields)
            if (applied.ContainsKey(field) && !JsonNode.DeepEquals(applied[field], current[field])) return false;
        return true;
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

/// <summary>The Qoder build a <see cref="QoderClientAdapter"/> configures (Qoder and Qoder CN share the settings layout).</summary>
/// <param name="Kind">The client kind.</param>
/// <param name="DirName">The settings folder under home (<c>.qoder</c>, <c>.qoder-cn</c>).</param>
/// <param name="EnvVariable">The variable that moves the settings folder.</param>
/// <param name="Executable">The command on PATH.</param>
public sealed record QoderFlavour(string Kind, string DirName, string EnvVariable, string Executable)
{
    public static readonly QoderFlavour Qoder = new(ClientKinds.Qoder, ".qoder", "QODER_CONFIG_DIR", "qodercli");

    public static readonly QoderFlavour QoderCn = new(ClientKinds.QoderCn, ".qoder-cn", "QODERCN_CONFIG_DIR", "qoderclicn");
}
