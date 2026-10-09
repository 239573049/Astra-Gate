using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// Presents a Claude Code identity for a Claude subscription (OAuth) upstream when the caller is
/// <b>not</b> Claude Code itself. Anthropic's subscription terms tie the OAuth token to the
/// Claude Code / Claude Desktop clients: non-Haiku requests without a Claude Code identity fail
/// (Haiku is exempt). When the user turns on <c>subscription.mimic_claude_code</c>, Astra applies
/// the identity the real CLI presents, aligned with sub2api (<c>internal/pkg/claude/constants.go</c>,
/// 2026-04 traffic capture; routin-fax <c>AnthropicService.cs</c> is the same derivation):
/// <list type="bullet">
/// <item>headers: pinned <c>claude-cli</c> User-Agent, the Anthropic-SDK <c>X-Stainless-*</c> runtime
/// fingerprint (host profile stable per account), <c>X-App: cli</c>, the full beta set, and no
/// client headers on top;</item>
/// <item>body: the canonical 3-block <c>system</c> (billing-attribution block with the cc
/// fingerprint, the "You are Claude Code" identity block, and an expansion block with a cache
/// breakpoint), the caller's original system prompt moved into the message history, and the
/// Claude-Code-shaped <c>metadata.user_id</c>.</item>
/// </list>
/// This is deliberate spoofing of Anthropic's client check so a subscription can serve the user's
/// own non-CLI clients; it is off by default and it may violate Anthropic's terms — the account
/// bears that risk (the UI says so where the switch lives).
/// </summary>
public static class ClaudeCodeMimicry
{
    /// <summary>The CLI version the identity presents. Kept in lockstep with the UA and billing block.</summary>
    public const string CliVersion = "2.1.281";

    /// <summary>Pinned claude-cli User-Agent (the shape of real CLI traffic).</summary>
    public const string UserAgent = $"claude-cli/{CliVersion} (external, cli)";

    /// <summary>
    /// Full beta set an OAuth request must carry to be treated as genuine Claude Code
    /// (order matches the real CLI capture; sub2api FullClaudeCodeMimicryBetas).
    /// </summary>
    public static readonly string[] Betas = ClaudeOAuthHeaders.RelayRequiredBetas;

    /// <summary>
    /// Salt for the cc_version fingerprint suffix, from real CLI traffic
    /// (sub2api gateway_billing_block.go fingerprintSalt; must not change).
    /// </summary>
    private const string FingerprintSalt = "59cf53e54c78";

    /// <summary>The "You are Claude Code" identity prompt, verbatim from the real CLI.</summary>
    private const string IdentityPrompt = "You are Claude Code, Anthropic's official CLI for Claude.";

    /// <summary>
    /// Tool-agnostic slice of the real CLI system prompt (sub2api claudeCodeSystemPromptExpansion),
    /// so the synthesized payload's block sizes resemble genuine traffic.
    /// </summary>
    private const string ExpansionPrompt = """
        You are an interactive agent that helps users with software engineering tasks. Use the instructions below and the tools available to you to assist the user.

        IMPORTANT: Assist with authorized security testing, defensive security, CTF challenges, and educational contexts. Refuse requests for destructive techniques, DoS attacks, mass targeting, supply chain compromise, or detection evasion for malicious purposes. Dual-use security tools (C2 frameworks, credential testing, exploit development) require clear authorization context: pentesting engagements, CTF competitions, security research, or defensive use cases.
        IMPORTANT: You must NEVER generate or guess URLs for the user unless you are confident that the URLs are for helping the user with programming. You may use URLs provided by the user in their messages or local files.
        """;

    /// <summary>Anthropic-SDK runtime headers that are identical on every real CLI install.</summary>
    private static readonly (string Name, string Value)[] StainlessBase =
    [
        ("X-Stainless-Lang", "js"),
        ("X-Stainless-Package-Version", "0.94.0"),
        ("X-Stainless-Runtime", "node"),
        ("X-Stainless-Retry-Count", "0"),
        ("X-Stainless-Timeout", "600"),
        ("X-App", "cli"),
        ("Anthropic-Dangerous-Direct-Browser-Access", "true"),
    ];

