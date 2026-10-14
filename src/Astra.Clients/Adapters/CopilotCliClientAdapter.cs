using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// GitHub Copilot CLI (Coexist): registers a bring-your-own-key provider in
/// <c>&lt;COPILOT_HOME&gt;/providers.json</c> (or COPILOT_PROVIDERS_CONFIG) — an element
/// <c>{"name":"astra","type":"openai","baseUrl":…,"apiKey":…}</c> of <c>providers</c> plus one
/// <c>{"id":…,"provider":"astra"}</c> element of <c>models</c> per bound model — and, when a model is
/// chosen, selects it with <c>"model": "astra/&lt;id&gt;"</c> in <c>settings.json</c>. COPILOT_HOME defaults
/// to <c>~/.copilot</c>. A declared providers.json takes precedence over the COPILOT_PROVIDER_* variables;
/// GitHub-hosted models stay available. Array elements are addressed with selectors
/// (<see cref="JsoncEditor.Selector"/>), so the user's own BYOK entries are never touched. Schema verified
/// against Copilot CLI 1.0.93 (validation errors and the requests it sends).
/// <para>
/// A provider speaks one protocol (<c>type</c> plus, for <c>openai</c>, <c>wireApi</c> = <c>completions</c> | <c>responses</c>;
/// Copilot CLI 1.0.97 schema), so models whose native protocol is Responses or Anthropic are listed under providers of their
/// own — <c>astra-responses</c> (<c>"wireApi":"responses"</c>) and <c>astra-anthropic</c> (<c>type</c> anthropic, gateway root:
/// it posts to <c>/v1/messages</c>) — which exist only while they have models. Each model carries the context and output
/// limits and vision support Astra knows.
/// </para>
/// </summary>
public sealed class CopilotCliClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private string HomeDir =>
        Env.GetEnvironmentVariable("COPILOT_HOME") is { Length: > 0 } dir ? dir : Env.Combine(".copilot");

    private string ProvidersFile =>
        Env.GetEnvironmentVariable("COPILOT_PROVIDERS_CONFIG") is { Length: > 0 } file ? file : Path.Combine(HomeDir, "providers.json");

    private string SettingsFile => Path.Combine(HomeDir, "settings.json");

    /// <summary>The providers Astra registers, one per protocol: name, <c>type</c> and <c>wireApi</c>.</summary>
    private static readonly (ApiProtocol Protocol, string Name, string Type, string? WireApi)[] Providers =
    [
        (ApiProtocol.OpenAIChat, ProviderId, "openai", null),
        (ApiProtocol.OpenAIResponses, ProviderId + "-responses", "openai", "responses"),
        (ApiProtocol.Anthropic, ProviderId + "-anthropic", "anthropic", null),
    ];

    private static string ProviderPath => ProviderPathOf(ProviderId);

    private static string ProviderPathOf(string name) => "providers." + JsoncEditor.Selector(("name", name));

    private static string ModelPathPrefix(string provider) => "models.[provider=" + provider + "&id=";

    private static string ModelPath(string provider, string id) => "models." + JsoncEditor.Selector(("provider", provider), ("id", id));

    private const string SelectedModelPath = "model";

    public override string Kind => ClientKinds.CopilotCli;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = HomeDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("copilot");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no copilot executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ProvidersFile, SettingsFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ProvidersFile;
        var root = ctx.GatewayBaseUrl.TrimEnd('/');
        var hints = ModelHints(ctx);
        var protocols = ModelProtocols(ctx, ApiProtocol.OpenAIChat);
        var models = ModelEntries(ctx).Where(e => CanSelect(e.Id)).ToList(); // selector metacharacters cannot be addressed safely
        var changes = new List<ConfigChange>();
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        var selected = (string?)null;
        foreach (var (protocol, name, type, wireApi) in Providers)
        {
            var own = models.Where(m => protocols[m.Id] == protocol).ToList();
            var providerPath = ProviderPathOf(name);
            var before = CurrentValue(file, ConfigFileFormat.Json, providerPath);
            if (protocol != ApiProtocol.OpenAIChat && own.Count == 0)
            {
                if (before is not null && Store.Get(Kind, file, providerPath) is not null)
                    changes.Add(new ConfigChange(file, ConfigFileFormat.Json, providerPath, before, null));
                continue;
            }
            var provider = new JsonObject
            {
                ["name"] = name,
                ["type"] = type,
                ["baseUrl"] = protocol == ApiProtocol.Anthropic ? root : root + "/v1",
                ["apiKey"] = ctx.LocalKey,
            };
            if (wireApi is not null) provider["wireApi"] = wireApi;
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, providerPath, before, provider.ToJsonString()));
            foreach (var (id, _) in own)
            {
                var path = ModelPath(name, id);
                wanted.Add(path);
                if (id == ctx.Model) selected = $"{name}/{id}";
                changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, CurrentValue(file, ConfigFileFormat.Json, path),
                    ModelJson(id, name, hints.GetValueOrDefault(id)).ToJsonString()));
            }
        }
        // Models Astra registered earlier that the bound provider no longer offers (or that moved to another protocol's
        // provider) are taken out again.
        foreach (var path in TrackedModelPaths(file))
        {
            if (wanted.Contains(path)) continue;
            var current = CurrentValue(file, ConfigFileFormat.Json, path);
            if (current is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, current, null));
        }

        if (selected is not null)
        {
            changes.Add(new ConfigChange(SettingsFile, ConfigFileFormat.Json, SelectedModelPath,
                CurrentValue(SettingsFile, ConfigFileFormat.Json, SelectedModelPath), JsonString(selected)));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ProvidersFile;
        var changes = new List<ConfigChange>();
        var providerBefore = CurrentValue(file, ConfigFileFormat.Json, ProviderPath);
        if (providerBefore is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, ProviderPath, providerBefore, null));
        foreach (var (_, name, _, _) in Providers.Skip(1))
        {
            var path = ProviderPathOf(name);
            if (Store.Get(Kind, file, path) is not null && CurrentValue(file, ConfigFileFormat.Json, path) is { } before)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, before, null));
        }
        foreach (var path in TrackedModelPaths(file))
        {
            var current = CurrentValue(file, ConfigFileFormat.Json, path);
            if (current is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, current, null));
        }
        // A model the user picked afterwards (GitHub-hosted or another BYOK provider) is theirs and stays.
        var selected = CurrentValue(SettingsFile, ConfigFileFormat.Json, SelectedModelPath);
        if (Providers.Any(p => StringOf(selected)?.StartsWith(p.Name + "/", StringComparison.Ordinal) == true))
            changes.Add(new ConfigChange(SettingsFile, ConfigFileFormat.Json, SelectedModelPath, selected, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var entry = Store.Get(Kind, ProvidersFile, ProviderPath);
        var enabled = entry is not null && ConfigValueCodec.EqualsValue(
            ConfigFileFormat.Json,
            CurrentValue(ProvidersFile, ConfigFileFormat.Json, ProviderPath),
            entry.AppliedValueJson);
        return BuildStatus(detection, enabled, []);
    }

    private IEnumerable<string> TrackedModelPaths(string file) => Store.List(Kind)
        .Where(e => e.FilePath == file && e.KeyPath.EndsWith(']')
                    && Providers.Any(p => e.KeyPath.StartsWith(ModelPathPrefix(p.Name), StringComparison.Ordinal)))
        .Select(e => e.KeyPath)
        .ToList();

    /// <summary>A model entry: id, provider, and the limits / vision support Astra knows (Copilot uses its defaults otherwise).</summary>
    private static JsonObject ModelJson(string id, string provider, JsonObject? info)
    {
        var model = new JsonObject { ["id"] = id, ["provider"] = provider };
        if (PositiveLong(info?["contextWindow"]) is { } context) model["maxContextWindowTokens"] = context;
        if (PositiveLong(info?["maxOutputTokens"]) is { } maxOutput) model["maxOutputTokens"] = maxOutput;
        if (BoolHint(info?["vision"]) is { } vision)
            model["capabilities"] = new JsonObject { ["supports"] = new JsonObject { ["vision"] = vision } };
        return model;
    }

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
