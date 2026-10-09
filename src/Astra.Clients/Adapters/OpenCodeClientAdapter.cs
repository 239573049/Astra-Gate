using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// OpenCode (Coexist): adds <c>provider.astra</c> (an @ai-sdk/openai-compatible provider) to
/// <c>opencode.json</c>/<c>opencode.jsonc</c> and optionally sets top-level <c>model</c> to
/// "astra/&lt;id&gt;". Astra's own providers keep working untouched. Refreshing the model list
/// rewrites only <c>provider.astra.models</c> (<see cref="PlanModelsRefresh"/>).
/// </summary>
public sealed class OpenCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store, OpenCodeFlavour flavour)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

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

    public override ClientDetection Detect()
    {
        var dir = ConfigDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath(flavour.Executable);
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no {flavour.Executable} executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => ConfigCandidates;

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange> { ProviderChange(file, ctx) };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            var path = "model";
            var before = CurrentValue(file, ConfigFileFormat.Json, path);
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, before, JsonString($"{ProviderId}/{ctx.Model}")));
        }
        return BuildPlan(changes);
    }

    /// <summary>Rewrites only <c>provider.astra.models</c> (used when the bound provider's model list changes).</summary>
    public ConfigChangePlan PlanModelsRefresh(EnableContext ctx)
    {
        var file = ConfigFile;
        var path = $"provider.{ProviderId}.models";
        var before = CurrentValue(file, ConfigFileFormat.Json, path);
        return BuildPlan([new ConfigChange(file, ConfigFileFormat.Json, path, before, ModelsJson(ctx))]);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        var providerBefore = CurrentValue(file, ConfigFileFormat.Json, $"provider.{ProviderId}");
        if (providerBefore is not null)
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, $"provider.{ProviderId}", providerBefore, null));
        var modelBefore = CurrentValue(file, ConfigFileFormat.Json, "model");
        if (modelBefore is not null && IsOurModelSelection(modelBefore))
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, "model", modelBefore, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var entry = Store.Get(Kind, ConfigFile, $"provider.{ProviderId}");
        var enabled = entry is not null && ConfigValueCodec.EqualsValue(
            ConfigFileFormat.Json,
            CurrentValue(ConfigFile, ConfigFileFormat.Json, $"provider.{ProviderId}"),
            entry.AppliedValueJson);
        var warnings = new List<string>();
        if (flavour.HomeVariable is { } variable && !string.IsNullOrEmpty(Env.GetEnvironmentVariable(variable)))
            warnings.Add($"{variable} is set; its effect on the config location is not documented, so Astra still uses {ConfigDir}.");
        return BuildStatus(detection, enabled, warnings);
    }

    private ConfigChange ProviderChange(string file, EnableContext ctx)
    {
        var path = $"provider.{ProviderId}";
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
        var models = new JsonObject();
        foreach (var model in list)
        {
            if (model.Value is not JsonObject m) continue;
            var id = m["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id)) continue;
            models[id] = new JsonObject();
            var name = m["name"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(name)) models[id]!["name"] = name;
        }
        return models.ToJsonString();
    }

    private bool IsOurModelSelection(string modelJsonText)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(modelJsonText);
            return node?.GetValue<string>()?.StartsWith($"{ProviderId}/", StringComparison.Ordinal) == true;
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
    bool WindowsLocalAppData = false, string? HomeVariable = null)
{
    public static readonly OpenCodeFlavour OpenCode = new(ClientKinds.OpenCode, "opencode", "opencode", "opencode");

    /// <summary>MiMo Code (Xiaomi, npm <c>@mimo-ai/cli</c>, bin <c>mimo</c>) is an OpenCode fork: <c>~/.config/mimocode/mimocode.json(c)</c>, same provider schema.</summary>
    public static readonly OpenCodeFlavour MiMoCode = new(ClientKinds.MiMoCode, "mimocode", "mimocode", "mimo",
        WindowsLocalAppData: true, HomeVariable: "MIMOCODE_HOME");
}
