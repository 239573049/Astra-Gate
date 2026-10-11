using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Claude Code (Switch): writes <c>env.ANTHROPIC_BASE_URL</c> and <c>env.ANTHROPIC_AUTH_TOKEN</c> in
/// <c>~/.claude/settings.json</c> (honoring CLAUDE_CONFIG_DIR), temporarily removes a present
/// <c>env.ANTHROPIC_API_KEY</c> (recorded), and optionally pins the model selection env vars
/// (<see cref="ClaudeCodeModels.Slots"/>, extras key "models"; the older "smallFastModel" extra still works).
/// The "options" extras (<see cref="ClientOptions"/>) add the <c>[1m]</c> model suffix, <c>MAX_THINKING_TOKENS</c> and
/// <c>DISABLE_PROMPT_CACHING</c>; the cache TTL option is applied by the gateway, not written here.
/// Warns on <c>apiKeyHelper</c>. Everything else is untouched.
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
        .. ClaudeCodeModels.Slots,
        .. ClientOptions.ConfigKeyPaths(ClientKinds.ClaudeCode).Select(p => p["env.".Length..]),
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
        // Model slots are written only when a model was picked; an empty slot is never touched. The older
        // single-key "smallFastModel" extra still sets the Haiku tier, unless the slot map already does.
        var slots = ClaudeCodeModels.Parse(ctx.Extras);
        var context1M = ClientOptions.GetBool(ctx.Extras, ClientOptions.Context1M);
        foreach (var (slot, model) in slots)
        {
            var wants1M = context1M && ctx.ModelsWithoutContext1M?.Contains(model) != true;
            changes.Add(EnvKey(file, slot, wants1M ? ClaudeCodeModels.With1M(slot, model) : model));
        }
        var smallFast = ctx.ExtraString("smallFastModel");
        if (!string.IsNullOrEmpty(smallFast) && slots.All(s => s.Key != "ANTHROPIC_DEFAULT_HAIKU_MODEL"))
            changes.Add(EnvKey(file, "ANTHROPIC_DEFAULT_HAIKU_MODEL", smallFast));

        if (ClientOptions.GetInt(ctx.Extras, ClientOptions.MaxThinkingTokens) is { } thinking)
            changes.Add(EnvKey(file, "MAX_THINKING_TOKENS", thinking.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (ClientOptions.GetBool(ctx.Extras, ClientOptions.DisablePromptCaching))
            changes.Add(EnvKey(file, "DISABLE_PROMPT_CACHING", "1"));

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
