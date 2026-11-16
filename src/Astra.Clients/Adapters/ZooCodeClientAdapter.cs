using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Zoo Code (the VS Code extension <c>ZooCodeOrganization.zoo-code</c>, a fork of Roo Code; Coexist) keeps its provider profiles in VS
/// Code's encrypted secret storage, out of reach. What it does read from a file is the one its <c>zoo-code.autoImportSettingsPath</c>
/// setting names: on every start of the extension it imports that file as its Settings → Import does. Astra writes
/// <c>astra-import.json</c> in Zoo Code's own folder of VS Code's (<c>User/globalStorage/zoocodeorganization.zoo-code/settings</c>) with one
/// OpenAI Compatible profile <c>astra</c> (the gateway's <c>/v1</c>, a literal key, the model chosen, which becomes the profile in use)
/// and points the setting at it in VS Code's <c>settings.json</c> and in each other profile's (listed in <c>globalStorage/storage.json</c>;
/// a value of the user's own is kept and put back by a restore). Imported profiles merge over the user's own. Zoo Code reads the file only
/// when it starts (run Developer: Reload Window after a change) and imports it again on every start, so while connected each start puts
/// Zoo Code back on the Astra profile. A profile once imported lives in Zoo Code's secret storage, which Astra cannot write: turning
/// the client off stops the import and removes the file, but the profile <c>astra</c> stays in Zoo Code for the user to switch from or
/// delete. Only VS Code (Stable) is wired; Zoo Code for JetBrains starts its extension with an empty configuration. Verified against
/// magpie's wiring (Zoo Code 3.90.0) only.
/// </summary>
public sealed class ZooCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : ConfigFileClientAdapter(env, store)
{
    public const string ProfileName = "astra";
    public const string ExtensionId = "zoocodeorganization.zoo-code";

    /// <summary>VS Code's setting that names the file Zoo Code imports at start-up (a key with a dot in it, escaped for key paths).</summary>
    private const string ImportSetting = @"zoo-code\.autoImportSettingsPath";

    private const long DefaultContextWindow = 128_000;

    private string UserDir => AppSupportDir("Code", "User");

    private string ExtensionsDir =>
        Env.GetEnvironmentVariable("VSCODE_EXTENSIONS") is { Length: > 0 } dir ? dir : Env.Combine(".vscode", "extensions");

    private string SettingsFile => Path.Combine(UserDir, "settings.json");

    private string ImportFile => Path.Combine(UserDir, "globalStorage", ExtensionId, "settings", "astra-import.json");

    public override string Kind => ClientKinds.ZooCode;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        if (Directory.Exists(ExtensionsDir)
            && Directory.EnumerateDirectories(ExtensionsDir).Any(d => Path.GetFileName(d).StartsWith(ExtensionId + "-", StringComparison.OrdinalIgnoreCase)))
            return ClientDetection.Found(null, $"extension in {ExtensionsDir}");
        return Applied(ImportFile, "providerProfiles")
            ? ClientDetection.Found(null, $"import file {ImportFile}")
            : ClientDetection.NotFound($"no Zoo Code extension in {ExtensionsDir}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [SettingsFile, ImportFile];

    /// <summary>The <c>settings.json</c> of VS Code's default profile and of every other profile that has settings of its own.</summary>
    private IEnumerable<string> SettingsFiles()
    {
        yield return SettingsFile;
        if (Env.ReadTextOrNull(Path.Combine(UserDir, "globalStorage", "storage.json")) is not { } text) yield break;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            yield break;
        }
        if (root?["userDataProfiles"] is not JsonArray profiles) yield break;
        foreach (var profile in profiles)
        {
            var folder = ProfileFolder(profile?["location"]);
            if (folder is null || !Env.DirectoryExists(folder)) continue;
            if (profile?["useDefaultFlags"]?["settings"] is JsonValue shared && shared.TryGetValue(out bool useDefault) && useDefault) continue;
            yield return Path.Combine(folder, "settings.json");
        }
    }

    /// <summary>A profile's folder: <c>User/profiles/&lt;location&gt;</c>, or the <c>fsPath</c> / <c>path</c> of a URI location.</summary>
    private string? ProfileFolder(JsonNode? location)
    {
        if (location is JsonValue v && v.TryGetValue(out string? name) && !string.IsNullOrEmpty(name))
            return Path.Combine(UserDir, "profiles", name);
        if (location is not JsonObject uri) return null;
        var fsPath = (uri["fsPath"] as JsonValue)?.TryGetValue(out string? fs) == true ? fs : null;
        if (!string.IsNullOrEmpty(fsPath)) return fsPath;
        var path = (uri["path"] as JsonValue)?.TryGetValue(out string? p) == true ? p : null;
        if (string.IsNullOrEmpty(path)) return null;
        if (path.Length > 2 && path[0] == '/' && path[2] == ':') path = path[1..];
        return path.Replace('/', Path.DirectorySeparatorChar);
    }

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var entries = ModelEntries(ctx);
        var model = !string.IsNullOrEmpty(ctx.Model) ? ctx.Model : entries.FirstOrDefault().Id;
        var profile = new JsonObject
        {
            ["id"] = ProfileName,
            ["apiProvider"] = "openai",
            ["openAiBaseUrl"] = V1(ctx),
            ["openAiApiKey"] = ctx.LocalKey,
        };
        if (model is not null) profile["openAiModelId"] = model;
        profile["openAiStreamingEnabled"] = true;
        if (model is not null)
        {
            ModelHints(ctx).TryGetValue(model, out var info);
            var modelInfo = new JsonObject
            {
                ["supportsPromptCache"] = false,
                ["supportsImages"] = BoolHint(info?["vision"]) ?? false,
                ["contextWindow"] = PositiveLong(info?["contextWindow"]) ?? DefaultContextWindow,
            };
            if (PositiveLong(info?["maxOutputTokens"]) is { } maxTokens) modelInfo["maxTokens"] = maxTokens;
            profile["openAiCustomModelInfo"] = modelInfo;
        }
        var profiles = new JsonObject
        {
            ["currentApiConfigName"] = ProfileName,
            ["apiConfigs"] = new JsonObject { [ProfileName] = profile },
        };
        var changes = new List<ConfigChange> { SetKey(ImportFile, "providerProfiles", profiles.ToJsonString()) };
        foreach (var settings in SettingsFiles().Distinct(StringComparer.Ordinal))
            changes.Add(SetKey(settings, ImportSetting, JsonString(ImportFile)));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable() => BuildPlan(TrackedRemovals());

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var enabled = Applied(ImportFile, "providerProfiles");
        return BuildStatus(Detect(), enabled,
            enabled ? ["Zoo Code reads Astra's profile when it starts: run Developer: Reload Window in VS Code to use this. Turning Astra off leaves the imported profile in Zoo Code."] : []);
    }
}
