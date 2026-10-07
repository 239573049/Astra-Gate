using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core.Models;

namespace Astra.Providers.Subscription;

/// <summary>
/// OAuth endpoints for one subscription family. Shipping values come from public reverse-engineering
/// (cc-switch / AstrLink) and are marked <see cref="Verified"/>=false until validated against the live
/// services (plan §15). A provider instance can override any field via
/// <c>providers.settings_json.subscription_oauth</c>.
/// </summary>
public sealed class SubscriptionOAuthConfig
{
    /// <summary>Catalog key: "claude" | "openai" | "grok".</summary>
    public required string ProviderKey { get; init; }
    public string DisplayName { get; init; } = "";
    public string AuthorizeUrl { get; init; } = "";
    public string TokenUrl { get; init; } = "";
    public string DeviceCodeUrl { get; init; } = "";
    public string ClientId { get; init; } = "";
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>true = authorization-code flow with PKCE; false = device-code flow.</summary>
    public bool UsePkce { get; init; } = true;

    /// <summary>
    /// 流程形态。空 = 标准 OAuth（PKCE 授权码或设备码）；"zcode" = ZCode 的非标准
    /// 授权码链路：无 PKCE、无 scope，换码是 JSON body <c>{provider, code, redirect_uri,
    /// state}</c>（没有 grant_type），换到 OAuth token 后还要跑一跳业务登录换取真正
    /// 的调用令牌，刷新 = 用存下的 OAuth token 重跑业务登录。来源：NextCoWork
    /// `issuers/zcode.ts`（逆向 Vibe Coding Labs《ZCode RE》 + ZCode.app v3.11.2）。
    /// </summary>
    public string Style { get; init; } = "";

    /// <summary>
    /// ZCode 形态的第四跳：把 OAuth access_token 换成真正发请求用的令牌（Z.AI 渠道
    /// 返回业务 JWT）。空 = 没有这一跳。刷新时重跑这里，因此令牌的刷新槽里存的是
    /// OAuth token 本身。
    /// </summary>
    public string BusinessLoginUrl { get; init; } = "";

    /// <summary>可选的身份端点（登录后拉 email / 套餐名做展示）。失败不致命。</summary>
    public string IdentityUrl { get; init; } = "";

