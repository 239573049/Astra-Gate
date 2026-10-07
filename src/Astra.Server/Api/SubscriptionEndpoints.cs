using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;
using Astra.Server.Hosting;
using Astra.Server.Security;

namespace Astra.Server.Api;

/// <summary>In-memory pending OAuth logins (PKCE verifiers / device codes); entries expire after 10 minutes.</summary>
public sealed class PendingSubscriptionLogins
{
    public sealed record PendingLogin(
        string State,
        string ProviderId,
        string? AccountId,
        SubscriptionOAuthConfig Config,
        string? CodeVerifier,
        string? DeviceCode,
        DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, PendingLogin> _pending = new(StringComparer.Ordinal);

    public void Put(PendingLogin login)
    {
        Sweep();
        _pending[login.State] = login;
    }

    /// <summary>Removes and returns the login (a completed or failed attempt consumes its state).</summary>
    public PendingLogin? Take(string state)
    {
        Sweep();
        return _pending.TryRemove(state, out var login) ? login : null;
    }

    public PendingLogin? Peek(string state)
    {
        Sweep();
        return _pending.TryGetValue(state, out var login) ? login : null;
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (state, login) in _pending)
            if (login.ExpiresAt < now)
                _pending.TryRemove(state, out _);
    }
}

/// <summary>Admin API for subscription accounts (plan §5.4): list, login (PKCE / device), refresh, logout.</summary>
public static class SubscriptionEndpoints
{
    private static readonly TimeSpan LoginTtl = TimeSpan.FromMinutes(10);

    public sealed record LoginOptions(string? AccountId);

    public static void MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        // ----- accounts (secrets never leave the server) -----

        app.MapGet("/api/providers/{providerId}/accounts", async (string providerId, AstraDatabase db) =>
            Results.Ok((await db.Accounts.ListAsync(providerId)).Select(ToDto)));

        app.MapDelete("/api/provider-accounts/{id}", async (string id, AstraDatabase db) =>
        {
            var removed = await db.Accounts.DeleteAsync(id);
            return removed ? Results.Ok(new { removed = true }) : Results.NotFound(new { error = "账号不存在" });
        });

