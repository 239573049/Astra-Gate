using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;

using Astra.Clients.Config;

namespace Astra.Clients.Adapters;

/// <summary>
/// Crush (Charm, npm <c>@charmland/crush</c>; Coexist): adds <c>providers.astra</c> (an <c>openai-compat</c> provider
/// listing the bound provider's models) to <c>crush.json</c> and, when a model is chosen, selects it with
/// <c>models.large</c> = <c>{provider, model}</c> (an unset <c>models.small</c> falls back to the large model for a custom
/// provider). The file is <c>&lt;CRUSH_GLOBAL_CONFIG&gt;/crush.json</c> (the variable names a directory), else
/// <c>$XDG_CONFIG_HOME/crush/crush.json</c>, else <c>~/.config/crush/crush.json</c>. Verified against Crush's
/// docs/config/README.md, schema.json and internal/config/load.go (2026-10): <c>crushrc</c> is the newer format and
/// <c>crush.json</c> is deprecated but still loaded; where both define the same top-level key <c>crushrc</c> wins, so
/// <see cref="Inspect"/> warns when a <c>crushrc</c> exists. Every model field in the schema is required, so the
/// unknown ones get defaults. Crush's own providers stay untouched.
/// </summary>
public sealed class CrushClientAdapter(ClientEnvironment env, IClientConfigStateStore store)
    : ClientAdapterBase(env, store)
{
    public const string ProviderId = "astra";

    public const long DefaultContextWindow = 128_000;

    public const long DefaultMaxOutputTokens = 16_384;

    private string ConfigDir =>
        Env.GetEnvironmentVariable("CRUSH_GLOBAL_CONFIG") is { Length: > 0 } dir ? dir
        : Env.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? Path.Combine(xdg, "crush")
        : Env.Combine(".config", "crush");

    private string ConfigFile => Path.Combine(ConfigDir, "crush.json");

    private string CrushRcFile => Path.Combine(ConfigDir, "crushrc");

    private static string ProviderPath => $"providers.{ProviderId}";

    private const string LargeModelPath = "models.large";

    public override string Kind => ClientKinds.Crush;

    public override ClientMode Mode => ClientMode.Coexist;

    public override ClientAvailability Availability => ClientAvailability.Available;

    public override ClientDetection Detect()
    {
        var dir = ConfigDir;
        if (Env.DirectoryExists(dir)) return ClientDetection.Found(null, $"config directory {dir}");
        var exe = Env.FindOnPath("crush");
        return exe is not null ? ClientDetection.Found(null, $"executable {exe}") : ClientDetection.NotFound($"no {dir} and no crush executable on PATH");
    }

    public override IReadOnlyList<string> ConfigPaths() => [ConfigFile];

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var provider = new JsonObject
        {
            ["name"] = "Astra",
            ["type"] = "openai-compat",
            ["base_url"] = ctx.GatewayBaseUrl.TrimEnd('/') + "/v1",
            ["api_key"] = ctx.LocalKey,
        };
        var models = Models(ctx);
        if (models.Count > 0) provider["models"] = models; // none listed: Crush discovers them from /v1/models
        var changes = new List<ConfigChange>
        {
            new(ConfigFile, ConfigFileFormat.Json, ProviderPath, CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath),
                provider.ToJsonString()),
        };
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            var selection = new JsonObject { ["provider"] = ProviderId, ["model"] = ctx.Model };
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, LargeModelPath,
                CurrentValue(ConfigFile, ConfigFileFormat.Json, LargeModelPath), selection.ToJsonString()));
        }
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanDisable()
    {
        var changes = new List<ConfigChange>();
        var providerBefore = CurrentValue(ConfigFile, ConfigFileFormat.Json, ProviderPath);
        if (providerBefore is not null)
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, ProviderPath, providerBefore, null));
        // A model the user selected afterwards (another provider) is theirs and stays.
        if (Store.Get(Kind, ConfigFile, LargeModelPath) is not null
            && CurrentValue(ConfigFile, ConfigFileFormat.Json, LargeModelPath) is { } large && SelectsAstra(large))
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, LargeModelPath, large, null));
        return BuildPlan(changes);
    }

    public override ConfigChangePlan PlanPurge() => PlanDisable();

    public override ClientStatus Inspect()
    {
        var detection = Detect();
        var warnings = new List<string>();
        if (Env.FileExists(CrushRcFile))
            warnings.Add($"{CrushRcFile} exists; Crush gives crushrc priority over crush.json for the same top-level keys (not modified by Astra).");
        return BuildStatus(detection, StillOurs(ConfigFile, ConfigFileFormat.Json, ProviderPath), warnings);
    }

    private static bool SelectsAstra(string selectionJson)
    {
        try
        {
            return JsonNode.Parse(selectionJson) is JsonObject o && (o["provider"] as JsonValue)?.TryGetValue(out string? p) == true && p == ProviderId;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>One entry per bound model; every field of Crush's model schema is required.</summary>
    private static JsonArray Models(EnableContext ctx)
    {
        var hints = ModelHints(ctx);
        var models = new JsonArray();
        foreach (var (id, name) in ModelEntries(ctx))
        {
            hints.TryGetValue(id, out var info);
            var context = PositiveLong(info?["contextWindow"]) ?? DefaultContextWindow;
            var maxOutput = PositiveLong(info?["maxOutputTokens"]) ?? DefaultMaxOutputTokens;
            if (maxOutput >= context) maxOutput = Math.Max(1, context / 4);
            models.AddNode(new JsonObject
            {
                ["id"] = id,
                ["name"] = name ?? id,
                ["context_window"] = context,
                ["default_max_tokens"] = maxOutput,
                ["cost_per_1m_in"] = 0,
                ["cost_per_1m_out"] = 0,
                ["cost_per_1m_in_cached"] = 0,
                ["cost_per_1m_out_cached"] = 0,
                ["can_reason"] = BoolHint(info?["reasoning"]) ?? false,
                ["supports_attachments"] = BoolHint(info?["vision"]) ?? false,
            });
        }
        return models;
    }
}
