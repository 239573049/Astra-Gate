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

    public sealed record DevicePoll(DevicePollKind Kind, TokenResult? Token, string? Error, string? Description);

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
        if (config.UsePkce) form["code_verifier"] = codeVerifier;
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
        var json = Parse(body);
        return new DeviceStart(
            Required(json, "device_code"),
            Required(json, "user_code"),
            Optional(json, "verification_uri") ?? Optional(json, "verification_url"),
            Optional(json, "verification_uri_complete"),
            IntOr(json, "expires_in", 600),
            IntOr(json, "interval", 5));
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
    private static string? NestedAccessToken(JsonObject data, string provider)
    {
        if (data[provider] is JsonObject nested && Optional(nested, "access_token") is { } nestedToken) return nestedToken;
        return Optional(data, "access_token");
    }

    private async Task<JsonObject> PostZcodeEnvelopeAsync(
        SubscriptionOAuthConfig config, string url, Dictionary<string, object?> bodyFields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(Json.Serialize(bodyFields), Encoding.UTF8, "application/json"),
        };
        ApplyExtraHeaders(request, config);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var client = Create();
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var json = Parse(body);
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
