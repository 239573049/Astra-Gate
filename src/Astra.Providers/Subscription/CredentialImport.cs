using System.Text.Json.Nodes;

namespace Astra.Providers.Subscription;

/// <summary>What one pasted entry of an account import turned out to be.</summary>
public static class CredentialKinds
{
    /// <summary>An OAuth refresh token: exchanged once at import, then refreshed like a login.</summary>
    public const string RefreshToken = "refresh_token";

    /// <summary>Claude <c>claude setup-token</c> output: a long-lived inference token with no refresh grant.</summary>
    public const string SetupToken = "setup_token";

    /// <summary>A static key the subscription's coding endpoint accepts (Kimi / Z.AI / BigModel Coding Plan).</summary>
    public const string ApiKey = "api_key";

    /// <summary>A GitHub token that is exchanged for Copilot tokens.</summary>
    public const string GitHubToken = "github_token";

    /// <summary>A short-lived Copilot token used as is (no GitHub token behind it, so it cannot be renewed).</summary>
    public const string CopilotToken = "copilot_token";

    /// <summary>Claude Code's <c>.credentials.json</c> (<c>claudeAiOauth</c> object).</summary>
    public const string ClaudeCredentials = "claude_credentials";

    /// <summary>Codex's <c>auth.json</c>.</summary>
    public const string CodexAuthJson = "codex_auth_json";

    /// <summary>A Grok Web SSO cookie: converted to Grok OAuth tokens through a device-code approval, never stored itself.</summary>
    public const string GrokSso = "grok_sso";
}

/// <summary>One classified import entry. <see cref="Secret"/> is the access token / key / refresh token by kind.</summary>
public sealed record ParsedCredential(
    string Kind, string Secret, string? RefreshToken = null, string? IdToken = null, DateTimeOffset? ExpiresAt = null,
    string? Plan = null);

/// <summary>Which pasted formats a subscription family accepts (ids the UI turns into hints).</summary>
/// <param name="ApiKey">The family's endpoint takes static API keys, so "add API key" is offered.</param>
/// <param name="Formats">Format ids, e.g. <c>claude-refresh-token</c>; empty = no token import at all.</param>
public sealed record CredentialImportFormats(bool ApiKey, IReadOnlyList<string> Formats);

/// <summary>
/// Parsing and classification for pasted subscription credentials (plan §5.4 account import). Pure functions: nothing
/// here talks to the network — the endpoint validates an entry by actually exchanging / probing it.
/// </summary>
public static class CredentialImport
{
    /// <summary>Formats per subscription family (catalog key); unknown families import nothing.</summary>
    public static CredentialImportFormats FormatsOf(string providerKey) => providerKey switch
    {
        "claude-subscription" => new(false, ["claude-refresh-token", "claude-setup-token", "claude-credentials-json"]),
        "openai-subscription" => new(false, ["codex-auth-json", "refresh-token"]),
        "grok-subscription" => new(false, ["refresh-token", "grok-sso"]),
        "kimi-subscription" => new(true, ["kimi-api-key", "refresh-token"]),
        "zcode-subscription" => new(true, ["zai-api-key"]),
        "bigmodel-subscription" => new(true, ["bigmodel-api-key"]),
        "github-copilot-subscription" => new(false, ["github-token", "copilot-token"]),
        _ => new(false, []),
    };