    /// <summary>A coherent (OS, arch, node) triple of a plausible CLI host; the pool spreads across them.</summary>
    private static readonly (string Os, string Arch, string RuntimeVersion)[] HostProfiles =
    [
        ("MacOS", "arm64", "v22.14.0"), ("MacOS", "arm64", "v24.3.0"), ("MacOS", "arm64", "v20.18.1"),
        ("MacOS", "x64", "v22.11.0"), ("MacOS", "x64", "v20.18.1"),
        ("Linux", "x64", "v22.14.0"), ("Linux", "x64", "v20.18.1"), ("Linux", "arm64", "v24.3.0"),
        ("Windows", "x64", "v22.11.0"), ("Windows", "x64", "v20.18.1"),
    ];

    /// <summary>
    /// Applies the header identity: pinned UA, the per-account X-Stainless fingerprint, X-App,
    /// the full beta set and the CLI's anthropic-version. Anything the caller sent for these is
    /// replaced — a stray client header that contradicts the injected identity is itself a tell.
    /// </summary>
    public static void ApplyHeaders(HttpRequestMessage request, string accountId, string? clientSessionId)
    {
        foreach (var (name, value) in StainlessBase) Set(request.Headers, name, value);
        var profile = HostProfiles[StableIndex(DeviceId(accountId), HostProfiles.Length)];
        Set(request.Headers, "X-Stainless-OS", profile.Os);
        Set(request.Headers, "X-Stainless-Arch", profile.Arch);
        Set(request.Headers, "X-Stainless-Runtime-Version", profile.RuntimeVersion);
        ClaudeOAuthHeaders.SetUserAgent(request, UserAgent);
        Set(request.Headers, "anthropic-beta", string.Join(',', Betas));
        Set(request.Headers, "anthropic-version", "2023-06-01");
        Set(request.Headers, "Accept-Language", "en-US,en;q=0.9");
        if (!string.IsNullOrWhiteSpace(clientSessionId))
            Set(request.Headers, "x-claude-code-session-id", clientSessionId.Trim());
    }

    /// <summary>
    /// Rewrites the body into the Claude Code shape: canonical 3-block system (billing attribution
    /// with a fresh cc fingerprint, identity, expansion with a cache breakpoint), the caller's
    /// original system prompt moved into the message history, and the JSON-form
    /// <c>metadata.user_id</c>. Returns the input unchanged when it is not a JSON object.
    /// <paramref name="cliVersion"/> overrides the version stamped into the billing block and
    /// fingerprint — the relay passes the caller's own <c>claude-cli/&lt;version&gt;</c> so the block
    /// stays consistent with the forwarded User-Agent (sub2api syncBillingHeaderVersion).
    /// </summary>
    public static string ApplyBody(string upstreamBody, string accountId, string? clientSessionId, string? cliVersion = null)
    {
        try
        {
            if (JsonNode.Parse(upstreamBody) is not JsonObject root) return upstreamBody;

            var version = VersionFromUserAgent(cliVersion) ?? CliVersion;
            var firstUserText = FirstUserText(root);
            var fingerprint = CcFingerprint(firstUserText, version);
            var originalSystem = SystemText(root["system"]);

            root["system"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = $"x-anthropic-billing-header: cc_version={version}.{fingerprint}; cc_entrypoint=cli;",
                },
                new JsonObject { ["type"] = "text", ["text"] = IdentityPrompt },
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = ExpansionPrompt,
                    ["cache_control"] = new JsonObject { ["type"] = "ephemeral", ["ttl"] = "5m" },
                });

