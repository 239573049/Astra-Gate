using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// WorkBuddy (Tencent's AI work agent; Coexist): adds one custom-model entry per bound model to the <c>models</c> array of
/// the user-level <c>~/.workbuddy/models.json</c> — <c>{"id":…,"name":"Astra: &lt;id&gt;","vendor":"Astra",
/// "url":"&lt;gateway&gt;/v1/chat/completions","apiKey":…,"supportsToolCall":true}</c> (the schema of CodeBuddy's
/// models.json, codebuddy.cn/docs/cli/models — but not its directory: WorkBuddy keeps <c>~/.workbuddy</c>, and its
/// daemon rejects a bare-array file; <c>url</c> must be the full Chat Completions path). WorkBuddy documents that models
/// configured there stay usable and editable in its model settings. The file
/// is hot-reloaded. Entries are addressed with a selector on <c>name</c> (<see cref="JsoncEditor.Selector"/>), which survives a
/// gateway port change and never matches the user's own models; <c>id</c> is the model id sent upstream. The
/// <c>availableModels</c> filter is left alone — when the user set one, Astra's models stay hidden until they are added to it.
/// </summary>
public sealed class WorkBuddyClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string NamePrefix = "Astra: ";

    private string ConfigFile => Env.Combine(".workbuddy", "models.json");

    /// <summary>
    /// Astra wrote WorkBuddy's models into CodeBuddy's <c>~/.codebuddy/models.json</c> before 0.3.1 (the products share
    /// the file format, not the directory). Entries recorded there are removed on the next apply or restore.
    /// </summary>
    private string LegacyConfigFile => Env.Combine(".codebuddy", "models.json");

    private static string ModelPathPrefix => "models.[name=" + NamePrefix;

    private static string ModelPath(string id) => "models." + JsoncEditor.Selector(("name", NamePrefix + id));

    public override string Kind => ClientKinds.WorkBuddy;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        foreach (var dir in new[] { Env.Combine(".workbuddy"), Env.Combine(".codebuddy") })
        {
            if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        }
        var exe = Env.FindOnPath("codebuddy");
        return exe is not null
            ? ClientDetection.Found(null, $"executable {exe}")
            : ClientDetection.NotFound($"no {Env.Combine(".workbuddy")} or {Env.Combine(".codebuddy")} and no codebuddy executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var url = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1/chat/completions";
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
                ["id"] = id,
                ["name"] = NamePrefix + id,
                ["vendor"] = "Astra",
                ["url"] = url,
                ["apiKey"] = ctx.LocalKey,
            };
            if (PositiveLong(info?["contextWindow"]) is { } context) entry["maxInputTokens"] = context;
            if (PositiveLong(info?["maxOutputTokens"]) is { } maxOutput) entry["maxOutputTokens"] = maxOutput;
            entry["supportsToolCall"] = true;
            if (BoolHint(info?["vision"]) is { } vision) entry["supportsImages"] = vision;
            if (BoolHint(info?["reasoning"]) is { } reasoning) entry["supportsReasoning"] = reasoning;
            var path = ModelPath(id);
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, path, CurrentValue(ConfigFile, ConfigFileFormat.Json, path), entry.ToJsonString()));
        }
        foreach (var (file, path) in TrackedModels())
        {
            // Entries are written to ConfigFile now; anything recorded in the legacy file is removed on apply.
            if (file == ConfigFile && wanted.Contains(path[ModelPathPrefix.Length..^1])) continue;
            var current = CurrentValue(file, ConfigFileFormat.Json, path);
            if (current is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, current, null));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        foreach (var (file, path) in TrackedModels())
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
        var enabled = TrackedModels().Any(t => StillOurs(t.File, ConfigFileFormat.Json, t.Path));
        return BuildStatus(detection, enabled, []);
    }

    /// <summary>The Astra entries recorded in this client's files (current and legacy), as (file, key path).</summary>
    private IEnumerable<(string File, string Path)> TrackedModels() => Store.List(Kind)
        .Where(e => e.FilePath == ConfigFile || e.FilePath == LegacyConfigFile)
        .Where(e => e.KeyPath.StartsWith(ModelPathPrefix, StringComparison.Ordinal) && e.KeyPath.EndsWith(']'))
        .Select(e => (e.FilePath, e.KeyPath))
        .ToList();

    private static bool CanSelect(string id)
    {
        try
        {
            _ = JsoncEditor.Selector(("name", NamePrefix + id));
            return true;
        }
        catch (EditorException)
        {
            return false;
        }
    }
}
