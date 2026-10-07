using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Codex CLI (Switch): adds a <c>[model_providers.astra]</c> table and flips the top-level
/// <c>model_provider</c> (plus optional <c>model</c>). Disabling restores the file byte-identically:
/// a provider table Astra created is removed again, and fields refreshed inside a user's own
/// table are reset to their originals. <see cref="PlanPurge"/> additionally removes the table even
/// when it pre-existed. <c>auth.json</c> is never touched.
/// </summary>
public sealed class CodexClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private string ConfigDir =>
        Env.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir ? dir : Env.Combine(".codex");

    private string ConfigFile => Path.Combine(ConfigDir, "config.toml");

    public override string Kind => ClientKinds.Codex;

    public override ClientMode Mode => ClientMode.Switch;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = ConfigDir;
        var exe = Env.FindOnPath("codex");
        if (Env.DirectoryExists(dir))
            return ClientDetection.Found(null, $"config directory {dir}");
        if (exe is not null)
            return ClientDetection.Found(null, $"executable {exe}");
        return ClientDetection.NotFound($"no {dir} and no codex executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        // Stored "after" values are JSON texts of RAW TOML source (a TOML string includes its quotes).
        var changes = new List<ConfigChange> { TopKey(file, "model_provider", JsonString(TomlRaw(ProviderId))) };
        if (!string.IsNullOrEmpty(ctx.Model)) changes.Add(TopKey(file, "model", JsonString(TomlRaw(ctx.Model))));
        changes.AddRange(ProviderTableChanges(file, ctx));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        foreach (var key in new[] { "model_provider", "model" })
        {
            var before = CurrentValue(file, ConfigFileFormat.Toml, key);
            if (before is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, key, before, null));
        }
        // Fields refreshed inside a pre-existing provider table were tracked individually; restore them.
        changes.AddRange(TrackedFieldRestores(file, "base_url", "experimental_bearer_token"));
        // When the provider table itself did not exist before the takeover, remove it again so the
        // file restores byte-identically.
        var tablePath = $"model_providers.{ProviderId}";
        if (Store.Get(Kind, file, tablePath) is { OriginalAbsent: true })
            changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, tablePath,
                CurrentValue(file, ConfigFileFormat.Toml, tablePath, ConfigChangeKind.Table), null,
                ConfigChangeKind.Table));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge()
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>(PlanDisable().Changes);
        changes.Add(new ConfigChange(file, ConfigFileFormat.Toml, $"model_providers.{ProviderId}",
            CurrentValue(file, ConfigFileFormat.Toml, $"model_providers.{ProviderId}", ConfigChangeKind.Table), null,
            ConfigChangeKind.Table));
        return BuildPlan(changes);
    }

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var current = CurrentValue(ConfigFile, ConfigFileFormat.Toml, "model_provider");
        var entry = Store.Get(Kind, ConfigFile, "model_provider");
        var enabled = entry is not null
            && string.Equals(current, ConfigValueCodec.DecodeToSource(ConfigFileFormat.Toml, entry.AppliedValueJson), StringComparison.Ordinal);
        return BuildStatus(detection, enabled, []);
    }

    protected override bool IsTableKeyPath(string keyPath) => keyPath == $"model_providers.{ProviderId}";

    /// <summary>The provider table: created wholesale on first enable, otherwise only URL/key fields refreshed.</summary>
    private IEnumerable<ConfigChange> ProviderTableChanges(string file, EnableContext ctx)
    {
        var tablePath = $"model_providers.{ProviderId}";
        var baseUrl = TomlRaw(ctx.GatewayBaseUrl.TrimEnd('/') + "/v1");
        var token = TomlRaw(ctx.LocalKey);
        if (CurrentValue(file, ConfigFileFormat.Toml, tablePath, ConfigChangeKind.Table) is null)
        {
            var items = new JsonObject
            {
                ["name"] = TomlRaw("Astra"),
                ["base_url"] = baseUrl,
                ["wire_api"] = TomlRaw("responses"),
                ["experimental_bearer_token"] = token,
                ["requires_openai_auth"] = "false",
            };
            yield return new ConfigChange(file, ConfigFileFormat.Toml, tablePath,
                null, items.ToJsonString(), ConfigChangeKind.Table);
            yield break;
        }
        foreach (var (key, value) in new[] { ("base_url", baseUrl), ("experimental_bearer_token", token) })
        {
            var fieldPath = $"{tablePath}.{key}";
            var before = CurrentValue(file, ConfigFileFormat.Toml, fieldPath);
            if (!string.Equals(before, value, StringComparison.Ordinal))
                yield return new ConfigChange(file, ConfigFileFormat.Toml, fieldPath, before, JsonString(value));
        }
    }

    /// <summary>Restore changes for tracked fields of an existing provider table (absent when we created it).</summary>
    private IEnumerable<ConfigChange> TrackedFieldRestores(string file, params string[] fields)
    {
        var tablePath = $"model_providers.{ProviderId}";
        foreach (var field in fields)
        {
            var fieldPath = $"{tablePath}.{field}";
            if (Store.Get(Kind, file, fieldPath) is null) continue;
            var before = CurrentValue(file, ConfigFileFormat.Toml, fieldPath);
            if (before is not null) yield return new ConfigChange(file, ConfigFileFormat.Toml, fieldPath, before, null);
        }
    }

    private ConfigChange TopKey(string file, string key, string afterJson)
    {
        var before = CurrentValue(file, ConfigFileFormat.Toml, key);
        return new ConfigChange(file, ConfigFileFormat.Toml, key, before, afterJson);
    }

    private static string TomlRaw(string value) => Editing.TomlValueText.ForString(value);
}
