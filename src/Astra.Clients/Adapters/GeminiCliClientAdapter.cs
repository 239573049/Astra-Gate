using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Gemini CLI (Switch): writes <c>GOOGLE_GEMINI_BASE_URL</c>, <c>GEMINI_API_KEY</c> and optionally
/// <c>GEMINI_MODEL</c> into <c>~/.gemini/.env</c>, and pins
/// <c>security.auth.selectedType = "gemini-api-key"</c> in <c>~/.gemini/settings.json</c>.
/// (Docs: GOOGLE_GEMINI_BASE_URL is honored with gemini-api-key auth; localhost is exempt from the HTTPS rule.)
/// </summary>
public sealed class GeminiCliClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    private string GemniDir => Env.Combine(".gemini");

    private string EnvFile => Path.Combine(GemniDir, ".env");

    private string SettingsFile => Path.Combine(GemniDir, "settings.json");

    private const string AuthTypePath = "security.auth.selectedType";

    private const string AuthTypeValue = "gemini-api-key";

    private static readonly string[] ManagedEnvKeys = ["GOOGLE_GEMINI_BASE_URL", "GEMINI_API_KEY", "GEMINI_MODEL"];

    public override string Kind => ClientKinds.GeminiCli;

    public override ClientMode Mode => ClientMode.Switch;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = GemniDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("gemini");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no gemini executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [EnvFile, SettingsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var changes = new List<ConfigChange>
        {
            EnvValue(EnvFile, "GOOGLE_GEMINI_BASE_URL", ctx.GatewayBaseUrl),
            EnvValue(EnvFile, "GEMINI_API_KEY", ctx.LocalKey),
        };
        if (!string.IsNullOrEmpty(ctx.Model)) changes.Add(EnvValue(EnvFile, "GEMINI_MODEL", ctx.Model));
        changes.Add(new ConfigChange(SettingsFile, ConfigFileFormat.Json, AuthTypePath,
            CurrentValue(SettingsFile, ConfigFileFormat.Json, AuthTypePath), JsonString(AuthTypeValue)));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        foreach (var key in ManagedEnvKeys)
        {
            var before = CurrentValue(EnvFile, ConfigFileFormat.Env, key);
            if (before is not null) changes.Add(new ConfigChange(EnvFile, ConfigFileFormat.Env, key, before, null));
        }
        var before2 = CurrentValue(SettingsFile, ConfigFileFormat.Json, AuthTypePath);
        if (before2 is not null) changes.Add(new ConfigChange(SettingsFile, ConfigFileFormat.Json, AuthTypePath, before2, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var entry = Store.Get(Kind, EnvFile, "GOOGLE_GEMINI_BASE_URL");
        var enabled = entry is not null && ConfigValueCodec.EqualsValue(
            ConfigFileFormat.Env,
            CurrentValue(EnvFile, ConfigFileFormat.Env, "GOOGLE_GEMINI_BASE_URL"),
            entry.AppliedValueJson);
        return BuildStatus(detection, enabled, []);
    }

    private ConfigChange EnvValue(string file, string key, string value)
    {
        var before = CurrentValue(file, ConfigFileFormat.Env, key);
        return new ConfigChange(file, ConfigFileFormat.Env, key, before, JsonString(value));
    }
}
