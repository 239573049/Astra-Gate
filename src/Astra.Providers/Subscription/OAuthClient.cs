using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;

namespace Astra.Providers.Subscription;

/// <summary>Standard OAuth 2.0 flows (authorization code + PKCE, refresh, device code) over raw HTTP.</summary>
public sealed class OAuthClient(IHttpClientFactory factory)
{
    public const string HttpClientName = "astra-oauth";

    public sealed record TokenResult(
        string AccessToken,
        string? RefreshToken,
        int? ExpiresIn,
        string? IdToken,
        string? Scope);

    public sealed record DeviceStart(
        string DeviceCode,
        string UserCode,
        string? VerificationUrl,
        string? VerificationUrlComplete,
        int ExpiresIn,
        int Interval);

    public enum DevicePollKind { Success, Pending, SlowDown, Expired, Denied, Error }

    /// <param name="Token">标准设备码流程换到的令牌；非标准形态（deviceauth）为 null。</param>
    /// <param name="Grant">deviceauth 形态轮询到的授权码 + 上游配对的 PKCE 对。</param>
    public sealed record DevicePoll(DevicePollKind Kind, TokenResult? Token, string? Error, string? Description,
        DeviceAuthGrant? Grant = null);

    private HttpClient Create() => factory.CreateClient(HttpClientName);

    /// <summary>Builds the authorization URL (response_type=code, PKCE S256 when configured).</summary>
    public static string BuildAuthorizeUrl(
        SubscriptionOAuthConfig config, string redirectUri, string state, string codeChallenge)
    {
        // Claude（Anthropic）对参数**顺序**敏感：顺序不对时授权端点直接回 "Invalid request format"。
        // 这里逐字复刻 Claude Code 自己的构造顺序（其二进制里的 builder）：
        //   code=true → client_id → response_type → redirect_uri → scope → code_challenge →
        //   code_challenge_method=S256 → state（可选的 orgUUID / login_hint 在最后）
        // 空格按 URLSearchParams 编成 '+'（Claude Code 与 sub2api 都是这个形态）。
        if (config.Style == "claude-loopback")
        {
            var claudeQuery = new List<string> { "code=true" };
            claudeQuery.Add($"client_id={SearchParam(config.ClientId)}");
            claudeQuery.Add("response_type=code");
            claudeQuery.Add($"redirect_uri={SearchParam(redirectUri)}");
            if (config.Scopes.Count > 0)
                claudeQuery.Add($"scope={SearchParam(string.Join(' ', config.Scopes))}");
            claudeQuery.Add($"code_challenge={SearchParam(codeChallenge)}");
            claudeQuery.Add("code_challenge_method=S256");
            claudeQuery.Add($"state={SearchParam(state)}");
            // code 已经在最前面发过了；其它额外参数（orgUUID / login_hint 之类）按 Claude Code 放在最后。
            foreach (var (key, value) in config.ExtraAuthorizeParams)
                if (key != "code") claudeQuery.Add($"{SearchParam(key)}={SearchParam(value)}");
            return $"{config.AuthorizeUrl}?{string.Join('&', claudeQuery)}";
        }

        var query = new List<string>
        {
            $"response_type=code",
            $"client_id={Uri.EscapeDataString(config.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"state={Uri.EscapeDataString(state)}",
        };
        if (config.Scopes.Count > 0)
            query.Add($"scope={Uri.EscapeDataString(string.Join(' ', config.Scopes))}");
        if (config.UsePkce)
        {
            query.Add("code_challenge_method=S256");
            query.Add($"code_challenge={Uri.EscapeDataString(codeChallenge)}");
        }
        foreach (var (key, value) in config.ExtraAuthorizeParams)
            query.Add($"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}");
        return $"{config.AuthorizeUrl}{(config.AuthorizeUrl.Contains('?') ? '&' : '?')}{string.Join('&', query)}";
    }

    /// <summary>URLSearchParams 形态的参数编码：空格编成 '+'（Claude Code 的授权端点按这个形态匹配）。</summary>
    private static string SearchParam(string value) =>
        Uri.EscapeDataString(value).Replace("%20", "+");


