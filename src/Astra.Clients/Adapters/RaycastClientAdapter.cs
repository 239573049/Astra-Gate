using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Raycast AI (Raycast Pro, on Mac and Windows; Coexist) takes models of one's own as Custom Providers from
/// <c>~/.config/raycast/ai/providers.yaml</c> in the user's folder on both systems (manual.raycast.com/ai/custom-providers), whatever
/// <c>XDG_CONFIG_HOME</c> says. Astra adds one entry to the <c>providers</c> list, addressed by <c>id: astra</c> (see
/// <see cref="YamlEditor"/>'s row paths): <c>{id, name, base_url (the gateway's /v1, OpenAI compatible, without /chat/completions), api_keys:
/// {astra: key}, models: [{id, name, context, abilities}]}</c>. Every ability is said, as Raycast assumes tools and vision off and temperature on when
/// one is not: tools and system messages on, vision on a model that takes images, reasoning effort on a model that reasons. Raycast watches the
/// file and picks a change up without a restart. The model is picked in Raycast's own picker (Quick AI, AI Chat, AI Commands), so Astra only decides
/// whether its models are in it. The user's other entries, the top-level <c>tools</c> block and comments stay, and so do keys Astra does not write in its own
/// entry (<c>include_user_email</c>, <c>additional_parameters</c>, a model's <c>description</c>); they are not drift. Verified against magpie's wiring only.
/// </summary>
public sealed class RaycastClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : ConfigFileClientAdapter(env, store)
{
    public const string ProviderId = "astra";

    private static readonly string[] OwnedFields = ["id", "base_url", "api_keys", "models"];
    private static readonly string[] OwnedModelFields = ["id", "name", "context", "abilities"];

    private string Dir => Env.Combine(".config", "raycast");

    private string ProvidersFile => Path.Combine(Dir, "ai", "providers.yaml");

    private static string RowPath => "providers." + JsoncEditor.Selector(("id", ProviderId));

    public override string Kind => ClientKinds.Raycast;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        // The app on a Mac; its config folder elsewhere, which Raycast for Windows makes at Reveal Providers Config. Raycast has no command.
        var app = FindApp("Raycast.app");
        if (app is not null) return ClientDetection.Found(null, $"application {app}");
        return Env.DirectoryExists(Dir) ? ClientDetection.Found(null, $"config directory {Dir}") : ClientDetection.NotFound($"no {Dir}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ProvidersFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ProvidersFile;
        var hints = ModelHints(ctx);
        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            hints.TryGetValue(id, out var info);
            var model = new JsonObject { ["id"] = id, ["name"] = name ?? id };
            if (PositiveLong(info?["contextWindow"]) is { } context) model["context"] = context;
            model["abilities"] = new JsonObject
            {
                ["temperature"] = Supported(true),
                ["vision"] = Supported(BoolHint(info?["vision"]) ?? false),
                ["system_message"] = Supported(true),
                ["tools"] = Supported(true),
                ["reasoning_effort"] = Supported(BoolHint(info?["reasoning"]) ?? false),
            };
            models.AddNode(model);
        }
        var row = new JsonObject
        {
            ["id"] = ProviderId,
            ["name"] = "Astra",
            ["base_url"] = V1(ctx),
            ["api_keys"] = new JsonObject { [ProviderId] = ctx.LocalKey },
            ["models"] = models,
        };
        var before = CurrentValue(file, ConfigFileFormat.Yaml, RowPath);
        // Keys Astra does not write in its entry (and in a model) are the user's and stay.
        if (Parse(before) is { } current) CarryOver(current, row);
        return BuildPlan([new ConfigChange(file, ConfigFileFormat.Yaml, RowPath, before, row.ToJsonString()) { StillOurs = OwnedPartsEqual }]);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = TrackedRemovals();
        for (var i = 0; i < changes.Count; i++)
        {
            if (changes[i].KeyPath == RowPath)
                changes[i] = new ConfigChange(changes[i].File, changes[i].Format, RowPath, changes[i].Before, null) { StillOurs = OwnedPartsEqual };
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect() =>
        BuildStatus(Detect(), Applied(ProvidersFile, RowPath), ["Custom Providers need Raycast Pro; Raycast picks the file up without a restart."]);

    protected override bool ValueStillOurs(string file, string keyPath, ConfigFileFormat format, string? current, string? appliedJson) =>
        keyPath == RowPath ? OwnedPartsEqual(current, appliedJson) : base.ValueStillOurs(file, keyPath, format, current, appliedJson);

    private static JsonObject Supported(bool value) => new() { ["supported"] = value };

    private static void CarryOver(JsonObject current, JsonObject row)
    {
        foreach (var (key, value) in current)
            if (!OwnedFields.Contains(key) && !row.ContainsKey(key)) row[key] = value?.DeepClone();
        if (current["models"] is not JsonArray theirs || row["models"] is not JsonArray ours) return;
        foreach (var model in ours.OfType<JsonObject>())
        {
            var id = (model["id"] as JsonValue)?.TryGetValue(out string? s) == true ? s : null;
            var match = theirs.OfType<JsonObject>().FirstOrDefault(m => (m["id"] as JsonValue)?.TryGetValue(out string? t) == true && t == id);
            if (match is null) continue;
            foreach (var (key, value) in match)
                if (!OwnedModelFields.Contains(key) && !model.ContainsKey(key)) model[key] = value?.DeepClone();
        }
    }

    /// <summary>Whether the entry still holds what Astra wrote in the fields Astra owns; keys the user added are not compared.</summary>
    private static bool OwnedPartsEqual(string? currentText, string? appliedJson)
    {
        if (currentText is null || appliedJson is null) return currentText is null && appliedJson is null;
        if (Parse(currentText) is not { } current || Parse(appliedJson) is not { } applied) return false;
        foreach (var field in OwnedFields)
        {
            if (field == "models") continue;
            if (!JsonNode.DeepEquals(applied[field], current[field])) return false;
        }
        var appliedModels = applied["models"] as JsonArray ?? [];
        var currentModels = current["models"] as JsonArray ?? [];
        if (appliedModels.Count != currentModels.Count) return false;
        for (var i = 0; i < appliedModels.Count; i++)
        {
            if (appliedModels[i] is not JsonObject written || currentModels[i] is not JsonObject now) return false;
            foreach (var field in OwnedModelFields)
                if (written.ContainsKey(field) && !JsonNode.DeepEquals(written[field], now[field])) return false;
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
