using System.Text.Json.Nodes;
using Astra.Clients.Editing;
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

    private static string ProviderPath => "providers." + JsoncEditor.Selector(("name", ProviderId));

    private static string ModelPathPrefix => "models.[provider=" + ProviderId + "&id=";

    private static string ModelPath(string id) => "models." + JsoncEditor.Selector(("provider", ProviderId), ("id", id));

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
        var provider = new JsonObject
        {
            ["name"] = ProviderId,
            ["type"] = "openai",
            ["baseUrl"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["apiKey"] = ctx.LocalKey,
        };
        var changes = new List<ConfigChange>
        {
            new(file, ConfigFileFormat.Json, ProviderPath, CurrentValue(file, ConfigFileFormat.Json, ProviderPath), provider.ToJsonString()),
        };

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, _) in ModelEntries(ctx))
        {
            if (!CanSelect(id)) continue; // ids with selector metacharacters cannot be addressed safely
            wanted.Add(id);
            var path = ModelPath(id);
            var model = new JsonObject { ["id"] = id, ["provider"] = ProviderId };
            changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, CurrentValue(file, ConfigFileFormat.Json, path), model.ToJsonString()));
        }
        // Models Astra registered earlier that the bound provider no longer offers are taken out again.
        foreach (var path in TrackedModelPaths(file))
        {
            if (wanted.Contains(path[ModelPathPrefix.Length..^1])) continue;
            var current = CurrentValue(file, ConfigFileFormat.Json, path);
            if (current is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, current, null));
        }

        if (!string.IsNullOrEmpty(ctx.Model) && wanted.Contains(ctx.Model))
        {
            changes.Add(new ConfigChange(SettingsFile, ConfigFileFormat.Json, SelectedModelPath,
                CurrentValue(SettingsFile, ConfigFileFormat.Json, SelectedModelPath), JsonString($"{ProviderId}/{ctx.Model}")));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var file = ProvidersFile;
        var changes = new List<ConfigChange>();
        var providerBefore = CurrentValue(file, ConfigFileFormat.Json, ProviderPath);
        if (providerBefore is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, ProviderPath, providerBefore, null));
        foreach (var path in TrackedModelPaths(file))
        {
            var current = CurrentValue(file, ConfigFileFormat.Json, path);
            if (current is not null) changes.Add(new ConfigChange(file, ConfigFileFormat.Json, path, current, null));
        }
        // A model the user picked afterwards (GitHub-hosted or another BYOK provider) is theirs and stays.
        var selected = CurrentValue(SettingsFile, ConfigFileFormat.Json, SelectedModelPath);
        if (StringOf(selected)?.StartsWith(ProviderId + "/", StringComparison.Ordinal) == true)
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
        .Where(e => e.FilePath == file && e.KeyPath.StartsWith(ModelPathPrefix, StringComparison.Ordinal) && e.KeyPath.EndsWith(']'))
        .Select(e => e.KeyPath)
        .ToList();

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
