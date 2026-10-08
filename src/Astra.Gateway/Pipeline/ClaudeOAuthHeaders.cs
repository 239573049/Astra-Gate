using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Outbound header policy for the Claude subscription (OAuth) upstream. Two modes:
/// <list type="bullet">
/// <item><b>Relay</b> — the caller is Claude Code itself: its own headers are forwarded verbatim (allow-list below),
/// only the credential is swapped and the OAuth beta is merged into <c>anthropic-beta</c> (Claude Code talking to a
/// gateway through <c>ANTHROPIC_AUTH_TOKEN</c> does not send it, but OAuth tokens are rejected without it).</item>
/// <item><b>Compat</b> — any other client, only reachable once the user lifts the "Claude Code only" policy: the
/// minimum the OAuth upstream needs (<c>claude-code</c> + <c>oauth</c> betas). Nothing else is impersonated, so
/// Anthropic may still bill it as third-party usage or reject it.</item>
/// </list>
/// Header choices follow sub2api (<c>allowedHeaders</c>, <c>getBetaHeader</c> in its gateway service).
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

    /// <summary>Client headers copied verbatim on the relay path (plus every <c>x-stainless-*</c>).</summary>
    private static readonly string[] RelayedHeaders =
    [
        "User-Agent", "Accept", "Accept-Language", "x-app", "anthropic-version",
        "anthropic-dangerous-direct-browser-access", "x-claude-code-session-id", "x-client-request-id",
    ];

    /// <summary>Upstream response headers handed back to Claude Code on the relay path (it reads its usage warnings from them).</summary>
    public static bool IsRelayedResponseHeader(string name) =>
        name.StartsWith("anthropic-ratelimit-", StringComparison.OrdinalIgnoreCase)
        || name.Equals("request-id", StringComparison.OrdinalIgnoreCase)
        || name.Equals("retry-after", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The client's <c>anthropic-beta</c> with the OAuth beta guaranteed: inserted right after <c>claude-code-…</c>, or
    /// first when that is absent. No client value → the CLI defaults for the model.
    /// </summary>
    public static string MergeRelayBeta(string? clientBeta, string model)
    {
        var parts = Split(clientBeta);
        if (parts.Count == 0)
            return model.Contains("haiku", StringComparison.OrdinalIgnoreCase) ? HaikuBeta : DefaultBeta;
        if (parts.Contains(BetaOAuth)) return string.Join(",", parts);
        var claudeCode = parts.IndexOf(BetaClaudeCode);
        parts.Insert(claudeCode >= 0 ? claudeCode + 1 : 0, BetaOAuth);
        return string.Join(",", parts);
    }

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
