using System.Text.Json.Nodes;

namespace Astra.Core.Clients;

/// <summary>
/// Claude Code's model selection slots. Each slot is one env var in <c>~/.claude/settings.json</c>, written
/// only when the user picked a model for it (empty = the variable stays untouched); the gateway passes the
/// model names through, so any provider model works. The map lives in <c>clients.extra_json</c> as
/// <c>{"models":{"ANTHROPIC_MODEL":"…","ANTHROPIC_DEFAULT_HAIKU_MODEL":"…"}}</c>.
/// </summary>
public static class ClaudeCodeModels
{
    public const string ExtrasKey = "models";

    /// <summary>Slot env var names, in UI order: the default model, then the fable / opus / sonnet / haiku tiers.</summary>
    public static readonly IReadOnlyList<string> Slots =
    [
        "ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_FABLE_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL",
    ];

    /// <summary>The non-empty slot → model entries of an extras object, in <see cref="Slots"/> order.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Parse(JsonObject? extras)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (extras?[ExtrasKey] is not JsonObject map) return result;
        foreach (var slot in Slots)
        {
            if (map[slot] is JsonValue v && v.TryGetValue<string>(out var model) && !string.IsNullOrWhiteSpace(model))
                result.Add(new(slot, model.Trim()));
        }
        return result;
    }

    /// <summary>The 1M-context marker Claude Code understands as a model-name suffix.</summary>
    public const string OneMillionSuffix = "[1m]";

    /// <summary>
    /// The model with the <c>[1m]</c> suffix (Claude Code then sends the 1M-context beta header itself); unchanged when it
    /// already has it. The Haiku tier never gets it — Haiku has no 1M variant; the Fable tier gets it like Opus and Sonnet.
    /// </summary>
    public static string With1M(string slot, string model) =>
        slot == "ANTHROPIC_DEFAULT_HAIKU_MODEL" || model.EndsWith(OneMillionSuffix, StringComparison.OrdinalIgnoreCase)
            ? model
            : model + OneMillionSuffix;

    public static IReadOnlyList<KeyValuePair<string, string>> Parse(string? extraJson)
    {
        if (string.IsNullOrWhiteSpace(extraJson)) return [];
        try
        {
            return Parse(JsonNode.Parse(extraJson) as JsonObject);
        }
        catch (System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
