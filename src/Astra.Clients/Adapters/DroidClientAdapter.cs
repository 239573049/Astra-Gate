using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Droid (Factory, npm <c>droid</c>; Coexist): adds one bring-your-own-key entry per bound model to the
/// <c>customModels</c> array of <c>~/.factory/settings.json</c> — <c>{"model":…,"displayName":"Astra: &lt;id&gt;",
/// "baseUrl":"&lt;gateway&gt;/v1","apiKey":…,"provider":"generic-chat-completion-api"}</c> (camelCase schema from
/// docs.factory.ai/model-independence/byok; <c>generic-chat-completion-api</c> is the OpenAI Chat Completions type, the
/// plain <c>openai</c> type is the Responses API). Entries are addressed with a selector on <c>displayName</c>
/// (<see cref="JsoncEditor.Selector"/>), which survives a gateway port change and never matches the user's own models.
/// Astra does not select a default model: the id Droid derives for a custom model is not documented, so the user picks
/// it in <c>/model</c> (the top-level <c>model</c> key is left alone). Droid documents no directory override.
/// </summary>
public sealed class DroidClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string DisplayPrefix = "Astra: ";

    private string ConfigFile => Env.Combine(".factory", "settings.json");

    private static string ModelPathPrefix => "customModels.[displayName=" + DisplayPrefix;

    private static string ModelPath(string id) => "customModels." + JsoncEditor.Selector(("displayName", DisplayPrefix + id));

    public override string Kind => ClientKinds.Droid;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = Env.Combine(".factory");
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("droid");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no droid executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var baseUrl = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1";
        var hints = ModelHints(ctx);
        var changes = new List<ConfigChange>();
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, _) in ModelEntries(ctx))
        {
            if (!CanSelect(id)) continue; // ids with selector metacharacters cannot be addressed safely
            wanted.Add(id);
            hints.TryGetValue(id, out var info);
            var entry = new JsonObject
            {
                ["model"] = id,
                ["displayName"] = DisplayPrefix + id,
                ["baseUrl"] = baseUrl,
                ["apiKey"] = ctx.LocalKey,
                ["provider"] = "generic-chat-completion-api",
            };
            if (PositiveLong(info?["maxOutputTokens"]) is { } maxOutput) entry["maxOutputTokens"] = maxOutput;
            if (BoolHint(info?["vision"]) == false) entry["noImageSupport"] = true;
            var path = ModelPath(id);
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, CurrentValue(file, ConfigFileFormat.Json, path), entry.ToJsonString()));
        }
        foreach (var path in TrackedModelPaths(file))
        {
            if (wanted.Contains(path[ModelPathPrefix.Length..^1])) continue;
            var current = CurrentValue(file, ConfigFileFormat.Json, path);
            if (current is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, current, null));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        foreach (var path in TrackedModelPaths(file))
        {
            var current = CurrentValue(file, ConfigFileFormat.Json, path);
            if (current is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, current, null));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = TrackedModelPaths(ConfigFile).Any(path => StillOurs(ConfigFile, ConfigFileFormat.Json, path));
        return BuildStatus(detection, enabled, []);
    }

    private IEnumerable<string> TrackedModelPaths(string file) => Store.List(Kind)
        .Where(e => e.FilePath == file && e.KeyPath.StartsWith(ModelPathPrefix, StringComparison.Ordinal) && e.KeyPath.EndsWith(']'))
        .Select(e => e.KeyPath)
        .ToList();

    private static bool CanSelect(string id)
    {
        try
        {
            _ = JsoncEditor.Selector(("displayName", DisplayPrefix + id));
            return true;
        }
        catch (EditorException)
        {
            return false;
        }
    }
}
