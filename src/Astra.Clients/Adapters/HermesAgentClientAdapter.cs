using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Hermes Agent (Switch): points the <c>model:</c> section of <c>&lt;HERMES_HOME&gt;/config.yaml</c> at the
/// gateway — <c>provider: custom</c>, <c>base_url</c>, <c>api_key</c> and optionally the model id
/// (<c>model.default</c>, or its alias <c>model.model</c> when the file already uses that). HERMES_HOME
/// defaults to <c>~/.hermes</c> (<c>%LOCALAPPDATA%\hermes</c> on Windows). A non-chat <c>api_mode</c> is
/// switched to <c>chat_completions</c> while enabled. Per the Hermes docs config.yaml is the source of truth
/// for model and endpoint (OPENAI_BASE_URL in .env only applies to the openai-api provider). Every
/// original value is restored on disable; restart Hermes (or start a new session) after switching.
/// </summary>
public sealed class HermesAgentClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    private const string SectionPath = "model";
    private const string ProviderPath = "model.provider";
    private const string BaseUrlPath = "model.base_url";
    private const string ApiKeyPath = "model.api_key";
    private const string ApiModePath = "model.api_mode";
    private const string ChatApiMode = "chat_completions";

    /// <summary>Every key this adapter may write (restored on disable when tracked).</summary>
    private static readonly string[] ManagedPaths = [SectionPath, ProviderPath, BaseUrlPath, ApiKeyPath, "model.default", "model.model", ApiModePath];

    private string HomeDir
    {
        get
        {
            if (Env.GetEnvironmentVariable("HERMES_HOME") is { Length: > 0 } dir) return dir;
            if (string.Equals(Env.Os, "windows", StringComparison.OrdinalIgnoreCase)
                && Env.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local)
                return Path.Combine(local, "hermes");
            return Env.Combine(".hermes");
        }
    }

    private string ConfigFile => Path.Combine(HomeDir, "config.yaml");

    public override string Kind => ClientKinds.HermesAgent;

    public override ClientMode Mode => ClientMode.Switch;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = HomeDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("hermes");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no hermes executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        // Older configs write the model as a bare string ("model: provider/name"): replace that scalar with a
        // mapping as a whole (the original string comes back on disable). Once converted, later re-applies keep
        // owning the whole section so its recorded value stays exact.
        var section = CurrentValue(ConfigFile, ConfigFileFormat.Yaml, SectionPath);
        var bareModel = Store.Get(Kind, ConfigFile, SectionPath) is { OriginalValueJson: var original }
            ? StringOf(original)
            : StringOf(section);
        if (bareModel is not null)
        {
            var mapping = new System.Text.Json.Nodes.JsonObject
            {
                ["default"] = string.IsNullOrEmpty(ctx.Model) ? bareModel : ctx.Model,
                ["provider"] = "custom",
                ["base_url"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
                ["api_key"] = ctx.LocalKey,
            };
            return BuildPlan([new ConfigChange(ConfigFile, ConfigFileFormat.Yaml, SectionPath, section, mapping.ToJsonString())]);
        }

        var changes = new List<ConfigChange>
        {
            Key(ProviderPath, "custom"),
            Key(BaseUrlPath, ctx.GatewayBaseUrl.TrimEnd('/') + "/v1"),
            Key(ApiKeyPath, ctx.LocalKey),
        };
        if (!string.IsNullOrEmpty(ctx.Model)) changes.Add(Key(ModelPath, ctx.Model));
        // Anthropic / Responses wire modes would not match the OpenAI chat endpoint we hand out.
        var apiMode = CurrentValue(ConfigFile, ConfigFileFormat.Yaml, ApiModePath);
        if (apiMode is not null && StringOf(apiMode) != ChatApiMode) changes.Add(Key(ApiModePath, ChatApiMode));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        foreach (var path in ManagedPaths)
        {
            if (Store.Get(Kind, ConfigFile, path) is null) continue;
            var current = CurrentValue(ConfigFile, ConfigFileFormat.Yaml, path);
            if (current is not null) changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Yaml, path, current, null));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var section = Store.Get(Kind, ConfigFile, SectionPath);
        var entry = Store.Get(Kind, ConfigFile, BaseUrlPath);
        var enabled = section is not null
            ? ConfigValueCodec.EqualsValue(ConfigFileFormat.Yaml, CurrentValue(ConfigFile, ConfigFileFormat.Yaml, SectionPath), section.AppliedValueJson)
            : entry is not null && ConfigValueCodec.EqualsValue(
                ConfigFileFormat.Yaml,
                CurrentValue(ConfigFile, ConfigFileFormat.Yaml, BaseUrlPath),
                entry.AppliedValueJson);
        var warnings = new List<string>();
        foreach (var key in new[] { "key_env", "key_cmd" })
        {
            if (CurrentValue(ConfigFile, ConfigFileFormat.Yaml, $"model.{key}") is not null)
                warnings.Add($"config.yaml sets model.{key}; it may take precedence over the gateway key written to model.api_key (not modified by Astra).");
        }
        return BuildStatus(detection, enabled, warnings);
    }

    /// <summary>"model.default", or the "model.model" alias when the file already uses it instead.</summary>
    private string ModelPath =>
        CurrentValue(ConfigFile, ConfigFileFormat.Yaml, "model.default") is null
        && CurrentValue(ConfigFile, ConfigFileFormat.Yaml, "model.model") is not null
            ? "model.model"
            : "model.default";

    private ConfigChange Key(string path, string value) =>
        new(ConfigFile, ConfigFileFormat.Yaml, path, CurrentValue(ConfigFile, ConfigFileFormat.Yaml, path), JsonString(value));
}
