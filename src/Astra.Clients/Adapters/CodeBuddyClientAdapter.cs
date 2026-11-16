using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// CodeBuddy (Tencent's CodeBuddy Code CLI, npm <c>@tencent-ai/codebuddy-code</c>, and the CodeBuddy IDE; Coexist): one package for
/// the international (codebuddy.ai) and China (codebuddy.cn) editions. Both take models of one's own from <c>models.json</c> in
/// <c>$CODEBUDDY_CONFIG_DIR</c> (else <c>~/.codebuddy</c>), in WorkBuddy's format, hot-reloaded: Astra adds one entry per bound model
/// to its <c>models</c> array — <c>{"id","name":"Astra: &lt;id&gt;","vendor":"Astra","url":"&lt;gateway&gt;/v1/chat/completions","apiKey",…}</c>
/// (a model with such an entry needs no CodeBuddy sign-in) — addressed by a selector on <c>name</c>, and sets the model a session
/// starts on, <c>model</c> in <c>settings.json</c> beside it (read when a session starts). The IDE reads the same file from
/// <c>~/.codebuddy</c> whatever <c>CODEBUDDY_CONFIG_DIR</c> says and picks its model in its own UI. The user's own models and an
/// <c>availableModels</c> filter are left alone — when one is set, Astra's models stay hidden until they are added to it. Verified
/// against magpie's wiring (CodeBuddy Code, IDE 4.12.2) only.
/// </summary>
public sealed class CodeBuddyClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : ConfigFileClientAdapter(env, store)
{
    public const string NamePrefix = "Astra: ";

    private string Dir => AbsoluteEnv("CODEBUDDY_CONFIG_DIR") ?? Env.Combine(".codebuddy");

    private string ModelsFile => Path.Combine(Dir, "models.json");

    private string SettingsFile => Path.Combine(Dir, "settings.json");

    private static string ModelPathPrefix => "models.[name=" + NamePrefix;

    private static string ModelPath(string id) => "models." + JsoncEditor.Selector(("name", NamePrefix + id));

    public override string Kind => ClientKinds.CodeBuddy;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        if (Env.DirectoryExists(Dir)) return ClientDetection.Found(null, $"config directory {Dir}");
        var exe = Env.FindOnPath("codebuddy") ?? Env.FindOnPath("cbc");
        if (exe is not null) return ClientDetection.Found(null, $"executable {exe}");
        var app = FindApp("CodeBuddy.app", "CodeBuddy CN.app");
        return app is not null ? ClientDetection.Found(null, $"application {app}") : ClientDetection.NotFound($"no {Dir} and no codebuddy executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ModelsFile, SettingsFile];

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
            changes.Add(SetKey(ModelsFile, ModelPath(id), entry.ToJsonString()));
        }
        // A model that left the list takes its entry with it.
        foreach (var entry in Store.List(Kind))
        {
            if (entry.FilePath != ModelsFile || !entry.KeyPath.StartsWith(ModelPathPrefix, StringComparison.Ordinal) || !entry.KeyPath.EndsWith(']')) continue;
            if (wanted.Contains(entry.KeyPath[ModelPathPrefix.Length..^1])) continue;
            if (RemoveKey(ModelsFile, entry.KeyPath) is { } gone) changes.Add(gone);
        }
        if (!string.IsNullOrEmpty(ctx.Model) && wanted.Contains(ctx.Model))
            changes.Add(SetKey(SettingsFile, "model", JsonString(ctx.Model)));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable() => BuildPlan(TrackedRemovals());

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var enabled = Store.List(Kind).Any(e => e.FilePath == ModelsFile && e.KeyPath.StartsWith(ModelPathPrefix, StringComparison.Ordinal)
            && Applied(e.FilePath, e.KeyPath));
        return BuildStatus(Detect(), enabled, []);
    }

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
