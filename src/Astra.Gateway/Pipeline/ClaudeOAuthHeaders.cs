using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Outbound header policy for the Claude subscription (OAuth) upstream. Two modes:
/// <list type="bullet">
/// <item><b>Relay</b> — the caller is Claude Code itself: its own headers are forwarded verbatim (allow-list below),
/// only the credential is swapped and <c>anthropic-beta</c> is merged over the subscription floor
/// (<see cref="RelayRequiredBetas"/>): Claude Code talking to a gateway through
/// <c>ANTHROPIC_AUTH_TOKEN</c> sends a smaller beta set than it does with its own OAuth login, and
/// relaying that verbatim is rejected ("Request not allowed").</item>
/// <item><b>Compat</b> — any other client, only reachable once the user lifts the "Claude Code only" policy: the
/// minimum the OAuth upstream needs (<c>claude-code</c> + <c>oauth</c> betas). Nothing else is impersonated, so
/// Anthropic may still bill it as third-party usage or reject it.</item>
/// </list>
/// Header choices follow sub2api (<c>allowedHeaders</c>, <c>getBetaHeader</c>/<c>mergeAnthropicBeta</c> in its
/// gateway service).
/// </summary>
public static class ClaudeOAuthHeaders
{
    public const string BetaOAuth = "oauth-2025-04-20";
    public const string BetaClaudeCode = "claude-code-20250219";
    public const string BetaInterleavedThinking = "interleaved-thinking-2025-05-14";
    public const string BetaFineGrainedToolStreaming = "fine-grained-tool-streaming-2025-05-14";

    /// <summary>Default <c>anthropic-beta</c> when Claude Code sent none (non-Haiku models).</summary>
    public const string DefaultBeta = BetaClaudeCode + "," + BetaOAuth + "," + BetaInterleavedThinking + "," + BetaFineGrainedToolStreaming;

    /// <summary>Default <c>anthropic-beta</c> for Haiku when Claude Code sent none (no claude-code beta, like the CLI).</summary>
    public const string HaikuBeta = BetaOAuth + "," + BetaInterleavedThinking;

    /// <summary>
    /// Client headers copied verbatim on the relay path (plus every <c>x-stainless-*</c>).
    /// Matches sub2api's <c>allowedHeaders</c> (gateway_service.go) — forwarding MORE headers here
    /// would leak values that contradict the caller's identity, forwarding FEWER would strip
    /// something the real CLI sends (<c>sec-fetch-mode</c> / <c>accept-encoding</c> included).
    /// </summary>
    private static readonly string[] RelayedHeaders =
    [
        "Accept", "Accept-Language", "Accept-Encoding", "sec-fetch-mode", "x-app",
        "anthropic-version", "anthropic-dangerous-direct-browser-access",
        "x-claude-code-session-id", "x-client-request-id",
    ];

    /// <summary>The beta the real CLI attaches specifically to <c>/v1/messages/count_tokens</c> traffic.</summary>
    public const string BetaTokenCounting = "token-counting-2024-11-01";

    /// <summary>
    /// count_tokens relay (sub2api computeFinalCountTokensAnthropicBeta): the token-counting beta
    /// must be present — appended when the caller (Claude Code) didn't send it; a caller that sent
    /// no beta at all gets the CLI's count_tokens default.
    /// </summary>
    public static void EnsureCountTokensBeta(HttpRequestMessage request)
    {
        var existing = request.Headers.TryGetValues("anthropic-beta", out var values) ? string.Join(",", values) : "";
        if (existing.Length == 0)
        {
            Set(request.Headers, "anthropic-beta", BetaClaudeCode + "," + BetaOAuth + "," + BetaInterleavedThinking + "," + BetaTokenCounting);
            return;
        }
        var parts = Split(existing);
        if (!parts.Contains(BetaTokenCounting)) parts.Add(BetaTokenCounting);
        Set(request.Headers, "anthropic-beta", string.Join(",", parts));
    }

    /// <summary>Upstream response headers handed back to Claude Code on the relay path (it reads its usage warnings from them).</summary>
    public static bool IsRelayedResponseHeader(string name) =>
        name.StartsWith("anthropic-ratelimit-", StringComparison.OrdinalIgnoreCase)
        || name.Equals("request-id", StringComparison.OrdinalIgnoreCase)
        || name.Equals("retry-after", StringComparison.OrdinalIgnoreCase);

