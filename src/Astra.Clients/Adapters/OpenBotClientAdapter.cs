using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;
using Astra.Clients.Editing;

namespace Astra.Clients.Adapters;

/// <summary>
/// OpenBot (nightly-labs/openbot, an Electron workspace; Coexist). It lists the user's own OpenAI-compatible endpoints from
/// <c>openbot-custom-providers-v1.json</c> in Electron's userData folder (<c>~/Library/Application Support/OpenBot</c>,
/// <c>%APPDATA%\OpenBot</c>, <c>$XDG_CONFIG_HOME/OpenBot</c> or <c>~/.config/OpenBot</c>): <c>{"version":1,"providers":[…]}</c>. Astra owns the
/// element <c>id = astra</c> (<c>baseUrl</c> the gateway's <c>/v1</c>, the bound provider's models, <c>secret: null</c>) and the
/// <c>version</c> key when the file is new. An entry's key is sealed with Electron's safeStorage (Keychain / DPAPI), which Astra cannot open,
/// so the entry carries none: OpenBot hands it to OpenCode keyless, and Astra's "require gateway key" setting must be off for local
/// requests to be accepted (they are then the default token's). A secret the user gives the entry inside OpenBot is kept. OpenBot's list
/// decoders fail closed, so at most 64 models with ids it accepts and a context window in 1,024 to 100,000,000 are written. It reads the
/// file once at start-up and writes it whole when an endpoint is saved there. Verified against magpie's wiring (OpenBot 0.58.0) only.
/// </summary>
public sealed partial class OpenBotClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    public const string FileName = "openbot-custom-providers-v1.json";

    /// <summary>How many models OpenBot takes for one endpoint.</summary>
    public const int MaxModels = 64;

    [GeneratedRegex(@"^[A-Za-z0-9._:/\[\],=-]+$")]
    private static partial Regex ModelIdPattern();

    private string Dir
    {
        get
        {
            if (Env.Os == "osx") return Env.Combine("Library", "Application Support", "OpenBot");
            if (Env.Os == "windows")
                return Env.GetEnvironmentVariable("APPDATA") is { Length: > 0 } app ? Path.Combine(app, "OpenBot") : Env.Combine("AppData", "Roaming", "OpenBot");
            return Env.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? Path.Combine(xdg, "OpenBot") : Env.Combine(".config", "OpenBot");
        }
    }

    private string ConfigFile => Path.Combine(Dir, FileName);

    private static string EntryPath => "providers." + JsoncEditor.Selector(("id", ProviderId));

    public override string Kind => ClientKinds.OpenBot;

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
        var hints = ModelHints(ctx);
        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            if (id.Length > 128 || ProviderId.Length + 1 + id.Length > 160 || !ModelIdPattern().IsMatch(id)) continue;
            var label = name is null || name.Length > 160 ? id : name;
            var model = new JsonObject { ["id"] = id, ["name"] = label };
            if (PositiveLong(hints.GetValueOrDefault(id)?["contextWindow"]) is { } window and >= 1024 and <= 100_000_000) model["contextWindow"] = window;
            models.AddNode(model);
            if (models.Count == MaxModels) break;
        }
        var entry = new JsonObject
        {
            ["id"] = ProviderId,
            ["name"] = "Astra",
            ["baseUrl"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["models"] = models,
            ["secret"] = null,
        };
        // What the user gave the entry inside OpenBot (its name, harness and sealed secret) stays.
        if (CurrentValue(file, ConfigFileFormat.Json, EntryPath) is { } existing && JsonNode.Parse(existing) is JsonObject old)
        {
            if (old["name"] is JsonValue n && n.TryGetValue(out string? name) && !string.IsNullOrEmpty(name)) entry["name"] = name;
            if (old["harness"] is JsonValue h && h.TryGetValue(out string? harness)) entry["harness"] = harness;
            if (old["secret"] is JsonValue s && s.TryGetValue(out string? secret)) entry["secret"] = secret;
        }
        var changes = new List<ConfigChange>();
        if (CurrentValue(file, ConfigFileFormat.Json, "version") is null)
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, "version", null, "1"));
        changes.Add(new ConfigChange(file, ConfigFileFormat.Json, EntryPath, CurrentValue(file, ConfigFileFormat.Json, EntryPath), entry.ToJsonString()));
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
        var enabled = StillOurs(ConfigFile, ConfigFileFormat.Json, EntryPath);
        var warnings = new List<string>();
        if (enabled)
        {
            warnings.Add("OpenBot seals an endpoint's key with the system keychain, which Astra cannot write: turn off \"require gateway key\" in Astra's settings so local requests are accepted (they use the default token), or give the endpoint the key inside OpenBot.");
            warnings.Add("OpenBot reads its custom endpoints at start-up: restart OpenBot to see Astra's models in its model picker, before adding or editing an endpoint there.");
        }
        return BuildStatus(detection, enabled, warnings);
    }
}
