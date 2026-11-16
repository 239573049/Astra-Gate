using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Obsidian (obsidian.md; Coexist) has no AI of its own: its community plugins bring it. Astra wires the one that takes an OpenAI-compatible
/// endpoint the user names, Copilot for Obsidian 4+ (logancyang/obsidian-copilot, plugin id <c>copilot</c>), in every vault that has it.
/// Obsidian lists its vaults in <c>obsidian.json</c> in its config folder (<c>~/Library/Application Support/obsidian</c>, <c>%APPDATA%\obsidian</c>,
/// <c>$XDG_CONFIG_HOME/obsidian</c>, or the Flatpak's and the Snap's own folders): <c>{"vaults": {"&lt;id&gt;": {"path": …}}}</c>. A plugin keeps its
/// settings in <c>&lt;vault&gt;/.obsidian/plugins/&lt;id&gt;/data.json</c>. In each vault's Copilot file Astra adds <c>providers.astra</c> (an
/// <c>openai-compatible</c> provider at the gateway's <c>/v1</c>), one <c>configuredModels</c> row per bound model (id a UUID v5 of
/// <c>astra/&lt;model id&gt;</c>, addressed by a selector on it), the ids in <c>backends.chat.enabledModels</c> (the user's own ids stay) and, as
/// <c>defaultModelKey</c>, the chosen model (the user's default comes back on a restore). A key lives in Obsidian's keychain, which Astra
/// cannot write, so the provider is keyless (<c>requiresApiKey: false</c>): its requests carry no Authorization and count as the default
/// token, so the gateway must not require a key from this machine. <c>enableCors</c> is on so Copilot sends through Obsidian's own request
/// function (no CORS preflight against the gateway, whose CORS policy is the desktop app's; replies are then not streamed). Copilot 3 keeps models
/// elsewhere, so a vault with Copilot 3 is left alone. Obsidian holds Copilot's settings while it runs: reload Copilot or restart Obsidian.
/// Timestamps (<c>addedAt</c>, <c>configuredAt</c>) are written as 0: the adapters have no clock. Verified against magpie's wiring (Copilot
/// 4.0.19) only; the CORS choice differs from magpie's and is unverified.
/// </summary>
public sealed class ObsidianClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : ConfigFileClientAdapter(env, store)
{
    public const string ProviderId = "astra";
    private const string PluginId = "copilot";
    private const string ChatEnabledPath = "backends.chat.enabledModels";
    private const string DefaultModelPath = "defaultModelKey";
    private static readonly string ProviderPath = $"providers.{ProviderId}";
    private static readonly string[] OwnedProviderFields = ["providerId", "providerType", "baseUrl", "requiresApiKey"];

    /// <summary>The UUID v5 namespace of Astra's configured model ids (the URL namespace, named for Astra's Obsidian wiring).</summary>
    private static readonly string ModelNamespace = Uuid5("6ba7b811-9dad-11d1-80b4-00c04fd430c8", "https://astra-gate.si/obsidian/copilot");

    private IEnumerable<string> ConfigDirs() => Env.Os switch
    {
        "osx" => [Env.Combine("Library", "Application Support", "obsidian")],
        "windows" => [Path.Combine(AppData, "obsidian")],
        _ =>
        [
            Path.Combine(XdgConfigHome, "obsidian"),
            Env.Combine(".var", "app", "md.obsidian.Obsidian", "config", "obsidian"),
            Env.Combine("snap", "obsidian", "current", ".config", "obsidian"),
        ],
    };

