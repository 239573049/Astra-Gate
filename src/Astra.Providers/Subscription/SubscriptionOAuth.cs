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
    /// <summary>
    /// 设备码授权（RFC 8628）的轮询端点。空 = 轮询复用 <see cref="TokenUrl"/>。
    /// 非标准：OpenAI 的设备码轮询在另一个路径（/api/accounts/deviceauth/token）上，
    /// 拿到的不是令牌而是一次性授权码。
    /// </summary>
    public string DeviceTokenUrl { get; init; } = "";

    public string ClientId { get; init; } = "";
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>true = authorization-code flow with PKCE; false = device-code flow.</summary>
    public bool UsePkce { get; init; } = true;

    /// <summary>
    /// 流程形态。空 = 标准 OAuth（PKCE 授权码或设备码）；
    /// "zcode" = ZCode 的非标准授权码链路：无 PKCE、无 scope，换码是 JSON body <c>{provider, code, redirect_uri,
    /// state}</c>（没有 grant_type），换到 OAuth token 后还要跑一跳业务登录换取真正
    /// 的调用令牌，刷新 = 用存下的 OAuth token 重跑业务登录。来源：NextCoWork
    /// `issuers/zcode.ts`（逆向 Vibe Coding Labs《ZCode RE》 + ZCode.app v3.11.2）；
    /// "deviceauth" = 一类非标准设备码链路：申请非
    /// <c>/oauth2/token</c> 的端点、轮询返回一次性授权码（含它自己带的 PKCE
    /// challenge/verifier），再用该授权码走标准换码。OpenAI（Codex）走这条；
    /// "github-copilot" = 设备码登 GitHub，再拿 GitHub token 换 Copilot 短时令牌；
    /// "claude-loopback" = Anthropic 的授权码链路：redirect_uri 是 http://localhost:{临时端口}/callback
    /// （Claude Code 就这么登的），配合 PKCE；换码 body 是 JSON，redirect_uri 要逐字回传。
    /// 见 OAuthClient.BuildAuthorizeUrl 的 claude 分支（参数顺序敏感）。
    /// </summary>
    public string Style { get; init; } = "";

    /// <summary>
    /// ZCode 形态的第四跳：把 OAuth access_token 换成真正发请求用的令牌（Z.AI 渠道
    /// 返回业务 JWT）。空 = 没有这一跳。刷新时重跑这里，因此令牌的刷新槽里存的是
    /// OAuth token 本身。
    /// </summary>
    public string BusinessLoginUrl { get; init; } = "";

    /// <summary>
    /// ZCode 形态第四跳的另一种实现（智谱 BigModel 渠道）：biz API 主机（如 <c>https://bigmodel.cn</c>）。
    /// 非空 = 不走 <see cref="BusinessLoginUrl"/> 的业务 JWT，而是把 OAuth token 供应成一把真 API Key
    /// （<c>id.secret</c>）：查机构/项目 → 找或建名为 <see cref="ApiKeyName"/> 的 key → copy 出 secretKey。
    /// coding 端点认的是这把 key，不认 OAuth token（直接用会回 1234）。key 不过期，刷新 = 幂等地重跑一遍。
    /// 来源：NextCoWork `issuers/zcode.ts` provisionBizApiKey（逆向 ZCode.app v3.11.2 的 resolveCodingPlanApiKey）。
    /// </summary>
    public string BizHost { get; init; } = "";

    /// <summary>供应出来的 API Key 的名字（ZCode CLI 用死值 <c>zcode-api-key</c>）。</summary>
    public string ApiKeyName { get; init; } = "";

    /// <summary>ZAI CLI 链路：发起授权（server-mediated，无回调）。</summary>
    public string CliInitUrl { get; init; } = "";

    /// <summary>ZAI CLI 链路：轮询授权结果（拼 <c>/{flow_id}</c>）。</summary>
    public string CliPollUrl { get; init; } = "";

    /// <summary>
    /// 覆盖登录回调地址（实例级 settings.subscription_oauth.redirect_uri）。空 = 用目录默认：
    /// codex 用它的 1455/1457 回环（上游白名单），其它家族用 Astra 自己的回调。
    /// </summary>
    public string RedirectUriOverride { get; init; } = "";

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
            // 端点、client_id、scope 与 redirect 形态都取自 Claude Code 自己的二进制
            // （@anthropic-ai/claude-code 的 claude-code-darwin-arm64/claude 里内嵌的 OAuth 配置
            // 与授权 URL 构造函数），与 sub2api 的 Claude OAuth 客户端一致。要点：
            //  1. 授权页是 claude.com/cai/oauth/authorize（claude.ai/oauth/authorize 会 307 到它、
            //     参数保留），必须带 code=true；
            //  2. redirect_uri 是**回环地址、端口临时挑**：http://localhost:{port}/callback。
            //     platform.claude.com/oauth/code/callback 只是 Claude Code 的无头兜底，
            //     实际登录走回环（实测 Claude Code 用 http://localhost:55522/callback）。
            //     所以登录期间在临时端口上起一个接收器，见 SubscriptionCatalog.ClaudeLoopbackPath；
            //  3. 参数**顺序**敏感：顺序不对授权端点直接回 "Invalid request format"
            //     （见 OAuthClient.BuildAuthorizeUrl 的 claude 分支）；
            //  4. 换码在 platform.claude.com，body 是 JSON，code 与 state 分开发。
            AuthorizeUrl = "https://claude.com/cai/oauth/authorize",
            TokenUrl = "https://platform.claude.com/v1/oauth/token",
            ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e",
            // scopes 逐字对齐 Claude Code 的 yDr()。
            Scopes =
            [
                "org:create_api_key", "user:profile", "user:inference", "user:sessions:claude_code",
                "user:mcp_servers", "user:file_upload", "user:plugins",
            ],
            UsePkce = true,
            Style = "claude-loopback",
            ExtraAuthorizeParams = new Dictionary<string, string> { ["code"] = "true" },
            // 五家订阅一律目录默认开放；实例可用 settings.subscription_oauth.verified=false 关掉。
            Verified = true,
        },
        new SubscriptionOAuthConfig
        {
            ProviderKey = "openai-subscription",
            DisplayName = "ChatGPT 订阅（Codex）",
            // codex-cli 的默认登录（`codex login`）：授权码 + PKCE，回环回调固定
            // http://localhost:{1455|1457}/auth/callback —— codex 源码注释写明这一对端口就是
            // auth.openai.com（Hydra）的 redirect URI 白名单，任何别的回调（含 Astra 自己的
            // 127.0.0.1:{port}/api/oauth/callback）都会被判 invalid_authorize_request。
            // 所以这里记的是 codex 的本地回调；Astra 发登录时会在该端口起一个临时接收器，
            // 把 code 转给 Astra 自己的回调完成换码（SubscriptionEndpoints.ArmCodexCallbackAsync）。
            // 额外三个参数与 scope 集合同样取自 codex（server.rs build_authorize_url）。
            // 备选是 device code（codex login --device-auth，beta）：把 style 改成 "deviceauth"、
            // use_pkce 关掉即可，端点已在下面备好。
            AuthorizeUrl = "https://auth.openai.com/oauth/authorize",
            TokenUrl = "https://auth.openai.com/oauth/token",
            DeviceCodeUrl = "https://auth.openai.com/api/accounts/deviceauth/usercode",
            DeviceTokenUrl = "https://auth.openai.com/api/accounts/deviceauth/token",
            // 不是标准 OAuth 必填项：该 client 的登录必须带 codex 自己的回调（见 CodexLoopbackRedirect）。
            ClientId = "app_EMoamEEZ73f0CkXaXp7hrann",
            Scopes = ["openid", "profile", "email", "offline_access", "api.connectors.read", "api.connectors.invoke"],
            UsePkce = true,
            ExtraAuthorizeParams = new Dictionary<string, string>
            {
                ["id_token_add_organizations"] = "true",
                ["codex_cli_simplified_flow"] = "true",
                ["originator"] = "codex_cli_rs",
            },
            Verified = true,
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
            Verified = true,
        },
        new SubscriptionOAuthConfig
        {
            ProviderKey = "github-copilot-subscription",
            DisplayName = "GitHub Copilot 订阅",
            // GitHub 官方 device flow（github.com/login/device），client id 取自官方 Copilot 客户端
            // （copilot-language-server main.js：GitHubAppInfo 的 "Iv1.b507a08c87ecfe98"，scope repo+workflow）。
            // 换到的 GitHub token 再换 Copilot 短时令牌（CopilotLoginUrl）才能发请求——
            // 所以走 Style="github-copilot" 的那条"第二跳"分支。推理端点是
            // api.githubcopilot.com（OpenAI Chat 兼容，另有 /responses 与 /v1/messages）。
            TokenUrl = "https://github.com/login/oauth/access_token",
            DeviceCodeUrl = "https://github.com/login/device/code",
            ClientId = "Iv1.b507a08c87ecfe98",
            Scopes = ["repo", "workflow"],
            UsePkce = false,
            Style = "github-copilot",
            BusinessLoginUrl = "https://api.github.com/copilot_internal/v2/token",
            // 官方客户端身份头：缺了会被 Copilot 后端按"未知客户端"拒掉。
            ExtraHeaders = new Dictionary<string, string>
            {
                ["User-Agent"] = "GitHubCopilotChat/0.26.7",
                ["Editor-Version"] = "vscode/1.99.3",
                ["Editor-Plugin-Version"] = "copilot-chat/0.26.7",
                ["Copilot-Integration-Id"] = "vscode-chat",
                ["X-GitHub-Api-Version"] = "2024-12-15",
            },
            Verified = true,
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
            Verified = true,
        },
        new SubscriptionOAuthConfig
        {
            ProviderKey = "zcode-subscription",
            DisplayName = "ZCode 订阅（GLM Coding Plan · Z.AI）",
            // 授权码 + 回环回调这条（Style="zcode"，NextCoWork issuers/zcode-zai.ts）在 authorize
            // 阶段会被上游按 client 白名单拒掉：该 client 只登记了桌面端的自定义协议回调
            // （ZCode.app 3.14 里 redirectUri = "zcode://oauth/callback"，本地实测报
            // "Redirect URI not registered for this client"）。所以 Z.AI 渠道改走官方 CLI 链路
            // （CliInitUrl/CliPollUrl，无回调，见 zcode2api docs/development/05-upstream-protocols.md §2.1），
            // 回调字段保留给 bigmodel 渠道（Style="zcode" 分支）继续用。
            Style = "zcli",
            AuthorizeUrl = "https://chat.z.ai/api/oauth/authorize",
            TokenUrl = "https://zcode.z.ai/api/v1/oauth/token",
            CliInitUrl = "https://zcode.z.ai/api/v1/oauth/cli/init",
            CliPollUrl = "https://zcode.z.ai/api/v1/oauth/cli/poll",
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
            Verified = true,
        },
        new SubscriptionOAuthConfig
        {
            ProviderKey = "bigmodel-subscription",
            DisplayName = "智谱 GLM 订阅（Coding Plan · BigModel）",
            // 与 Z.AI 渠道同一条 CLI 链路（cli/init 的 provider 取 "bigmodel"，服务端签发的授权链接
            // 是 bigmodel.cn/login?appId=zcode&redirect=…/cli/callback/bigmodel&state=…，授权后由服务端
            // 收 authCode，客户端只轮询）。差别只在最后一步：BigModel 的 coding 端点要的是真 API Key，
            // 所以不是换业务 JWT，而是三步供应（BizHost / ApiKeyName，见 OAuthClient.ProvisionBizApiKeyAsync）。
            // 来源：NextCoWork issuers/zcode-bigmodel.ts（2026-09-15 逆向 ZCode.app v3.11.2）。
            // TokenUrl / AuthorizeUrl 在这条链路里不参与（保留给 IsReady 与回退用）。
            Style = "zcli",
            AuthorizeUrl = "https://bigmodel.cn/login",
            TokenUrl = "https://zcode.z.ai/api/v1/oauth/token",
            CliInitUrl = "https://zcode.z.ai/api/v1/oauth/cli/init",
            CliPollUrl = "https://zcode.z.ai/api/v1/oauth/cli/poll",
            ClientId = "zcode", // ZCode 桌面端的 appId（不是 Z.AI 那种 client_… 形状）
            Scopes = [],
            UsePkce = false,
            BizHost = "https://bigmodel.cn",
            ApiKeyName = "zcode-api-key",
            ExtraHeaders = new Dictionary<string, string>
            {
                ["User-Agent"] = "ZCode/3.10.2",
                ["http-referer"] = "https://zcode.z.ai",
            },
            Verified = true,
        },
    ];

    public static SubscriptionOAuthConfig? Find(string providerKey) =>
        All.FirstOrDefault(c => c.ProviderKey == providerKey);

    /// <summary>codex-cli 登录回调的路径（端口见 <see cref="CodexLoopbackPorts"/>）。</summary>
    public const string CodexLoopbackPath = "/auth/callback";

    /// <summary>
    /// Claude（Anthropic）登录回调的路径。redirect_uri 是 <c>http://localhost:{临时端口}/callback</c>
    /// ——端口由本地挑（Claude Code 实测用 <c>http://localhost:55522/callback</c>），所以没有固定值。
    /// </summary>
    public const string ClaudeLoopbackPath = "/callback";

    /// <summary>
    /// Claude Code 自己的无头兜底回调（授权页把 code 显示出来让用户粘回来）。
    /// 正常登录走回环地址，这个值只在用户显式指定时才用。
    /// </summary>
    public const string ClaudeCodeCallback = "https://platform.claude.com/oauth/code/callback";

    /// <summary>是否是 Claude 的回环登录（redirect_uri 是临时端口的 http://localhost）。</summary>
    public static bool IsClaudeLoopback(SubscriptionOAuthConfig config) =>
        config.Style == "claude-loopback";

    /// <summary>
    /// 是否是"授权页把 code 显示出来、用户粘回来"的链路：只有实例显式把 redirect_uri 覆盖成
    /// 非回环地址时才成立（Claude Code 的无头路径就是这种）。
    /// </summary>
    public static bool IsPasteLogin(SubscriptionOAuthConfig config) =>
        config.Style == "claude-loopback" && config.RedirectUriOverride.Length > 0;

    /// <summary>
    /// 把用户粘回来的东西还原成换码要的 <c>code</c> 与 <c>state</c>。
    ///
    /// 收的是**整条回调地址或整段回显值**，不是光秃秃一个 code——用户在授权页上一次全选复制，
    /// code 不会被手抖截断，state 也跟着一起回来了。三种形态都认：
    ///  1. 完整回调地址：<c>https://platform.claude.com/oauth/code/callback?code=…&amp;state=…</c>
    ///  2. Claude 回显的 <c>授权码#state</c>（sub2api 换码时就是这么拆的）
    ///  3. 只有授权码
    /// 返回的 <c>Raw</c> 是"上游要的那种形态"：有 state 就是 <c>授权码#state</c>，否则就是授权码本身。
    /// </summary>
    public static (string Raw, string? State) ParsePastedCode(string? pasted)
    {
        var text = pasted?.Trim() ?? "";
        if (text.Length == 0) return ("", null);
        // URL 里 code 可能被百分号编码（# 会被编码成 %23，正好是最容易被忽略的一种）。
        var decoded = Uri.UnescapeDataString(text);
        if (Uri.TryCreate(decoded, UriKind.Absolute, out var uri) && uri.Query.Length > 1)
        {
            string? code = null;
            string? state = null;
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var key = Uri.UnescapeDataString(pair[..eq]);
                var value = Uri.UnescapeDataString(pair[(eq + 1)..]);
                if (string.Equals(key, "code", StringComparison.OrdinalIgnoreCase)) code ??= value;
                else if (string.Equals(key, "state", StringComparison.OrdinalIgnoreCase)) state ??= value;
            }
            if (!string.IsNullOrEmpty(code)) return (state is { Length: > 0 } ? $"{code}#{state}" : code, state);
        }
        // 授权页的 "授权码#state" 形态。code 本身不含 '#'，所以按第一个 '#' 拆。
        var hash = decoded.IndexOf('#');
        if (hash > 0)
            return (decoded, decoded[(hash + 1)..] is { Length: > 0 } state ? state : null);
        return (decoded, null);
    }

    /// <summary>拆出 <c>授权码#state</c> 里的授权码部分；没有 '#' 时原样返回。</summary>
    public static string CodeWithoutState(string raw)
    {
        var hash = raw.IndexOf('#');
        return hash < 0 ? raw : raw[..hash];
    }

    /// <summary>
    /// codex-cli 允许的回环回调端口：1455 默认、1457 备用。这一对端口是 auth.openai.com 的
    /// redirect URI 白名单，其它回调一律 invalid_authorize_request。
    /// </summary>
    public static readonly IReadOnlyList<int> CodexLoopbackPorts = [1455, 1457];

    /// <summary>是否是 codex-cli 的 PKCE 登录（需要按上面的回调端口临时接管）。</summary>
    public static bool IsCodexLogin(SubscriptionOAuthConfig config) =>
        config.ProviderKey == "openai-subscription" && config.UsePkce && config.Style.Length == 0;

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
    /// device_token_url, cli_init_url, cli_poll_url, scopes (array), plus extra_authorize_params /
    /// extra_device_params (object).
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
            DeviceTokenUrl = Str("device_token_url") ?? baseConfig.DeviceTokenUrl,
            ClientId = Str("client_id") ?? baseConfig.ClientId,
            Scopes = o.TryGetPropertyValue("scopes", out var scopesNode) && scopesNode is JsonArray arr
                ? arr.Where(n => n is JsonValue).Select(n => n!.ToJsonString().Trim('"')).ToList()
                : baseConfig.Scopes,
            UsePkce = Bool("use_pkce") ?? baseConfig.UsePkce,
            Verified = Bool("verified") ?? baseConfig.Verified,
            Style = Str("style") ?? baseConfig.Style,
            BusinessLoginUrl = Str("business_login_url") ?? baseConfig.BusinessLoginUrl,
            BizHost = Str("biz_host") ?? baseConfig.BizHost,
            ApiKeyName = Str("api_key_name") ?? baseConfig.ApiKeyName,
            CliInitUrl = Str("cli_init_url") ?? baseConfig.CliInitUrl,
            CliPollUrl = Str("cli_poll_url") ?? baseConfig.CliPollUrl,
            RedirectUriOverride = Str("redirect_uri") ?? baseConfig.RedirectUriOverride,
            IdentityUrl = Str("identity_url") ?? baseConfig.IdentityUrl,
            ExtraAuthorizeParams = ReadExtra("extra_authorize_params", baseConfig.ExtraAuthorizeParams),
            ExtraDeviceParams = ReadExtra("extra_device_params", baseConfig.ExtraDeviceParams),
            ExtraHeaders = ReadExtra("extra_headers", baseConfig.ExtraHeaders),
        };
    }

    /// <summary>
    /// A login may only start when the flow is fully specified AND verified. "zcode" 只需要
    /// authorize url；"zcli" 需要 init + poll（无回环回调）；deviceauth（OpenAI）额外需要
    /// 一个设备码申请端点。
    /// </summary>
    public static bool IsReady(SubscriptionOAuthConfig config) =>
        config.Verified
        && !string.IsNullOrWhiteSpace(config.TokenUrl)
        && !string.IsNullOrWhiteSpace(config.ClientId)
        && config.Style switch
        {
            "zcli" => config.CliInitUrl.Length > 0 && config.CliPollUrl.Length > 0,
            // github-copilot：设备码登录 GitHub，再换 Copilot 短时令牌（BusinessLoginUrl）。
            "github-copilot" => !string.IsNullOrWhiteSpace(config.DeviceCodeUrl) && config.BusinessLoginUrl.Length > 0,
            // claude-loopback：授权码 + PKCE，回调走临时端口的回环地址（由登录流程起接收器）。
            "claude-loopback" => config.UsePkce && !string.IsNullOrWhiteSpace(config.AuthorizeUrl),
            "zcode" => !string.IsNullOrWhiteSpace(config.AuthorizeUrl),
            "deviceauth" => !string.IsNullOrWhiteSpace(config.DeviceCodeUrl) && config.DeviceTokenUrl.Length > 0,
            _ => config.UsePkce
                ? !string.IsNullOrWhiteSpace(config.AuthorizeUrl)
                : !string.IsNullOrWhiteSpace(config.DeviceCodeUrl),
        };
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

    /// <summary>
    /// Claude Code / sub2api 形态的 PKCE：verifier = 32 个随机字节的 base64url（43 字符）。
    /// Anthropic 的授权端对输入形态严格校验，Claude 登录用这个形态而不是上面的 64 字符版本。
    /// </summary>
    public static (string Verifier, string Challenge) CreateBase64Url()
    {
        var verifier = RandomBase64Url(32);
        return (verifier, Challenge(verifier));
    }

    /// <summary>
    /// 随机字节的 base64url（32 字节 = 43 字符，无填充）——Claude Code 与 sub2api 的 OAuth state 形态。
    /// 其它家族的 state 是 26 位 ULID；Claude 的授权端对它严格校验，发 ULID 会被判 "Invalid request format"。
    /// </summary>
    public static string RandomBase64Url(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

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

/// <summary>
/// 非标准设备码（<see cref="SubscriptionOAuthConfig.Style"/> = "deviceauth"）轮询成功时上游给的
/// 东西：一次性授权码，以及上游自己配对好的 PKCE challenge/verifier——换码时原样带上，
/// 不需要本地重新生成。来源：openai/codex device_code_auth.rs 的 CodeSuccessResp。
/// </summary>
public sealed record DeviceAuthGrant(string AuthorizationCode, string CodeChallenge, string CodeVerifier);

/// <summary>
/// ZAI CLI 链路（<see cref="SubscriptionOAuthConfig.Style"/> = "zcli"）的发起响应：
/// flow id、可直接在浏览器打开的授权链接，以及轮询要用的 poll_token。
/// </summary>
public sealed record ZcodeCliStart(string FlowId, string AuthorizeUrl, string PollToken);

public enum ZcodeCliPollKind { Pending, Done, Expired, Error }

/// <summary>ZAI CLI 轮询结果：授权完成给出 access_token（及响应里自带的 user.email），否则是过期/失败。</summary>
public sealed record ZcodeCliPoll(ZcodeCliPollKind Kind, string? AccessToken, string? Error, string? Description, string? Email = null);

/// <summary>
/// Copilot 短时令牌（GitHub token 换来的第二跳）：<c>token</c> 是实际发请求用的，
/// <c>expiresAt</c> 是它的硬过期时间，<c>apiBase</c> 是渠道下发的 API 主机（企业版会不同）。
/// </summary>
public sealed record CopilotToken(string Token, DateTimeOffset? ExpiresAt, int? RefreshInSeconds, string? ApiBase, string? Sku);