        app.MapPost("/api/provider-accounts/{id}/refresh", async (
            string id, AstraDatabase db, SubscriptionTokenService tokens) =>
        {
            var account = await db.Accounts.GetAsync(id);
            if (account is null) return Results.NotFound(new { error = "账号不存在" });
            var provider = await db.Providers.GetAsync(account.ProviderId);
            if (provider is null) return Results.NotFound(new { error = "所属提供商不存在" });

            try
            {
                var updated = await tokens.RefreshAsync(account, SubscriptionSupport.EffectiveConfig(provider), force: true);
                return Results.Ok(ToDto(updated));
            }
            catch (SubscriptionAuthException e)
            {
                var status = (await db.Accounts.GetAsync(id))?.Status ?? AccountStatus.Expired;
                return Results.Json(new { error = e.Message, status }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        // 额度查询（plan §5.4）：实时拉取上游用量并持久化快照；失效账号会被自动禁用（409 + status）。
        app.MapPost("/api/provider-accounts/{id}/quota", async (
            string id, AstraDatabase db, SubscriptionQuotaService quotaService) =>
        {
            var account = await db.Accounts.GetAsync(id);
            if (account is null) return Results.NotFound(new { error = "账号不存在" });
            var provider = await db.Providers.GetAsync(account.ProviderId);
            if (provider is null) return Results.NotFound(new { error = "所属提供商不存在" });

            try
            {
                var (updated, quota) = await quotaService.FetchAsync(provider, account, SubscriptionSupport.EffectiveConfig(provider));
                return quota is null
                    ? Results.Json(new { error = "该订阅暂不支持额度查询" }, statusCode: StatusCodes.Status501NotImplemented)
                    : Results.Ok(ToDto(updated));
            }
            catch (SubscriptionAuthException e)
            {
                var status = (await db.Accounts.GetAsync(id))?.Status ?? AccountStatus.Expired;
                return Results.Json(new { error = e.Message, status }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        // ----- login flows -----

        app.MapPost("/api/providers/{providerId}/accounts/login", async (
            string providerId, LoginOptions? body, AstraDatabase db, ServerOptions options,
            PendingSubscriptionLogins pending, OAuthClient client, CancellationToken ct) =>
        {
            var provider = await db.Providers.GetAsync(providerId, ct);
            if (provider is null) return Results.NotFound(new { error = "提供商不存在" });
            if (provider.AuthScheme != AuthSchemes.OAuthSubscription)
                return Results.BadRequest(new { error = "该提供商不是订阅类型（auth_scheme ≠ oauth-subscription）" });

            var config = SubscriptionSupport.EffectiveConfig(provider);
            if (!SubscriptionCatalog.IsReady(config))
                return Results.Json(new
                {
                    error = "该订阅的 OAuth 流程尚未核实或配置不完整，暂不能登录（可在提供商设置中补全 subscription_oauth）",
                    needsVerification = true,
                }, statusCode: StatusCodes.Status400BadRequest);

            var state = Ulid.NewUlid();
            if (config.UsePkce || config.Style == "zcode")
            {
                // PKCE 授权码（Claude/Codex）与 ZCode 形态（无 PKCE、无 scope，BuildAuthorizeUrl
                // 按配置自动省略）都走浏览器回环回调；区别在回调完成时的换码请求。
                var (verifier, challenge) = Pkce.Create();
                var redirectUri = $"http://127.0.0.1:{options.Port}/api/oauth/callback";
                pending.Put(new PendingSubscriptionLogins.PendingLogin(
                    state, providerId, body?.AccountId, config, verifier, null, DateTimeOffset.UtcNow + LoginTtl));
                return Results.Ok(new
                {
                    mode = "pkce",
                    state,
                    authorizeUrl = OAuthClient.BuildAuthorizeUrl(config, redirectUri, state, challenge),
                });
            }

            var device = await client.StartDeviceAsync(config, ct);
            pending.Put(new PendingSubscriptionLogins.PendingLogin(
                state, providerId, body?.AccountId, config, null, device.DeviceCode,
                DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(device.ExpiresIn, 60))));
            return Results.Ok(new
            {
                mode = "device",
                state,
                userCode = device.UserCode,
                verificationUrl = device.VerificationUrlComplete ?? device.VerificationUrl,
                interval = device.Interval,
            });
        });

        // OAuth redirect target (system browser). Completes the authorization-code exchange and shows a local HTML page.
        app.MapGet("/api/oauth/callback", async (
            HttpContext ctx, AstraDatabase db, ServerOptions options, ISecretProtector protector,
            PendingSubscriptionLogins pending, OAuthClient client) =>
        {
            var error = ctx.Request.Query["error"].ToString();
            var state = ctx.Request.Query["state"].ToString();
            if (!string.IsNullOrEmpty(error))
                return Html($"授权被拒绝：{error}");

            var login = pending.Take(state);
            if (login is null)
                return Html("登录会话不存在或已过期，请在 Astra 中重新发起登录。");
            // PKCE 登录必须带着 verifier 完成；ZCode 形态没有 verifier（本就不发 code_challenge）。
            var isPkce = login.Config.UsePkce;
            if (isPkce != (login.CodeVerifier is not null))
                return Html("登录会话不存在或已过期，请在 Astra 中重新发起登录。");

            // ZCode 的回调码参数名是 authCode（bigmodel 渠道）；Z.AI 渠道实测用标准 code。
            var code = ctx.Request.Query["code"].ToString();
            if (string.IsNullOrEmpty(code) && login.Config.Style == "zcode")
                code = ctx.Request.Query["authCode"].ToString();
            if (string.IsNullOrEmpty(code)) return Html("回调缺少授权码。");

            var redirectUri = $"http://127.0.0.1:{options.Port}/api/oauth/callback";
            try
            {
                var token = isPkce
                    ? await client.ExchangeCodeAsync(login.Config, code, login.CodeVerifier!, redirectUri)
                    : await CompleteZcodeLoginAsync(client, login.Config, code, redirectUri, state);
                var account = await CompleteLoginAsync(db, protector, client, login, token);
                return Html($"授权完成（{account.DisplayName}），请返回 Astra。");
            }
            catch (OAuthProtocolException e)
            {
                return Html($"换取令牌失败：{e.Message}");
            }
        });

        // Device-flow polling (the UI calls this every `interval` seconds until done/expired).
        app.MapPost("/api/providers/{providerId}/accounts/login/{state}/poll", async (
            string providerId, string state, AstraDatabase db, ISecretProtector protector,
            PendingSubscriptionLogins pending, OAuthClient client, CancellationToken ct) =>
        {
            var login = pending.Peek(state);
            if (login is null || login.DeviceCode is null)
                return Results.NotFound(new { status = "expired", error = "登录会话不存在或已过期" });

            var poll = await client.PollDeviceAsync(login.Config, login.DeviceCode, ct);
            switch (poll.Kind)
            {
                case OAuthClient.DevicePollKind.Pending:
                    return Results.Ok(new { status = "pending" });
                case OAuthClient.DevicePollKind.SlowDown:
                    return Results.Ok(new { status = "slow_down" });
                case OAuthClient.DevicePollKind.Success when poll.Token is not null:
                {
                    pending.Take(state);
                    var account = await CompleteLoginAsync(db, protector, client, login, poll.Token);
                    return Results.Ok(new { status = "done", account = ToDto(account) });
                }
                default:
                    pending.Take(state);
                    return Results.Json(new
                    {
                        status = poll.Kind.ToString().ToLowerInvariant(),
                        error = poll.Description ?? poll.Error,
                    }, statusCode: StatusCodes.Status400BadRequest);
            }
        });
    }

    /// <summary>Persists the exchanged tokens (encrypted) as a new or updated account row.</summary>
    private static async Task<ProviderAccount> CompleteLoginAsync(
        AstraDatabase db, ISecretProtector protector, OAuthClient client,
        PendingSubscriptionLogins.PendingLogin login, OAuthClient.TokenResult token)
    {
        var email = EmailFromIdToken(token.IdToken);
        string? plan = null;
        if (login.Config.IdentityUrl.Length > 0)
        {
            // 没有 id_token 的家族（Kimi / ZCode）从这里补 email 与套餐名；装饰性字段，失败不致命。
            // ZCode 的身份端点认的是 OAuth token（在刷新槽里），不是换出来的业务 JWT。
            var identityToken = login.Config.Style == "zcode" ? token.RefreshToken ?? token.AccessToken : token.AccessToken;
            var (identityEmail, identityPlan) = await client.FetchIdentityAsync(login.Config, identityToken);
            email ??= identityEmail;
            plan = identityPlan;
        }
        var now = DateTimeOffset.UtcNow;

        var account = login.AccountId is not null ? await db.Accounts.GetAsync(login.AccountId) : null;
        if (account is null)
        {
            account = new ProviderAccount
            {
                Id = login.AccountId ?? Ulid.NewUlid(),
                ProviderId = login.ProviderId,
                CreatedAt = now,
            };
        }

        account.DisplayName = email ?? account.DisplayName ?? "";
        if (account.DisplayName.Length == 0) account.DisplayName = login.Config.DisplayName;
        account.AccountEmail = email ?? account.AccountEmail;
        account.Plan = plan ?? account.Plan;
        account.AccessTokenEnc = protector.Protect(token.AccessToken);
        account.RefreshTokenEnc = token.RefreshToken is null ? account.RefreshTokenEnc : protector.Protect(token.RefreshToken);
        account.ExpiresAtUtc = SubscriptionTokenService.ExpiresAt(token, now);
        account.Status = AccountStatus.Active;
        account.LastRefreshAtUtc = now;
        account.UpdatedAt = now;

        if (account.CreatedAt == default)
            await db.Accounts.InsertAsync(account);
        else
            await db.Accounts.UpdateAsync(account);
        return account;
    }

    /// <summary>
    /// ZCode 链路的回调完成：换码（<c>{provider, code, redirect_uri, state}</c> 信封）→
    /// 业务登录拿真正的调用令牌。OAuth token 进刷新槽（刷新 = 重跑业务登录），业务 JWT
    /// 是访问令牌，过期从 JWT exp 折算、折不出给保守的 1 小时。
    /// </summary>
    private static async Task<OAuthClient.TokenResult> CompleteZcodeLoginAsync(
        OAuthClient client, SubscriptionOAuthConfig config, string code, string redirectUri, string state)
    {
        var provider = config.ProviderKey switch
        {
            // 换码 body 里的 provider 字段按渠道取值（逆向实测：zai 认识，bigmodel/zcode 不认识）。
            "zcode-subscription" => "zai",
            var key => key.Replace("-subscription", ""),
        };
        var oauthToken = await client.ExchangeZcodeCodeAsync(config, provider, code, redirectUri, state);
        var apiToken = await client.ZcodeBusinessLoginAsync(config, oauthToken);
        var expiresIn = SubscriptionTokenService.ExpiresInSeconds(apiToken, DateTimeOffset.UtcNow) ?? 3600;
        return new OAuthClient.TokenResult(apiToken, oauthToken, expiresIn, null, null);
    }

    /// <summary>Best-effort email claim from the id_token payload (display only; never verified here).</summary>
    private static string? EmailFromIdToken(string? idToken)
    {
        if (string.IsNullOrEmpty(idToken)) return null;
        var parts = idToken.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = Base64UrlDecode(parts[1]);
            var json = JsonNode.Parse(payload);
            return json?["email"] is JsonValue v && v.TryGetValue<string>(out var email) ? email : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Base64UrlDecode(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        var rem = (4 - padded.Length % 4) % 4;
        padded += rem switch { 2 => "==", 3 => "=", _ => "" };
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static object ToDto(ProviderAccount a) => new
    {
        id = a.Id,
        providerId = a.ProviderId,
        displayName = a.DisplayName,
        accountEmail = a.AccountEmail,
        plan = a.Plan,
        status = a.Status,
        expiresAtUtc = a.ExpiresAtUtc,
        lastRefreshAtUtc = a.LastRefreshAtUtc,
        quota = a.Extra["quota"],
        createdAt = a.CreatedAt,
    };

    private static IResult Html(string message) => Results.Content(
        $"""<!doctype html><html lang="zh"><head><meta charset="utf-8"><title>Astra</title></head>""" +
        $"""<body style="font-family:-apple-system,sans-serif;display:grid;place-items:center;height:100vh;margin:0">""" +
        $"""<p style="font-size:15px">{message}</p></body></html>""",
        "text/html; charset=utf-8");
}