    /// <summary>The beta Claude Code attaches only when it authenticates with its own subscription (OAuth) login.</summary>
    public const string BetaExtendedCacheTtl = "extended-cache-ttl-2025-04-11";

    /// <summary>
    /// Relay (a real Claude Code through <c>ANTHROPIC_AUTH_TOKEN</c>): converts the CLI's auth-token-mode
    /// <c>anthropic-beta</c> into what the same CLI sends with its own OAuth login. Verified by capturing the raw
    /// requests of <c>claude-cli/2.1.281</c> in both modes for haiku-4-5 / sonnet-5 / opus-5-5: the subscription
    /// set is the auth-token set <b>plus exactly two tokens</b> — <c>oauth-2025-04-20</c> (right after
    /// <c>claude-code-…</c>) and <c>extended-cache-ttl-2025-04-11</c> (last); everything else is identical, and the
    /// set itself varies per model (8 / 10 / 12 tokens), so it must NOT be a fixed list. The client's betas are
    /// kept as the CLI chose them. Pairs with <see cref="PromoteCacheControlTtl"/> — the body's cache blocks gain
    /// the 1h ttl that this beta unlocks; a header without the matching body (or vice versa) is rejected.
    /// </summary>
    public static string MergeRelayBeta(string? clientBeta, string model)
    {
        var parts = Split(clientBeta);
        if (parts.Count == 0)
            parts = Split(model.Contains("haiku", StringComparison.OrdinalIgnoreCase) ? HaikuBeta : DefaultBeta);
        if (!parts.Contains(BetaOAuth))
        {
            var claudeCode = parts.IndexOf(BetaClaudeCode);
            parts.Insert(claudeCode >= 0 ? claudeCode + 1 : 0, BetaOAuth);
        }
        if (!parts.Contains(BetaExtendedCacheTtl)) parts.Add(BetaExtendedCacheTtl);
        return string.Join(",", parts);
    }

