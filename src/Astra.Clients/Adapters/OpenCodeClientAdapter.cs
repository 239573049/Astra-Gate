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
public sealed class OpenCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private string ConfigDir
    {
        get
        {
            var xdg = Env.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return string.IsNullOrEmpty(xdg) ? Env.Combine(".config", "opencode") : Path.Combine(xdg, "opencode");
        }
    }

    private string[] ConfigCandidates =>
    [
        Path.Combine(ConfigDir, "opencode.json"),
        Path.Combine(ConfigDir, "opencode.jsonc"),
    ];

    /// <summary>The existing config file (jsonc wins), or opencode.json when none exists yet.</summary>
    private string ConfigFile
    {
        get
        {
            var jsonc = Path.Combine(ConfigDir, "opencode.jsonc");
            return Env.FileExists(jsonc) ? jsonc : Path.Combine(ConfigDir, "opencode.json");
        }
    }

    public override string Kind => ClientKinds.OpenCode;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = ConfigDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("opencode");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no opencode executable on PATH");
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
        return BuildStatus(detection, enabled, []);
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