    /// <summary>换码 / 刷新 / 设备码 / 身份请求要带的固定头（客户端身份，如 kimi 的 X-Msh-*、ZCode 的 UA）。</summary>
    public IReadOnlyDictionary<string, string> ExtraHeaders { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Only verified configurations may start a login (plan: 核实后才开放).</summary>
    public bool Verified { get; init; }

    /// <summary>Extra fixed query parameters for the authorize URL (provider quirks).</summary>
    public IReadOnlyDictionary<string, string> ExtraAuthorizeParams { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Extra fixed form fields for the device-code request (provider quirks, e.g. referrer).</summary>
    public IReadOnlyDictionary<string, string> ExtraDeviceParams { get; init; } =
        new Dictionary<string, string>();
}

public static class SubscriptionCatalog
{
    public static readonly IReadOnlyList<SubscriptionOAuthConfig> All =
    [
        new SubscriptionOAuthConfig
        {
            // ProviderKey 与订阅模板 id 对齐（Templates/catalog.json: claude-subscription）。
            ProviderKey = "claude-subscription",
            DisplayName = "Claude 订阅（Pro/Max）",
            AuthorizeUrl = "https://claude.ai/oauth/authorize",
            TokenUrl = "https://claude.ai/v1/oauth/token",
            ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e",
            Scopes = ["org:create_api_key", "user:profile", "user:inference"],
            UsePkce = true,
            // 目录默认不开放（plan §15：核实前不登录）。claude-subscription 模板通过
            // settings.subscription_oauth.verified=true 显式开放——端点与 client_id 来自 cc-switch
            // 的公开配置，已在开发环境验证过完整登录 + 刷新链路。
            Verified = false,
        },
        new SubscriptionOAuthConfig
        {
            ProviderKey = "openai-subscription",
            DisplayName = "ChatGPT 订阅（Codex）",
            AuthorizeUrl = "https://auth.openai.com/oauth/authorize",
            TokenUrl = "https://auth.openai.com/oauth/token",
            // 公开逆向值（openai/codex codex-rs/login/src/auth/manager.rs 的 CLIENT_ID）。
            ClientId = "app_EMoamEEZ73f0CkXaXp7hrann",
            Scopes = ["openid", "profile", "email", "offline_access"],
            UsePkce = true,
            Verified = false,
        },
        new SubscriptionOAuthConfig
        {
            ProviderKey = "grok-subscription",
            DisplayName = "Grok 订阅",
            // 公开逆向值（xai-org/grok-build 官方源码 xai-grok-login：issuer auth.x.ai +
            // /oauth2/device/code、/oauth2/token；推理走 cli-chat-proxy.grok.com/v1 的 Responses）。
            TokenUrl = "https://auth.x.ai/oauth2/token",
            DeviceCodeUrl = "https://auth.x.ai/oauth2/device/code",
            ClientId = "b1a00492-073a-47ea-816f-4c329264a828",
            // FROZEN client contract：grok-build 固定请求的 10 个 scope（config.rs default_oauth2_scopes）。
            Scopes =
            [
                "openid", "profile", "email", "offline_access", "grok-cli:access", "api:access",
                "conversations:read", "conversations:write", "workspaces:read", "workspaces:write",
            ],
            UsePkce = false,
            ExtraDeviceParams = new Dictionary<string, string> { ["referrer"] = "grok-build" },
            Verified = false,
        },
        new SubscriptionOAuthConfig
        {
            ProviderKey = "kimi-subscription",
            DisplayName = "Kimi 订阅（Coding Plan）",
            // 官方一方源码值（@moonshot-ai/kimi-code v0.42.0 的 packages/oauth，非逆向）：
            // 设备码端点 device_authorization、token 端点 /api/oauth/token、公开 client id。
            // 推理走 api.kimi.com/coding/v1（openai-chat）。大陆区 auth.kimi.com；全球区
            // 是 auth.kimi.ai 且 client_id 相同——要支持时另开 ProviderKey，不要换 host。
            TokenUrl = "https://auth.kimi.com/api/oauth/token",
            DeviceCodeUrl = "https://auth.kimi.com/api/oauth/device_authorization",
            ClientId = "17e5f671-d194-4dfb-9706-5516cb48c098",
            Scopes = [],
            UsePkce = false,
            // kimi-code 对每个上游请求都带 X-Msh-* 设备头（含设备码申请、换码、刷新）。
            ExtraHeaders = KimiDeviceHeaders(),
            IdentityUrl = "https://api.kimi.com/coding/v1/me", // user_id / email / user_level_name
            Verified = false,
        },
        new SubscriptionOAuthConfig
        {
            ProviderKey = "zcode-subscription",
            DisplayName = "ZCode 订阅（GLM Coding Plan · Z.AI）",
            // 公开逆向值（NextCoWork issuers/zcode-zai.ts：Vibe Coding Labs《ZCode RE》 +
            // ZCode.app v3.11.2 逆向，Z.AI 渠道是被实测走通的那条）。换码/业务登录都要求
            // ZCode UA 与 referer 头；redirect_uri 在 authorize 阶段不校验，换码发真实回调。
            Style = "zcode",
            AuthorizeUrl = "https://chat.z.ai/api/oauth/authorize",
            TokenUrl = "https://zcode.z.ai/api/v1/oauth/token",
            ClientId = "client_P8X5CMWmlaRO9gyO-KSqtg",
            Scopes = [],
            UsePkce = false,
            BusinessLoginUrl = "https://api.z.ai/api/auth/z/login",
            IdentityUrl = "https://chat.z.ai/api/oauth/userinfo",
            ExtraHeaders = new Dictionary<string, string>
            {
                ["User-Agent"] = "ZCode/3.10.2",
                ["http-referer"] = "https://zcode.z.ai",
            },
            Verified = false,
        },
    ];

    public static SubscriptionOAuthConfig? Find(string providerKey) =>
        All.FirstOrDefault(c => c.ProviderKey == providerKey);

    /// <summary>
    /// kimi-code 的 X-Msh-* 设备头（值取自 @moonshot-ai/kimi-code v0.42.0 的
    /// KIMI_CODE_PLATFORM / CLI_USER_AGENT_PRODUCT）。设备 id 用机器信息的确定性
    /// GUID：稳定性是该字段的全部意义（上游拿它数设备），随机值会让重装变成新设备。
    /// 头值剔除非 ASCII——主机名带中文时塞进请求头会当场抛错。
    /// </summary>
    public static IReadOnlyDictionary<string, string> KimiDeviceHeaders()
    {
        var version = "0.42.0";
        var host = Ascii(Environment.MachineName);
        var os = Ascii(Environment.OSVersion.VersionString);
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        var seed = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes($"astra:kimi-device:{host}:{Environment.OSVersion.Platform}:{arch}")));
        var deviceId = $"{seed[..8]}-{seed[8..12]}-{seed[12..16]}-{seed[16..20]}-{seed[20..32]}".ToLowerInvariant();
        return new Dictionary<string, string>
        {
            ["User-Agent"] = $"kimi-code-cli/{version}",
            ["X-Msh-Platform"] = "kimi_code_cli",
            ["X-Msh-Version"] = version,
            ["X-Msh-Device-Name"] = host,
            ["X-Msh-Device-Model"] = $"{Ascii(System.Runtime.InteropServices.RuntimeInformation.OSDescription)} {arch}".Trim(),
            ["X-Msh-Os-Version"] = os,
            ["X-Msh-Device-Id"] = deviceId,
        };

        static string Ascii(string value)
        {
            var cleaned = System.Text.RegularExpressions.Regex.Replace(value, @"[^\x20-\x7e]", "").Trim();
            return cleaned.Length == 0 ? "unknown" : cleaned;
        }
    }

