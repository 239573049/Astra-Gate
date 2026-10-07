using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Grok Build CLI (Switch): config is <c>~/.grok/config.toml</c> (verified against cc-switch's
/// production implementation — not yet covered by official xAI docs): <c>[models] default</c> selects
/// a profile and <c>[model.&lt;profile&gt;]</c> holds <c>model</c>, <c>base_url</c>, <c>name</c>,
/// <c>api_key</c>, <c>api_backend</c> ("responses") and <c>context_window</c>. Astra adds a
/// "astra" profile and flips <c>models.default</c>; disabling restores the file byte-identically
/// (a profile we created is removed again). Restart the CLI after switching.
/// </summary>
public sealed class GrokBuildClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProfileId = "astra";

    /// <summary>Model id written when the user did not pick one (Grok Build requires a model per profile).</summary>
    public const string DefaultModel = "grok-4";

    /// <summary>Conservative context window written for the Astra profile (gateway passes models through).</summary>
    public const long DefaultContextWindow = 256000;

    private string ConfigDir =>
        Env.GetEnvironmentVariable("GROK_HOME") is { Length: > 0 } dir ? dir : Env.Combine(".grok");

    private string ConfigFile => Path.Combine(ConfigDir, "config.toml");

    private string DefaultKeyPath => "models.default";

    private string ProfileTablePath => $"model.{ProfileId}";

    public override string Kind => ClientKinds.GrokBuild;

    public override ClientMode Mode => ClientMode.Switch;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = ConfigDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("grok");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no grok executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>
        {
            new(file, ConfigFileFormat.Toml, DefaultKeyPath,
                CurrentValue(file, ConfigFileFormat.Toml, DefaultKeyPath), JsonString(TomlRaw(ProfileId))),
        };
        var model = string.IsNullOrEmpty(ctx.Model) ? DefaultModel : ctx.Model;
        if (CurrentValue(file, ConfigFileFormat.Toml, ProfileTablePath, ConfigChangeKind.Table) is null)
        {
            var items = new JsonObject
            {
                ["model"] = TomlRaw(model),
                ["base_url"] = TomlRaw(ctx.GatewayBaseUrl.TrimEnd('/') + "/v1"),
                ["name"] = TomlRaw("Astra"),
                ["api_key"] = TomlRaw(ctx.LocalKey),
                ["api_backend"] = TomlRaw("responses"),
                ["context_window"] = DefaultContextWindow.ToString(),
            };
            changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, ProfileTablePath, null, items.ToJsonString(),
                ConfigChangeKind.Table));
        }
        else
        {
            foreach (var (key, value) in new[]
                     {
                         ("base_url", TomlRaw(ctx.GatewayBaseUrl.TrimEnd('/') + "/v1")),
                         ("api_key", TomlRaw(ctx.LocalKey)),
                         ("model", TomlRaw(model)),
                     })
            {
                var fieldPath = $"{ProfileTablePath}.{key}";
                var before = CurrentValue(file, ConfigFileFormat.Toml, fieldPath);
                if (!string.Equals(before, value, StringComparison.Ordinal))
                    changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, fieldPath, before, JsonString(value)));
            }
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ConfigFile;
        var before = CurrentValue(file, ConfigFileFormat.Toml, DefaultKeyPath);
        var changes = new List<ConfigChange>();
        if (before is not null)
            changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, DefaultKeyPath, before, null));
        // Fields refreshed inside a pre-existing profile table were tracked individually; restore them.
        foreach (var field in new[] { "base_url", "api_key", "model" })
        {
            var fieldPath = $"{ProfileTablePath}.{field}";
            if (Store.Get(Kind, file, fieldPath) is null) continue;
            var current = CurrentValue(file, ConfigFileFormat.Toml, fieldPath);
            if (current is not null)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, fieldPath, current, null));
        }
        // A profile table Astra created is removed again so the file restores byte-identically.
        if (Store.Get(Kind, file, ProfileTablePath) is { OriginalAbsent: true })
            changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, ProfileTablePath,
                CurrentValue(file, ConfigFileFormat.Toml, ProfileTablePath, ConfigChangeKind.Table), null,
                ConfigChangeKind.Table));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge()
    {
        var changes = new List<ConfigChange>(PlanDisable().Changes);
        changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Toml, ProfileTablePath,
            CurrentValue(ConfigFile, ConfigFileFormat.Toml, ProfileTablePath, ConfigChangeKind.Table), null,
            ConfigChangeKind.Table));
        return BuildPlan(changes);
    }

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var entry = Store.Get(Kind, ConfigFile, DefaultKeyPath);
        var enabled = entry is not null && ConfigValueCodec.EqualsValue(
            ConfigFileFormat.Toml,
            CurrentValue(ConfigFile, ConfigFileFormat.Toml, DefaultKeyPath),
            entry.AppliedValueJson);
        var warnings = new List<string>
        {
            "Grok Build's config format is verified via cc-switch; no official xAI documentation yet.",
        };
        return BuildStatus(detection, enabled, warnings);
    }

    protected override bool IsTableKeyPath(string keyPath) => keyPath == ProfileTablePath;

    private static string TomlRaw(string value) => Editing.TomlValueText.ForString(value);
}