    /// <summary>
    /// Relay body counterpart of <see cref="MergeRelayBeta"/>: in subscription mode Claude Code's
    /// <c>cache_control</c> blocks are <c>{"type":"ephemeral","ttl":"1h"}</c>; in auth-token mode they are
    /// <c>{"type":"ephemeral"}</c> (captured: system[1], system[2] and the last message block differ in exactly
    /// this way, nothing else in the body does). Adds <c>ttl:"1h"</c> to every ephemeral block that has none.
    /// Returns the input untouched when it is not a JSON object or nothing needs to change.
    /// </summary>
    public static string PromoteCacheControlTtl(string body)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(body) is not System.Text.Json.Nodes.JsonObject root) return body;
            var changed = false;
            Promote(root, ref changed);
            return changed ? root.ToJsonString() : body;
        }
        catch (Exception)
        {
            return body; // best effort: never drop a request because the rewrite failed
        }

        static void Promote(System.Text.Json.Nodes.JsonNode? node, ref bool changed)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject obj:
                    if (obj["cache_control"] is System.Text.Json.Nodes.JsonObject cc
                        && cc["type"]?.GetValue<string>() == "ephemeral" && cc["ttl"] is null)
                    {
                        cc["ttl"] = "1h";
                        changed = true;
                    }
                    foreach (var (_, child) in obj.ToList()) Promote(child, ref changed);
                    break;
                case System.Text.Json.Nodes.JsonArray arr:
                    foreach (var child in arr) Promote(child, ref changed);
                    break;
            }
        }
    }

    /// <summary>
    /// Betas for the Claude Code <i>mimic</i> identity (a non-Claude-Code client): the 12 tokens a real
    /// <c>claude-cli/2.1.281</c> sends with its OAuth login on an effort-capable model (opus-5-5 capture).
    /// </summary>
    public const string RelayBetaFloor =
        "claude-code-20250219,oauth-2025-04-20,interleaved-thinking-2025-05-14,thinking-token-count-2026-05-13," +
        "context-management-2025-06-27,prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07," +
        "per-turn-control-2026-07-01,mid-conversation-tool-changes-2026-07-01,advisor-tool-2026-03-01," +
        "effort-2025-11-24,extended-cache-ttl-2025-04-11";

    /// <summary><see cref="RelayBetaFloor"/> as tokens, in order.</summary>
    public static readonly string[] RelayRequiredBetas = RelayBetaFloor.Split(',');

    /// <summary>Compat path: the required betas first, then whatever the client asked for (deduplicated).</summary>
    public static string MergeCompatBeta(string? clientBeta)
    {
        var merged = new List<string> { BetaClaudeCode, BetaOAuth, BetaInterleavedThinking };
        foreach (var part in Split(clientBeta))
            if (!merged.Contains(part)) merged.Add(part);
        return string.Join(",", merged);
    }

    /// <summary>
    /// Relay: copies the allow-listed client headers onto <paramref name="request"/> (replacing what is there), merges the
    /// OAuth beta and fills <c>anthropic-version</c> / provider extra headers only when the client left them out.
    /// Credentials are applied afterwards by the caller; the client's own <c>x-api-key</c> / <c>Authorization</c>
    /// (the Astra token) are never copied.
    /// </summary>
    public static void ApplyRelay(HttpRequestMessage request, IHeaderDictionary client, string model,
        IReadOnlyDictionary<string, string> providerExtraHeaders, string defaultAnthropicVersion)
    {
        foreach (var name in RelayedHeaders)
            Copy(request.Headers, client, name);
        // User-Agent 必须是**单个规范值**：头集合会把 "claude-cli/x (external, cli)" 按逗号拆散
        // 再重拼（实测变成 "claude-cli/2.1.281, (external, cli)"），上游据此判定非 CLI 请求。
        // 按客户端自己的版本重建规范串，与真实 CLI 逐字一致。
        var clientUa = client["User-Agent"].ToString();
        SetUserAgent(request,
            "claude-cli/" + (ClaudeCodeMimicry.VersionFromUserAgent(clientUa) ?? ClaudeCodeMimicry.CliVersion)
            + " (external, cli)");
        foreach (var (name, value) in client)
            if (name.StartsWith("x-stainless-", StringComparison.OrdinalIgnoreCase))
                Set(request.Headers, name, value.ToString());

        Set(request.Headers, "anthropic-beta", MergeRelayBeta(client["anthropic-beta"].ToString(), model));
        if (!request.Headers.Contains("anthropic-version")) Set(request.Headers, "anthropic-version", defaultAnthropicVersion);
        if (!request.Headers.Contains("Accept")) Set(request.Headers, "Accept", "application/json");
        foreach (var (name, value) in providerExtraHeaders)
            if (!request.Headers.Contains(name))
                request.Headers.TryAddWithoutValidation(name, value);

        // Claude Code sends a bare application/json; keep the wire identical (no charset parameter).
        if (request.Content is not null) request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    /// <summary>Compat: makes sure the OAuth upstream sees the betas it requires; everything else stays as built.</summary>
    public static void ApplyCompat(HttpRequestMessage request, IHeaderDictionary client)
    {
        var existing = request.Headers.TryGetValues("anthropic-beta", out var values) ? string.Join(",", values) : client["anthropic-beta"].ToString();
        Set(request.Headers, "anthropic-beta", MergeCompatBeta(existing));
    }

    private static void Copy(HttpRequestHeaders target, IHeaderDictionary source, string name)
    {
        var value = source[name].ToString();
        if (value.Length > 0) Set(target, name, value);
    }

    /// <summary>
    /// 把 User-Agent 设成单个 product token（<c>claude-cli/&lt;version&gt;</c>）。
    /// .NET 的 <see cref="ProductInfoHeaderValue"/> 只接受单独 product 或单独 comment——
    /// 真实 CLI 的组合形态 <c>claude-cli/x.y.z (external, cli)</c> 无法表示：<c>TryParse</c> 直接
    /// false，<c>TryAddWithoutValidation</c> 会拆成两个 token 并用逗号连接上线（实测
    /// <c>claude-cli/2.1.281,(external, cli)</c>），上游据此判定非 CLI。因此只发 product 部分；
    /// 与真实 CLI 的差别仅是缺 "(external, cli)" 注释。
    /// </summary>
    public static void SetUserAgent(HttpRequestMessage request, string userAgent)
    {
        request.Headers.UserAgent.Clear();
        var version = ClaudeCodeMimicry.VersionFromUserAgent(userAgent) ?? ClaudeCodeMimicry.CliVersion;
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("claude-cli", version));
    }

    private static void Set(HttpRequestHeaders headers, string name, string value)
    {
        headers.Remove(name);
        headers.TryAddWithoutValidation(name, value);
    }

    private static List<string> Split(string? header)
    {
        var parts = new List<string>();
        if (string.IsNullOrWhiteSpace(header)) return parts;
        foreach (var raw in header.Split(','))
        {
            var part = raw.Trim();
            if (part.Length > 0 && !parts.Contains(part)) parts.Add(part);
        }
        return parts;
    }
}
