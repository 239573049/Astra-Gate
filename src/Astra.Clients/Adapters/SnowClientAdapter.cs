using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Snow CLI (<c>snow</c>, npm <c>snow-ai</c>, MayDay-wpf/snow-cli; Coexist) keeps its settings in <c>~/.snow</c> (<c>SNOW_CONFIG_DIR</c> when
/// absolute). Each API setup is a profile, <c>profiles/&lt;name&gt;.json</c> (<c>{"snowcfg": {baseUrl, baseUrlMode, apiKey, requestMethod,
/// advancedModel, basicModel, maxContextTokens, maxTokens, …}}</c>); <c>active-profile.json</c> (<c>{"activeProfile": "&lt;name&gt;"}</c>, "default"
/// when missing) names the one in use, and <c>config.json</c> is a copy of that one which Snow writes again from the profile at every start.
/// Astra adds <c>profiles/astra.json</c>: the <c>snowcfg</c> of the profile the user was on (their other settings kept, a subscription
/// sign-in <c>oauth</c> left out) with the gateway's <c>/v1</c> as its base URL, the local key, Chat Completions as its protocol, the
/// chosen model (else the bound provider's first) as both the main (<c>advancedModel</c>) and light (<c>basicModel</c>) model, and the
/// model's window, output limit and image support. It makes that profile the active one and copies <c>snowcfg</c> into <c>config.json</c>, as
/// Snow's own profile switch does; the active profile and the old <c>snowcfg</c> are recorded and put back on disable. Only the
/// <c>snowcfg</c> key of a profile is carried over (the other top-level keys of the user's profile are not copied). Snow's requests carry
/// no User-Agent of their own, so the key is what names the client. Verified against magpie's wiring only.
/// </summary>
public sealed class SnowClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private const string ProfileName = "astra";

    /// <summary>What Snow is told of a model whose limits are unknown: its own defaults.</summary>
    public const long DefaultContext = 200_000;

    public const long DefaultOutput = 64_000;

    private string Dir => Env.GetEnvironmentVariable("SNOW_CONFIG_DIR") is { } dir && dir.Trim() is { Length: > 0 } trimmed && Path.IsPathRooted(trimmed)
        ? trimmed
        : Env.Combine(".snow");

    private string ProfileFile(string name) => Path.Combine(Dir, "profiles", name + ".json");

    private string ActiveFile => Path.Combine(Dir, "active-profile.json");

    private string ConfigFile => Path.Combine(Dir, "config.json");

    public override string Kind => ClientKinds.Snow;

    protected override string MarkerFile => ProfileFile(ProfileName);

    protected override string MarkerKey => "snowcfg";

    protected override IReadOnlyList<string> EnabledWarnings =>
        ["Snow reads its profile at start-up: restart open snow sessions to use this. Snow App takes the profile in with \"Sync Snow CLI API config\"."];

    public override ClientDetection Detect()
    {
        var dir = Dir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("snow");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no snow executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ProfileFile(ProfileName), ActiveFile, ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var entries = ModelEntries(ctx);
        var model = !string.IsNullOrEmpty(ctx.Model) ? ctx.Model : entries.FirstOrDefault().Id;
        // The profile needs a model: with none there is nothing to write.
        if (string.IsNullOrEmpty(model)) return BuildPlan([]);

        var cfg = BaseConfig();
        cfg.Remove("oauth");
        var info = ModelHints(ctx).GetValueOrDefault(model);
        var context = PositiveLong(info?["contextWindow"]) ?? DefaultContext;
        var output = Math.Min(PositiveLong(info?["maxOutputTokens"]) ?? DefaultOutput, context);
        cfg["maxContextTokens"] = context;
        cfg["maxTokens"] = output;
        cfg["supportsVision"] = BoolHint(info?["vision"]) == true;
        cfg["baseUrl"] = Gateway(ctx);
        cfg["baseUrlMode"] = "base";
        cfg["apiKey"] = ctx.LocalKey;
        cfg["requestMethod"] = "chat";
        cfg["advancedModel"] = model;
        cfg["basicModel"] = model;

        return BuildPlan(
        [
            JsonSet(ProfileFile(ProfileName), "snowcfg", cfg),
            JsonSet(ActiveFile, "activeProfile", ProfileName),
            JsonSet(ConfigFile, "snowcfg", cfg),
        ]);
    }

    /// <summary>The <c>snowcfg</c> of the profile the user is on (config.json's when the profile file is missing); empty when there is none.</summary>
    private JsonObject BaseConfig()
    {
        var active = StringOf(CurrentValue(ActiveFile, ConfigFileFormat.Json, "activeProfile")) is { Length: > 0 } name ? name : "default";
        var text = CurrentValue(ProfileFile(active), ConfigFileFormat.Json, "snowcfg")
            ?? (Env.FileExists(ProfileFile(active)) ? null : CurrentValue(ConfigFile, ConfigFileFormat.Json, "snowcfg"));
        if (text is null) return new JsonObject();
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }
}
