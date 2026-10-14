using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Qwen Code (npm <c>@qwen-code/qwen-code</c>, Node 22+; Coexist): adds one owned element per bound model to the
/// <c>modelProviders.openai</c> array of <c>~/.qwen/settings.json</c> — <c>{"id":…,"name":…,"envKey":"ASTRA_QWEN_API_KEY",
/// "baseUrl":"&lt;gateway&gt;/v1"}</c> — and the local key to <c>env.ASTRA_QWEN_API_KEY</c> (settings.json's <c>env</c> block
/// is read like the process environment). When a model is chosen it is activated with <c>security.auth.selectedType</c> =
/// "openai" and <c>model.name</c>. Verified against Qwen Code's model-providers and settings docs (2026-10): the array
/// is a bare array per auth type, <c>baseUrl</c> is the <c>/v1</c> root (the SDK appends the path), <c>envKey</c> names
/// the variable holding the key, and <c>security.auth.apiKey</c>/<c>baseUrl</c> are deprecated. Elements are addressed
/// with selectors (<see cref="JsoncEditor.Selector"/>) on <c>envKey</c> + <c>id</c>, so the user's own entries are never
/// touched. A custom provider id would need <c>providerProtocol</c> (read once at startup), so the built-in
/// <c>openai</c> type is used instead. No directory override exists for <c>~/.qwen</c>. The model list is hot-reloaded;
/// selecting the model may need a restart.
/// <para>
/// Each model goes to the array of its native protocol: <c>openai</c> (Chat Completions, or Responses with
/// <c>"wireApi":"responses"</c>), <c>anthropic</c> (base without <c>/v1</c>: the SDK appends <c>/v1/messages</c>) or
/// <c>gemini</c> (the gateway root: the SDK adds <c>/v1beta/models/…</c>). The selection names the matching auth type
/// (<c>openai</c>, <c>openai-responses</c>, <c>anthropic</c>, <c>gemini</c>) — what Qwen's own <c>/model</c> writes.
/// </para>
/// </summary>
public sealed class QwenCodeClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string KeyVariable = "ASTRA_QWEN_API_KEY";

    private string ConfigFile => Env.Combine(".qwen", "settings.json");

    private static string KeyPath => "env." + KeyVariable;

    /// <summary>The <c>modelProviders</c> arrays Astra writes models into.</summary>
    private static readonly string[] Buckets = ["openai", "anthropic", "gemini"];

    private static string ModelPathPrefix(string bucket) => $"modelProviders.{bucket}.[envKey={KeyVariable}&id=";

    private static string ModelPath(string bucket, string id) =>
        $"modelProviders.{bucket}." + JsoncEditor.Selector(("envKey", KeyVariable), ("id", id));

    private const string AuthTypePath = "security.auth.selectedType";

    private const string ModelNamePath = "model.name";

    public override string Kind => ClientKinds.QwenCode;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = Env.Combine(".qwen");
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("qwen");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no qwen executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>
        {
            new(file, ConfigFileFormat.Json, KeyPath, CurrentValue(file, ConfigFileFormat.Json, KeyPath), JsonString(ctx.LocalKey)),
        };
        var root = ctx.GatewayBaseUrl.TrimEnd('/');
        var protocols = ModelProtocols(ctx, ApiProtocol.OpenAIChat);
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, name) in ModelEntries(ctx))
        {
            if (!CanSelect(id)) continue; // ids with selector metacharacters cannot be addressed safely
            var protocol = protocols[id];
            var path = ModelPath(BucketOf(protocol), id);
            wanted.Add(path);
            var entry = new JsonObject
            {
                ["id"] = id,
                ["name"] = name ?? id,
                ["envKey"] = KeyVariable,
                ["baseUrl"] = protocol is ApiProtocol.Anthropic or ApiProtocol.Gemini ? root : root + "/v1",
            };
            if (protocol == ApiProtocol.OpenAIResponses) entry["wireApi"] = "responses";
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, CurrentValue(file, ConfigFileFormat.Json, path), entry.ToJsonString()));
        }
        // Models Astra registered earlier that the bound provider no longer offers (or that moved to another protocol's
        // array) are taken out again.
        foreach (var path in TrackedModelPaths(file))
        {
            if (wanted.Contains(path)) continue;
            var current = CurrentValue(file, ConfigFileFormat.Json, path);
            if (current is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, current, null));
        }
        if (!string.IsNullOrEmpty(ctx.Model) && protocols.TryGetValue(ctx.Model, out var selected) && wanted.Contains(ModelPath(BucketOf(selected), ctx.Model)))
        {
            changes.Add(Key(AuthTypePath, JsonString(selected switch
            {
                ApiProtocol.OpenAIResponses => "openai-responses",
                ApiProtocol.Anthropic => "anthropic",
                ApiProtocol.Gemini => "gemini",
                _ => "openai",
            })));
            changes.Add(Key(ModelNamePath, JsonString(ctx.Model)));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ConfigFile;
        var changes = new List<ConfigChange>();
        foreach (var path in TrackedModelPaths(file).Prepend(KeyPath))
        {
            var current = CurrentValue(file, ConfigFileFormat.Json, path);
            if (current is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, current, null));
        }
        // The selection is only ours while it still holds what Astra wrote; a model picked afterwards stays.
        foreach (var path in new[] { ModelNamePath, AuthTypePath })
        {
            if (StillOurs(file, ConfigFileFormat.Json, path))
                changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, CurrentValue(file, ConfigFileFormat.Json, path), null));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var warnings = new List<string>();
        if (CurrentValue(ConfigFile, ConfigFileFormat.Json, "providerProtocol") is not null)
            warnings.Add("settings.json defines providerProtocol; Astra uses the built-in openai provider type and leaves it untouched.");
        return BuildStatus(detection, StillOurs(ConfigFile, ConfigFileFormat.Json, KeyPath), warnings);
    }

    private ConfigChange Key(string path, string valueJson) =>
        new(ConfigFile, ConfigFileFormat.Json, path, CurrentValue(ConfigFile, ConfigFileFormat.Json, path), valueJson);

    private IEnumerable<string> TrackedModelPaths(string file) => Store.List(Kind)
        .Where(e => e.FilePath == file && e.KeyPath.EndsWith(']')
                    && Buckets.Any(b => e.KeyPath.StartsWith(ModelPathPrefix(b), StringComparison.Ordinal)))
        .Select(e => e.KeyPath)
        .ToList();

    private static string BucketOf(ApiProtocol protocol) => protocol switch
    {
        ApiProtocol.Anthropic => "anthropic",
        ApiProtocol.Gemini => "gemini",
        _ => "openai",
    };

    private static bool CanSelect(string id)
    {
        try
        {
            _ = JsoncEditor.Selector(("id", id));
            return true;
        }
        catch (EditorException)
        {
            return false;
        }
    }
}