    /// <summary>
    /// Merges instance-level overrides from <c>providers.settings_json</c> key
    /// <c>subscription_oauth</c>: verified, client_id, authorize_url, token_url, device_code_url,
    /// scopes (array), plus extra_authorize_params / extra_device_params (object).
    /// </summary>
    public static SubscriptionOAuthConfig Effective(SubscriptionOAuthConfig baseConfig, JsonObject? providerSettings)
    {
        if (providerSettings?.TryGetPropertyValue("subscription_oauth", out var node) != true || node is not JsonObject o)
            return baseConfig;

        string? Str(string key) => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<string>(out var s) ? s : null;
        bool? Bool(string key) => o.TryGetPropertyValue(key, out var v) && v is JsonValue val && val.TryGetValue<bool>(out var b) ? b : null;

        Dictionary<string, string> ReadExtra(string key, IReadOnlyDictionary<string, string> fallback) =>
            o.TryGetPropertyValue(key, out var extraNode) && extraNode is JsonObject extra
                ? extra.Where(kv => kv.Value is JsonValue)
                    .ToDictionary(kv => kv.Key, kv => ((JsonValue)kv.Value!).ToJsonString().Trim('"'))
                : new Dictionary<string, string>(fallback);

        return new SubscriptionOAuthConfig
        {
            ProviderKey = baseConfig.ProviderKey,
            DisplayName = baseConfig.DisplayName,
            AuthorizeUrl = Str("authorize_url") ?? baseConfig.AuthorizeUrl,
            TokenUrl = Str("token_url") ?? baseConfig.TokenUrl,
            DeviceCodeUrl = Str("device_code_url") ?? baseConfig.DeviceCodeUrl,
            ClientId = Str("client_id") ?? baseConfig.ClientId,
            Scopes = o.TryGetPropertyValue("scopes", out var scopesNode) && scopesNode is JsonArray arr
                ? arr.Where(n => n is JsonValue).Select(n => n!.ToJsonString().Trim('"')).ToList()
                : baseConfig.Scopes,
            UsePkce = Bool("use_pkce") ?? baseConfig.UsePkce,
            Verified = Bool("verified") ?? baseConfig.Verified,
            Style = Str("style") ?? baseConfig.Style,
            BusinessLoginUrl = Str("business_login_url") ?? baseConfig.BusinessLoginUrl,
            IdentityUrl = Str("identity_url") ?? baseConfig.IdentityUrl,
            ExtraAuthorizeParams = ReadExtra("extra_authorize_params", baseConfig.ExtraAuthorizeParams),
            ExtraDeviceParams = ReadExtra("extra_device_params", baseConfig.ExtraDeviceParams),
            ExtraHeaders = ReadExtra("extra_headers", baseConfig.ExtraHeaders),
        };
    }

    /// <summary>A login may only start when the flow is fully specified AND verified.</summary>
    public static bool IsReady(SubscriptionOAuthConfig config) =>
        config.Verified
        && !string.IsNullOrWhiteSpace(config.TokenUrl)
        && !string.IsNullOrWhiteSpace(config.ClientId)
        && (config.Style == "zcode" || config.UsePkce
            ? !string.IsNullOrWhiteSpace(config.AuthorizeUrl)
            : !string.IsNullOrWhiteSpace(config.DeviceCodeUrl));
}

/// <summary>PKCE helpers (RFC 7636, S256).</summary>
public static class Pkce
{
    private const string Unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~";

    /// <summary>Creates a 64-character code verifier and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        var verifier = new StringBuilder(64);
        foreach (var b in bytes) verifier.Append(Unreserved[b % Unreserved.Length]);
        return (verifier.ToString(), Challenge(verifier.ToString()));
    }

    public static string Challenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}

/// <summary>An OAuth endpoint replied with an error (token exchange, refresh or device poll).</summary>
public sealed class OAuthProtocolException(string error, string? description)
    : Exception(description is null ? error : $"{error}: {description}")
{
    public string Error { get; } = error;
    public string? Description { get; } = description;

    public bool IsInvalidGrant => Error == "invalid_grant";
}

/// <summary>The account cannot be used for upstream auth; the user must log in again.</summary>
public sealed class SubscriptionAuthException(string accountId, string message) : Exception(message)
{
    public string AccountId { get; } = accountId;
}
