using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Requests;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Copies the reasoning configuration a client explicitly set on its inbound request body into the
/// request log. Only configuration metadata is recorded — never thinking content, secrets, a level
/// derived from a budget, or a provider default. Missing settings stay null (rows written before the
/// fields existed have no reasoning metadata at all). Both the pass-through fast path and the
/// conversion path call this on the raw inbound body, BEFORE any codec derives upstream budgets from
/// it, so the log always holds the client's own words. Reading is defensive: unexpected JSON value
/// types are ignored and nothing here throws.
/// </summary>
public static class RequestReasoning
{
    public static void Capture(RequestRecord record, ApiProtocol inbound, JsonObject body)
    {
        switch (inbound)
        {
            case ApiProtocol.OpenAIChat:
                record.ReasoningEffort = Str(body, "reasoning_effort");
                break;
            case ApiProtocol.OpenAIResponses:
                if (body["reasoning"] is JsonObject reasoning) record.ReasoningEffort = Str(reasoning, "effort");
                break;
            case ApiProtocol.Anthropic:
                if (body["thinking"] is JsonObject thinking)
                {
                    record.ReasoningMode = Str(thinking, "type"); // adaptive | enabled | disabled (verbatim, future values kept)
                    record.ReasoningBudgetTokens = PositiveInt(thinking, "budget_tokens");
                }
                if (body["output_config"] is JsonObject outputConfig) record.ReasoningEffort = Str(outputConfig, "effort");
                break;
            case ApiProtocol.Gemini:
                if (Node(body, "generationConfig", "generation_config") is not JsonObject gen) break;
                if (Node(gen, "thinkingConfig", "thinking_config") is JsonObject cfg)
                {
                    // Same normalization the Gemini codec applies to thinkingLevel ("THINKING_LEVEL_HIGH" → "high").
                    if (Str(cfg, "thinkingLevel", "thinking_level") is { Length: > 0 } level)
                        record.ReasoningEffort = level.StartsWith("THINKING_LEVEL_", StringComparison.OrdinalIgnoreCase)
                            ? level["THINKING_LEVEL_".Length..].ToLowerInvariant()
                            : level.ToLowerInvariant();
                    // -1 = auto, 0 = disabled, > 0 = a real budget.
                    if (Integral(cfg, "thinkingBudget", "thinking_budget") is { } budget)
                    {
                        if (budget == -1) record.ReasoningMode = "auto";
                        else if (budget == 0) record.ReasoningMode = "disabled";
                        else if (budget > 0) record.ReasoningBudgetTokens = budget;
                    }
                }
                break;
        }
    }

    /// <summary>First key present as a JSON object, or null (camelCase / snake_case aliases).</summary>
    private static JsonObject? Node(JsonObject o, string camel, string snake) =>
        (o[camel] ?? o[snake]) as JsonObject;

    /// <summary>Non-empty string value, or null.</summary>
    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static string? Str(JsonObject o, string camel, string snake) => Str(o, camel) ?? Str(o, snake);

    /// <summary>Integer value from a JSON number (integral doubles accepted), or null.</summary>
    private static long? Integral(JsonObject o, string camel, string snake)
    {
        var node = o[camel] ?? o[snake];
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (!v.TryGetValue<double>(out var d) || !double.IsFinite(d) || d != Math.Truncate(d)
            || d < long.MinValue || d >= (double)long.MaxValue) return null;
        return (long)d;
    }

    private static long? PositiveInt(JsonObject o, string key) => Integral(o, key, key) is { } v && v > 0 ? v : null;
}
