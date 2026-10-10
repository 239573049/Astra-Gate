using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// The conversation identity a request carries, used to keep one conversation on one subscription account
/// (<see cref="AccountScheduler"/>, switch mode <see cref="SwitchModes.Balanced"/>): upstream prompt caches live per
/// account, so a conversation that hops accounts loses its cache.
/// <list type="bullet">
/// <item>OpenAI Chat / Responses: the body's <c>prompt_cache_key</c>.</item>
/// <item>Anthropic: Claude Code's session id (header <c>x-claude-code-session-id</c>, else the <c>session_id</c> inside
/// <c>metadata.user_id</c>), else a fingerprint of the system prompt plus the first user message.</item>
/// </list>
/// Nothing is written to the request; Gemini has no sticky key.
/// </summary>
public static partial class StickyKeys
{
    /// <summary>Keys longer than this are hashed so the binding table stays small.</summary>
    public const int MaxKeyLength = 256;

    [GeneratedRegex(@"_session_([a-fA-F0-9-]{36})$")]
    private static partial Regex LegacySessionPattern();

    /// <summary>The sticky key of a request, or null when it carries no usable conversation identity.</summary>
    public static string? Of(ApiProtocol inbound, JsonObject? body, IHeaderDictionary headers)
    {
        if (body is null) return null;
        switch (inbound)
        {
            case ApiProtocol.OpenAIChat:
            case ApiProtocol.OpenAIResponses:
                return Str(body, "prompt_cache_key") is { Length: > 0 } cacheKey ? Bound("pck:" + cacheKey) : null;
            case ApiProtocol.Anthropic:
                if (headers["x-claude-code-session-id"].ToString().Trim() is { Length: > 0 } header)
                    return Bound("cc:" + header);
                if (body["metadata"] is JsonObject meta && Str(meta, "user_id") is { } userId
                    && SessionIdOf(userId) is { Length: > 0 } session)
                    return Bound("cc:" + session);
                return Fingerprint(body);
            default:
                return null;
        }
    }

    /// <summary>The session id inside Claude Code's <c>metadata.user_id</c> (legacy string or CLI ≥ 2.1.78 JSON).</summary>
    public static string? SessionIdOf(string userId)
    {
        userId = userId.Trim();
        if (userId.Length == 0) return null;
        if (userId[0] == '{')
        {
            try
            {
                return JsonNode.Parse(userId) is JsonObject o ? Str(o, "session_id") : null;
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
        var match = LegacySessionPattern().Match(userId);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Last resort for clients without a session id: system prompt + first user message. Null when both are empty.</summary>
    private static string? Fingerprint(JsonObject body)
    {
        var system = TextOf(body["system"]);
        var first = "";
        if (body["messages"] is JsonArray messages)
            foreach (var message in messages)
                if (message is JsonObject m && Str(m, "role") == "user")
                {
                    first = TextOf(m["content"]);
                    break;
                }
        if (system.Length == 0 && first.Length == 0) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(system + "\n\u0000\n" + first));
        return "fp:" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    /// <summary>Plain text of a string or an array of content blocks (text blocks only).</summary>
    private static string TextOf(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue<string>(out var text):
                return text;
            case JsonArray blocks:
                var sb = new StringBuilder();
                foreach (var block in blocks)
                    if (block is JsonObject o && Str(o, "text") is { } t)
                        sb.Append(t);
                return sb.ToString();
            default:
                return "";
        }
    }

    private static string Bound(string key) =>
        key.Length <= MaxKeyLength
            ? key
            : "h:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
