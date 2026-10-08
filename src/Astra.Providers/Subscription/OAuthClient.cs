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
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = config.ClientId,
        };
        return await SendTokenRequestAsync(config, form, ct);
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
            return new ZcodeCliPoll(ZcodeCliPollKind.Done,
                Optional(data, "accessToken") ?? Optional(data, "access_token"), null, null);
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
        var json = Parse(text);
        if (!response.IsSuccessStatusCode)
        {
            // 401/403 → GitHub token 失效或没有 Copilot 订阅；403 也可能是没开 Copilot。
            throw new OAuthProtocolException($"http_{(int)response.StatusCode}",
                Optional(json, "message") ?? Optional(json, "error") ?? (text.Length <= 200 ? text : null));
        }
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
