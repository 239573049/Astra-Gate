using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Goose (Block's agent, CLI and desktop app; Coexist): Astra becomes a <em>custom provider</em> of Goose's, a JSON file of its own,
/// <c>custom_providers/astra.json</c> beside <c>config.yaml</c> (<c>~/.config/goose</c>, <c>%APPDATA%\Block\goose\config</c> on Windows),
/// which Goose loads at start-up: <c>engine: openai</c>, <c>base_url</c> the gateway's <c>/v1</c>, the client's local key as an
/// <c>Authorization: Bearer</c> header (<c>requires_auth: false</c>, no key variable), the bound provider's models as a static list
/// (<c>dynamic_models: false</c>, so Goose keeps to it and to each model's <c>context_limit</c>). The file is Astra's alone: a disable
/// removes every key and Astra deletes the file once it is empty. A chosen model is then written into <c>config.yaml</c> the way
/// Goose keeps it: the new layout (a <c>providers</c> map or <c>active_provider</c>: <c>active_provider</c> plus
/// <c>providers.astra.{enabled,model,configured}</c>, the flat keys it would delete on its next write removed — recorded and restored)
/// or the old one (<c>GOOSE_PROVIDER</c> / <c>GOOSE_MODEL</c>). A model is declared <c>reasoning</c> only for the OpenAI reasoning
/// families (o-series, gpt-5/6), the only ones Goose sends a thinking level to. Verified against magpie's wiring only.
/// </summary>
public sealed class GooseClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    private static readonly Regex Thinks = new(@"(?:^|[-/])(?:o\d+(?:$|-)|gpt-(?:5|6)(?:$|[-.]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] ProviderKeys =
    [
        "name", "engine", "display_name", "description", "api_key_env", "base_url", "headers", "requires_auth",
        "skip_canonical_filtering", "models", "dynamic_models",
    ];

    private string ConfigDir
    {
        get
        {
            if (Env.Os == "windows" && Env.GetEnvironmentVariable("APPDATA") is { Length: > 0 } app) return Path.Combine(app, "Block", "goose", "config");
            var xdg = Env.GetEnvironmentVariable("XDG_CONFIG_HOME");
            return Path.Combine(string.IsNullOrEmpty(xdg) ? Env.Combine(".config") : xdg, "goose");
        }
    }

    private string ConfigFile => Path.Combine(ConfigDir, "config.yaml");

    private string ProviderFile => Path.Combine(ConfigDir, "custom_providers", ProviderId + ".json");

    public override string Kind => ClientKinds.Goose;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = ConfigDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("goose");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no goose executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ProviderFile, ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var file = ProviderFile;
        var provider = new JsonObject
        {
            ["name"] = ProviderId,
            ["engine"] = "openai",
            ["display_name"] = "Astra",
            ["description"] = "Every model Astra has, through its local gateway",
            ["api_key_env"] = "",
            ["base_url"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["headers"] = new JsonObject { ["Authorization"] = "Bearer " + ctx.LocalKey },
            ["requires_auth"] = false,
            ["skip_canonical_filtering"] = true,
        };
        var hints = ModelHints(ctx);
        var models = new JsonArray();
        foreach (var (id, _) in ModelEntries(ctx))
        {
            var model = new JsonObject { ["name"] = id, ["reasoning"] = Thinks.IsMatch(id) };
            if (PositiveLong(hints.GetValueOrDefault(id)?["contextWindow"]) is { } context) model["context_limit"] = context;
            models.AddNode(model);
        }
        provider["models"] = models;
        // Goose refuses a provider whose static list is empty.
        if (models.Count > 0) provider["dynamic_models"] = false;

        var changes = new List<ConfigChange>();
        foreach (var key in ProviderKeys)
            if (provider[key] is { } value)
                changes.Add(new ConfigChange(file, ConfigFileFormat.Json, key, CurrentValue(file, ConfigFileFormat.Json, key), value.ToJsonString()));
        if (!string.IsNullOrEmpty(ctx.Model)) changes.AddRange(ModelChanges(ctx.Model));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        // Every key Astra recorded: removed where it is still there (drift is the applier's call), a key Astra deleted put back.
        foreach (var file in new[] { ConfigFile, ProviderFile })
        {
            var format = file == ConfigFile ? ConfigFileFormat.Yaml : ConfigFileFormat.Json;
            foreach (var entry in Store.List(Kind).Where(e => e.FilePath == file))
            {
                var current = CurrentValue(file, format, entry.KeyPath);
                if (current is not null) changes.Add(new ConfigChange(file, format, entry.KeyPath, current, null));
                else if (!entry.OriginalAbsent) changes.Add(new ConfigChange(file, format, entry.KeyPath, null, entry.OriginalValueJson));
            }
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var enabled = Store.Get(Kind, ProviderFile, "base_url") is not null && StillOurs(ProviderFile, ConfigFileFormat.Json, "base_url");
        return BuildStatus(detection, enabled, enabled ? ["Goose loads custom providers when it starts: restart Goose (CLI sessions and the desktop app) to use this."] : []);
    }

    /// <summary>The model selection, in the layout Goose's config is in.</summary>
    private IEnumerable<ConfigChange> ModelChanges(string model)
    {
        var file = ConfigFile;
        ConfigChange Set(string path, string json) => new(file, ConfigFileFormat.Yaml, path, CurrentValue(file, ConfigFileFormat.Yaml, path), json);
        var newLayout = CurrentValue(file, ConfigFileFormat.Yaml, "providers") is not null || CurrentValue(file, ConfigFileFormat.Yaml, "active_provider") is not null;
        if (!newLayout)
        {
            yield return Set("GOOSE_PROVIDER", JsonString(ProviderId));
            yield return Set("GOOSE_MODEL", JsonString(model));
            yield break;
        }
        yield return Set("active_provider", JsonString(ProviderId));
        yield return Set($"providers.{ProviderId}.enabled", "true");
        yield return Set($"providers.{ProviderId}.model", JsonString(model));
        yield return Set($"providers.{ProviderId}.configured", "true");
        // Flat keys beside the map are read by no Goose that reads the map, and deleted by the next one that writes.
        foreach (var flat in new[] { "GOOSE_PROVIDER", "GOOSE_MODEL" })
            if (CurrentValue(file, ConfigFileFormat.Yaml, flat) is { } before)
                yield return new ConfigChange(file, ConfigFileFormat.Yaml, flat, before, null);
    }
}
