using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// OpenCode (Coexist): adds <c>provider.astra</c> (an @ai-sdk/openai-compatible provider) to
/// <c>opencode.json</c>/<c>opencode.jsonc</c> and optionally sets top-level <c>model</c> to
/// "astra/&lt;id&gt;". Astra's own providers keep working untouched. Refreshing the model list
/// rewrites only <c>provider.astra.models</c> (<see cref="PlanModelsRefresh"/>).
/// <para>
/// A model whose native protocol is Responses or Anthropic names its own AI SDK package (<c>models.&lt;id&gt;.provider.npm</c>
/// = <c>@ai-sdk/openai</c> / <c>@ai-sdk/anthropic</c>, the way OpenCode Zen mixes protocols); both append their path to the
/// same <c>options.baseURL</c> (<c>…/v1</c>), which every OpenCode version reads. Gemini is not offered: its SDK needs a
/// <c>/v1beta</c> base, and a per-model <c>provider.api</c> loses to <c>options.baseURL</c>. Per-model limits, input
/// modalities and the reasoning flag are written when Astra knows them — without them OpenCode never compacts (context 0),
/// caps output at 32k, drops images and offers no reasoning variants.
/// </para>
/// </summary>
public sealed class OpenCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store, OpenCodeFlavour flavour)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    /// <summary>The provider id this flavour writes: <c>astra</c>, or its own for a front end that must not share OpenCode's block.</summary>
    private string ProviderKey => flavour.ProviderKey;

    /// <summary>OpenCode itself.</summary>
    public OpenCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
        : this(env, store, OpenCodeFlavour.OpenCode)
    {
    }

    private string ConfigDir
    {
        get
        {
            if (flavour.WindowsLocalAppData && Env.Os == "windows" && Env.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local)
                return Path.Combine(local, flavour.DirName);
            var xdg = Env.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return string.IsNullOrEmpty(xdg) ? Env.Combine(".config", flavour.DirName) : Path.Combine(xdg, flavour.DirName);
        }
    }

    private string[] ConfigCandidates =>
    [
        Path.Combine(ConfigDir, flavour.FileBase + ".json"),
        Path.Combine(ConfigDir, flavour.FileBase + ".jsonc"),
    ];

    /// <summary>The existing config file (jsonc wins), or opencode.json when none exists yet.</summary>
    private string ConfigFile
    {
        get
        {
            var jsonc = Path.Combine(ConfigDir, flavour.FileBase + ".jsonc");
            return Env.FileExists(jsonc) ? jsonc : Path.Combine(ConfigDir, flavour.FileBase + ".json");
        }
    }

    public override string Kind => flavour.Kind;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    /// <summary>
    /// OpenChamber's own data directory (<c>OPENCHAMBER_DATA_DIR</c> when absolute, else <c>~/.config/openchamber</c>, not moved by
    /// XDG_CONFIG_HOME). Only a <see cref="OpenCodeFlavour.Profile"/> flavour has one: it selects its model there, not in OpenCode's file.
    /// </summary>
    private string ProfileDir =>
        Env.GetEnvironmentVariable("OPENCHAMBER_DATA_DIR") is { Length: > 0 } dir && Path.IsPathRooted(dir) ? dir : Env.Combine(".config", "openchamber");

    private string ProfilePreferences => Path.Combine(ProfileDir, "preferences.json");

    private string ProfileSettings => Path.Combine(ProfileDir, "settings.json");

    public override ClientDetection Detect()
    {
        var dir = flavour.Profile ? ProfileDir : ConfigDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath(flavour.Executable);
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no {flavour.Executable} executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() =>
        flavour.Profile ? [.. ConfigCandidates, ProfilePreferences, ProfileSettings] : ConfigCandidates;

    /// <summary>
    /// The keys that hold OpenChamber's default model: <c>fields.defaultModel.value</c> in <c>preferences.json</c> (a version-1
    /// document; it wins) and the copy in <c>settings.json</c> that builds from before the split read, which is the only place
    /// when there is no preferences file. A <c>preferences.json</c> that is not a version-1 document is left alone.
    /// </summary>
    private IEnumerable<(string File, string Path)> ProfileModelKeys()
    {
        var hasPreferences = Env.FileExists(ProfilePreferences)
            && CurrentValue(ProfilePreferences, ConfigFileFormat.Json, "fields") is { } fields && fields.TrimStart().StartsWith('{');
        if (hasPreferences) yield return (ProfilePreferences, "fields.defaultModel.value");
        if (!hasPreferences || Env.FileExists(ProfileSettings)) yield return (ProfileSettings, "defaultModel");
    }

    private IEnumerable<ConfigChange> ProfileModelChanges(string reference) =>
        ProfileModelKeys().Select(k => new ConfigChange(k.File, ConfigFileFormat.Json, k.Path,
            CurrentValue(k.File, ConfigFileFormat.Json, k.Path), JsonString(reference)));

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange> { ProviderChange(file, ctx) };
        if (!string.IsNullOrEmpty(ctx.Model) && flavour.Profile)
            changes.AddRange(ProfileModelChanges($"{ProviderKey}/{ctx.Model}"));
        else if (!string.IsNullOrEmpty(ctx.Model))
        {
            var path = "model";
            var before = CurrentValue(file, ConfigFileFormat.Json, path);
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, before, JsonString($"{ProviderKey}/{ctx.Model}")));
        }
        return BuildPlan(changes);
    }

    /// <summary>Rewrites only <c>provider.astra.models</c> (used when the bound provider's model list changes).</summary>
    public ConfigChangePlan PlanModelsRefresh(EnableContext ctx)
    {
        var file = ConfigFile;
        var path = $"provider.{ProviderKey}.models";
        var before = CurrentValue(file, ConfigFileFormat.Json, path);
        return BuildPlan([new ConfigChange(file, ConfigFileFormat.Json, path, before, ModelsJson(ctx))]);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        var providerBefore = CurrentValue(file, ConfigFileFormat.Json, $"provider.{ProviderKey}");
        if (providerBefore is not null)
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, $"provider.{ProviderKey}", providerBefore, null));
        if (flavour.Profile)
        {
            // Each default-model key is ours only while it still names the Astra provider.
            foreach (var (profileFile, profilePath) in ProfileModelKeys())
                if (CurrentValue(profileFile, ConfigFileFormat.Json, profilePath) is { } current && IsOurModelSelection(current))
                    changes.Add(new ConfigChange(profileFile, ConfigFileFormat.Json, profilePath, current, null));
            return BuildPlan(changes);
        }
        var modelBefore = CurrentValue(file, ConfigFileFormat.Json, "model");
        if (modelBefore is not null && IsOurModelSelection(modelBefore))
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, "model", modelBefore, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var entry = Store.Get(Kind, ConfigFile, $"provider.{ProviderKey}");
        var enabled = entry is not null && ConfigValueCodec.EqualsValue(
            ConfigFileFormat.Json,
            CurrentValue(ConfigFile, ConfigFileFormat.Json, $"provider.{ProviderKey}"),
            entry.AppliedValueJson);
        var warnings = new List<string>();
        if (flavour.HomeVariable is { } variable && !string.IsNullOrEmpty(Env.GetEnvironmentVariable(variable)))
            warnings.Add($"{variable} is set; its effect on the config location is not documented, so Astra still uses {ConfigDir}.");
        return BuildStatus(detection, enabled, warnings);
    }

    private ConfigChange ProviderChange(string file, EnableContext ctx)
    {
        var path = $"provider.{ProviderKey}";
        var provider = new JsonObject
        {
            ["npm"] = "@ai-sdk/openai-compatible",
            ["name"] = "Astra",
            ["options"] = new JsonObject
            {
                ["baseURL"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
                ["apiKey"] = ctx.LocalKey,
            },
        };
        var models = ModelsJson(ctx);
        if (models is not null) provider["models"] = JsonNode.Parse(models);
        var before = CurrentValue(file, ConfigFileFormat.Json, path);
        return new ConfigChange(file, ConfigFileFormat.Json, path, before, provider.ToJsonString());
    }

    private string? ModelsJson(EnableContext ctx)
    {
        var list = ctx.ExtraObject("models");
        if (list is null || list.Count == 0) return null;
        var protocols = ModelProtocols(ctx, ApiProtocol.OpenAIChat);
        var models = new JsonObject();
        foreach (var model in list)
        {
            if (model.Value is not JsonObject m) continue;
            var id = m["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id)) continue;
            var entry = new JsonObject();
            var name = m["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name)) entry["name"] = name;
            var protocol = protocols.GetValueOrDefault(id, ApiProtocol.OpenAIChat);
            if (protocol is ApiProtocol.OpenAIResponses or ApiProtocol.Anthropic)
                entry["provider"] = new JsonObject { ["npm"] = protocol == ApiProtocol.Anthropic ? "@ai-sdk/anthropic" : "@ai-sdk/openai" };
            var output = PositiveLong(m["maxOutputTokens"]);
            // limit needs both numbers; an unknown output stays OpenCode's default (0 = its 32k cap).
            if (PositiveLong(m["contextWindow"]) is { } context) entry["limit"] = new JsonObject { ["context"] = context, ["output"] = output ?? 0 };
            var vision = BoolHint(m["vision"]);
            var pdf = BoolHint(m["pdf"]);
            if (vision is not null || pdf is not null)
            {
                // Listing modalities replaces the default (text) entirely, and MiMo Code requires both lists.
                var input = new JsonArray("text");
                if (vision == true) input.Add((JsonNode)"image");
                if (pdf == true) input.Add((JsonNode)"pdf");
                entry["modalities"] = new JsonObject { ["input"] = input, ["output"] = new JsonArray("text") };
            }
            // Anthropic thinking budgets are derived from limit.output; without it they come out negative.
            if (BoolHint(m["reasoning"]) is { } reasoning && (!reasoning || protocol != ApiProtocol.Anthropic || output is not null))
                entry["reasoning"] = reasoning;
            models[id] = entry;
        }
        return models.ToJsonString();
    }

    private bool IsOurModelSelection(string modelJsonText)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(modelJsonText);
            return node?.GetValue<string>()?.StartsWith($"{ProviderKey}/", StringComparison.Ordinal) == true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}

/// <summary>The OpenCode build an <see cref="OpenCodeClientAdapter"/> configures (OpenCode and its forks share the schema).</summary>
/// <param name="Kind">The client kind.</param>
/// <param name="DirName">The directory under the XDG config home (<c>opencode</c>, <c>mimocode</c>).</param>
/// <param name="FileBase">The config file name without extension (<c>opencode</c>, <c>mimocode</c>).</param>
/// <param name="Executable">The command on PATH.</param>
/// <param name="WindowsLocalAppData">The directory lives under <c>%LOCALAPPDATA%</c> on Windows (not the XDG layout).</param>
/// <param name="HomeVariable">An environment variable that relocates the client's paths in an undocumented way (warned about).</param>
public sealed record OpenCodeFlavour(string Kind, string DirName, string FileBase, string Executable,
    bool WindowsLocalAppData = false, string? HomeVariable = null, string ProviderKey = "astra", bool Profile = false)
{
    public static readonly OpenCodeFlavour OpenCode = new(ClientKinds.OpenCode, "opencode", "opencode", "opencode");

    /// <summary>MiMo Code (Xiaomi, npm <c>@mimo-ai/cli</c>, bin <c>mimo</c>) is an OpenCode fork: <c>~/.config/mimocode/mimocode.json(c)</c>, same provider schema.</summary>
    public static readonly OpenCodeFlavour MiMoCode = new(ClientKinds.MiMoCode, "mimocode", "mimocode", "mimo",
        WindowsLocalAppData: true, HomeVariable: "MIMOCODE_HOME");

    /// <summary>DevEco Code (Huawei's fork of OpenCode for HarmonyOS apps, npm <c>@deveco/deveco-code</c>): <c>~/.config/deveco/deveco.json(c)</c>, same provider schema.</summary>
    public static readonly OpenCodeFlavour DevEcoCode = new(ClientKinds.DevEcoCode, "deveco", "deveco", "deveco");

    /// <summary>
    /// OpenChamber (a desktop, web and VS Code front end for OpenCode): it runs an OpenCode on the user's OpenCode config, so Astra writes a
    /// provider of its own (<c>astra-openchamber</c>, never OpenCode's <c>astra</c> block, whose key belongs to the OpenCode client) there and
    /// selects the model in OpenChamber's own profile instead of OpenCode's <c>model</c>.
    /// </summary>
    public static readonly OpenCodeFlavour OpenChamber = new(ClientKinds.OpenChamber, "opencode", "opencode", "openchamber",
        ProviderKey: "astra-openchamber", Profile: true);
}
