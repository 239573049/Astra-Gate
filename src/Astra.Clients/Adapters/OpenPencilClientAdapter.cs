using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// OpenPencil (ZSeven-W/openpencil, the <c>openpencil-desktop</c> app; Coexist) is a design canvas whose chat runs models through
/// "builtin agents" of the user's own: an OpenAI-compatible endpoint, a key and a list of models, each in the chat's model picker. They
/// live in one <c>settings.json</c> in the platform's config folder joined with <c>openpencil</c> (<c>~/Library/Application Support</c>,
/// <c>$XDG_CONFIG_HOME</c>, <c>%APPDATA%</c>). Astra adds one entry to <c>builtin_agents</c>, addressed by <c>id: astra</c>:
/// <c>{"id","preset":"custom","display_name","kind":"openai-compat","api_key","model","models"?,"base_url","enabled"}</c> — <c>openai-compat</c>
/// posts to <c>base_url + /chat/completions</c>, so <c>base_url</c> is the gateway's <c>/v1</c>; <c>models</c> is the list (at most 64, written
/// only for two or more, the first again as <c>model</c>). <c>version</c> is required (a file without it fails to parse and OpenPencil refuses
/// to save), so Astra writes version 1 in a file it creates and leaves alone a file whose version is not 1. The model is picked in
/// OpenPencil's chat, so Astra only decides whether its models are in that picker. OpenPencil reads the file once at start-up and writes the
/// whole of it back when a setting changes and when it quits: quit it before changing this, or the change is written over. The name and the
/// on/off the user gave Astra's entry in OpenPencil's settings are kept. Verified against magpie's wiring (OpenPencil 0.8.4) only.
/// </summary>
public sealed class OpenPencilClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : ConfigFileClientAdapter(env, store)
{
    public const string AgentId = "astra";

    private const int MaxModels = 64;

    private static readonly string[] OwnedFields = ["id", "preset", "kind", "api_key", "model", "models", "base_url"];

    private string Dir => AppSupportDir("openpencil");

    private string SettingsFile => Path.Combine(Dir, "settings.json");

    private static string RowPath => "builtin_agents." + JsoncEditor.Selector(("id", AgentId));

    public override string Kind => ClientKinds.OpenPencil;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        if (Env.DirectoryExists(Dir)) return ClientDetection.Found(null, $"config directory {Dir}");
        // No command is looked up: `op` is OpenPencil's CLI name, and other tools'.
        if (Env.DirectoryExists(Env.Combine(".openpencil"))) return ClientDetection.Found(null, $"config directory {Env.Combine(".openpencil")}");
        var app = FindApp("OpenPencil.app");
        return app is not null ? ClientDetection.Found(null, $"application {app}") : ClientDetection.NotFound($"no {Dir}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [SettingsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = SettingsFile;
        var text = Env.ReadTextOrNull(file);
        var hasVersion = false;
        if (text is { } existing && !string.IsNullOrWhiteSpace(existing))
        {
            // A file OpenPencil would not read, or whose version it does not know, is left alone.
            if (Version(existing) is not { } version || version != "1") return BuildPlan([]);
            hasVersion = true;
        }

        var models = new List<string>();
        if (!string.IsNullOrEmpty(ctx.Model)) models.Add(ctx.Model);
        foreach (var (id, _) in ModelEntries(ctx))
        {
            if (id.Length == 0 || models.Contains(id) || id.Length > 256) continue;
            models.Add(id);
            if (models.Count == MaxModels) break;
        }
        var row = new JsonObject
        {
            ["id"] = AgentId,
            ["preset"] = "custom",
            ["display_name"] = "Astra",
            ["kind"] = "openai-compat",
            ["api_key"] = ctx.LocalKey,
            ["model"] = models.Count > 0 ? models[0] : "",
        };
        if (models.Count > 1) row["models"] = new JsonArray([.. models.Select(m => (JsonNode)m)]);
        row["base_url"] = V1(ctx);
        row["enabled"] = true;

        var before = CurrentValue(file, ConfigFileFormat.Json, RowPath);
        // The name and the on/off the user gave the entry in OpenPencil's settings stay.
        if (Store.Get(Kind, file, RowPath) is not null && Parse(before) is { } current) CarryOver(current, row);
        var changes = new List<ConfigChange>();
        if (!hasVersion) changes.Add(SetKey(file, "version", "1"));
        changes.Add(new ConfigChange(file, ConfigFileFormat.Json, RowPath, before, row.ToJsonString()) { StillOurs = OwnedPartsEqual });
        return BuildPlan(changes);
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

    public override ClientStatus Inspect()
    {
        var enabled = Applied(SettingsFile, RowPath);
        var warnings = new List<string>();
        if (enabled)
            warnings.Add("OpenPencil reads its settings when it starts and writes them all back when it quits: quit OpenPencil before changing this, then open it and pick an Astra model in its chat's model picker.");
        var text = Env.ReadTextOrNull(SettingsFile);
        if (text is { } existing && !string.IsNullOrWhiteSpace(existing) && Version(existing) != "1")
            warnings.Add($"{SettingsFile} does not say version 1, so Astra leaves it alone.");
        return BuildStatus(Detect(), enabled, warnings);
    }

    protected override bool ValueStillOurs(string file, string keyPath, ConfigFileFormat format, string? current, string? appliedJson) =>
        keyPath == RowPath ? OwnedPartsEqual(current, appliedJson) : base.ValueStillOurs(file, keyPath, format, current, appliedJson);

    private static void CarryOver(JsonObject current, JsonObject row)
    {
        if (current["display_name"] is JsonValue name && name.TryGetValue(out string? text)) row["display_name"] = text;
        if (current["enabled"] is JsonValue on && on.TryGetValue(out bool enabled)) row["enabled"] = enabled;
    }

    private static bool OwnedPartsEqual(string? currentText, string? appliedJson)
    {
        if (currentText is null || appliedJson is null) return currentText is null && appliedJson is null;
        if (Parse(currentText) is not { } current || Parse(appliedJson) is not { } applied) return false;
        foreach (var field in OwnedFields)
            if (applied.ContainsKey(field) != current.ContainsKey(field) || (applied.ContainsKey(field) && !JsonNode.DeepEquals(applied[field], current[field])))
                return false;
        return true;
    }

    /// <summary>The raw text of the document's <c>version</c> member when it is a JSON number, else null (not an object, or no number).</summary>
    private static string? Version(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject root
                && root["version"] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.ToJsonString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
