using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Claude Code (Switch): writes <c>env.ANTHROPIC_BASE_URL</c> and <c>env.ANTHROPIC_AUTH_TOKEN</c> in
/// <c>~/.claude/settings.json</c> (honoring CLAUDE_CONFIG_DIR), temporarily removes a present
/// <c>env.ANTHROPIC_API_KEY</c> (recorded), and optionally pins ANTHROPIC_MODEL /
/// ANTHROPIC_DEFAULT_HAIKU_MODEL. Warns on <c>apiKeyHelper</c>. Everything else is untouched.
/// </summary>
public sealed class ClaudeCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    private string ConfigDir =>
        Env.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Env.Combine(".claude");

    private string SettingsFile => Path.Combine(ConfigDir, "settings.json");

    private static readonly string[] ManagedEnvKeys =
    [
        "ANTHROPIC_BASE_URL",
        "ANTHROPIC_AUTH_TOKEN",
        "ANTHROPIC_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL",
    ];

    private const string ApiKeyPath = "env.ANTHROPIC_API_KEY";

    public override string Kind => ClientKinds.ClaudeCode;

    public override ClientMode Mode => ClientMode.Switch;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = ConfigDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("claude");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no claude executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [SettingsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = SettingsFile;
        var changes = new List<ConfigChange>
        {
            EnvKey(file, "ANTHROPIC_BASE_URL", ctx.GatewayBaseUrl),
            EnvKey(file, "ANTHROPIC_AUTH_TOKEN", ctx.LocalKey),
        };
        if (!string.IsNullOrEmpty(ctx.Model)) changes.Add(EnvKey(file, "ANTHROPIC_MODEL", ctx.Model));
        var smallFast = ctx.ExtraString("smallFastModel");
        if (!string.IsNullOrEmpty(smallFast)) changes.Add(EnvKey(file, "ANTHROPIC_DEFAULT_HAIKU_MODEL", smallFast));

        // An existing ANTHROPIC_API_KEY would fight the gateway auth token: remove it (recorded, restored on disable).
        var apiKeyBefore = CurrentValue(file, ConfigFileFormat.Json, ApiKeyPath);
        if (apiKeyBefore is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, ApiKeyPath, apiKeyBefore, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = SettingsFile;
        var changes = new List<ConfigChange>();
        foreach (var key in ManagedEnvKeys)
        {
            var path = $"env.{key}";
            var before = CurrentValue(file, ConfigFileFormat.Json, path);
            if (before is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, before, null));
        }
        // If we removed ANTHROPIC_API_KEY on enable, put it back.
        var entry = Store.Get(Kind, file, ApiKeyPath);
        if (entry is not null && !entry.OriginalAbsent && CurrentValue(file, ConfigFileFormat.Json, ApiKeyPath) is null)
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, ApiKeyPath, null, entry.OriginalValueJson));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var entry = Store.Get(Kind, SettingsFile, "env.ANTHROPIC_BASE_URL");
        var enabled = entry is not null && ConfigValueCodec.EqualsValue(
            ConfigFileFormat.Json,
            CurrentValue(SettingsFile, ConfigFileFormat.Json, "env.ANTHROPIC_BASE_URL"),
            entry.AppliedValueJson);

        var warnings = new List<string>();
        var helper = CurrentValue(SettingsFile, ConfigFileFormat.Json, "apiKeyHelper");
        if (helper is not null)
            warnings.Add("settings.json sets apiKeyHelper; it may override the gateway auth token (not modified by Astra).");
        return BuildStatus(detection, enabled, warnings);
    }

    private ConfigChange EnvKey(string file, string key, string value)
    {
        var path = $"env.{key}";
        var before = CurrentValue(file, ConfigFileFormat.Json, path);
        return new ConfigChange(file, ConfigFileFormat.Json, path, before, JsonString(value));
    }
}
