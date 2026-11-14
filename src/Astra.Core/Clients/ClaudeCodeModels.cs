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

    /// <summary>
    /// Claude Code's default subagent model (issue #42): used by subagents whose definition or spawn names no model, instead of
    /// the main session's. A frontmatter alias such as <c>model: haiku</c> still resolves through the tier slots.
    /// </summary>
    public const string SubagentSlot = "CLAUDE_CODE_SUBAGENT_MODEL";

    /// <summary>
    /// Slot env var names, in UI order: the default model, then the fable / opus / sonnet / haiku tiers, then the default
    /// subagent model.
    /// </summary>
    public static readonly IReadOnlyList<string> Slots =
    [
        "ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_FABLE_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL",
        "ANTHROPIC_DEFAULT_HAIKU_MODEL", SubagentSlot,
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
    /// already has it. Every tier gets it, Haiku included (Haiku 5.5 is natively 1M); whether a model's window allows it is
    /// the caller's check (<c>EnableContext.ModelsWithoutContext1M</c>). The subagent slot's <c>inherit</c> (lowercase, Claude
    /// Code's "no subagent default") is a keyword, not a model, and stays as it is.
    /// </summary>
    public static string With1M(string slot, string model) =>
        (slot == SubagentSlot && model == "inherit") || model.EndsWith(OneMillionSuffix, StringComparison.OrdinalIgnoreCase)
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