    /// <summary>The vault folders the obsidian.json files list that are still there, each once, in the order of their ids.</summary>
    private List<string> Vaults()
    {
        var vaults = new List<string>();
        foreach (var dir in ConfigDirs())
        {
            if (Env.ReadTextOrNull(Path.Combine(dir, "obsidian.json")) is not { Length: > 0 } text) continue;
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                continue;
            }
            if (root?["vaults"] is not JsonObject list) continue;
            foreach (var id in list.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal))
            {
                var path = (list[id]?["path"] as JsonValue)?.TryGetValue(out string? p) == true ? p : null;
                if (string.IsNullOrEmpty(path) || !Env.DirectoryExists(path)) continue;
                var clean = Path.GetFullPath(path);
                if (!vaults.Contains(clean)) vaults.Add(clean);
            }
        }
        return vaults;
    }

    private static string PluginDir(string vault) => Path.Combine(vault, ".obsidian", "plugins", PluginId);

    private static string DataFile(string vault) => Path.Combine(PluginDir(vault), "data.json");

    /// <summary>The vaults whose Copilot is installed (a manifest.json) and is version 4 or later, with the vaults that have an older Copilot.</summary>
    private (List<string> Ready, List<string> Old) CopilotVaults()
    {
        var ready = new List<string>();
        var old = new List<string>();
        foreach (var vault in Vaults())
        {
            var manifest = Path.Combine(PluginDir(vault), "manifest.json");
            if (!Env.FileExists(manifest)) continue;
            if (MajorVersion(Env.ReadTextOrNull(manifest)) is > 0 and < 4) old.Add(vault);
            else ready.Add(vault);
        }
        return (ready, old);
    }

    public override string Kind => ClientKinds.Obsidian;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        // Obsidian has no AI of its own: it is listed when a vault has Copilot, since the app alone gives Astra nothing to set.
        var (ready, old) = CopilotVaults();
        if (ready.Count + old.Count > 0) return ClientDetection.Found(null, $"Copilot for Obsidian in {(ready.Count > 0 ? ready : old)[0]}");
        return Store.List(Kind).Count > 0
            ? ClientDetection.Found(null, "vault already configured")
            : ClientDetection.NotFound("no Obsidian vault has Copilot for Obsidian");
    }

    public override IReadOnlyList<string> ConfigPaths() => [.. CopilotVaults().Ready.Select(DataFile)];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var changes = new List<ConfigChange>();
        var entries = ModelEntries(ctx);
        if (entries.Count == 0) return BuildPlan(changes);
        var hints = ModelHints(ctx);
        var provider = new JsonObject
        {
            ["providerId"] = ProviderId,
            ["providerType"] = "openai-compatible",
            ["displayName"] = "Astra",
            ["baseUrl"] = V1(ctx),
            ["enableCors"] = true,
            ["apiKeyKeychainId"] = null,
            ["requiresApiKey"] = false,
            ["origin"] = new JsonObject { ["kind"] = "byok" },
            ["addedAt"] = 0,
        };
        var wantedIds = entries.Select(e => ModelId(e.Id)).ToList();
        var defaultId = ModelId(!string.IsNullOrEmpty(ctx.Model) ? ctx.Model : entries[0].Id);

        foreach (var vault in CopilotVaults().Ready)
        {
            var file = DataFile(vault);
            var before = CurrentValue(file, ConfigFileFormat.Json, ProviderPath);
            // A provider of the user's own under this name, at another address, is theirs: the vault is left alone.
            if (before is not null && Store.Get(Kind, file, ProviderPath) is null && ParseObject(before) is { } theirs && !IsOurs(theirs)) continue;
            var next = (JsonObject)provider.DeepClone();
            if (Store.Get(Kind, file, ProviderPath) is not null && ParseObject(before) is { } current)
            {
                if (current["displayName"] is JsonValue name && name.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text)) next["displayName"] = text;
                if (current["apiKeyKeychainId"] is JsonValue keychain && keychain.TryGetValue(out string? id)) next["apiKeyKeychainId"] = id;
            }
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, ProviderPath, before, next.ToJsonString()) { StillOurs = ProviderStillOurs });

            foreach (var (id, name) in entries)
            {
                hints.TryGetValue(id, out var info);
                var row = new JsonObject
                {
                    ["configuredModelId"] = ModelId(id),
                    ["providerId"] = ProviderId,
                    ["info"] = Info(id, name, info),
                    ["configuredAt"] = 0,
                };
                changes.Add(new ConfigChange(file, ConfigFileFormat.Json, RowPath(ModelId(id)), CurrentValue(file, ConfigFileFormat.Json, RowPath(ModelId(id))), row.ToJsonString())
                {
                    StillOurs = RowStillOurs,
                });
            }
            // A model that left the list takes its row, and its place in the chat list, with it.
            var gone = new List<string>();
            foreach (var entry in Store.List(Kind))
            {
                if (entry.FilePath != file || !entry.KeyPath.StartsWith(RowPrefix, StringComparison.Ordinal)) continue;
                var rowId = entry.KeyPath[RowPrefix.Length..^1];
                if (wantedIds.Contains(rowId)) continue;
                gone.Add(rowId);
                if (RemoveKey(file, entry.KeyPath) is { } removal)
                    changes.Add(new ConfigChange(file, ConfigFileFormat.Json, entry.KeyPath, removal.Before, null) { StillOurs = RowStillOurs });
            }
            AddEnabledModels(changes, file, wantedIds, gone);

            var defaultBefore = CurrentValue(file, ConfigFileFormat.Json, DefaultModelPath);
            var recorded = Store.Get(Kind, file, DefaultModelPath);
            // The user's default is replaced the first time; after that the model they picked since stays.
            if (recorded is null || ConfigValueCodec.EqualsValue(ConfigFileFormat.Json, defaultBefore, recorded.AppliedValueJson))
                changes.Add(new ConfigChange(file, ConfigFileFormat.Json, DefaultModelPath, defaultBefore, JsonString(defaultId)));
        }
        return BuildPlan(changes);
    }

    private void AddEnabledModels(List<ConfigChange> changes, string file, List<string> wantedIds, List<string> goneIds)
    {
        var before = CurrentValue(file, ConfigFileFormat.Json, ChatEnabledPath);
        var ids = new List<string>();
        if (ParseArray(before) is { } existing)
            foreach (var node in existing)
                if (node is JsonValue v && v.TryGetValue(out string? s) && !goneIds.Contains(s)) ids.Add(s);
        foreach (var id in wantedIds)
            if (!ids.Contains(id)) ids.Add(id);
        changes.Add(new ConfigChange(file, ConfigFileFormat.Json, ChatEnabledPath, before, new JsonArray([.. ids.Select(i => (JsonNode)i)]).ToJsonString())
        {
            StillOurs = EnabledStillOurs,
        });
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = TrackedRemovals();
        for (var i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            Func<string?, string?, bool>? still = c.KeyPath == ProviderPath ? ProviderStillOurs
                : c.KeyPath.StartsWith(RowPrefix, StringComparison.Ordinal) ? RowStillOurs
                : c.KeyPath == ChatEnabledPath ? EnabledStillOurs
                : null;
            if (still is not null) changes[i] = new ConfigChange(c.File, c.Format, c.KeyPath, c.Before, null) { StillOurs = still };
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var (ready, old) = CopilotVaults();
        var enabled = ready.Any(v => Applied(DataFile(v), ProviderPath));
        var warnings = new List<string>();
        if (enabled)
            warnings.Add("Obsidian holds Copilot's settings while it runs: reload Copilot in Settings, Community plugins (or restart Obsidian) to see the Astra models. The provider is keyless, so the gateway must not require a key from this machine.");
        foreach (var vault in old)
            warnings.Add($"{vault} has Copilot 3: Astra sets Copilot for Obsidian 4 and later, so that vault is left alone.");
        return BuildStatus(Detect(), enabled, warnings);
    }

    protected override bool ValueStillOurs(string file, string keyPath, ConfigFileFormat format, string? current, string? appliedJson)
    {
        if (keyPath == ProviderPath) return ProviderStillOurs(current, appliedJson);
        if (keyPath.StartsWith(RowPrefix, StringComparison.Ordinal)) return RowStillOurs(current, appliedJson);
        if (keyPath == ChatEnabledPath) return EnabledStillOurs(current, appliedJson);
        return base.ValueStillOurs(file, keyPath, format, current, appliedJson);
    }

    // ---------------------------------------------------------------- rows and ids

    private const string RowPrefix = "configuredModels.[configuredModelId=";

    private static string RowPath(string id) => "configuredModels." + JsoncEditor.Selector(("configuredModelId", id));

    /// <summary>Astra's configured model id in Copilot for a model id: a UUID v5, the same on every apply.</summary>
    private static string ModelId(string modelId) => Uuid5(ModelNamespace, "astra/" + modelId);

    private static JsonObject Info(string id, string? name, JsonObject? hints)
    {
        var input = new JsonArray("text");
        if (BoolHint(hints?["vision"]) == true) input.Add((JsonNode)"image");
        var info = new JsonObject
        {
            ["id"] = id,
            ["displayName"] = name ?? id,
            ["modalities"] = new JsonObject { ["input"] = input, ["output"] = new JsonArray("text") },
        };
        var context = PositiveLong(hints?["contextWindow"]);
        var output = PositiveLong(hints?["maxOutputTokens"]);
        if (context is not null || output is not null)
        {
            var limits = new JsonObject();
            if (context is { } c) limits["context"] = c;
            if (output is { } o) limits["output"] = o;
            info["limits"] = limits;
        }
        if (BoolHint(hints?["reasoning"]) == true) info["reasoning"] = true;
        return info;
    }

    // ---------------------------------------------------------------- what is still Astra's

    private static bool IsOurs(JsonObject provider) =>
        provider["baseUrl"] is JsonValue v && v.TryGetValue(out string? url) && url.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal)
        && provider["providerType"] is JsonValue t && t.TryGetValue(out string? type) && type == "openai-compatible";

    private static bool ProviderStillOurs(string? currentText, string? appliedJson)
    {
        if (currentText is null || appliedJson is null) return currentText is null && appliedJson is null;
        if (ParseObject(currentText) is not { } current || ParseObject(appliedJson) is not { } applied) return false;
        foreach (var field in OwnedProviderFields)
            if (!JsonNode.DeepEquals(applied[field], current[field])) return false;
        return true;
    }

    private static bool RowStillOurs(string? currentText, string? appliedJson)
    {
        if (currentText is null || appliedJson is null) return currentText is null && appliedJson is null;
        if (ParseObject(currentText) is not { } current || ParseObject(appliedJson) is not { } applied) return false;
        return JsonNode.DeepEquals(applied["providerId"], current["providerId"]) && JsonNode.DeepEquals(applied["info"]?["id"], current["info"]?["id"]);
    }

    /// <summary>The chat list still holds the user's own ids as Astra saw them: ids of Astra's models may come and go.</summary>
    private static bool EnabledStillOurs(string? currentText, string? appliedJson)
    {
        if (currentText is null || appliedJson is null) return currentText is null && appliedJson is null;
        if (ParseArray(currentText) is not { } current || ParseArray(appliedJson) is not { } applied) return false;
        static List<string> Foreign(JsonArray list) =>
            [.. list.Select(n => (n as JsonValue)?.TryGetValue(out string? s) == true ? s : null).OfType<string>().Where(s => !IsAstraId(s))];
        return Foreign(current).SequenceEqual(Foreign(applied));
    }

    /// <summary>Astra's ids are UUID v5s of its namespace: recognised by their version digit alone (the user's are the plugin's own random ones).</summary>
    private static bool IsAstraId(string id) => id.Length == 36 && id[14] == '5';

    private static JsonObject? ParseObject(string? json)
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

    private static JsonArray? ParseArray(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The major version a manifest.json says, 0 when it says none.</summary>
    private static int MajorVersion(string? manifest)
    {
        if (manifest is null) return 0;
        try
        {
            var version = (JsonNode.Parse(manifest)?["version"] as JsonValue)?.TryGetValue(out string? v) == true ? v : null;
            var major = version?.Split('.')[0];
            return int.TryParse(major, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>RFC 4122 version 5 UUID of <paramref name="name"/> in the namespace <paramref name="ns"/>.</summary>
    private static string Uuid5(string ns, string name)
    {
        var nsBytes = Convert.FromHexString(ns.Replace("-", "", StringComparison.Ordinal));
        var data = Encoding.UTF8.GetBytes(name);
        var hash = SHA1.HashData([.. nsBytes, .. data]);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
