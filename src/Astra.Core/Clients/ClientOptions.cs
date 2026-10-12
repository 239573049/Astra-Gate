using System.Text.Json.Nodes;

namespace Astra.Core.Clients;

/// <summary>
/// One client-specific option. <see cref="Scope"/> "config" options are written into the client's own configuration by
/// its adapter on enable; "gateway" options are applied by the gateway to that client's requests. Type is "bool",
/// "int" or "enum"; <see cref="Default"/> is the value used when the option is unset ("" = leave the client alone).
/// </summary>
public sealed record ClientOptionDescriptor(string Key, string Type, string Default, string Scope, string[]? Choices = null);

/// <summary>
/// Client-specific options live in <c>clients.extra_json</c> under <see cref="ExtrasKey"/>
/// (<c>{"options":{"context1m":true,"cacheTtl":"1h"}}</c>) next to the "models" map, so they need no migration.
/// </summary>
public static class ClientOptions
{
    public const string ExtrasKey = "options";

    public const string Context1M = "context1m";
    public const string CacheTtl = "cacheTtl";
    public const string MaxThinkingTokens = "maxThinkingTokens";
    public const string DisablePromptCaching = "disablePromptCaching";
    public const string ContextWindow = "contextWindow";
    public const string AutoCompactTokenLimit = "autoCompactTokenLimit";
    public const string ReasoningEffort = "reasoningEffort";
    public const string ModelCatalog = "modelCatalog";

    private static readonly IReadOnlyList<ClientOptionDescriptor> ClaudeCode =
    [
        new(Context1M, "bool", "false", "config"),
        new(CacheTtl, "enum", "default", "gateway", ["default", "5m", "1h"]),
        new(MaxThinkingTokens, "int", "", "config"),
        new(DisablePromptCaching, "bool", "false", "config"),
    ];

    private static readonly IReadOnlyList<ClientOptionDescriptor> Codex =
    [
        new(ContextWindow, "int", "", "config"),
        new(AutoCompactTokenLimit, "int", "", "config"),
        new(ReasoningEffort, "enum", "default", "config", ["default", "minimal", "low", "medium", "high"]),
        new(ModelCatalog, "bool", "false", "config"),
    ];

    /// <summary>The options a client supports; empty for clients without any.</summary>
    public static IReadOnlyList<ClientOptionDescriptor> For(string kind) => kind switch
    {
        ClientKinds.ClaudeCode => ClaudeCode,
        ClientKinds.Codex => Codex,
        _ => [],
    };

    /// <summary>
    /// Config key paths a client's "config" options can write, so unsetting an option restores what Astra
    /// replaced (see <c>ClientService.EnablePlan</c>) and disabling the client removes them.
    /// </summary>
    public static IReadOnlyList<string> ConfigKeyPaths(string kind) => kind switch
    {
        ClientKinds.ClaudeCode => ["env.MAX_THINKING_TOKENS", "env.DISABLE_PROMPT_CACHING"],
        // "models" is the key of the Astra-owned model catalog file (CodexClientAdapter), not of config.toml.
        ClientKinds.Codex => ["model_context_window", "model_auto_compact_token_limit", "model_reasoning_effort", "model_catalog_json", "models"],
        _ => [],
    };

    /// <summary>The "options" object of an extras object, or null.</summary>
    public static JsonObject? Of(JsonObject? extras) => extras?[ExtrasKey] as JsonObject;

    public static JsonObject? Of(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return null;
        try
        {
            return Of(JsonNode.Parse(extraJson) as JsonObject);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static bool GetBool(JsonObject? extras, string key) =>
        Of(extras)?[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>A positive integer option, or null when unset / zero / not a number.</summary>
    public static long? GetInt(JsonObject? extras, string key) =>
        Of(extras)?[key] is JsonValue v && TryLong(v, out var n) && n > 0 ? n : null;

    // A JsonValue built in code holds an int, one parsed from text a JsonElement; both must read as a number.
    private static bool TryLong(JsonValue v, out long n)
    {
        n = 0;
        return v.GetValueKind() == System.Text.Json.JsonValueKind.Number
               && long.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n);
    }

    /// <summary>An enum / string option; null when unset, blank or "default".</summary>
    public static string? GetChoice(JsonObject? extras, string key) =>
        Of(extras)?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) && s != "default" ? s.Trim() : null;

    /// <summary>The client's cache TTL preference ("5m" / "1h"), or null to leave the request's cache_control alone.</summary>
    public static string? GetCacheTtl(string? extraJson) =>
        Of(extraJson)?[CacheTtl] is JsonValue v && v.TryGetValue<string>(out var ttl) && ttl is "5m" or "1h" ? ttl : null;

    /// <summary>Validates an "options" object against the client's descriptors; returns an error message, or null when valid.</summary>
    public static string? Validate(string kind, JsonObject options)
    {
        var descriptors = For(kind);
        foreach (var (key, value) in options)
        {
            var d = descriptors.FirstOrDefault(x => x.Key == key);
            if (d is null) return $"Unknown option '{key}' for {kind}";
            if (value is null) continue;
            var ok = d.Type switch
            {
                "bool" => value is JsonValue b && b.TryGetValue<bool>(out _),
                "int" => value is JsonValue n && TryLong(n, out var l) && l >= 0,
                _ => value is JsonValue s && s.TryGetValue<string>(out var t) && (d.Choices?.Contains(t) ?? false),
            };
            if (!ok) return $"Invalid value for option '{key}'";
        }
        return null;
    }
}
