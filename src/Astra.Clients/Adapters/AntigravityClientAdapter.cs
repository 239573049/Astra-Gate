using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Antigravity, the desktop agent app (Google, <c>Antigravity.app</c>; Switch). It is the CLI's engine (<c>language_server</c>) in an Electron
/// shell, so it takes the same two inputs: <c>settings.json</c> in its app data directory — <c>~/.gemini/antigravity/</c> here, the CLI's being
/// <c>~/.gemini/antigravity-cli/</c> — where <c>"modelProvider": "gemini"</c> sends it to a Gemini API endpoint instead of Google's backend and
/// <c>customModelsConfig.customModels</c> lists the models (each <c>{apiProvider, modelName}</c>), and the environment
/// (<c>GOOGLE_GEMINI_BASE_URL</c> and <c>GEMINI_API_KEY</c>), which the engine reads from nowhere else. The app starts the engine with the
/// environment of the user's interactive login shell (read at launch and again at every engine restart), so a Dock launch sees what the rc
/// file exports. Astra therefore (1) writes <c>modelProvider</c>, <c>model</c> and <c>customModelsConfig</c> into that <c>settings.json</c>
/// through the applier (the app has no <c>--settings</c> switch like the CLI; each key's original is recorded and given back), the user's own
/// custom models kept beside <c>astra/&lt;id&gt;</c> per bound model because the engine takes the key whole, and (2) takes over one managed
/// block of the shell rc file that exports the two variables. Side effects, both unavoidable with this wiring: the app signs in with the API
/// key, not the Google account, while it is on, and every shell started from that rc file sees the two variables (Gemini CLI included, which
/// the gateway serves too; the CLI client's <c>agy</c> function sets the same values per run). The rc file is a Unix profile, so Windows is
/// reported as not found. Disabling restores the keys and removes the block; the app must be restarted (the engine reads both at start).
/// Verified against the app's own sources and the CLI wiring only: whether the desktop honours the settings file was not run against a
/// signed-in app.
/// </summary>
public sealed class AntigravityClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    /// <summary>The block id, i.e. the key path inside the rc file.</summary>
    public const string BlockId = "antigravity";

    public const string ModelPrefix = "astra/";

    private const string BlockNote = "# Antigravity is routed through Astra. Turn the Antigravity client off in Astra to remove this block.";

    private const string ProviderApi = "API_PROVIDER_GOOGLE_GEMINI";

    private const string ModelsPath = "customModelsConfig";

    private string Dir => Env.Combine(".gemini", "antigravity");

    private string SettingsFile => Path.Combine(Dir, "settings.json");

    public override string Kind => ClientKinds.Antigravity;

    public override ClientMode Mode => ClientMode.Switch;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        if (Env.Os == "windows") return ClientDetection.NotFound("Astra routes Antigravity through the shell profile, which Windows does not have");
        var app = Env.ApplicationDirectories.Select(a => Path.Combine(a, "Antigravity.app")).FirstOrDefault(Env.DirectoryExists);
        if (app is not null) return ClientDetection.Found(null, $"application {app}");
        var dir = Dir;
        return Env.DirectoryExists(dir) ? ClientDetection.Found(null, $"config directory {dir}") : ClientDetection.NotFound($"no Antigravity app and no {dir}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [SettingsFile, ShellRc.File(Env)];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var rc = ShellRc.File(Env);
        var entries = ModelEntries(ctx);
        var model = string.IsNullOrEmpty(ctx.Model) ? entries.FirstOrDefault().Id : ctx.Model;
        var custom = new JsonObject();
        // The user's own custom models first (the engine takes customModels whole); a resync drops the ones Astra wrote before.
        if (CurrentValue(SettingsFile, ConfigFileFormat.Json, ModelsPath + ".customModels") is { } own
            && TryObject(own) is { } ownModels)
            foreach (var (key, value) in ownModels)
                if (!key.StartsWith(ModelPrefix, StringComparison.Ordinal)) custom[key] = value?.DeepClone();
        foreach (var (id, _) in entries)
            custom[ModelPrefix + id] = new JsonObject { ["apiProvider"] = ProviderApi, ["modelName"] = id };

        var changes = new List<ConfigChange>
        {
            Key("modelProvider", JsonString("gemini")),
        };
        if (!string.IsNullOrEmpty(model)) changes.Add(Key("model", JsonString(ModelPrefix + model)));
        changes.Add(Key(ModelsPath + ".customModels", custom.ToJsonString()));
        changes.Add(new ConfigChange(rc, ConfigFileFormat.ShellBlock, BlockId, CurrentValue(rc, ConfigFileFormat.ShellBlock, BlockId),
            JsonString(BlockBody(ctx))));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var rc = ShellRc.File(Env);
        var changes = new List<ConfigChange>();
        if (CurrentValue(rc, ConfigFileFormat.ShellBlock, BlockId) is { } block)
            changes.Add(new ConfigChange(rc, ConfigFileFormat.ShellBlock, BlockId, block, null));
        // Only the keys Astra recorded in the user's settings.json: everything else in it is theirs.
        foreach (var entry in Store.List(Kind).Where(e => e.FilePath == SettingsFile))
            if (CurrentValue(SettingsFile, ConfigFileFormat.Json, entry.KeyPath) is { } current)
                changes.Add(new ConfigChange(SettingsFile, ConfigFileFormat.Json, entry.KeyPath, current, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = StillOurs(ShellRc.File(Env), ConfigFileFormat.ShellBlock, BlockId);
        return BuildStatus(detection, enabled, enabled
            ? ["Quit and reopen Antigravity (from a new terminal or the Dock) so it reads the shell profile again."]
            : []);
    }

    private ConfigChange Key(string key, string json) =>
        new(SettingsFile, ConfigFileFormat.Json, key, CurrentValue(SettingsFile, ConfigFileFormat.Json, key), json);

    /// <summary>The block body: the two variables the engine reads. Exported, because a Dock-launched app gets them from a login shell.</summary>
    private static string BlockBody(EnableContext ctx) => string.Join('\n',
        BlockNote,
        $"export GOOGLE_GEMINI_BASE_URL={ShellRc.Quote(ctx.GatewayBaseUrl.TrimEnd('/'))}",
        $"export GEMINI_API_KEY={ShellRc.Quote(ctx.LocalKey)}");

    private static JsonObject? TryObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