            // The caller's own instructions survive as a leading exchange instead of a system block.
            if (!string.IsNullOrWhiteSpace(originalSystem)
                && !originalSystem.StartsWith(IdentityPrompt, StringComparison.OrdinalIgnoreCase)
                && root["messages"] is JsonArray messages)
            {
                messages.Insert(0, new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Understood. I will follow these instructions." }),
                });
                messages.Insert(0, new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "[System Instructions]\n" + originalSystem }),
                });
            }

            // New-format metadata (CLI ≥ 2.1.78): user_id is a JSON string. account_uuid stays
            // empty unless it is actually known — a wrong uuid is worse than none.
            var sessionId = !string.IsNullOrWhiteSpace(clientSessionId)
                ? clientSessionId.Trim()
                : SessionUuid($"{accountId}::{DeviceId(accountId)}::{firstUserText}");
            root["metadata"] = new JsonObject
            {
                ["user_id"] = new JsonObject
                {
                    ["device_id"] = DeviceId(accountId),
                    ["account_uuid"] = "",
                    ["session_id"] = sessionId,
                }.ToJsonString().Replace("\r", "").Replace("\n", ""),
            };
            return root.ToJsonString();
        }
        catch (Exception)
        {
            // 身份是尽力而为的包装：解析不了就按原样发，不能因为包装失败把请求丢掉。
            return upstreamBody;
        }
    }

    /// <summary>
    /// The 3-char cc fingerprint suffix: sha256(salt + three bytes of the first user message +
    /// CLI version), hex, truncated — the exact derivation real CLI traffic carries
    /// (sub2api gateway_billing_block.go).
    /// </summary>
    public static string CcFingerprint(string firstUserText, string? cliVersion = null)
    {
        var version = VersionFromUserAgent(cliVersion) ?? CliVersion;
        var textBytes = Encoding.UTF8.GetBytes(firstUserText);
        ReadOnlySpan<int> indices = [4, 7, 20];
        var chars = new byte[indices.Length];
        for (var i = 0; i < indices.Length; i++)
            chars[i] = indices[i] < textBytes.Length ? textBytes[indices[i]] : (byte)'0';
        var input = Encoding.UTF8.GetBytes(FingerprintSalt).Concat(chars).Concat(Encoding.UTF8.GetBytes(version)).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(input))[..3];
    }

    /// <summary>The three-part version in a <c>claude-cli/&lt;version&gt; …</c> User-Agent; null otherwise.</summary>
    public static string? VersionFromUserAgent(string? userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(userAgent, @"claude-cli/(\d+\.\d+\.\d+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>A stable per-account device id (64-char lowercase hex) — never changes for an account.</summary>
    public static string DeviceId(string accountId) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("astra-claude-device::" + accountId)));

    /// <summary>Deterministic UUID (v4-shaped) so one conversation keeps one session id across turns.</summary>
    public static string SessionUuid(string seed)
    {
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(seed))[..16];
        b[6] = (byte)((b[6] & 0x0f) | 0x40);
        b[8] = (byte)((b[8] & 0x3f) | 0x80);
        return $"{Convert.ToHexStringLower(b, 0, 4)}-{Convert.ToHexStringLower(b, 4, 2)}-{Convert.ToHexStringLower(b, 6, 2)}-{Convert.ToHexStringLower(b, 8, 2)}-{Convert.ToHexStringLower(b, 10, 6)}";
    }

    private static string FirstUserText(JsonObject root)
    {
        if (root["messages"] is not JsonArray messages) return "";
        foreach (var msg in messages)
        {
            if (msg is not JsonObject mo || mo["role"]?.GetValue<string>() != "user") continue;
            var text = mo["content"] switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonArray arr => arr.OfType<JsonObject>()
                    .Select(b => b["text"]?.GetValue<string>())
                    .FirstOrDefault(t => !string.IsNullOrEmpty(t)),
                _ => null,
            };
            if (!string.IsNullOrEmpty(text)) return text.Length > 512 ? text[..512] : text;
        }
        return "";
    }

    private static string SystemText(JsonNode? system) => system switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonArray arr => string.Join("\n\n", arr.OfType<JsonObject>()
            .Select(b => b["text"]?.GetValue<string>())
            .Where(t => !string.IsNullOrEmpty(t))),
        _ => "",
    };

    /// <summary>Stable index in [0, modulo) from a seed, so one account always lands on the same host profile.</summary>
    private static int StableIndex(string seed, int modulo)
    {
        if (modulo <= 1) return 0;
        var value = BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes("cc-hostprofile::" + seed)), 0);
        return (int)(value % (uint)modulo);
    }

    private static void Set(HttpRequestHeaders headers, string name, string value)
    {
        headers.Remove(name);
        headers.TryAddWithoutValidation(name, value);
    }
}
