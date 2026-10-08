using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Astra.Clients.Config;
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
        DateTimeOffset ExpiresAt,
        /// <summary>The family's own device poll (standard device_code vs OpenAI deviceauth, which needs the returned id + user code).</summary>
        Func<OAuthClient, CancellationToken, Task<OAuthClient.DevicePoll>>? PollAsync = null,
        /// <summary>ZAI CLI 链路：flow id 与本次发起用的 poll_token（授权链接由上游下发）。</summary>
        string? CliFlowId = null,
        string? CliPollToken = null,
        /// <summary>codex-cli 登录期间临时接管 1455/1457 的接收器；换码用它自己的回调地址。</summary>
        LoopbackCaptureListener? Capture = null,
        /// <summary>The exact redirect_uri sent in the authorize request (must match verbatim at exchange time).</summary>
        string? RedirectUri = null);

    private readonly ConcurrentDictionary<string, PendingLogin> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// 回调接收器的持有者：给接收器自己一个句柄，好在响应页面写完之后才释放端口
    /// （先释放会让浏览器拿到空响应）。
    /// </summary>
    public sealed class CaptureSlot
    {
        public LoopbackCaptureListener? Listener { get; set; }

        public void Release() => _ = Listener?.DisposeAsync();
    }

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

    /// <summary>A subscription account as shown in the admin API; tokens are never included.</summary>
    public sealed record ProviderAccountDto(
        string Id, string ProviderId, string DisplayName, string? AccountEmail, string? Plan, string Status,
        DateTimeOffset? ExpiresAtUtc, DateTimeOffset? LastRefreshAtUtc, JsonNode? Quota, DateTimeOffset CreatedAt,
        /// <summary>codex 的额度重置卡列表（<c>extra.credits</c> 快照；列表接口实时刷新）。</summary>
        JsonNode? Credits = null,
        bool Enabled = true,
        /// <summary>The account requests currently go to (without a client / token pin).</summary>
        bool IsCurrent = false,
        int SortOrder = 0,
        DateTimeOffset? CooldownUntilUtc = null,
        string? LastError = null);

    /// <summary>PATCH body for one account; omitted fields keep their value.</summary>
    public sealed record AccountPatch(bool? Enabled, string? DisplayName);

    /// <summary>New failover order (account ids, first = tried first).</summary>
    public sealed record AccountOrder(List<string> Ids);

    /// <summary>
    /// Subscription policy of a provider: who may use it (<c>claude-code-only</c> | <c>any</c>) and how accounts switch
    /// (<c>manual</c> | <c>failover</c>). <see cref="ClaudeSubscription"/> tells the UI whether the client policy
    /// applies by default.
    /// </summary>
    public sealed record SubscriptionPolicyDto(string ClientPolicy, string SwitchMode, bool ClaudeSubscription);

    /// <summary>PUT body for the subscription policy; omitted fields keep their value.</summary>
    public sealed record SubscriptionPolicyPatch(string? ClientPolicy, string? SwitchMode);

    /// <summary>Authorization-code (PKCE) login start; the UI opens <see cref="AuthorizeUrl"/>.</summary>
    public sealed record LoginModeDto(string Mode, string State, string AuthorizeUrl);

    /// <summary>Device-code login start; the UI shows <see cref="UserCode"/> and polls.</summary>
    public sealed record LoginDeviceDto(string Mode, string State, string? UserCode, string VerificationUrl, int Interval);

    /// <summary>Server-mediated CLI login start (zcli); the UI opens <see cref="AuthorizeUrl"/> and polls.</summary>
    public sealed record LoginCliDto(string Mode, string State, string AuthorizeUrl, int Interval);

    /// <summary>Device-flow poll heartbeat (status = pending | slow_down).</summary>
    public sealed record LoginPollDto(string Status);

    /// <summary>Terminal device-flow poll: success carries the account, anything else the error.</summary>
    public sealed record PollFailureDto(string Status, string? Error);

    /// <summary>Successful device-flow poll.</summary>
    public sealed record LoginDoneDto(ProviderAccountDto Account)
    {
        public string Status => "done";
    }

    /// <summary>Whether the machine already holds a usable <c>codex login</c> credential set.</summary>
    public sealed record LocalCodexLoginDto(bool Available, string? AccountEmail, string? Plan, string? Detail);

    /// <summary>Imported account + a fresh quota snapshot (when the probe succeeded).</summary>
    public sealed record ImportedAccountDto(ProviderAccountDto Account, JsonNode? Quota, string? Warning);

    /// <summary>After consuming a reset credit: the account and its refreshed quota snapshot.</summary>
    public sealed record ResetCreditResultDto(ProviderAccountDto Account, JsonNode? Quota);

    public static void MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        // ----- accounts (secrets never leave the server) -----

        app.MapGet("/api/providers/{providerId}/accounts", async (string providerId, AstraDatabase db) =>
            Results.Ok(await ListDtosAsync(db, providerId)));

        // ----- switching (plan §5.4): current account, enable switch, failover order, policy -----

        // "切换到此账号"：成为提供商的当前账号（停用的账号顺带启用），立即对下一个请求生效。
        // 客户端 / 令牌上固定了账号的请求不受影响。
        app.MapPost("/api/provider-accounts/{id}/activate", async (string id, AstraDatabase db) =>
        {
            var account = await db.Accounts.GetAsync(id);
            if (account is null) return Results.NotFound(new ErrorOnlyDto("账号不存在"));
            if (account.Status != AccountStatus.Active)
                return ApiJson.Result(new ErrorStatusDto("账号登录已失效，请先重新登录", account.Status), StatusCodes.Status409Conflict);
            if (!account.Enabled) await db.Accounts.SetEnabledAsync(id, true);
            await db.Accounts.SetCurrentAsync(account.ProviderId, id);
            return Results.Ok(await ListDtosAsync(db, account.ProviderId));
        });

        app.MapPatch("/api/provider-accounts/{id}", async (string id, AccountPatch body, AstraDatabase db) =>
        {
            var account = await db.Accounts.GetAsync(id);
            if (account is null) return Results.NotFound(new ErrorOnlyDto("账号不存在"));
            if (body.DisplayName is { } name)
            {
                if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest(new ErrorOnlyDto("账号名称不能为空"));
                await db.Accounts.SetDisplayNameAsync(id, name.Trim());
            }
            if (body.Enabled is { } enabled)
            {
                await db.Accounts.SetEnabledAsync(id, enabled);
                // A disabled account cannot stay current: the next usable one takes over by order.
                if (!enabled && account.IsCurrent) await db.Accounts.SetCurrentAsync(account.ProviderId, null);
            }
            return Results.Ok(await ListDtosAsync(db, account.ProviderId));
        });

        app.MapPut("/api/providers/{providerId}/accounts/order", async (string providerId, AccountOrder body, AstraDatabase db) =>
        {
            if (await db.Providers.GetAsync(providerId) is null) return Results.NotFound(new ErrorOnlyDto("提供商不存在"));
            await db.Accounts.ReorderAsync(providerId, body.Ids ?? []);
            return Results.Ok(await ListDtosAsync(db, providerId));
        });

        app.MapGet("/api/providers/{providerId}/subscription-policy", async (string providerId, AstraDatabase db) =>
            await db.Providers.GetAsync(providerId) is { } provider
                ? Results.Ok(PolicyOf(provider))
                : Results.NotFound(new ErrorOnlyDto("提供商不存在")));

        app.MapPut("/api/providers/{providerId}/subscription-policy", async (
            string providerId, SubscriptionPolicyPatch body, AstraDatabase db) =>
        {
            var provider = await db.Providers.GetAsync(providerId);
            if (provider is null) return Results.NotFound(new ErrorOnlyDto("提供商不存在"));
            if (provider.AuthScheme != AuthSchemes.OAuthSubscription)
                return Results.BadRequest(new ErrorOnlyDto("该提供商不是订阅类型（auth_scheme ≠ oauth-subscription）"));
            if (body.ClientPolicy is { } policy && !ClientPolicies.All.Contains(policy))
                return Results.BadRequest(new ErrorOnlyDto($"未知的客户端策略：{policy}"));
            if (body.SwitchMode is { } mode && !SwitchModes.All.Contains(mode))
                return Results.BadRequest(new ErrorOnlyDto($"未知的切换模式：{mode}"));

            var section = provider.Settings["subscription"] as JsonObject ?? new JsonObject();
            if (body.ClientPolicy is not null) section["client_policy"] = body.ClientPolicy;
            if (body.SwitchMode is not null) section["switch_mode"] = body.SwitchMode;
            provider.Settings["subscription"] = section;
            await db.Providers.UpdateAsync(provider);
            return Results.Ok(PolicyOf(provider));
        });

        // 本机是否已有 `codex login` 的登录态（~/.codex/auth.json）——有的话可以直接导入，
        // 不必再走一遍浏览器授权。凭据内容永远不外传，只回是否可用与展示用信息。
        app.MapGet("/api/subscription/codex/local-login", (ClientEnvironment env) => ToLocalDto(env));

        // 把本机 codex 的登录态接进来：整份凭据（含刷新令牌）原样加密入库，之后刷新走标准 OAuth。
        app.MapPost("/api/providers/{providerId}/accounts/import-codex", async (
            string providerId, AstraDatabase db, ISecretProtector protector, ClientEnvironment env,
            SubscriptionTokenService tokens, SubscriptionQuotaService quotaService, CancellationToken ct) =>
        {
            var provider = await db.Providers.GetAsync(providerId, ct);
            if (provider is null) return Results.NotFound(new ErrorOnlyDto("提供商不存在"));
            if (provider.AuthScheme != AuthSchemes.OAuthSubscription)
                return Results.BadRequest(new ErrorOnlyDto("该提供商不是订阅类型（auth_scheme ≠ oauth-subscription）"));
            var config = SubscriptionSupport.EffectiveConfig(provider);
            if (config.ProviderKey != "openai-subscription")
                return Results.BadRequest(new ErrorOnlyDto("只有 ChatGPT 订阅（Codex）可以从本机 Codex 导入登录态"));

            var path = CodexAuthPath(env);
            var text = path is null ? null : env.ReadTextOrNull(path);
            if (text is null) return Results.NotFound(new ErrorOnlyDto("没有找到本机的 Codex 登录文件（~/.codex/auth.json）"));
            var credentials = CodexAuthFile.Parse(text, out var parseError);
            if (credentials is null) return Results.BadRequest(new ErrorOnlyDto(parseError ?? "本机 Codex 登录文件不可用"));

            var token = new OAuthClient.TokenResult(
                credentials.AccessToken, credentials.RefreshToken,
                SubscriptionTokenService.ExpiresInSeconds(credentials.AccessToken, DateTimeOffset.UtcNow),
                credentials.IdToken, null);
            var account = await CompleteLoginAsync(db, protector, null, new PendingSubscriptionLogins.PendingLogin(
                Ulid.NewUlid(), providerId, null, config, credentials.AccessToken, null,
                DateTimeOffset.UtcNow + LoginTtl, null, null, null, null), token);

            // 立刻验一次，确认这份凭据现在还能用（额度探测失败不致命，只回一个警告）。
            string? warning = null;
            var snapshot = (JsonNode?)null;
            try
            {
                var (updated, quota) = await quotaService.FetchAsync(provider, account, config, ct);
                account = updated;
                snapshot = quota;
            }
            catch (Exception e) when (e is SubscriptionAuthException or OAuthProtocolException)
            {
                warning = e.Message;
            }
            return Results.Ok(new ImportedAccountDto(ToDto(account), snapshot, warning));
        });

        app.MapDelete("/api/provider-accounts/{id}", async (string id, AstraDatabase db) =>
        {
            var removed = await db.Accounts.DeleteAsync(id);
            return removed ? Results.Ok(new RemovedDto(true)) : Results.NotFound(new ErrorOnlyDto("账号不存在"));
        });

        app.MapPost("/api/provider-accounts/{id}/refresh", async (
            string id, AstraDatabase db, SubscriptionTokenService tokens) =>
        {
            var account = await db.Accounts.GetAsync(id);
            if (account is null) return Results.NotFound(new ErrorOnlyDto("账号不存在"));
            var provider = await db.Providers.GetAsync(account.ProviderId);
            if (provider is null) return Results.NotFound(new ErrorOnlyDto("所属提供商不存在"));

            try
            {
                var updated = await tokens.RefreshAsync(account, SubscriptionSupport.EffectiveConfig(provider), force: true);
                return Results.Ok(ToDto(updated));
            }
            catch (SubscriptionAuthException e)
            {
                var status = (await db.Accounts.GetAsync(id))?.Status ?? AccountStatus.Expired;
                return ApiJson.Result(new ErrorStatusDto(e.Message, status), StatusCodes.Status409Conflict);
            }
        });

        // 额度查询（plan §5.4）：实时拉取上游用量并持久化快照；失效账号会被自动禁用（409 + status）。
        app.MapPost("/api/provider-accounts/{id}/quota", async (
            string id, AstraDatabase db, SubscriptionQuotaService quotaService) =>
        {
            var account = await db.Accounts.GetAsync(id);
            if (account is null) return Results.NotFound(new ErrorOnlyDto("账号不存在"));
            var provider = await db.Providers.GetAsync(account.ProviderId);
            if (provider is null) return Results.NotFound(new ErrorOnlyDto("所属提供商不存在"));

            try
            {
                var (updated, quota) = await quotaService.FetchAsync(provider, account, SubscriptionSupport.EffectiveConfig(provider));
                return quota is null
                    ? ApiJson.Result(new ErrorOnlyDto("该订阅暂不支持额度查询"), StatusCodes.Status501NotImplemented)
                    : Results.Ok(ToDto(updated));
            }
            catch (SubscriptionAuthException e)
            {
                var status = (await db.Accounts.GetAsync(id))?.Status ?? AccountStatus.Expired;
                return ApiJson.Result(new ErrorStatusDto(e.Message, status), StatusCodes.Status409Conflict);
            }
        });

        // 重置卡（codex）：列出账号手上的额度重置卡。卡有 status（可用／已用／过期），所以每次实时拉。
        app.MapGet("/api/provider-accounts/{id}/reset-credits", async (
            string id, AstraDatabase db, SubscriptionQuotaService quotaService) =>
        {
            var account = await db.Accounts.GetAsync(id);
            if (account is null) return Results.NotFound(new ErrorOnlyDto("账号不存在"));
            var provider = await db.Providers.GetAsync(account.ProviderId);
            if (provider is null) return Results.NotFound(new ErrorOnlyDto("所属提供商不存在"));
            if (!SubscriptionQuotaService.SupportsResetCredits(provider))
                return ApiJson.Result(new ErrorOnlyDto("该订阅没有重置卡"), StatusCodes.Status501NotImplemented);

            try
            {
                var credits = await quotaService.ListResetCreditsAsync(provider, account, SubscriptionSupport.EffectiveConfig(provider));
                return credits is null
                    ? ApiJson.Result(new ErrorOnlyDto("上游没有返回重置卡信息"), StatusCodes.Status501NotImplemented)
                    : Results.Ok(credits);
            }
            catch (SubscriptionAuthException e)
            {
                var status = (await db.Accounts.GetAsync(id))?.Status ?? AccountStatus.Expired;
                return ApiJson.Result(new ErrorStatusDto(e.Message, status), StatusCodes.Status409Conflict);
            }
            catch (OAuthProtocolException e)
            {
                return ApiJson.Result(new ErrorOnlyDto(e.Message), StatusCodes.Status502BadGateway);
            }
        });

        // 用掉一张重置卡；成功后再拉一次额度，让 UI 立刻看到新的限流窗口。
        app.MapPost("/api/provider-accounts/{id}/reset-credits/{creditId}/consume", async (
            string id, string creditId, AstraDatabase db, SubscriptionQuotaService quotaService) =>
        {
            var account = await db.Accounts.GetAsync(id);
            if (account is null) return Results.NotFound(new ErrorOnlyDto("账号不存在"));
            var provider = await db.Providers.GetAsync(account.ProviderId);
            if (provider is null) return Results.NotFound(new ErrorOnlyDto("所属提供商不存在"));
            if (!SubscriptionQuotaService.SupportsResetCredits(provider))
                return ApiJson.Result(new ErrorOnlyDto("该订阅没有重置卡"), StatusCodes.Status501NotImplemented);

            var config = SubscriptionSupport.EffectiveConfig(provider);
            try
            {
                await quotaService.ConsumeResetCreditAsync(provider, account, config, creditId);
            }
            catch (SubscriptionAuthException e)
            {
                var status = (await db.Accounts.GetAsync(id))?.Status ?? AccountStatus.Expired;
                return ApiJson.Result(new ErrorStatusDto(e.Message, status), StatusCodes.Status409Conflict);
            }
            catch (OAuthProtocolException e)
            {
                return ApiJson.Result(new ErrorOnlyDto(e.Message), StatusCodes.Status409Conflict);
            }

            // 重置后额度窗口通常立刻变化：顺手刷新，失败也不影响"重置成功"这个结果。
            var refreshed = await db.Accounts.GetAsync(id);
            var quota = refreshed?.Extra["quota"];
            try
            {
                var (updated, fresh) = await quotaService.FetchAsync(provider, refreshed ?? account, config);
                account = updated;
                quota = fresh ?? quota;
            }
            catch (Exception e) when (e is SubscriptionAuthException or OAuthProtocolException)
            {
                // 保留旧快照
            }
            return Results.Ok(new ResetCreditResultDto(ToDto(account), quota));
        });

        // ----- login flows -----

        app.MapPost("/api/providers/{providerId}/accounts/login", async (
            string providerId, LoginOptions? body, AstraDatabase db, ServerOptions options,
            ISecretProtector protector, PendingSubscriptionLogins pending, OAuthClient client,
            ILoggerFactory loggerFactory, CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("SubscriptionLogin");
            var provider = await db.Providers.GetAsync(providerId, ct);
            if (provider is null) return Results.NotFound(new ErrorOnlyDto("提供商不存在"));
            if (provider.AuthScheme != AuthSchemes.OAuthSubscription)
                return Results.BadRequest(new ErrorOnlyDto("该提供商不是订阅类型（auth_scheme ≠ oauth-subscription）"));

            var config = SubscriptionSupport.EffectiveConfig(provider);
            if (!SubscriptionCatalog.IsReady(config))
                return ApiJson.Result(new ErrorVerificationDto(
                    "该订阅的 OAuth 流程尚未核实或配置不完整，暂不能登录（可在提供商设置中补全 subscription_oauth）", true),
                    StatusCodes.Status400BadRequest);

            var state = Ulid.NewUlid();
            if (config.Style == "zcli")
            {
                // ZAI CLI 链路：官方提供的授权（无回调）。poll_token 本地随机，除非上游下发自己的。
                var pollToken = Pkce.Create().Verifier[..32];
                // 渠道键与换码一致：CLI init 的 provider 取 "zai"（zcode2api 实测），bigmodel 取自己的键。
                var channel = config.ProviderKey == "zcode-subscription" ? "zai" : config.ProviderKey.Replace("-subscription", "");
                var start = await client.StartZcodeCliAsync(config, channel, pollToken, ct);
                pending.Put(new PendingSubscriptionLogins.PendingLogin(
                    state, providerId, body?.AccountId, config, null, "cli", DateTimeOffset.UtcNow + LoginTtl,
                    null, start.FlowId, start.PollToken));
                return Results.Ok(new LoginCliDto("cli", state, start.AuthorizeUrl, 3));
            }

            if (config.UsePkce || config.Style == "zcode")
            {
                // PKCE 授权码（Claude）与 ZCode 形态（无 PKCE、无 scope，BuildAuthorizeUrl
                // 按配置自动省略）都走浏览器回环回调；区别在回调完成时的换码请求。
                var (verifier, challenge) = Pkce.Create();
                var redirectUri = $"http://127.0.0.1:{options.Port}/api/oauth/callback";
                var capture = (LoopbackCaptureListener?)null;
                if (config.RedirectUriOverride.Length > 0)
                {
                    // 实例显式指定了回调（例如自己已经跑着 codex 的 1455 接收器）。
                    redirectUri = config.RedirectUriOverride;
                }
                else if (SubscriptionCatalog.IsCodexLogin(config))
                {
                    // codex 的 client 只认自己的回调端口，Astra 监听的端口不在白名单里：
                    // 登录期间临时接管 1455/1457，收到 code 后直接在本进程内完成换码
                    //（不走 HTTP 回环，测试宿主没有真实端口也能跑）。页面写完之后才释放端口。
                    var captureSlot = new PendingSubscriptionLogins.CaptureSlot();
                    capture = LoopbackCaptureListener.TryStart(SubscriptionCatalog.CodexLoopbackPorts,
                        SubscriptionCatalog.CodexLoopbackPath,
                        async query =>
                        {
                            var captured = pending.Take(state);
                            if (captured is null)
                                return new LoopbackCaptureResult(false, "登录会话不存在或已过期，请在 Astra 中重新发起登录。");
                            captureSlot.Listener = captured.Capture;
                            return await CompleteCallbackAsync(db, protector, client, captured,
                                code: query.GetValueOrDefault("code", ""),
                                error: query.GetValueOrDefault("error", ""),
                                state: state,
                                redirectUri: captured.RedirectUri ?? $"http://localhost:{options.Port}{SubscriptionCatalog.CodexLoopbackPath}");
                        }, logger, () => captureSlot.Release());
                    captureSlot.Listener = capture;
                    if (capture is null)
                        return ApiJson.Result(new ErrorOnlyDto(
                            "codex 登录需要占用 http://localhost:1455 或 1457 接收授权回调，这两个端口都被占用了；" +
                            "可先关闭正在运行的 Codex CLI，或在提供商设置里改用设备码登录（subscription_oauth.style = deviceauth）"),
                            StatusCodes.Status409Conflict);
                    redirectUri = $"http://localhost:{capture.Port}{SubscriptionCatalog.CodexLoopbackPath}";
                }
                pending.Put(new PendingSubscriptionLogins.PendingLogin(
                    state, providerId, body?.AccountId, config, verifier, null, DateTimeOffset.UtcNow + LoginTtl,
                    null, null, null, capture, redirectUri));
                return Results.Ok(new LoginModeDto("pkce", state, OAuthClient.BuildAuthorizeUrl(config, redirectUri, state, challenge)));
            }

            var device = await client.StartDeviceAsync(config, ct);
            var expires = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(device.ExpiresIn, 60));
            // deviceauth 形态的轮询要回带申请到的 id 与 user code；标准设备码只要 device_code。
            Func<OAuthClient, CancellationToken, Task<OAuthClient.DevicePoll>> poll = config.Style == "deviceauth"
                ? (c, token) => c.PollDeviceAuthAsync(config, device.DeviceCode, device.UserCode, token)
                : (c, token) => c.PollDeviceAsync(config, device.DeviceCode, token);
            pending.Put(new PendingSubscriptionLogins.PendingLogin(
                state, providerId, body?.AccountId, config, null, device.DeviceCode, expires, poll));
            return Results.Ok(new LoginDeviceDto(
                "device", state, device.UserCode,
                device.VerificationUrlComplete ?? device.VerificationUrl ?? "", device.Interval));
        });

        // OAuth redirect target (system browser). Completes the authorization-code exchange and shows a local HTML page.
        app.MapGet("/api/oauth/callback", async (
            HttpContext ctx, AstraDatabase db, ServerOptions options, ISecretProtector protector,
            PendingSubscriptionLogins pending, OAuthClient client) =>
        {
            var state = ctx.Request.Query["state"].ToString();
            var login = pending.Take(state);
            if (login is null)
                return Html(new LoopbackCaptureResult(false, "登录会话不存在或已过期，请在 Astra 中重新发起登录。"));

            // 换码必须用发起时逐字一致的 redirect_uri：codex 登录是它自己的回调端口，
            // 其它家族是 Astra 自己的回调。
            var result = await CompleteCallbackAsync(db, protector, client, login,
                ctx.Request.Query["code"].ToString(),
                ctx.Request.Query["error"].ToString(),
                state,
                login.RedirectUri ?? $"http://127.0.0.1:{options.Port}/api/oauth/callback",
                // ZCode 的回调码参数名是 authCode（bigmodel 渠道）；Z.AI 渠道实测用标准 code。
                altCode: ctx.Request.Query["authCode"].ToString());
            if (login.Capture is not null) await login.Capture.DisposeAsync();
            return Html(result);
        });

        // Device-flow polling (the UI calls this every `interval` seconds until done/expired).
        app.MapPost("/api/providers/{providerId}/accounts/login/{state}/poll", async (
            string providerId, string state, AstraDatabase db, ISecretProtector protector,
            PendingSubscriptionLogins pending, OAuthClient client, ProviderProbe probe, CancellationToken ct) =>
        {
            var login = pending.Peek(state);
            if (login is null)
                return Results.NotFound(new PollFailureDto("expired", "登录会话不存在或已过期"));

            // ZAI CLI 链路是服务端中介的轮询，没有设备码也没有回调。
            if (login.Config.Style == "zcli")
            {
                var cli = login.CliFlowId is null || login.CliPollToken is null
                    ? new ZcodeCliPoll(ZcodeCliPollKind.Error, null, "invalid_state", "登录会话缺少 CLI flow id")
                    : await client.PollZcodeCliAsync(login.Config, login.CliFlowId, login.CliPollToken, ct);
                if (cli.Kind != ZcodeCliPollKind.Done)
                {
                    if (cli.Kind == ZcodeCliPollKind.Expired) pending.Take(state);
                    var status = cli.Kind == ZcodeCliPollKind.Expired ? "expired" : "pending";
                    return cli.Kind == ZcodeCliPollKind.Error
                        ? ApiJson.Result(new PollFailureDto(status, cli.Description ?? cli.Error), StatusCodes.Status400BadRequest)
                        : Results.Ok(new LoginPollDto(status));
                }
                pending.Take(state);
                var cliToken = new OAuthClient.TokenResult(cli.AccessToken!, cli.AccessToken, null, null, null);
                var cliAccount = await CompleteLoginAsync(db, protector, client, login, cliToken);
                return Results.Ok(new LoginDoneDto(ToDto(cliAccount)));
            }

            if (login.DeviceCode is null || login.PollAsync is null)
                return Results.NotFound(new PollFailureDto("expired", "登录会话不存在或已过期"));

            var poll = await login.PollAsync(client, ct);
            switch (poll.Kind)
            {
                case OAuthClient.DevicePollKind.Pending:
                    return Results.Ok(new LoginPollDto("pending"));
                case OAuthClient.DevicePollKind.SlowDown:
                    return Results.Ok(new LoginPollDto("slow_down"));
                case OAuthClient.DevicePollKind.Success:
                {
                    // 标准设备码在这一步直接拿令牌；deviceauth 拿到的是授权码，还要换一次码。
                    var token = poll.Token ?? await ExchangeDeviceAuthGrantAsync(client, login, poll.Grant!);
                    pending.Take(state);
                    ProviderAccount account;
                    try
                    {
                        account = await CompleteLoginAsync(db, protector, client, login, token);
                    }
                    catch (OAuthProtocolException e)
                    {
                        // 用户已经授权过了，但后面换令牌这一步失败（例如该 GitHub 账号没有 Copilot 订阅，
                        // 或者设备码已被消费）。必须把失败报出来，否则前端会一直"等待授权…"。
                        return ApiJson.Result(new PollFailureDto("error", e.Message), StatusCodes.Status400BadRequest);
                    }
                    // 登录成功即把上游的真实模型列表同步进来：订阅家族（GitHub Copilot）的模型
                    // 是服务端下发的，模板里那几条只是兜底猜测。失败不影响登录结果。
                    if (login.Config.Style == "github-copilot")
                        await probe.SyncModelsFromUpstreamAsync(providerId, ct);
                    return Results.Ok(new LoginDoneDto(ToDto(account)));
                }
                default:
                    pending.Take(state);
                    return ApiJson.Result(new PollFailureDto(
                        poll.Kind.ToString().ToLowerInvariant(), poll.Description ?? poll.Error),
                        StatusCodes.Status400BadRequest);
            }
        });
    }

    /// <summary>
    /// 授权的最后一步（Astra 自己的回调和 codex 的临时接收器共用）：校验 / 换码 / 落库，
    /// 返回给浏览器看的一段提示。调用方已经把 pending 里的会话取走（<c>Take</c>）。
    /// </summary>
    private static async Task<LoopbackCaptureResult> CompleteCallbackAsync(
        AstraDatabase db, ISecretProtector protector, OAuthClient client,
        PendingSubscriptionLogins.PendingLogin login, string code, string error, string state, string redirectUri,
        string altCode = "")
    {
        try
        {
            if (!string.IsNullOrEmpty(error)) return new LoopbackCaptureResult(false, $"授权被拒绝：{error}");
            // PKCE 登录必须带着 verifier 完成；ZCode 形态没有 verifier（本就不发 code_challenge）。
            var isPkce = login.Config.UsePkce;
            if (isPkce != (login.CodeVerifier is not null))
                return new LoopbackCaptureResult(false, "登录会话不存在或已过期，请在 Astra 中重新发起登录。");
            if (string.IsNullOrEmpty(code) && login.Config.Style == "zcode") code = altCode;
            if (string.IsNullOrEmpty(code)) return new LoopbackCaptureResult(false, "回调缺少授权码。");

            var token = isPkce
                ? await client.ExchangeCodeAsync(login.Config, code, login.CodeVerifier!, redirectUri)
                : await CompleteZcodeLoginAsync(client, login.Config, code, redirectUri, state);
            var account = await CompleteLoginAsync(db, protector, client, login, token);
            return new LoopbackCaptureResult(true, $"授权完成（{account.DisplayName}）。");
        }
        catch (OAuthProtocolException e)
        {
            return new LoopbackCaptureResult(false, $"换取令牌失败：{e.Message}");
        }
    }

    /// <summary>Persists the exchanged tokens (encrypted) as a new or updated account row.</summary>
    private static async Task<ProviderAccount> CompleteLoginAsync(
        AstraDatabase db, ISecretProtector protector, OAuthClient? client,
        PendingSubscriptionLogins.PendingLogin login, OAuthClient.TokenResult token)
    {
        // ZCode/ZAI 形态（"zcode" 授权码 / "zcli" CLI）：上游存的是 OAuth token，真正能发请求的
        // 是它换出来的业务 JWT（第四跳）。这里先换一次，刷新槽里仍存 OAuth token（刷新时重跑这一跳）。
        if (login.Config.Style is "zcode" or "zcli")
        {
            var business = await client!.ZcodeBusinessLoginAsync(login.Config, token.AccessToken);
            var expiresIn = SubscriptionTokenService.ExpiresInSeconds(business, DateTimeOffset.UtcNow) ?? 3600;
            // 刷新槽存 OAuth token（刷新 = 重跑这一跳）；访问令牌是业务 JWT。
            token = new OAuthClient.TokenResult(business, token.AccessToken, expiresIn, null, null);
        }

        // GitHub Copilot：GitHub token（长期有效）进刷新槽，访问令牌是它换来的 Copilot 短时令牌。
        // 换一次顺便确认这个账号确实有 Copilot 订阅——没有的话当场报错，而不是等发请求才 403。
        string? plan = null;
        if (login.Config.Style == "github-copilot" && !string.IsNullOrWhiteSpace(login.Config.BusinessLoginUrl))
        {
            var copilot = await client!.CopilotTokenAsync(login.Config, token.AccessToken);
            var expiresAt = copilot.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30);
            var expiresIn = (int)Math.Max(60, (expiresAt - DateTimeOffset.UtcNow).TotalSeconds);
            token = new OAuthClient.TokenResult(copilot.Token, token.AccessToken, expiresIn, null, null);
            plan = copilot.Sku;
        }

        var email = EmailFromIdToken(token.IdToken);
        if (login.Config.IdentityUrl.Length > 0 && client is not null)
        {
            // 没有 id_token 的家族（Kimi / ZCode）从这里补 email 与套餐名；装饰性字段，失败不致命。
            // ZCode 的身份端点认的是 OAuth token（在刷新槽里），不是换出来的业务 JWT。
            var identityToken = login.Config.Style is "zcode" or "zcli" ? token.RefreshToken ?? token.AccessToken : token.AccessToken;
            var (identityEmail, identityPlan) = await client.FetchIdentityAsync(login.Config, identityToken);
            email ??= identityEmail;
            plan = identityPlan;
        }
        // 没有身份端点的家族（ChatGPT 订阅）从令牌的命名空间 claim 补套餐名，展示用。
        plan ??= JwtNestedString(token.AccessToken, "https://api.openai.com/auth", "chatgpt_plan_type")
                 ?? JwtNestedString(token.IdToken, "https://api.openai.com/auth", "chatgpt_plan_type");
        var now = DateTimeOffset.UtcNow;

        var account = login.AccountId is not null ? await db.Accounts.GetAsync(login.AccountId) : null;
        var isNew = account is null;
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

        // 只能按"库里有没有"判断新建：InsertAsync 会给 CreatedAt 补当前时间，之后 CreatedAt == default
        // 永远不成立，用那个判断会让新账号走 UPDATE（0 行受影响），登录等于没保存。
        if (isNew)
            await db.Accounts.InsertAsync(account, CancellationToken.None);
        else
            await db.Accounts.UpdateAsync(account, CancellationToken.None);
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

    /// <summary>
    /// deviceauth 形态的一跳：轮询拿到的一次性授权码 + 上游配对的 PKCE 对，用标准换码拿令牌。
    /// </summary>
    private static async Task<OAuthClient.TokenResult> ExchangeDeviceAuthGrantAsync(
        OAuthClient client, PendingSubscriptionLogins.PendingLogin login, DeviceAuthGrant issued)
    {
        // 换码发的是真实回调（上游配对过的那个），不是回环地址。
        var redirectUri = $"{Issuer(login.Config.DeviceCodeUrl)}/deviceauth/callback";
        return await client.ExchangeCodeAsync(login.Config, issued.AuthorizationCode, issued.CodeVerifier, redirectUri);
    }

    /// <summary>issuer 前缀（scheme + host），设备码端点与授权页同源。</summary>
    private static string Issuer(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}" : "";

    /// <summary>
    /// 本机 Codex 登录文件的位置（<c>CODEX_HOME</c> 优先，否则 <c>~/.codex/auth.json</c>）。
    /// </summary>
    private static string? CodexAuthPath(ClientEnvironment env)
    {
        var home = env.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(home)) return Path.Combine(home, "auth.json");
        return string.IsNullOrEmpty(env.HomeDirectory) ? null : Path.Combine(env.HomeDirectory, ".codex", "auth.json");
    }

    /// <summary>Lightweight probe of the local login: parses it, never returns any token.</summary>
    private static LocalCodexLoginDto ToLocalDto(ClientEnvironment env)
    {
        var path = CodexAuthPath(env);
        var text = path is null ? null : env.ReadTextOrNull(path);
        if (text is null) return new LocalCodexLoginDto(false, null, null, "本机没有 Codex 登录文件（~/.codex/auth.json）");
        var credentials = CodexAuthFile.Parse(text, out var error);
        if (credentials is null) return new LocalCodexLoginDto(false, null, null, error);
        // 展示用：从 id_token / 访问令牌里读邮箱与套餐名，失败就留空。
        var email = EmailFromIdToken(credentials.IdToken);
        var plan = JwtNestedString(credentials.AccessToken, "https://api.openai.com/auth", "chatgpt_plan_type")
                   ?? JwtNestedString(credentials.IdToken, "https://api.openai.com/auth", "chatgpt_plan_type");
        var expired = JwtNumeric(credentials.AccessToken, "exp") is { } exp
                      && DateTimeOffset.FromUnixTimeSeconds((long)exp) <= DateTimeOffset.UtcNow;
        return new LocalCodexLoginDto(true, email, plan, expired ? "本机令牌已过期，导入后会自动刷新" : null);
    }

    /// <summary>Best-effort string claim nested one level under a namespaced claim of a JWT.</summary>
    private static string? JwtNestedString(string? token, string ns, string claim)
    {
        try
        {
            if (token is null || token.Split('.').Length < 2) return null;
            var json = JsonNode.Parse(Base64UrlDecode(token.Split('.')[1]));
            return json?[ns] is JsonObject nested && nested[claim] is JsonValue v && v.TryGetValue<string>(out var value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Best-effort numeric claim of a JWT payload.</summary>
    private static double? JwtNumeric(string? token, string claim)
    {
        try
        {
            if (token is null || token.Split('.').Length < 2) return null;
            var json = JsonNode.Parse(Base64UrlDecode(token.Split('.')[1]));
            return json?[claim] is JsonValue v && v.TryGetValue<double>(out var value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
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
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
    }

    private static ProviderAccountDto ToDto(ProviderAccount a) => ToDto(a, a.IsCurrent);

    private static ProviderAccountDto ToDto(ProviderAccount a, bool isCurrent) => new(
        a.Id,
        a.ProviderId,
        a.DisplayName,
        a.AccountEmail,
        a.Plan,
        a.Status,
        a.ExpiresAtUtc,
        a.LastRefreshAtUtc,
        a.Extra["quota"],
        a.CreatedAt,
        a.Extra["credits"],
        a.Enabled,
        isCurrent,
        a.SortOrder,
        a.CooldownUntilUtc,
        a.LastError);

    /// <summary>The provider's accounts in failover order; <c>isCurrent</c> marks the one requests go to right now.</summary>
    private static async Task<List<ProviderAccountDto>> ListDtosAsync(AstraDatabase db, string providerId)
    {
        var all = await db.Accounts.ListAsync(providerId);
        var current = SubscriptionSupport.SelectAccount(all, null, DateTimeOffset.UtcNow)?.Id;
        return all.Select(a => ToDto(a, a.Id == current)).ToList();
    }

    private static SubscriptionPolicyDto PolicyOf(Provider provider) => new(
        SubscriptionSupport.ClientPolicyOf(provider),
        SubscriptionSupport.SwitchModeOf(provider),
        SubscriptionSupport.IsClaudeSubscription(provider));

    private static IResult Html(LoopbackCaptureResult result)
    {
        var accent = result.Ok ? "#22c55e" : "#ef4444";
        var icon = result.Ok ? "✓" : "✕";
        return Results.Content(
            $"""<!doctype html><html lang="zh"><head><meta charset="utf-8"><title>Astra</title></head>""" +
            $"""<body style="font-family:-apple-system,sans-serif;background:#0b0b0e;color:#e5e5e7;display:grid;place-items:center;height:100vh;margin:0">""" +
            $"""<div style="text-align:center;max-width:420px;padding:24px">""" +
            $"""<div style="width:56px;height:56px;border-radius:50%;background:{accent}22;color:{accent};font-size:28px;line-height:56px;margin:0 auto 16px">{icon}</div>""" +
            $"""<div style="font-size:17px;font-weight:600;margin-bottom:6px">Astra</div>""" +
            $"""<p style="font-size:14px;color:#a1a1aa;line-height:1.6;margin:0">{result.Message}</p>""" +
            $"""<p style="font-size:12px;color:#71717a;margin-top:18px">可以关闭此页面并返回 Astra。</p>""" +
            """</div></body></html>""",
            "text/html; charset=utf-8");
    }
}
