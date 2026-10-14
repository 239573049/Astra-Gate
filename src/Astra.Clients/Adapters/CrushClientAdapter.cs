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
/// <para>
/// A Crush provider speaks one protocol (its <c>type</c>), so models whose native protocol is not Chat Completions go to a
/// provider of their own: <c>astra-responses</c> (<c>openai</c>, which Crush sends over Responses only for the ids its
/// <c>IsResponsesModel</c> knows — <c>gpt-4</c> and later — so other ids stay on <c>astra</c>), <c>astra-anthropic</c>
/// (<c>anthropic</c>) and <c>astra-gemini</c> (<c>google</c>); the last two take the gateway root, their SDKs add the version
/// path. Such a provider exists only while it has models; <c>astra</c> always does, and keeps discovery off when it ends up
/// without models of its own.
/// </para>
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

    /// <summary>The providers Astra writes, one per protocol: id, Crush <c>type</c> and display name.</summary>
    private static readonly (ApiProtocol Protocol, string Id, string Type, string Name)[] Providers =
    [
        (ApiProtocol.OpenAIChat, ProviderId, "openai-compat", "Astra"),
        (ApiProtocol.OpenAIResponses, ProviderId + "-responses", "openai", "Astra (Responses)"),
        (ApiProtocol.Anthropic, ProviderId + "-anthropic", "anthropic", "Astra (Anthropic)"),
        (ApiProtocol.Gemini, ProviderId + "-gemini", "google", "Astra (Gemini)"),
    ];

    /// <summary>Crush's <c>IsResponsesModel</c>: the ids its <c>openai</c> type sends over Responses (others go over Chat).</summary>
    private static bool CrushUsesResponses(string id) =>
        System.Text.RegularExpressions.Regex.IsMatch(id.ToLowerInvariant(), @"gpt-(?:[4-9]|[1-9]\d)") || id is "chatgpt-4o-latest";

    public override ConfigChangePlan PlanEnable(EnableContext ctx)
    {
        var root = ctx.GatewayBaseUrl.TrimEnd('/');
        var protocols = ModelProtocols(ctx, ApiProtocol.OpenAIChat,
            accepts: (id, p) => p != ApiProtocol.OpenAIResponses || CrushUsesResponses(id));
        var models = Models(ctx);
        var changes = new List<ConfigChange>();
        foreach (var (protocol, id, type, name) in Providers)
        {
            var own = new JsonArray([.. models.Where(m => protocols.GetValueOrDefault(m!["id"]!.GetValue<string>()) == protocol)
                .Select(m => m!.DeepClone())]);
            var path = $"providers.{id}";
            var before = CurrentValue(ConfigFile, ConfigFileFormat.Json, path);
            if (protocol != ApiProtocol.OpenAIChat && own.Count == 0)
            {
                // Not needed (any more): a provider Astra added goes again.
                if (before is not null && Store.Get(Kind, ConfigFile, path) is not null)
                    changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, path, before, null));
                continue;
            }
            var provider = new JsonObject
            {
                ["name"] = name,
                ["type"] = type,
                ["base_url"] = protocol is ApiProtocol.Anthropic or ApiProtocol.Gemini ? root : root + "/v1",
                ["api_key"] = ctx.LocalKey,
            };
            // No models known at all: Crush discovers them from /v1/models. Models that all live elsewhere: no discovery.
            if (own.Count > 0) provider["models"] = own;
            else if (models.Count > 0) provider["discover_models"] = false;
            changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, path, before, provider.ToJsonString()));
        }
        if (!string.IsNullOrEmpty(ctx.Model))
        {
            var selected = protocols.GetValueOrDefault(ctx.Model, ApiProtocol.OpenAIChat);
            var selection = new JsonObject { ["provider"] = Providers.First(p => p.Protocol == selected).Id, ["model"] = ctx.Model };
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
        foreach (var (_, id, _, _) in Providers.Skip(1))
        {
            var path = $"providers.{id}";
            if (Store.Get(Kind, ConfigFile, path) is not null && CurrentValue(ConfigFile, ConfigFileFormat.Json, path) is { } before)
                changes.Add(new ConfigChange(ConfigFile, ConfigFileFormat.Json, path, before, null));
        }
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
            return JsonNode.Parse(selectionJson) is JsonObject o && (o["provider"] as JsonValue)?.TryGetValue(out string? p) == true
                   && Providers.Any(x => x.Id == p);
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