    /// <summary>
    /// Splits pasted text into entries. A JSON document (an object, or an array of objects / strings) is taken whole;
    /// otherwise one entry per line — a line holding a JSON object stays whole, other lines are split like
    /// <see cref="SplitTokens"/>.
    /// </summary>
    public static List<string> SplitEntries(string? text)
    {
        var entries = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return entries;
        var trimmed = text.Trim();
        if (trimmed[0] is '{' or '[')
        {
            try
            {
                switch (JsonNode.Parse(trimmed))
                {
                    case JsonObject o:
                        entries.Add(o.ToJsonString());
                        return entries;
                    case JsonArray a:
                        foreach (var item in a)
                        {
                            if (item is JsonObject obj) entries.Add(obj.ToJsonString());
                            else if (item is JsonValue v && v.TryGetValue<string>(out var s) && s.Trim().Length > 0) entries.Add(s.Trim());
                        }
                        return entries;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // Not one JSON document (e.g. one JSON object per line): fall through to the line split.
            }
        }
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith('{')) entries.Add(t);
            // Grok SSO 的 cookie 文本（sso=…; sso-rw=…）与组合行（email----password----…----sso）自带分隔符，整行是一条。
            else if (t.Length > 0 && t[0] != '#' && XaiSsoNormalizer.LooksLikeSso(t) && (t.Contains(XaiSsoNormalizer.ComboSeparator) || t.Contains('=')))
                entries.Add(t);
            else entries.AddRange(SplitTokens(t));
        }
        return entries;
    }

    /// <summary>
    /// Splits pasted text into tokens: one per line, also separated by commas / semicolons / whitespace. Blank lines
    /// and <c>#</c> comment lines are ignored, surrounding quotes are stripped. Order is kept; duplicates are kept too
    /// (the caller reports them).
    /// </summary>
    public static List<string> SplitTokens(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return tokens;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            foreach (var piece in trimmed.Split([',', ' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var cleaned = piece.Trim('"', '\'');
                // 直连的 Copilot 令牌（tid=…;exp=…;…）本身含分号，不能按分号拆。
                var parts = cleaned.StartsWith("tid=", StringComparison.Ordinal) || cleaned.StartsWith("exp=", StringComparison.Ordinal)
                    ? new[] { cleaned }
                    : cleaned.Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var token = part.Trim('"', '\'');
                    if (token.Length > 0) tokens.Add(token);
                }
            }
        }
        return tokens;
    }

    /// <summary>
    /// Classifies one entry for a family. <paramref name="asApiKey"/> forces the API-key reading (the "add API key"
    /// dialog). Returns null with a user-facing <paramref name="error"/> when the entry is not importable here.
    /// </summary>
    public static ParsedCredential? Classify(string providerKey, string entry, bool asApiKey, out string? error)
    {
        error = null;
        var formats = FormatsOf(providerKey);
        if (formats.Formats.Count == 0)
        {
            error = "该订阅不支持导入令牌，请使用浏览器登录";
            return null;
        }
        if (asApiKey)
        {
            if (!formats.ApiKey)
            {
                error = "该订阅不接受 API Key，请使用登录或导入令牌";
                return null;
            }
            return LooksOpaque(entry, 16) ? new ParsedCredential(CredentialKinds.ApiKey, entry) : Fail(out error, "这不是可识别的 API Key");
        }

        if (entry.StartsWith('{'))
            return ClassifyJson(providerKey, entry, out error);

        switch (providerKey)
        {
            case "claude-subscription":
                if (entry.StartsWith("sk-ant-ort01-", StringComparison.Ordinal))
                    return new ParsedCredential(CredentialKinds.RefreshToken, entry);
                if (entry.StartsWith("sk-ant-oat01-", StringComparison.Ordinal))
                    return new ParsedCredential(CredentialKinds.SetupToken, entry);
                if (entry.StartsWith("sk-ant-api", StringComparison.Ordinal))
                    return Fail(out error, "这是 Anthropic API Key（按量计费），请添加到 Anthropic 提供商，而不是 Claude 订阅");
                return Fail(out error, "这不是可识别的 Claude 令牌（refresh token 以 sk-ant-ort01- 开头，setup-token 以 sk-ant-oat01- 开头）");

            case "openai-subscription":
                if (entry.StartsWith("sk-", StringComparison.Ordinal))
                    return Fail(out error, "这是 OpenAI API Key（按量计费），不能用于 ChatGPT 订阅");
                return LooksOpaque(entry, 20)
                    ? new ParsedCredential(CredentialKinds.RefreshToken, entry)
                    : Fail(out error, "这不是可识别的 refresh token（也可以直接粘贴 ~/.codex/auth.json 的内容）");

            case "grok-subscription":
                if (entry.StartsWith("xai-", StringComparison.Ordinal))
                    return Fail(out error, "这是 xAI API Key（按量计费），请添加到 xAI 提供商，而不是 Grok 订阅");
                if (XaiSsoNormalizer.LooksLikeSso(entry))
                {
                    var sso = XaiSsoNormalizer.Normalize(entry);
                    return LooksOpaque(sso, 20)
                        ? new ParsedCredential(CredentialKinds.GrokSso, sso)
                        : Fail(out error, "没有在这一行里找到 Grok SSO（应为 sso cookie 的值，或 email----password----…----sso 组合行）");
                }
                return LooksOpaque(entry, 20)
                    ? new ParsedCredential(CredentialKinds.RefreshToken, entry)
                    : Fail(out error, "这不是可识别的 refresh token");

            case "kimi-subscription":
                // Coding Plan 的 Key 是 sk-kimi-…；其余 sk- 开头的也按 Key 试（上游会判断）。refresh token 是 JWT。
                if (entry.StartsWith("sk-", StringComparison.Ordinal))
                    return new ParsedCredential(CredentialKinds.ApiKey, entry);
                return LooksOpaque(entry, 20)
                    ? new ParsedCredential(CredentialKinds.RefreshToken, entry)
                    : Fail(out error, "这不是可识别的 Kimi 令牌（API Key 以 sk- 开头）");

            case "zcode-subscription":
            case "bigmodel-subscription":
                // GLM Coding Plan 只导入 API Key（形如 {id}.{secret}）；OAuth token 的业务换取链路不对外开放导入。
                return LooksOpaque(entry, 16)
                    ? new ParsedCredential(CredentialKinds.ApiKey, entry)
                    : Fail(out error, "这不是可识别的 API Key");

            case "github-copilot-subscription":
                if (LocalCopilotLogin.LooksLikeCopilotToken(entry))
                    return new ParsedCredential(CredentialKinds.CopilotToken, entry,
                        ExpiresAt: LocalCopilotLogin.CopilotTokenExpiry(entry), Plan: LocalCopilotLogin.CopilotTokenField(entry, "sku"));
                if (LocalCopilotLogin.LooksLikeGitHubToken(entry))
                    return new ParsedCredential(CredentialKinds.GitHubToken, entry);
                return Fail(out error,
                    "这不是可识别的令牌（GitHub 令牌应以 gho_ / ghp_ / github_pat_ 开头；直连的 Copilot 令牌应以 tid= 开头并带 exp=）");
        }
        return Fail(out error, "该订阅不支持导入令牌，请使用浏览器登录");
    }

    private static ParsedCredential? ClassifyJson(string providerKey, string entry, out string? error)
    {
        error = null;
        if (providerKey == "openai-subscription")
        {
            var codex = CodexAuthFile.Parse(entry, out var parseError);
            return codex is null
                ? Fail(out error, parseError ?? "这不是可用的 Codex auth.json")
                : new ParsedCredential(CredentialKinds.CodexAuthJson, codex.AccessToken, codex.RefreshToken, codex.IdToken);
        }
        if (providerKey == "claude-subscription")
        {
            JsonObject? root;
            try
            {
                root = JsonNode.Parse(entry) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                return Fail(out error, "JSON 格式不正确");
            }
            // Claude Code 的 .credentials.json：{"claudeAiOauth":{accessToken, refreshToken, expiresAt(ms), subscriptionType}}；
            // 也接受直接粘贴里面那一层。
            var oauth = root?["claudeAiOauth"] as JsonObject ?? root;
            var access = Str(oauth, "accessToken");
            var refresh = Str(oauth, "refreshToken");
            if (access is null && refresh is null)
                return Fail(out error, "没有在 JSON 里找到 claudeAiOauth.accessToken / refreshToken");
            DateTimeOffset? expires = oauth?["expiresAt"] is JsonValue v && v.TryGetValue<long>(out var ms) && ms > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;
            return refresh is null
                ? new ParsedCredential(CredentialKinds.SetupToken, access!, ExpiresAt: expires, Plan: Str(oauth, "subscriptionType"))
                : new ParsedCredential(CredentialKinds.ClaudeCredentials, access ?? "", refresh, ExpiresAt: expires,
                    Plan: Str(oauth, "subscriptionType"));
        }
        return Fail(out error, "该订阅不支持导入 JSON 凭据");
    }

    private static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>A single opaque token: long enough and free of whitespace / control characters.</summary>
    private static bool LooksOpaque(string entry, int minLength) =>
        entry.Length >= minLength && !entry.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private static ParsedCredential? Fail(out string? error, string message)
    {
        error = message;
        return null;
    }
}
