using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Antigravity CLI (Google, command <c>agy</c>; Switch). agy keeps its settings in <c>~/.gemini/antigravity-cli/settings.json</c>. With
/// <c>"modelProvider": "gemini"</c> it asks a Gemini API endpoint rather than Google's own backend, and lists the models of
/// <c>customModelsConfig.customModels</c> (each <c>{apiProvider, modelName}</c>); the gateway speaks the Gemini API. The endpoint and key
/// agy takes only from its environment (<c>GOOGLE_GEMINI_BASE_URL</c> and <c>GEMINI_API_KEY</c>), and a custom model picked in settings
/// with no model name asks "model is empty": only <c>--model</c> on the command line resolves. Writing <c>modelProvider: gemini</c> into
/// <c>settings.json</c> would also put every agy on an API key, with no Google account (remote control gone), so <c>settings.json</c>
/// stays the user's. Astra instead (1) writes its own settings file beside it, <c>astra-settings.json</c> — the provider, the model and a
/// custom model <c>astra/&lt;id&gt;</c> per bound model, the user's own custom models copied in because agy takes the key whole — and
/// (2) takes over one managed block of the shell rc file that defines an <c>agy</c> function: it starts the real agy with the gateway
/// root, the client's key, <c>--settings</c> naming that file and <c>--model</c> (the user's wins), except for <c>remote-control</c>,
/// which runs plain on the Google sign-in. Thinking-level variants of a model are not written (Astra has no per-model level list).
/// Disabling removes the block and the file. Verified against magpie's wiring only.
/// </summary>
public sealed class AntigravityCliClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    /// <summary>The block id, i.e. the key path inside the rc file.</summary>
    public const string BlockId = "antigravity-cli";

    public const string ModelPrefix = "astra/";

    private const string BlockNote = "# Antigravity CLI is routed through Astra. Turn the Antigravity CLI client off in Astra to remove this block.";

    private const string ProviderApi = "API_PROVIDER_GOOGLE_GEMINI";

    private string Dir => Env.Combine(".gemini", "antigravity-cli");

    private string SettingsFile => Path.Combine(Dir, "settings.json");

    private string OverrideFile => Path.Combine(Dir, "astra-settings.json");

    public override string Kind => ClientKinds.AntigravityCli;

    public override ClientMode Mode => ClientMode.Switch;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = Dir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("agy");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no agy executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [OverrideFile, ShellRc.File(Env)];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var rc = ShellRc.File(Env);
        var entries = ModelEntries(ctx);
        var model = string.IsNullOrEmpty(ctx.Model) ? entries.FirstOrDefault().Id : ctx.Model;
        var custom = new JsonObject();
        // The user's own custom models first: agy takes customModels whole, so a --settings file must carry them too.
        if (CurrentValue(SettingsFile, ConfigFileFormat.Json, "customModelsConfig.customModels") is { } own
            && TryObject(own) is { } ownModels)
            foreach (var (key, value) in ownModels)
                if (!key.StartsWith(ModelPrefix, StringComparison.Ordinal)) custom[key] = value?.DeepClone();
        foreach (var (id, _) in entries)
            custom[ModelPrefix + id] = new JsonObject { ["apiProvider"] = ProviderApi, ["modelName"] = id };

        var changes = new List<ConfigChange>
        {
            Key(OverrideFile, "modelProvider", JsonString("gemini")),
        };
        if (!string.IsNullOrEmpty(model)) changes.Add(Key(OverrideFile, "model", JsonString(ModelPrefix + model)));
        changes.Add(Key(OverrideFile, "customModelsConfig", new JsonObject { ["customModels"] = custom }.ToJsonString()));
        changes.Add(new ConfigChange(rc, ConfigFileFormat.ShellBlock, BlockId, CurrentValue(rc, ConfigFileFormat.ShellBlock, BlockId),
            JsonString(BlockBody(ctx, string.IsNullOrEmpty(model) ? null : ModelPrefix + model))));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var rc = ShellRc.File(Env);
        var changes = new List<ConfigChange>();
        if (CurrentValue(rc, ConfigFileFormat.ShellBlock, BlockId) is { } block)
            changes.Add(new ConfigChange(rc, ConfigFileFormat.ShellBlock, BlockId, block, null));
        // The file is Astra's alone: every key it recorded goes (the file with the last one).
        foreach (var entry in Store.List(Kind).Where(e => e.FilePath == OverrideFile))
            if (CurrentValue(OverrideFile, ConfigFileFormat.Json, entry.KeyPath) is { } current)
                changes.Add(new ConfigChange(OverrideFile, ConfigFileFormat.Json, entry.KeyPath, current, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = StillOurs(ShellRc.File(Env), ConfigFileFormat.ShellBlock, BlockId);
        return BuildStatus(detection, enabled, enabled ? ["Open a new terminal (or source your shell rc file) so the agy function is defined."] : []);
    }

    private ConfigChange Key(string file, string key, string json) =>
        new(file, ConfigFileFormat.Json, key, CurrentValue(file, ConfigFileFormat.Json, key), json);

    /// <summary>
    /// The block body: an <c>agy</c> function. Key and gateway are passed per invocation, never exported; <c>remote-control</c> (which only a
    /// Google sign-in has) and a <c>--model</c> of the user's own are left as given.
    /// </summary>
    private static string BlockBody(EnableContext ctx, string? model)
    {
        var settings = "\"$HOME/.gemini/antigravity-cli/astra-settings.json\"";
        return string.Join('\n',
            BlockNote,
            "unalias agy 2>/dev/null || true",
            "agy() {",
            "  local m a",
            $"  m={(model is null ? "''" : ShellRc.Quote(model))}",
            "  for a in \"$@\"; do case \"$a\" in remote-control|--remote-control|--remote-control=*|-remote-control) command agy \"$@\"; return;; esac; done",
            "  for a in \"$@\"; do case \"$a\" in --model|--model=*|-model|-model=*) m=;; esac; done",
            "  if [ -n \"$m\" ]; then set -- --model \"$m\" \"$@\"; fi",
            $"  set -- --settings {settings} \"$@\"",
            $"  GEMINI_API_KEY={ShellRc.Quote(ctx.LocalKey)} GOOGLE_GEMINI_BASE_URL={ShellRc.Quote(ctx.GatewayBaseUrl.TrimEnd('/'))} command agy \"$@\"",
            "}");
    }

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