    public async Task<TokenResult> ExchangeCodeAsync(
        SubscriptionOAuthConfig config, string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = config.ClientId,
            ["redirect_uri"] = redirectUri,
        };
        if (config.UsePkce || codeVerifier.Length > 0) form["code_verifier"] = codeVerifier;
        return await SendTokenRequestAsync(config, form, ct);
    }

    public async Task<TokenResult> RefreshAsync(SubscriptionOAuthConfig config, string refreshToken, CancellationToken ct = default)
    {
        // Anthropic 那套换令牌端点是 JSON body（不是表单）——sub2api 的 Claude 客户端就是这么发的。
        if (config.Style == "claude-loopback")
            return await PostTokenJsonAsync(config, new JsonObject
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = config.ClientId,
            }, ct);

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = config.ClientId,
        };
        return await SendTokenRequestAsync(config, form, ct);
    }

    /// <summary>
    /// Anthropic（Claude）的换码：JSON body，<c>redirect_uri</c> 必须逐字等于发起授权时用的那个
    /// （回环地址或实例覆盖值）；<c>code</c> 是"授权码#state"形态时要把 state 拆出来单独发
    /// ——只发授权码会被判「参数错误」（Claude Code 自己的换码也是这个形状）。
    /// </summary>
    public async Task<TokenResult> ExchangeClaudeCodeAsync(
        SubscriptionOAuthConfig config, string code, string codeVerifier, string redirectUri, CancellationToken ct = default)
    {
        var raw = code.Trim();
        var body = new JsonObject
        {
            ["grant_type"] = "authorization_code",
            ["code"] = SubscriptionCatalog.CodeWithoutState(raw),
            ["client_id"] = config.ClientId,
            // 回调地址必须与授权时一致：漏发或不一致都会被判参数错误。
            ["redirect_uri"] = redirectUri.Length > 0 ? redirectUri : SubscriptionCatalog.ClaudeCodeCallback,
        };
        if (codeVerifier.Length > 0) body["code_verifier"] = codeVerifier;
        var hash = raw.IndexOf('#');
        if (hash >= 0 && hash + 1 < raw.Length) body["state"] = raw[(hash + 1)..];
        return await PostTokenJsonAsync(config, body, ct);
    }

    /// <summary>换令牌端点的 JSON body 形态（Anthropic）：Accept 要同时接受 json 与 text/plain。</summary>
    private async Task<TokenResult> PostTokenJsonAsync(SubscriptionOAuthConfig config, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, config.TokenUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyExtraHeaders(request, config);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain", 0.9));
        using var client = Create();
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = TryParse(text);
        if (json is null)
            throw new OAuthProtocolException($"http_{(int)response.StatusCode}",
                text.Length is > 0 and <= 300 ? text.Trim() : "换令牌响应不是 JSON");
        var error = Optional(json, "error");
        if (error is not null)
            throw new OAuthProtocolException(error, Optional(json, "error_description") ?? Optional(json, "message"));
        if (!response.IsSuccessStatusCode)
            throw new OAuthProtocolException($"http_{(int)response.StatusCode}",
                Optional(json, "message") ?? (text.Length <= 300 ? text : null));
        return new TokenResult(
            Required(json, "access_token"),
            Optional(json, "refresh_token"),
            IntOptional(json, "expires_in"),
            Optional(json, "id_token"),
            Optional(json, "scope"));
    }

    public async Task<DeviceStart> StartDeviceAsync(SubscriptionOAuthConfig config, CancellationToken ct = default)
    {
        // deviceauth（OpenAI）的设备码请求是 JSON body，且不要 scope；标准设备码是表单 + scope。
        if (config.Style == "deviceauth")
        {
            var json = await PostJsonAsync(config, config.DeviceCodeUrl,
                new JsonObject { ["client_id"] = config.ClientId }, ct);
            return new DeviceStart(
                Required(json, "device_auth_id"),
                Required(json, "user_code"),
                DeviceVerificationUrl(config),
                null,
                IntOr(json, "expires_in", 900),
                IntOr(json, "interval", 5));
        }

        var form = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
        };
        // RFC 8628: the scope ships with the device request, not with the token poll.
        if (config.Scopes.Count > 0) form["scope"] = string.Join(' ', config.Scopes);
        foreach (var (key, value) in config.ExtraDeviceParams)
            form[key] = value;
        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, config.DeviceCodeUrl) { Content = content };
        ApplyExtraHeaders(request, config);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Create().SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var payload = Parse(body);
        return new DeviceStart(
            Required(payload, "device_code"),
            Required(payload, "user_code"),
            Optional(payload, "verification_uri") ?? Optional(payload, "verification_url"),
            Optional(payload, "verification_uri_complete"),
            IntOr(payload, "expires_in", 600),
            IntOr(payload, "interval", 5));
    }

    /// <summary>
    /// deviceauth 形态的轮询：POST 设备码申请端点返回的 id + user code，成功时拿到的是
    /// <b>一次性授权码</b>（连同上游配对的 code_challenge / code_verifier），不是令牌。
    /// 未授权时上游回 403/404（没有 RFC 8628 的 error 字段），一律当"继续等待"。
    /// 来源：openai/codex device_code_auth.rs 的 poll_for_token。
    /// </summary>
    public async Task<DevicePoll> PollDeviceAuthAsync(
        SubscriptionOAuthConfig config, string deviceAuthId, string userCode, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, config.DeviceTokenUrl)
            {
                Content = new StringContent(Json.Serialize(new Dictionary<string, string>
                {
                    ["device_auth_id"] = deviceAuthId,
                    ["user_code"] = userCode,
                }), Encoding.UTF8, "application/json"),
            };
            ApplyExtraHeaders(request, config);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await Create().SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                || response.StatusCode == (HttpStatusCode)429)
                return new DevicePoll(DevicePollKind.Pending, null, null, null);
            var payload = Parse(body);
            if (!response.IsSuccessStatusCode)
            {
                var error = Optional(payload, "error");
                var kind = error switch
                {
                    "expired_token" or "expired_device_code" => DevicePollKind.Expired,
                    "access_denied" => DevicePollKind.Denied,
                    _ => DevicePollKind.Error,
                };
                return new DevicePoll(kind, null, error ?? $"http_{(int)response.StatusCode}", Optional(payload, "error_description"));
            }
            var grant = new DeviceAuthGrant(
                Required(payload, "authorization_code"),
                Required(payload, "code_challenge"),
                Required(payload, "code_verifier"));
            return new DevicePoll(DevicePollKind.Success, null, null, null, grant);
        }
        catch (OAuthProtocolException e)
        {
            return new DevicePoll(DevicePollKind.Error, null, e.Error, e.Description);
        }
    }

    /// <summary>deviceauth 的授权页：固定 <c>{issuer}/codex/device</c>（与 deviceauth 端点同 host）。</summary>
    private static string? DeviceVerificationUrl(SubscriptionOAuthConfig config) =>
        Uri.TryCreate(config.DeviceCodeUrl, UriKind.Absolute, out var url)
            ? $"{url.Scheme}://{url.Authority}/codex/device"
            : null;

    /// <summary>POST 一个 JSON body 并解析 JSON 响应（deviceauth 的设备码申请走这个形状）。</summary>
    private async Task<JsonObject> PostJsonAsync(
        SubscriptionOAuthConfig config, string url, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        ApplyExtraHeaders(request, config);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Create().SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = Parse(text);
        if (!response.IsSuccessStatusCode)
        {
            var error = Optional(json, "error");
            throw new OAuthProtocolException(error ?? $"http_{(int)response.StatusCode}",
                Optional(json, "error_description") ?? (error is null && text.Length <= 300 ? text : null));
        }
        return json;
    }

    /// <summary>
    /// ZCode CLI 链路（官方、headless 友好、无回调）：<c>POST {CliInitUrl}</c>（Bearer 本地随机
    /// poll_token，body <c>{"provider":"zai"}</c>）→ <c>data.{flow_id, authorize_url, poll_token?}</c>；
    /// 浏览器打开 authorize_url 授权后 <c>GET {CliPollUrl}/{flow_id}</c>（Bearer poll_token）
    /// 轮询取 <c>data.accessToken</c>。来源：zcode2api docs/development/05-upstream-protocols.md §2.1。
    /// </summary>
    public async Task<ZcodeCliStart> StartZcodeCliAsync(
        SubscriptionOAuthConfig config, string provider, string pollToken, CancellationToken ct = default)
    {
        var data = await SendZcodeEnvelopeAsync(config, HttpMethod.Post, config.CliInitUrl,
            new Dictionary<string, object?> { ["provider"] = provider }, ct, $"Bearer {pollToken}");
        return new ZcodeCliStart(
            Required(data, "flow_id"),
            Required(data, "authorize_url"),
            Optional(data, "poll_token") ?? pollToken);
    }

    /// <summary>ZCode CLI 轮询：4xx 里的 code=3004 是会话过期，其余 4xx 是终态失败。</summary>
    public async Task<ZcodeCliPoll> PollZcodeCliAsync(
        SubscriptionOAuthConfig config, string flowId, string pollToken, CancellationToken ct = default)
    {
        try
        {
            var data = await SendZcodeEnvelopeAsync(config, HttpMethod.Get, $"{config.CliPollUrl}/{flowId}",
                null, ct, $"Bearer {pollToken}");
            // 真实响应是 data.status ∈ pending / ready / failed（逆向 ZCode.app v3.11.2，NextCoWork flow.ts
            // pollCliFlow）；ready 时凭证在渠道键下：data.{zai|bigmodel}.access_token。
            // 没有 status 的老形态（data.accessToken，zcode2api 文档）继续认，保持向后兼容。
            switch (Optional(data, "status"))
            {
                case "pending":
                    return new ZcodeCliPoll(ZcodeCliPollKind.Pending, null, null, null);
                case "failed":
                    return new ZcodeCliPoll(ZcodeCliPollKind.Error, null, "failed", "授权失败（服务端标记该次授权未完成）");
                case null or "ready":
                    break;
                case var other:
                    return new ZcodeCliPoll(ZcodeCliPollKind.Error, null, "unknown_status", $"未知的授权状态：{other}");
            }
            var token = Optional(data, "accessToken") ?? Optional(data, "access_token")
                ?? data.Select(p => p.Value as JsonObject).Select(o => o is null ? null : Optional(o, "access_token"))
                    .FirstOrDefault(t => t is not null);
            return token is null
                ? new ZcodeCliPoll(ZcodeCliPollKind.Error, null, "invalid_token_response", "授权完成但响应里没有 access_token")
                : new ZcodeCliPoll(ZcodeCliPollKind.Done, token, null, null,
                    data["user"] is JsonObject user ? Optional(user, "email") : null);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // 网络抖动 / 超时可重试（真实 ZCode.app 同款）：轮询本来就是对着一个还没发生的事件反复问，
            // 瞬时断网不该废掉用户已经在浏览器里点了「同意」的授权。
            return new ZcodeCliPoll(ZcodeCliPollKind.Pending, null, null, null);
        }
        catch (OAuthProtocolException e)
        {
            // code=3004 = 会话过期（zcode2api 明确映射）；其余 4xx 终态失败。
            return e.Error == "zcode_3004"
                ? new ZcodeCliPoll(ZcodeCliPollKind.Expired, null, e.Error, e.Description)
                : new ZcodeCliPoll(ZcodeCliPollKind.Error, null, e.Error, e.Description);
        }
    }

    /// <summary>
    /// ZCode 形态的换码：JSON body <c>{provider, code, redirect_uri, state}</c> —— 没有
    /// grant_type / client_id / code_verifier；响应是 <c>{code, msg, data}</c> 信封，
    /// access_token 在 data 的渠道键下（如 zai），找不到再退回平铺形态。
    /// 来源：NextCoWork issuers/zcode.ts（逆向 Vibe Coding Labs《ZCode RE》）。
    /// </summary>
    public async Task<string> ExchangeZcodeCodeAsync(
        SubscriptionOAuthConfig config, string provider, string code, string redirectUri, string state, CancellationToken ct = default)
    {
        var data = await PostZcodeEnvelopeAsync(config, config.TokenUrl,
            new Dictionary<string, object?> { ["provider"] = provider, ["code"] = code, ["redirect_uri"] = redirectUri, ["state"] = state },
            ct);
        var token = NestedAccessToken(data, provider);
        return token ?? throw new OAuthProtocolException("invalid_token_response", "换码响应里没有 access_token");
    }

    /// <summary>ZCode 第四跳：OAuth token → 真正发请求用的令牌（Z.AI 渠道是业务 JWT）。</summary>
    public async Task<string> ZcodeBusinessLoginAsync(SubscriptionOAuthConfig config, string oauthAccessToken, CancellationToken ct = default)
    {
        var data = await PostZcodeEnvelopeAsync(config, config.BusinessLoginUrl,
            new Dictionary<string, object?> { ["token"] = oauthAccessToken }, ct);
        return Optional(data, "access_token")
               ?? throw new OAuthProtocolException("invalid_token_response", "业务登录响应里没有 access_token");
    }

    /// <summary>
    /// ZCode 第四跳的统一入口：OAuth token → 真正发请求用的凭据。配了 <see cref="SubscriptionOAuthConfig.BizHost"/>
    /// （智谱 BigModel）就供应一把真 API Key，否则换业务 JWT（Z.AI）。刷新时也走这里。
    /// </summary>
    public Task<string> ZcodeApiCredentialAsync(SubscriptionOAuthConfig config, string oauthAccessToken, CancellationToken ct = default) =>
        config.BizHost.Length > 0
            ? ProvisionBizApiKeyAsync(config, oauthAccessToken, ct)
            : ZcodeBusinessLoginAsync(config, oauthAccessToken, ct);

    /// <summary>
    /// 把 BigModel 的 OAuth token 供应成一把真 API Key（<c>id.secret</c>）。逆向自 ZCode.app 打包的 CLI
    /// （<c>resolveCodingPlanApiKey</c>，经 NextCoWork `issuers/zcode.ts` provisionBizApiKey）：
    /// <list type="number">
    /// <item><c>GET {BizHost}/api/biz/customer/getCustomerInfo</c>：挑名字含「默认机构」的第一个机构（否则第 0 个），
    /// 其下同样规则挑「默认项目」；</item>
    /// <item><c>GET …/api/biz/v1/organization/{org}/projects/{proj}/api_keys</c>：找 name==<see cref="SubscriptionOAuthConfig.ApiKeyName"/> 的那条，
    /// 没有就 POST <c>{name}</c> 创建；</item>
    /// <item><c>GET …/api_keys/copy/{apiKey}</c> → <c>secretKey</c>；最终凭据 = <c>apiKey.secretKey</c>。</item>
    /// </list>
    /// 鉴权头是裸 <c>Authorization: &lt;OAuth token&gt;</c>（不带 Bearer）。copy 失败不致命：只有 id 没有 secret 的 key
    /// 形态不完整，但先把能用的返回，让请求端去暴露真实问题。
    /// </summary>
    public async Task<string> ProvisionBizApiKeyAsync(SubscriptionOAuthConfig config, string oauthAccessToken, CancellationToken ct = default)
    {
        var host = config.BizHost.TrimEnd('/');
        var keyName = config.ApiKeyName.Length > 0 ? config.ApiKeyName : "zcode-api-key";

        var customer = await BizRequestAsync(HttpMethod.Get, $"{host}/api/biz/customer/getCustomerInfo", oauthAccessToken, null, ct) as JsonObject;
        var org = PickByMarker(customer?["organizations"], "默认机构", "organizationName");
        var orgId = ScalarText(org?["organizationId"]);
        var project = PickByMarker(org?["projects"], "默认项目", "projectName");
        var projectId = ScalarText(project?["projectId"]);
        if (orgId is null || projectId is null)
            throw new OAuthProtocolException("biz_no_project", "账号下没有可用的机构/项目");

        var keysUrl = $"{host}/api/biz/v1/organization/{orgId}/projects/{projectId}/api_keys";
        var existing = await BizRequestAsync(HttpMethod.Get, keysUrl, oauthAccessToken, null, ct);
        var entry = (existing as JsonArray)?.OfType<JsonObject>().FirstOrDefault(r => ScalarText(r["name"]) == keyName)
                    ?? await BizRequestAsync(HttpMethod.Post, keysUrl, oauthAccessToken,
                        new JsonObject { ["name"] = keyName }.ToJsonString(), ct) as JsonObject;
        var apiKey = ScalarText(entry?["apiKey"])
                     ?? throw new OAuthProtocolException("invalid_token_response", "供应 API Key 失败：响应里没有 apiKey");

        try
        {
            var copied = await BizRequestAsync(HttpMethod.Get,
                $"{keysUrl}/copy/{Uri.EscapeDataString(apiKey)}", oauthAccessToken, null, ct) as JsonObject;
            if (ScalarText(copied?["secretKey"]) is { } secret) return $"{apiKey}.{secret}";
        }
        catch (OAuthProtocolException)
        {
            // 见方法注释：copy 失败不致命。
        }
        return apiKey;
    }

    /// <summary>biz API 请求：成功码是 {缺省, 0, 200, "0", "200"}（比换码那套的"非 0 即失败"宽），返回信封里的 <c>data</c>。</summary>
    private async Task<JsonNode?> BizRequestAsync(
        HttpMethod method, string url, string oauthAccessToken, string? jsonBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Authorization", oauthAccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (jsonBody is not null) request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var client = Create();
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new OAuthProtocolException($"http_{(int)response.StatusCode}", body is { Length: > 0 and <= 300 } ? body : null);
        var json = Parse(body);
        if (json["code"] is JsonValue c)
        {
            var code = c.TryGetValue<string>(out var text) ? text : c.ToJsonString();
            if (code is not ("0" or "200"))
                throw new OAuthProtocolException($"biz_{code}", Optional(json, "msg") ?? Optional(json, "message"));
        }
        return json["data"];
    }

    /// <summary>名字含 marker 的第一个，否则第 0 个（CLI 的 pickOrgAndProject 原样规则）。</summary>
    private static JsonObject? PickByMarker(JsonNode? list, string marker, string nameKey)
    {
        var records = (list as JsonArray)?.OfType<JsonObject>().ToList();
        if (records is null || records.Count == 0) return null;
        return records.FirstOrDefault(r => ScalarText(r[nameKey])?.Contains(marker, StringComparison.Ordinal) == true) ?? records[0];
    }

    /// <summary>字符串或数字节点的文本；空串/缺失/其它类型给 null（id 在有的响应里是数字）。</summary>
    private static string? ScalarText(JsonNode? node) =>
        node is JsonValue v
            ? (v.TryGetValue<string>(out var s) ? s : v.TryGetValue<long>(out var n) ? n.ToString() : null) is { Length: > 0 } text ? text : null
            : null;

    /// <summary>信封里的 access_token：先看渠道键（如 zai），再退回平铺形态。</summary>
    /// <summary>
    /// GitHub Copilot 的第二跳：拿 GitHub token（<c>token &lt;gho_…&gt;</c>）换 Copilot 短时令牌
    /// <c>GET/POST https://api.github.com/copilot_internal/v2/token</c>。
    /// 响应是 <c>{ token, expires_at, refresh_in, endpoints:{api,proxy}, sku, organization_list }</c>。
    /// 来源：官方 copilot-language-server main.js（tokenURL + Editor-* 身份头）。
    /// </summary>
    public async Task<CopilotToken> CopilotTokenAsync(
        SubscriptionOAuthConfig config, string githubToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, config.BusinessLoginUrl);
        request.Headers.TryAddWithoutValidation("Authorization", $"token {githubToken}");
        ApplyExtraHeaders(request, config);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Create().SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            // 先看状态码再解析：上游的错误体不一定是 JSON——api.github.com 对不认识的客户端会直接回
            // "403 Forbidden. …" 纯文本。若先 Parse，这种响应会被报成 invalid_token_response，
            // 把"这个账号没有 Copilot 订阅"变成一个看不懂的 JSON 错误。
            var error = TryParse(text);
            throw new OAuthProtocolException($"http_{(int)response.StatusCode}",
                error is null
                    ? (text.Length is > 0 and <= 200 ? text.Trim() : null)
                    : Optional(error, "message") ?? Optional(error, "error"));
        }
        var json = Parse(text);
        var token = Required(json, "token");
        var expiresAt = IntOptional(json, "expires_at");
        var refreshIn = IntOptional(json, "refresh_in");
        var apiBase = json["endpoints"] is JsonObject endpoints ? Optional(endpoints, "api") : null;
        return new CopilotToken(
            token,
            expiresAt is { } epoch ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null,
            refreshIn,
            apiBase,
            Optional(json, "sku"));
    }

    private static string? NestedAccessToken(JsonObject data, string provider)
    {
        if (data[provider] is JsonObject nested && Optional(nested, "access_token") is { } nestedToken) return nestedToken;
        return Optional(data, "access_token");
    }

    private async Task<JsonObject> PostZcodeEnvelopeAsync(
        SubscriptionOAuthConfig config, string url, Dictionary<string, object?> bodyFields, CancellationToken ct) =>
        await SendZcodeEnvelopeAsync(config, HttpMethod.Post, url, bodyFields, ct, authorization: null);

    private async Task<JsonObject> SendZcodeEnvelopeAsync(
        SubscriptionOAuthConfig config, HttpMethod method, string url, Dictionary<string, object?>? bodyFields,
        CancellationToken ct, string? authorization)
    {
        using var request = new HttpRequestMessage(method, url);
        if (bodyFields is not null)
            request.Content = new StringContent(Json.Serialize(bodyFields), Encoding.UTF8, "application/json");
        if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
        ApplyExtraHeaders(request, config);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var client = Create();
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var json = Parse(body);
        // 错误信封：非 2xx 也要按 code/msg 报出来（CLI 轮询的 3004 就藏在这里）。
        if (!response.IsSuccessStatusCode && json["code"] is null && json["data"] is null)
            throw new OAuthProtocolException($"http_{(int)response.StatusCode}", body is { Length: <= 300 } ? body : null);
        if (json["code"] is JsonValue codeNode && codeNode.TryGetValue<int>(out var code) && code != 0)
            throw new OAuthProtocolException($"zcode_{code}", Optional(json, "msg") ?? Optional(json, "message"));
        return json["data"] as JsonObject
               ?? throw new OAuthProtocolException("invalid_token_response", "响应里没有 data");
    }

    /// <summary>
    /// 可选的身份端点（登录后展示 email / 套餐名）。兼容 <c>{data:{…}}</c> 包装与平铺
    /// 两种形态；任何失败都返回空——装饰性字段不值得炸掉登录。
    /// </summary>
    public async Task<(string? Email, string? Plan)> FetchIdentityAsync(
        SubscriptionOAuthConfig config, string accessToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.IdentityUrl)) return (null, null);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, config.IdentityUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            ApplyExtraHeaders(request, config);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var client = Create();
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return (null, null);
            var json = Parse(await response.Content.ReadAsStringAsync(ct));
            var data = json["data"] as JsonObject ?? json;
            return (Optional(data, "email"),
                Optional(data, "user_level_name") ?? Optional(data, "plan_name") ?? Optional(data, "domain_name"));
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private static void ApplyExtraHeaders(HttpRequestMessage request, SubscriptionOAuthConfig config)
    {
        foreach (var (name, value) in config.ExtraHeaders)
            request.Headers.TryAddWithoutValidation(name, value);
    }

    public async Task<DevicePoll> PollDeviceAsync(SubscriptionOAuthConfig config, string deviceCode, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            ["device_code"] = deviceCode,
            ["client_id"] = config.ClientId,
        };
        try
        {
            var token = await SendTokenRequestAsync(config, form, ct);
            return new DevicePoll(DevicePollKind.Success, token, null, null);
        }
        catch (OAuthProtocolException e)
        {
            var kind = e.Error switch
            {
                "authorization_pending" => DevicePollKind.Pending,
                "slow_down" => DevicePollKind.SlowDown,
                "expired_token" or "expired_device_code" => DevicePollKind.Expired,
                "access_denied" => DevicePollKind.Denied,
                _ => DevicePollKind.Error,
            };
            return new DevicePoll(kind, null, e.Error, e.Description);
        }
    }

    private async Task<TokenResult> SendTokenRequestAsync(
        SubscriptionOAuthConfig config, Dictionary<string, string> form, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, config.TokenUrl) { Content = content };
        ApplyExtraHeaders(request, config);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var client = Create();
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var json = Parse(body);

        var error = Optional(json, "error");
        if (error is not null)
            throw new OAuthProtocolException(error, Optional(json, "error_description"));

        if (!response.IsSuccessStatusCode)
            throw new OAuthProtocolException($"http_{(int)response.StatusCode}", body is { Length: <= 300 } ? body : null);

        return new TokenResult(
            Required(json, "access_token"),
            Optional(json, "refresh_token"),
            IntOptional(json, "expires_in"),
            Optional(json, "id_token"),
            Optional(json, "scope"));
    }

    private static JsonObject Parse(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject
                   ?? throw new OAuthProtocolException("invalid_token_response", body is { Length: <= 300 } ? body : null);
        }
        catch (JsonException e)
        {
            throw new OAuthProtocolException("invalid_token_response", e.Message);
        }
    }

    /// <summary>Like <see cref="Parse"/> but returns null instead of throwing (for error bodies, which need not be JSON).</summary>
    private static JsonObject? TryParse(string body)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Required(JsonObject json, string key) =>
        Optional(json, key) ?? throw new OAuthProtocolException("invalid_token_response", $"missing '{key}'");

    private static string? Optional(JsonObject json, string key) =>
        json.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s)
            ? s
            : null;

    private static int? IntOptional(JsonObject json, string key) =>
        json.TryGetPropertyValue(key, out var node) && node is JsonValue v &&
        v.TryGetValue<int>(out var i) ? i : null;

    private static int IntOr(JsonObject json, string key, int fallback) => IntOptional(json, key) ?? fallback;
}
