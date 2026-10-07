using System.Text.Json.Nodes;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Claude Desktop (Switch): writes the third-party "3p" gateway profile layout (verified against
/// cc-switch, https://github.com/farion1231/cc-switch — not an official Anthropic-documented format):
/// a gateway profile under <c>Claude-3p/configLibrary/&lt;profileId&gt;.json</c>, our entry in
/// <c>configLibrary/_meta.json</c> (appliedId), and <c>deploymentMode = "3p"</c> in both
/// <c>claude_desktop_config.json</c> files. The role map (sonnet/opus/haiku) is applied by the
/// gateway itself, not written here. Claude Desktop must be restarted after enabling.
/// </summary>
public sealed class ClaudeDesktopClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    /// <summary>Fixed profile id (a UUID) so Astra's profile never collides with other tools' profiles.</summary>
    public const string ProfileId = "7f3c1a55-0a17-4000-8000-1cabad000001";

    public const string ProfileName = "Astra";

    private string NormalConfigFile => Path.Combine(NormalDir, "claude_desktop_config.json");

    private string ThreepConfigFile => Path.Combine(ThreepDir, "claude_desktop_config.json");

    private string ProfileFile => Path.Combine(ThreepDir, "configLibrary", $"{ProfileId}.json");

    private string MetaFile => Path.Combine(ThreepDir, "configLibrary", "_meta.json");

    private string NormalDir => Os switch
    {
        "osx" => Env.Combine("Library", "Application Support", "Claude"),
        "windows" => Path.Combine(LocalAppData(), "Claude"),
        _ => Path.Combine(ConfigRoot(), "Claude"),
    };

    private string ThreepDir => Os switch
    {
        "osx" => Env.Combine("Library", "Application Support", "Claude-3p"),
        "windows" => Path.Combine(LocalAppData(), "Claude-3p"),
        _ => Path.Combine(ConfigRoot(), "Claude-3p"),
    };

    private string Os => Env.Os;

    private string LocalAppData() =>
        Env.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } d ? d : Env.Combine("AppData", "Local");

    private string ConfigRoot()
    {
        var xdg = Env.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrEmpty(xdg) ? Env.Combine(".config") : xdg;
    }

    public override string Kind => ClientKinds.ClaudeDesktop;

    public override ClientMode Mode => ClientMode.Switch;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        if (Env.DirectoryExists(ThreepDir) || Env.DirectoryExists(NormalDir))
            return ClientDetection.Found(null, $"Claude Desktop directories under {ThreepDir}");
        return ClientDetection.NotFound($"no {ThreepDir} and no {NormalDir}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [NormalConfigFile, ThreepConfigFile, ProfileFile, MetaFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var changes = new List<ConfigChange>
        {
            DeploymentMode(NormalConfigFile, "3p"),
            DeploymentMode(ThreepConfigFile, "3p"),
        };

        // The gateway profile: role IDs are resolved by the gateway, so we only point at it.
        foreach (var (key, value) in ProfileFields(ctx))
        {
            var before = CurrentValue(ProfileFile, ConfigFileFormat.Json, key);
            changes.Add(new ConfigChange(ProfileFile, ConfigFileFormat.Json, key, before, value));
        }

        // Register the profile in _meta.json: our entry in "entries" + appliedId pointing at it.
        var entries = CurrentEntries(MetaFile) ?? [];
        var existingIndex = entries.FindIndex(e => e is JsonObject o && o["id"]?.GetValue<string>() == ProfileId);
        if (existingIndex >= 0)
        {
            if (entries[existingIndex] is JsonObject ours) ours["name"] = ProfileName;
        }
        else entries.Add(new JsonObject { ["id"] = ProfileId, ["name"] = ProfileName });
        changes.Add(new ConfigChange(MetaFile, ConfigFileFormat.Json, "entries",
            CurrentValue(MetaFile, ConfigFileFormat.Json, "entries"), new JsonArray([.. entries]).ToJsonString()));
        changes.Add(new ConfigChange(MetaFile, ConfigFileFormat.Json, "appliedId",
            CurrentValue(MetaFile, ConfigFileFormat.Json, "appliedId"), JsonString(ProfileId)));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        foreach (var file in new[] { NormalConfigFile, ThreepConfigFile })
        {
            var before = CurrentValue(file, ConfigFileFormat.Json, "deploymentMode");
            if (before is not null)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Json, "deploymentMode", before, null));
        }
        foreach (var key in ProfileFieldNames())
        {
            var before = CurrentValue(ProfileFile, ConfigFileFormat.Json, key);
            if (before is not null) changes.Add(new ConfigChange(ProfileFile, ConfigFileFormat.Json, key, before, null));
        }
        var entry = Store.Get(Kind, MetaFile, "entries");
        if (entry is not null && !entry.OriginalAbsent && entry.OriginalValueJson is not null)
            changes.Add(new ConfigChange(MetaFile, ConfigFileFormat.Json, "entries", null, entry.OriginalValueJson));
        else
        {
            var entries = CurrentEntries(MetaFile);
            if (entries is not null)
            {
                var kept = entries.Where(e => e is not JsonObject o || o["id"]?.GetValue<string>() != ProfileId).ToList();
                if (kept.Count != entries.Count)
                    changes.Add(new ConfigChange(MetaFile, ConfigFileFormat.Json, "entries",
                        CurrentValue(MetaFile, ConfigFileFormat.Json, "entries"), new JsonArray([.. kept]).ToJsonString()));
            }
        }
        var appliedBefore = CurrentValue(MetaFile, ConfigFileFormat.Json, "appliedId");
        if (appliedBefore is not null && appliedBefore == JsonString(ProfileId))
            changes.Add(new ConfigChange(MetaFile, ConfigFileFormat.Json, "appliedId", appliedBefore, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var entry = Store.Get(Kind, MetaFile, "appliedId");
        var enabled = entry is not null && ConfigValueCodec.EqualsValue(
            ConfigFileFormat.Json,
            CurrentValue(MetaFile, ConfigFileFormat.Json, "appliedId"),
            entry.AppliedValueJson);
        var warnings = new List<string>
        {
            "The Claude Desktop 3P profile format is reverse-engineered (cc-switch); re-verify after Claude Desktop updates.",
            "Restart Claude Desktop to apply.",
        };
        return BuildStatus(detection, enabled, warnings);
    }

    private IEnumerable<(string Key, string Value)> ProfileFields(EnableContext ctx)
    {
        var baseUrl = ctx.GatewayBaseUrl.TrimEnd('/');
        yield return ("coworkEgressAllowedHosts", new JsonArray("*").ToJsonString());
        yield return ("disableDeploymentModeChooser", "true");
        yield return ("inferenceProvider", JsonString("gateway"));
        yield return ("inferenceGatewayAuthScheme", JsonString("bearer"));
        yield return ("inferenceGatewayBaseUrl", JsonString(baseUrl));
        yield return ("inferenceGatewayApiKey", JsonString(ctx.LocalKey));
    }

    private static IEnumerable<string> ProfileFieldNames() =>
    [
        "coworkEgressAllowedHosts",
        "disableDeploymentModeChooser",
        "inferenceProvider",
        "inferenceGatewayAuthScheme",
        "inferenceGatewayBaseUrl",
        "inferenceGatewayApiKey",
    ];

    private ConfigChange DeploymentMode(string file, string mode) => new(
        file, ConfigFileFormat.Json, "deploymentMode",
        CurrentValue(file, ConfigFileFormat.Json, "deploymentMode"), JsonString(mode));

    private List<JsonNode>? CurrentEntries(string file)
    {
        var text = CurrentValue(file, ConfigFileFormat.Json, "entries");
        if (text is null) return null;
        try
        {
            if (JsonNode.Parse(text) is not JsonArray array) return null;
            var list = new List<JsonNode>();
            foreach (var item in array)
            {
                if (item is null) continue;
                // Clone: the parsed nodes belong to their array and cannot be re-parented.
                list.Add(JsonNode.Parse(item.ToJsonString()) ?? item.DeepClone());
            }
            return list;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
