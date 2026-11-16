using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Tuanjie Cowork (codely.tuanjie.cn, Unity China's coworker app, a Tauri app whose core runs Codely sessions; Coexist): its models of
/// one's own are its Settings' Custom Models, kept in <c>~/.codely/settings.json</c>, read afresh on every lookup:
/// <c>customModelProviders</c> (<c>{id, name, baseUrl, apiKey, authType}</c>) and <c>customModels</c> (<c>{id, model, providerId,
/// displayName, roles, wireApi, supportsMultimodal}</c>); a model's id is unique across providers. A session on one is started with its
/// provider's base URL and key as <c>OPENAI_BASE_URL</c> / <c>OPENAI_API_KEY</c>. Astra adds the provider <c>astra</c> (the gateway's
/// <c>/v1</c> with the local key, <c>authType: "openai"</c>) and one model entry per bound model, id <c>astra-&lt;id&gt;</c> (letters, digits,
/// <c>_</c>, <c>.</c> and <c>-</c> kept, the rest turned into <c>-</c>) on <c>wireApi: "chat"</c>; a model the provider no longer offers is
/// taken out again. Which model a chat is on is Cowork's own pick in its model menu, which Astra does not write: pick "&lt;model&gt; (Astra)"
/// there. Cowork lists a provider's models at <c>baseUrl + /models</c> as well. Verified against magpie's wiring (Cowork 2.1.6) only.
/// </summary>
public sealed class TuanjieCoworkClientAdapter(ClientEnvironment env, IClientConfigStateStore store) : FileClientAdapter(env, store)
{
    private const string IdPrefix = "astra-";

    private static readonly string ProviderPath = "customModelProviders." + JsoncEditor.Selector(("id", ProviderId));

    private const string ModelPathPrefix = "customModels.[id=" + IdPrefix;

    private string Dir => Env.Combine(".codely");

    private string SettingsFile => Path.Combine(Dir, "settings.json");

    public override string Kind => ClientKinds.TuanjieCowork;

    protected override string MarkerFile => SettingsFile;

    protected override string MarkerKey => ProviderPath;

    protected override IReadOnlyList<string> EnabledWarnings =>
        ["Pick Astra's model in Tuanjie Cowork's model menu; if it is not there yet, restart Tuanjie Cowork."];

    public override ClientDetection Detect()
    {
        var dir = Dir;
        return Env.DirectoryExists(dir) ? ClientDetection.Found(null, $"config directory {dir}") : ClientDetection.NotFound($"no {dir}");
    }

    public override IReadOnlyList<string> ConfigPaths() => [SettingsFile];

    private static string ModelPath(string id) => "customModels." + JsoncEditor.Selector(("id", id));

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = SettingsFile;
        var provider = new JsonObject
        {
            ["id"] = ProviderId,
            ["name"] = "Astra",
            ["baseUrl"] = Gateway(ctx),
            ["apiKey"] = ctx.LocalKey,
            ["authType"] = "openai",
        };
        var changes = new List<ConfigChange> { JsonSet(file, ProviderPath, provider) };
        var hints = ModelHints(ctx);
        var ids = Aliases(IdPrefix, ModelEntries(ctx).Select(e => e.Id), keep: ".");
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (model, id) in ids)
        {
            if (!CanSelect("id", id)) continue;
            wanted.Add(id);
            var info = hints.GetValueOrDefault(model);
            var entry = new JsonObject
            {
                ["id"] = id,
                ["providerId"] = ProviderId,
                ["model"] = model,
                ["displayName"] = model,
                ["roles"] = new JsonArray("model"),
                ["wireApi"] = "chat",
                ["supportsMultimodal"] = BoolHint(info?["vision"]) == true,
            };
            changes.Add(JsonSet(file, ModelPath(id), entry));
        }
        foreach (var entry in Store.List(Kind).Where(e => e.FilePath == file && e.KeyPath.StartsWith(ModelPathPrefix, StringComparison.Ordinal) && e.KeyPath.EndsWith(']'))
                     .OrderBy(e => e.KeyPath, StringComparer.Ordinal).ToList())
        {
            if (wanted.Contains(entry.KeyPath[(ModelPathPrefix.Length - IdPrefix.Length)..^1])) continue;
            if (CurrentValue(file, ConfigFileFormat.Json, entry.KeyPath) is { } current)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Json, entry.KeyPath, current, null));
        }
        return BuildPlan(changes);
    }
}
