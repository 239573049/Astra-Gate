using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// MothX (oschina/mothx, formerly vibecoding; npm <c>mothx-installer</c>, command <c>mothx</c>; Coexist): one <c>settings.json</c> in
/// its config folder (<c>MOTHX_DIR</c>, else <c>%APPDATA%\mothx</c> on Windows and <c>~/.mothx</c> elsewhere) holds its providers and the
/// model it starts on. Astra adds <c>providers.astra</c> (<c>api: openai-chat</c>, <c>baseUrl</c> the gateway's <c>/v1</c>, a literal
/// <c>apiKey</c>, <c>thinkingFormat: "openai"</c> so a thinking level goes out as <c>reasoning_effort</c> for every model instead of
/// MothX guessing a vendor's own fields from the model id, and the bound provider's models) and, when a model is chosen, sets
/// <c>defaultProvider</c> / <c>defaultModel</c>. MothX reads the file at start-up, so a running session must be restarted. A project's
/// <c>.mothx/settings.json</c> overlays the global file and is left alone. Verified against magpie's wiring (mothx 1.3.104) only.
/// </summary>
public sealed class MothXClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private string ConfigDir
    {
        get
        {
            if (Env.GetEnvironmentVariable("MOTHX_DIR") is { Length: > 0 } dir) return dir;
            if (Env.Os == "windows")
                return Env.GetEnvironmentVariable("APPDATA") is { Length: > 0 } app ? Path.Combine(app, "mothx") : Env.Combine("AppData", "Roaming", "mothx");
            return Env.Combine(".mothx");
        }
    }

    private string SettingsFile => Path.Combine(ConfigDir, "settings.json");

    private static string ProviderPath => $"providers.{ProviderId}";

    public override string Kind => ClientKinds.MothX;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = ConfigDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("mothx");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no mothx executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [SettingsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = SettingsFile;
        var provider = new JsonObject
        {
            ["baseUrl"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["api"] = "openai-chat",
            ["apiKey"] = ctx.LocalKey,
            ["thinkingFormat"] = "openai",
        };
        var hints = ModelHints(ctx);
        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            var info = hints.GetValueOrDefault(id);
            var model = new JsonObject { ["id"] = id };
            if (name is not null) model["name"] = name;
            // reasoning is what has MothX send a thinking level at all.
            model["reasoning"] = BoolHint(info?["reasoning"]) ?? false;
            model["input"] = BoolHint(info?["vision"]) == true ? new JsonArray("text", "image") : new JsonArray("text");
            if (PositiveLong(info?["contextWindow"]) is { } context) model["contextWindow"] = context;
            if (PositiveLong(info?["maxOutputTokens"]) is { } maxTokens) model["maxTokens"] = maxTokens;
            models.AddNode(model);
        }
        provider["models"] = models;

        var changes = new List<ConfigChange>
        {
            new(file, ConfigFileFormat.Json, ProviderPath, CurrentValue(file, ConfigFileFormat.Json, ProviderPath), provider.ToJsonString()),
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            changes.Add(Key("defaultProvider", ProviderId));
            changes.Add(Key("defaultModel", ctx.Model));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = SettingsFile;
        var changes = new List<ConfigChange>();
        if (CurrentValue(file, ConfigFileFormat.Json, ProviderPath) is { } provider)
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, ProviderPath, provider, null));
        // The start-up selection is only ours while it still names the Astra provider.
        var defaultProvider = CurrentValue(file, ConfigFileFormat.Json, "defaultProvider");
        if (defaultProvider is not null && StringOf(defaultProvider) == ProviderId)
        {
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, "defaultProvider", defaultProvider, null));
            var defaultModel = CurrentValue(file, ConfigFileFormat.Json, "defaultModel");
            if (defaultModel is not null && Store.Get(Kind, file, "defaultModel") is not null)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Json, "defaultModel", defaultModel, null));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var entry = Store.Get(Kind, SettingsFile, ProviderPath);
        var enabled = entry is not null && ConfigValueCodec.EqualsValue(
            ConfigFileFormat.Json, CurrentValue(SettingsFile, ConfigFileFormat.Json, ProviderPath), entry.AppliedValueJson);
        return BuildStatus(detection, enabled, enabled ? ["MothX reads its settings at start-up: restart open mothx sessions to use this."] : []);
    }

    private ConfigChange Key(string key, string value) =>
        new(SettingsFile, ConfigFileFormat.Json, key, CurrentValue(SettingsFile, ConfigFileFormat.Json, key), JsonString(value));
}
