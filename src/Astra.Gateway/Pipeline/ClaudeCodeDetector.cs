using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Recognizes requests sent by the official Claude Code CLI (and its IDE / Agent SDK entry points, which share the
/// <c>claude-cli/x.y.z</c> User-Agent). Used by the Claude subscription's "Claude Code only" client policy and to pick
/// the faithful relay path. It judges the traffic itself — what Anthropic sees — not the Astra token suffix.
/// <para>
/// Rules follow sub2api's <c>ClaudeCodeValidator</c>: the UA must match; <c>count_tokens</c> and <c>max_tokens = 1</c>
/// probes (no system prompt) pass on the UA alone; a regular <c>/v1/messages</c> call must also carry
/// <c>x-app</c> + <c>anthropic-version</c> and either a Claude Code <c>metadata.user_id</c> or a Claude Code system
/// prompt marker. This is a guard against accidentally routing other tools through the subscription, not an
/// anti-spoofing boundary.
/// </para>
/// </summary>
public static partial class ClaudeCodeDetector
{
    /// <summary>System prompt openings Claude Code uses (main loop, Agent SDK, sub-agents, compaction).</summary>
    private static readonly string[] SystemPromptMarkers =
    [
        "x-anthropic-billing-header",
        "You are Claude Code, Anthropic's official CLI for Claude.",
        "You are a Claude agent, built on Anthropic's Claude Agent SDK.",
        "You are a file search specialist for Claude Code, Anthropic's official CLI for Claude.",
        "You are a helpful AI assistant tasked with summarizing conversations.",
        "You are an interactive CLI tool that helps users",
        "You are a security monitor for autonomous AI coding agents.",
    ];

    [GeneratedRegex(@"^claude-cli/\d+\.\d+\.\d+", RegexOptions.IgnoreCase)]
    private static partial Regex UserAgentPattern();

    [GeneratedRegex(@"^user_[a-fA-F0-9]{64}_account_[a-fA-F0-9-]*_session_[a-fA-F0-9-]{36}$")]
    private static partial Regex LegacyUserIdPattern();

    /// <summary>Whether the User-Agent is the Claude Code CLI's (<c>claude-cli/1.2.3 (external, cli)</c>).</summary>
    public static bool IsClaudeCliUserAgent(string? userAgent) =>
        !string.IsNullOrEmpty(userAgent) && UserAgentPattern().IsMatch(userAgent);

    /// <summary>Whether an Anthropic Messages request (or count_tokens when <paramref name="countTokens"/>) comes from Claude Code.</summary>
    public static bool IsClaudeCode(IHeaderDictionary headers, JsonObject? body, bool countTokens = false)
    {
        if (!IsClaudeCliUserAgent(headers.UserAgent.ToString())) return false;
        if (countTokens) return true;
        if (body is null) return false;
        // Connectivity / context probes: one output token, no system prompt.
        if (body["max_tokens"] is JsonValue max && max.TryGetValue<int>(out var maxTokens) && maxTokens == 1) return true;

        if (headers["x-app"].ToString().Length == 0 || headers["anthropic-version"].ToString().Length == 0) return false;
        return IsClaudeCodeUserId(body["metadata"] is JsonObject meta && meta["user_id"] is JsonValue id
                   && id.TryGetValue<string>(out var userId) ? userId : null)
               || HasSystemPromptMarker(body["system"]);
    }

    /// <summary>
    /// Claude Code's <c>metadata.user_id</c>: the legacy <c>user_{64hex}_account_{uuid?}_session_{uuid}</c> string, or
    /// (CLI ≥ 2.1.78) a JSON string with <c>device_id</c> and <c>session_id</c>.
    /// </summary>
    public static bool IsClaudeCodeUserId(string? raw)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw)) return false;
        if (raw[0] != '{') return LegacyUserIdPattern().IsMatch(raw);
        try
        {
            return JsonNode.Parse(raw) is JsonObject o
                   && o["device_id"] is JsonValue d && d.TryGetValue<string>(out var device) && device.Length > 0
                   && o["session_id"] is JsonValue s && s.TryGetValue<string>(out var session) && session.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasSystemPromptMarker(JsonNode? system)
    {
        switch (system)
        {
            case JsonValue v when v.TryGetValue<string>(out var text):
                return StartsWithMarker(text);
            case JsonArray blocks:
                foreach (var block in blocks)
                    if (block is JsonObject o && o["text"] is JsonValue t && t.TryGetValue<string>(out var blockText) && StartsWithMarker(blockText))
                        return true;
                return false;
            default:
                return false;
        }
    }

    private static bool StartsWithMarker(string text)
    {
        var trimmed = text.TrimStart();
        foreach (var marker in SystemPromptMarkers)
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
                return true;
        return false;
    }
}
