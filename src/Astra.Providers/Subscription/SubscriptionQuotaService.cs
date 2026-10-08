using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;

namespace Astra.Providers.Subscription;

/// <summary>
/// Fetches a normalized usage snapshot for one subscription account from the vendor's
/// (reverse-engineered) quota endpoint and persists it into <see cref="ProviderAccount.Extra"/>
/// under <c>quota</c>: <c>{ fetchedAtUtc, session: {usedPercent, resetsAtUtc}, weekly: {…},
/// credits: {usedPercent, monthlyLimit, prepaidBalance}, planLabel }</c>. Sources:
/// Claude <c>api.anthropic.com/api/oauth/usage</c> (+ <c>anthropic-beta: oauth-2025-04-20</c>),
/// Codex <c>chatgpt.com/backend-api/codex/usage</c> (rate_limits windows), Grok
/// <c>cli-chat-proxy.grok.com/v1/billing</c>. A 401/403 triggers one forced token refresh; if the
/// upstream still refuses the account it is marked revoked — a dead grant disables itself.
/// </summary>
public sealed class SubscriptionQuotaService(
    IProviderAccountStore accounts,
    SubscriptionTokenService tokens,
    ISecretProtector protector,
    IHttpClientFactory httpClientFactory,
    TimeProvider? timeProvider = null)
{
    public const string HttpClientName = "astra-subscription-quota";

    /// <summary>Snapshot older than this is considered stale by the UI (auto refetch).</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(5);

    /// <summary>
    /// codex-cli 的 User-Agent（形如 <c>codex_cli_rs/0.48.0</c>）。chatgpt.com 前端 Cloudflare
    /// 只放行带这个身份的请求，裸客户端会被 403 + HTML 挡下，所以订阅请求也要用同一身份。
    /// </summary>
    public const string CodexClientUserAgent = "codex_cli_rs/0.48.0";

    private static readonly string[] GrokBillingUrls =
    [
        "https://cli-chat-proxy.grok.com/v1/billing?format=credits",
        "https://cli-chat-proxy.grok.com/v1/user?include=subscription",
    ];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    /// <summary>True when the stored snapshot exists and is younger than <see cref="FreshFor"/>.</summary>
    public static bool IsFresh(ProviderAccount account) =>
        account.Extra["quota"]?["fetchedAtUtc"] is JsonValue v
        && v.TryGetValue<DateTimeOffset>(out var at)
        && at > TimeProvider.System.GetUtcNow() - FreshFor;

    /// <summary>Fetches the live snapshot, persists it, and returns the updated account with the quota node.</summary>
    /// <returns>Quota is null when this provider family has no quota probe implemented.</returns>
    public async Task<(ProviderAccount Account, JsonObject? Quota)> FetchAsync(
        Provider provider, ProviderAccount account, SubscriptionOAuthConfig config, CancellationToken ct = default)
    {
        // 与 Gateway 的 SubscriptionSupport.ProviderKeyOf 保持一致（模板 id 即订阅家族键）；
        // Providers 不引用 Gateway，这里本地实现同一规则。
        var family = provider.TemplateId ?? provider.PriceKey ?? provider.Id;
        var quota = family switch
        {
            "claude-subscription" => await ProbeWithRecoveryAsync(account, config, ProbeClaudeAsync, ct),
            "openai-subscription" => await ProbeWithRecoveryAsync(account, config, ProbeOpenAiAsync, ct),
            "grok-subscription" => await ProbeWithRecoveryAsync(account, config, ProbeGrokAsync, ct),
            "zcode-subscription" => await ProbeWithRecoveryAsync(account, config, ProbeZcodeAsync, ct),
            "github-copilot-subscription" => await ProbeCopilotAsync(account, config, ct),
            // Kimi：上游有 /coding/v1/usages 路由，但没有公开的响应形状可解析（NextCoWork
            // 同样未实现 kimi 额度）——返回 null 让 UI 显示"暂不支持"，而不是猜一个字段名。
            _ => null,
        };
        if (quota is null) return (account, null);

        account = await accounts.GetAsync(account.Id, ct) ?? account;
        account.Extra["quota"] = quota;
        await accounts.UpdateAsync(account, ct);
        return (account, quota);
    }

    /// <summary>
    /// 这个账号能不能用"重置卡"：目前只有 ChatGPT 订阅有（codex 的
    /// <c>/wham/rate-limit-reset-credits</c>）。前端据此决定是否显示重置卡区块。
    /// </summary>
    public static bool SupportsResetCredits(Provider provider) =>
        (provider.TemplateId ?? provider.PriceKey ?? provider.Id) == "openai-subscription";

    /// <summary>
    /// 读取该账号的重置卡列表（codex 的 rate limit reset credits）。列表每次实时拉取，
    /// 因为卡有 <c>status</c>（available / redeemed / expired）会变；同时把它快照进
    /// <c>extra.credits</c>，方便 UI 一进页面就有东西显示。
    /// </summary>
    public async Task<JsonObject?> ListResetCreditsAsync(
        Provider provider, ProviderAccount account, SubscriptionOAuthConfig config, CancellationToken ct = default)
    {
        var token = await tokens.GetValidAccessTokenAsync(account, config, ct);
        var payload = await GetResetCreditsAsync(account, token, ct);
        if (payload is null) return null;
        account = await accounts.GetAsync(account.Id, ct) ?? account;
        account.Extra["credits"] = payload.DeepClone();
        await accounts.UpdateAsync(account, ct);
        return payload;
    }

    /// <summary>
    /// 用掉一张重置卡：<c>POST /wham/rate-limit-reset-credits/consume</c>，
    /// body 是 <c>{ redeem_request_id（幂等键）, credit_id }</c>。成功后就地把该卡标成 redeemed，
    /// 调用方（端点）随后刷新额度。
    /// </summary>
    public async Task<JsonObject?> ConsumeResetCreditAsync(
        Provider provider, ProviderAccount account, SubscriptionOAuthConfig config, string? creditId, CancellationToken ct = default)
    {
        var token = await tokens.GetValidAccessTokenAsync(account, config, ct);
        var body = new JsonObject
        {
            ["redeem_request_id"] = Ulid.NewUlid(),
        };
        if (!string.IsNullOrWhiteSpace(creditId)) body["credit_id"] = creditId;
        using var request = new HttpRequestMessage(
            HttpMethod.Post, "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
        request.Headers.TryAddWithoutValidation("User-Agent", CodexClientUserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var (status, contentType, payload) = await SendAsync(request, ct);
        if (IsAuthRejection(status, contentType, payload))
            throw new SubscriptionAuthException(account.Id, "订阅上游拒绝了该请求，账号可能需要重新登录");
        if (!IsSuccess(status))
            throw new OAuthProtocolException($"http_{(int)status}", UpstreamErrorText(payload) ?? "重置失败");
        if (payload?["code"] is JsonValue code && code.TryGetValue<string>(out var codeText) && codeText is "nothing_to_reset" or "no_credit" or "already_redeemed")
            // 已知的终态码用本地化文案（比上游的英文 message 更好懂），未知码才透传上游消息。
            throw new OAuthProtocolException(codeText, ResetCodeHint(codeText));

        // 就地把用掉的那张卡标成已兑换，省掉一次额外拉取。
        account = await accounts.GetAsync(account.Id, ct) ?? account;
        if (account.Extra["credits"] is JsonObject credits && credits["credits"] is JsonArray cards)
        {
            foreach (var card in cards)
            {
                if (card is JsonObject o && (creditId is null || o["id"]?.GetValue<string>() == creditId))
                {
                    o["status"] = "redeemed";
                    o["redeemed_at"] = DateTimeOffset.UtcNow.ToString("o");
                    break;
                }
            }
            if (credits["available_count"] is JsonValue count && count.TryGetValue<int>(out var available) && available > 0)
                credits["available_count"] = available - 1;
            await accounts.UpdateAsync(account, ct);
        }
        return payload;
    }

    private static string ResetCodeHint(string code) => code switch
    {
        "nothing_to_reset" => "当前额度没有被限流，暂时不需要重置",
        "no_credit" => "没有可用的重置卡",
        "already_redeemed" => "这张重置卡已经被用掉了",
        _ => "重置失败",
    };

    /// <summary>
    /// GitHub Copilot 的额度：Copilot 按"高级请求"计费，没有 token 计量。
    /// <c>GET https://api.github.com/copilot_internal/user</c>（Bearer / token 认证，带官方身份头）
    /// 返回 <c>quota_snapshots.{chat,premium_models,premium_interactions}</c>，每项形如
    /// <c>{ entitlement, percent_remaining, remaining, unlimited, overage_count, overage_permitted, quota_id, timestamp_utc }</c>，
    /// 外加 <c>quota_reset_date</c> / <c>copilot_plan</c> / <c>login</c>。
    /// 来源：官方 copilot-language-server main.js（copilotUserInfoURL + ChatQuotaService 的字段名）。
    /// 与其它家族不同，这里不做 401 强制刷新——Copilot 短时令牌本来就靠专用刷新路径续，
    /// 额度查询失败不该把账号标成失效。
    /// </summary>
    private async Task<JsonObject?> ProbeCopilotAsync(ProviderAccount account, SubscriptionOAuthConfig config, CancellationToken ct)
    {
        var githubToken = account.RefreshTokenEnc is null ? null : protector.Unprotect(account.RefreshTokenEnc);
        if (githubToken is null) throw new SubscriptionAuthException(account.Id, "账号没有 GitHub 授权，请重新登录");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/user");
        request.Headers.TryAddWithoutValidation("Authorization", $"token {githubToken}");
        request.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.26.7");
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.99.3");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.26.7");
        request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", "vscode-chat");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2025-05-01");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var (status, contentType, payload) = await SendAsync(request, ct);
        if (IsAuthRejection(status, contentType, payload))
            throw new SubscriptionAuthException(account.Id, "GitHub 授权已失效，请重新登录");
        if (!IsSuccess(status)) throw new OAuthProtocolException($"http_{(int)status}", "Copilot 额度查询被上游拒绝");
        return NormalizeCopilot(payload);
    }

    private JsonObject NormalizeCopilot(JsonObject? payload)
    {
        var quota = NewSnapshot();
        if (payload is null) return quota;
        var snapshots = payload["quota_snapshots"] as JsonObject;
        // 付费账号看 premium_models / premium_interactions，免费账号看 chat。
        var snapshot = snapshots?["premium_models"] as JsonObject
                       ?? snapshots?["premium_interactions"] as JsonObject
                       ?? snapshots?["chat"] as JsonObject;
        var resetDate = ParseDate(payload["quota_reset_date"]);
        if (snapshot is not null)
        {
            var unlimited = Bool(snapshot, "unlimited") ?? false;
            // 卡片按"已用百分比"画条：上游给的是剩余百分比，换算一下。
            double? used = null;
            if (!unlimited)
            {
                if (Pct(snapshot["percent_remaining"], null) is { } remaining) used = Math.Clamp(100 - remaining, 0, 100);
                else if (Long(snapshot, "entitlement") is { } entitlement and > 0 && Long(snapshot, "remaining") is { } left)
                    used = Math.Clamp(100d * (entitlement - left) / entitlement, 0, 100);
            }
            if (unlimited) quota["planLabel"] = "unlimited";
            if (used is not null)
                quota["credits"] = WindowNode(used.Value, resetDate?.ToString("o"));
            quota["entitlement"] = Long(snapshot, "entitlement");
            quota["remaining"] = Long(snapshot, "remaining");
            quota["overageUsed"] = Long(snapshot, "overage_count");
            quota["overagePermitted"] = Bool(snapshot, "overage_permitted");
        }
        if (Str(payload, "copilot_plan") is { } plan) quota["planLabel"] = plan;
        if (Str(payload, "login") is { } login) quota["account"] = login;
        if (resetDate is not null && quota["credits"] is JsonObject window) window["resetsAtUtc"] = resetDate.Value.ToString("o");
        return quota;
    }

    private static DateTimeOffset? ParseDate(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var text) && DateTimeOffset.TryParse(text, out var at) ? at : null;

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static bool? Bool(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static long? Long(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;

    private static string? UpstreamErrorText(JsonObject? payload) =>
        payload?["error"] is JsonValue e && e.TryGetValue<string>(out var text) ? text
        : payload?["message"] is JsonValue m && m.TryGetValue<string>(out var message) ? message : null;

    /// <summary>GET the reset-credit list; null when the account/family has none (e.g. 404).</summary>
    private async Task<JsonObject?> GetResetCreditsAsync(ProviderAccount account, string accessToken, CancellationToken ct)
    {
        var request = Build("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits", accessToken);
        request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
        request.Headers.TryAddWithoutValidation("User-Agent", CodexClientUserAgent);
        var (status, contentType, payload) = await SendAsync(request, ct);
        if (IsAuthRejection(status, contentType, payload))
            throw new SubscriptionAuthException(account.Id, "订阅上游拒绝了该账号，请重新登录");
        return IsSuccess(status) ? payload : null;
    }

    /// <summary>Runs the probe; recovers once from an upstream rejection by force-refreshing the grant.</summary>
    private async Task<JsonObject> ProbeWithRecoveryAsync(
        ProviderAccount account, SubscriptionOAuthConfig config,
        Func<string, CancellationToken, Task<JsonObject?>> probe, CancellationToken ct)
    {
        var token = await tokens.GetValidAccessTokenAsync(account, config, ct);
        var quota = await probe(token, ct);
        if (quota is not null) return quota;

        var refreshed = await tokens.RefreshAfterUnauthorizedAsync(account, config, ct);
        if (refreshed.AccessTokenEnc is null)
            throw new SubscriptionAuthException(account.Id, "刷新后仍没有访问令牌，请重新登录");
        quota = await probe(protector.Unprotect(refreshed.AccessTokenEnc), ct);
        if (quota is not null) return quota;

        // 刷新后上游仍然拒绝：这个授权已经无法使用，自动禁用（revoked）直到用户重新登录。
        await accounts.SetStatusAsync(account.Id, AccountStatus.Revoked, ct);
        throw new SubscriptionAuthException(account.Id, "订阅上游拒绝了该账号的额度查询，账号已被禁用，请重新登录");
    }

    // ----- per-family probes (return null only for a rejected grant so the recovery path kicks in) -----

    private async Task<JsonObject?> ProbeClaudeAsync(string accessToken, CancellationToken ct)
    {
        using var request = Build("https://api.anthropic.com/api/oauth/usage", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        var (status, contentType, payload) = await SendAsync(request, ct);
        if (IsAuthRejection(status, contentType, payload)) return null;
        if (!IsSuccess(status)) throw new OAuthProtocolException($"http_{(int)status}", "额度查询被上游拒绝");
        return NormalizeClaude(payload);
    }

    private async Task<JsonObject?> ProbeOpenAiAsync(string accessToken, CancellationToken ct)
    {
        // 账号 id 在命名空间 claim 里（真实令牌是 https://api.openai.com/auth.chatgpt_account_id）；
        // 拿不到再退回平铺形态。缺这个头上游会 401——它按账号选工作区。
        var accountId = JwtClaim(accessToken, "chatgpt_account_id")
                        ?? JwtNestedClaim(accessToken, "https://api.openai.com/auth", "chatgpt_account_id");
        HttpStatusCode last = default;
        foreach (var url in new[] { "https://chatgpt.com/backend-api/codex/usage", "https://chatgpt.com/backend-api/wham/usage" })
        {
            using var request = Build(url, accessToken);
            if (accountId is not null) request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
            request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
            // codex 的客户端身份（形如 codex_cli_rs/0.48.0）：chatgpt.com 的 WAF 对裸客户端
            // 直接回 403 + HTML，带上官方身份更稳（实测两种端点都过）。
            request.Headers.TryAddWithoutValidation("User-Agent", CodexClientUserAgent);
            var (status, contentType, payload) = await SendAsync(request, ct);
            // 真·401/403（上游给了 JSON）：走强制刷新那条路。
            if (IsAuthRejection(status, contentType, payload)) return null;
            if (!IsSuccess(status))
            {
                // 其它失败按"这个端点不行"处理，换下一个端点——实测 codex/usage 会被 WAF 挡
                // 而 wham/usage 正常，不能因为前者就把整个探测量判死。
                last = status;
                continue;
            }
            return NormalizeOpenAi(payload);
        }
        throw new OAuthProtocolException($"http_{(int)last}", "额度查询被上游拒绝");
    }

    /// <summary>
    /// GLM Coding Plan（ZCode Z.AI 渠道）的额度：GET api.z.ai/api/monitor/usage/quota/limit，
    /// 鉴权头是裸 <c>authorization: &lt;apiToken&gt;</c>（无 Bearer——ZCode 原样如此）。
    /// 窗口挑选规则照抄 ZCode 渲染层：5 小时 = type∈{TOKENS_LIMIT,CREDIT_LIMIT} 且
    /// unit==3 且 number==5；每周 = 同 type 且 unit==6；percentage 是已用百分比；
    /// nextResetTime 是绝对毫秒。来源：NextCoWork kernel/upstream/coding-plan-quota.ts
    /// （2026-09-29 逆向 ZCode.app v3.11）。信封 success!==false 且 code 缺席/0/200 都算成功。
    /// </summary>
    private async Task<JsonObject?> ProbeZcodeAsync(string accessToken, CancellationToken ct)
    {
        using var request = Build("https://api.z.ai/api/monitor/usage/quota/limit", accessToken);
        // 裸值覆盖 Build 写的 "Bearer …"：biz/monitor API 只认这个形状。
        request.Headers.Authorization = new AuthenticationHeaderValue(accessToken);
        var (status, contentType, payload) = await SendAsync(request, ct);
        if (IsAuthRejection(status, contentType, payload)) return null;
        if (!IsSuccess(status)) throw new OAuthProtocolException($"http_{(int)status}", "额度查询被上游拒绝");
        return NormalizeZcode(payload);
    }

    private async Task<JsonObject?> ProbeGrokAsync(string accessToken, CancellationToken ct)
    {
        JsonObject? billing = null, user = null;
        foreach (var url in GrokBillingUrls)
        {
            using var request = Build(url, accessToken);
            request.Headers.TryAddWithoutValidation("x-grok-client-identifier", "xai-grok-cli");
            request.Headers.TryAddWithoutValidation("x-grok-client-version", "0.2.93");
            var (status, contentType, payload) = await SendAsync(request, ct);
            if (IsAuthRejection(status, contentType, payload)) return null;
            if (!IsSuccess(status)) throw new OAuthProtocolException($"http_{(int)status}", "额度查询被上游拒绝");
            if (url.Contains("/billing", StringComparison.Ordinal)) billing = payload;
            else user = payload;
        }
        return NormalizeGrok(billing, user);
    }

    private static HttpRequestMessage Build(string url, string accessToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<(HttpStatusCode Status, string ContentType, JsonObject? Payload)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        try
        {
            return (response.StatusCode, contentType, JsonNode.Parse(body) as JsonObject);
        }
        catch (System.Text.Json.JsonException)
        {
            return (response.StatusCode, contentType, null);
        }
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

    /// <summary>
    /// 401/403 只有在上游确实给了 JSON 响应时才算"授权死了"，触发强制刷新。
    /// Cloudflare / WAF 会因为出口 IP、缺浏览器指纹直接回 403 + HTML（实测 chatgpt.com
    /// 对裸 HTTP 客户端如此），那不是授权问题，把这种响应当成失效刷新一轮、
    /// 再把账号标成 revoked 会把好账号废掉。
    /// </summary>
    private static bool IsAuthRejection(HttpStatusCode status, string contentType, JsonObject? payload) =>
        status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        && (payload is not null || contentType.Contains("json", StringComparison.OrdinalIgnoreCase));

    // ----- normalizers -----

    private JsonObject NormalizeClaude(JsonObject? payload)
    {
        var quota = NewSnapshot();
        if (payload is null) return quota;
        var session = Pct(Window(payload, "five_hour"), "utilization") ?? LegacyPct(payload["five_hour_utilization"]);
        var weekly = Pct(Window(payload, "seven_day"), "utilization") ?? LegacyPct(payload["seven_day_utilization"]);
        // limits[] is the least ambiguous source (percent is 0..100 points); prefer it when present.
        foreach (var limit in payload["limits"] as JsonArray ?? [])
        {
            if (limit is not JsonObject entry) continue;
            var kind = entry["kind"]?.GetValue<string>();
            var group = entry["group"]?.GetValue<string>();
            if (kind == "session" && group == "session") session ??= Pct(entry["percent"], null);
            if (kind is "weekly_all" or "weekly_scoped" && group == "weekly") weekly ??= Pct(entry["percent"], null);
        }
        if (session is { } s) quota["session"] = WindowNode(s, Reset(payload, "five_hour"));
        if (weekly is { } w) quota["weekly"] = WindowNode(w, Reset(payload, "seven_day"));
        return quota;
    }

    private JsonObject NormalizeOpenAi(JsonObject? payload)
    {
        var quota = NewSnapshot();
        // 老的 codex/usage 用复数 rate_limits.{primary,secondary}_window；wham/usage 用单数
        // rate_limit.primary_window 且以 epoch 秒给 reset_at，两种都认。
        var limits = payload?["rate_limits"] as JsonObject ?? payload?["rate_limit"] as JsonObject;
        if (OpenAiWindow(limits, "primary_window") is { } session) quota["session"] = session;
        if (OpenAiWindow(limits, "secondary_window") is { } weekly) quota["weekly"] = weekly;
        if (payload?["plan_type"] is JsonValue plan && plan.TryGetValue<string>(out var planName) && planName.Length > 0)
            quota["planLabel"] = planName;
        return quota;
    }

    private JsonObject? OpenAiWindow(JsonObject? limits, string key)
    {
        if (limits?[key] is not JsonObject window) return null;
        if (Pct(window["used_percent"], null) is not { } used) return null;
        // codex/usage 给 resets_in_seconds；wham/usage 给 reset_at（epoch 秒）。
        var resetsAt = window["resets_in_seconds"] is JsonValue v && v.TryGetValue<double>(out var seconds)
            ? _clock.GetUtcNow().AddSeconds(seconds)
            : window["reset_at"] is JsonValue at && at.TryGetValue<double>(out var epoch) && epoch > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(epoch * 1000))
                : default(DateTimeOffset?);
        var node = WindowNode(used, resetsAt?.ToString("o"));
        if (window["window_minutes"] is JsonValue m && m.TryGetValue<double>(out var minutes)) node["windowMinutes"] = (int)minutes;
        else if (window["limit_window_seconds"] is JsonValue s && s.TryGetValue<double>(out var windowSeconds) && windowSeconds > 0)
            node["windowMinutes"] = (int)(windowSeconds / 60);
        return node;
    }

    /// <summary>
    /// <c>{success, code, data:{level, limits:[{type, unit, number, percentage, remaining, nextResetTime}]}}</c>
    /// → session/weekly 窗口。信封判定照 ZCode 的 isSuccessfulBigModelEnvelope：
    /// success!==false 且 code 缺席/0/200 都算成功。一条窗口三样凑不齐就整条丢弃，
    /// 绝不编 0；全部丢弃时返回只有 fetchedAtUtc 的快照（"没有可展示的额度项"不是错误）。
    /// </summary>
    private JsonObject NormalizeZcode(JsonObject? payload)
    {
        var quota = NewSnapshot();
        if (payload is null) return quota;
        if (payload["success"] is JsonValue s && s.TryGetValue<bool>(out var ok) && !ok) return quota;
        if (payload["code"] is JsonValue c && c.TryGetValue<int>(out var code) && code != 0 && code != 200) return quota;
        if (payload["data"] is not JsonObject data || data["limits"] is not JsonArray limits) return quota;

        JsonObject? session = null, weekly = null;
        foreach (var raw in limits)
        {
            if (raw is not JsonObject limit) continue;
            var type = limit["type"]?.GetValue<string>();
            // TOKENS_LIMIT（Z.AI）/ CREDIT_LIMIT（BigModel 积分制）是同一类"token 型"额度。
            if (type is not ("TOKENS_LIMIT" or "CREDIT_LIMIT")) continue;
            if (session is null && ZcodeUnit(limit) == (3, 5))
            {
                if (ZcodeWindow(limit, 300) is { } w) session = w;
                continue;
            }
            if (weekly is null && ZcodeUnit(limit).unit == 6 && ZcodeWindow(limit, 10_080) is { } week) weekly = week;
        }
        if (session is not null) quota["session"] = session;
        if (weekly is not null) quota["weekly"] = weekly;
        if (data.TryGetPropertyValue("level", out var level) && level is JsonValue lv)
            quota["planLabel"] = lv.ToJsonString().Trim('"');
        return quota;
    }

    /// <summary>(unit, number) 元组：5 小时窗是 (3,5)，周窗 unit=6（编号是服务端窗口语义，别按字面猜）。</summary>
    private static (int unit, int number) ZcodeUnit(JsonObject limit) =>
        (IntOf(limit["unit"]) ?? -1, IntOf(limit["number"]) ?? -1);

    private static int? IntOf(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static JsonObject? ZcodeWindow(JsonObject limit, int windowMinutes)
    {
        // 先信 percentage（上游给的已用 %），缺了用 remaining/number 反推；两条都没有就丢。
        double? used = Pct(limit["percentage"], null) ?? ZcodeRemainingPercent(limit);
        if (used is null) return null;
        if (limit["nextResetTime"] is not JsonValue r || !r.TryGetValue<double>(out var resetMs) || resetMs <= 0) return null;
        var node = WindowNode(used.Value, DateTimeOffset.FromUnixTimeMilliseconds((long)resetMs).ToString("o"));
        node["windowMinutes"] = windowMinutes;
        return node;
    }

    /// <summary>100 - remaining/number×100（两者都在场且 number>0 时）。</summary>
    private static double? ZcodeRemainingPercent(JsonObject limit)
    {
        if (limit["remaining"] is not JsonValue r || !r.TryGetValue<double>(out var remaining)) return null;
        if (limit["number"] is not JsonValue n || !n.TryGetValue<double>(out var total) || total <= 0) return null;
        return Math.Clamp(100 - remaining / total * 100, 0, 100);
    }

    private JsonObject NormalizeGrok(JsonObject? billing, JsonObject? user)
    {
        var quota = NewSnapshot();
        var used = Num(billing, "creditUsagePercent", "config.creditUsagePercent", "config.creditUsagePercent.val");
        if (used is { } percent) quota["credits"] = new JsonObject
        {
            ["usedPercent"] = percent,
            ["monthlyLimit"] = Num(billing, "monthlyLimit", "config.monthlyLimit", "config.monthlyLimit.val",
                "currentPeriod.monthlyLimit", "currentPeriod.monthlyLimit.val"),
            ["prepaidBalance"] = Num(billing, "prepaidBalance", "config.prepaidBalance", "config.prepaidBalance.val"),
        };
        var plan = Str(user, "planName", "plan_name", "subscription.planName", "subscription.plan_name",
            "subscription.plan", "subscription.display_name", "config.planName");
        if (plan is not null) quota["planLabel"] = plan;
        return quota;
    }

    private JsonObject NewSnapshot() => new() { ["fetchedAtUtc"] = _clock.GetUtcNow().ToString("o") };

    private static JsonObject WindowNode(double usedPercent, string? resetsAtUtc)
    {
        var node = new JsonObject { ["usedPercent"] = Math.Round(usedPercent, 1) };
        if (resetsAtUtc is not null) node["resetsAtUtc"] = resetsAtUtc;
        return node;
    }

    private static JsonObject? Window(JsonObject payload, string key) =>
        payload[key] is JsonObject window ? window : null;

    private static double? Pct(JsonNode? node, string? property)
    {
        if (property is not null)
        {
            if (node is not JsonObject o || o[property] is not JsonValue value) return null;
            node = value;
        }
        if (node is not JsonValue v || !v.TryGetValue<double>(out var d) || !double.IsFinite(d)) return null;
        return Math.Clamp(d, 0, 100);
    }

    /// <summary>The legacy flat fields reported fractions (0..1) instead of percentage points.</summary>
    private static double? LegacyPct(JsonNode? node)
    {
        if (Pct(node, null) is not { } value) return null;
        return value <= 1 ? value * 100 : value;
    }

    private static string? Reset(JsonObject payload, string key)
    {
        var node = (payload[key] as JsonObject)?["resets_at"] ?? payload[$"{key}_resets_at"];
        if (node is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0) return text;
        if (node is JsonValue n && n.TryGetValue<double>(out var seconds))
            return (seconds < 1e12
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))
                : DateTimeOffset.FromUnixTimeMilliseconds((long)seconds)).ToString("o");
        return null;
    }

    private static double? Num(JsonObject? payload, params string[] paths)
    {
        foreach (var path in paths)
        {
            var node = At(payload, path);
            if (node is JsonValue v && v.TryGetValue<double>(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
                return d;
        }
        return null;
    }

    private static string? Str(JsonObject? payload, params string[] paths)
    {
        foreach (var path in paths)
        {
            var node = At(payload, path);
            if (node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) return s;
        }
        return null;
    }

    private static JsonNode? At(JsonObject? payload, string path)
    {
        var node = (JsonNode?)payload;
        foreach (var segment in path.Split('.'))
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue(segment, out var next)) return null;
            node = next;
        }
        return node;
    }

    /// <summary>Best-effort claim from a JWT payload (display only; never verified here).</summary>
    private static string? JwtClaim(string token, string claim)
    {
        if (JwtPayload(token) is { } json && json[claim] is JsonValue v && v.TryGetValue<string>(out var value))
            return value;
        return null;
    }

    /// <summary>String claim nested one level under a namespaced object claim.</summary>
    private static string? JwtNestedClaim(string token, string ns, string claim) =>
        JwtPayload(token)?[ns] is JsonObject nested && nested[claim] is JsonValue v && v.TryGetValue<string>(out var value)
            ? value
            : null;

    /// <summary>Best-effort JWT payload object; null when the token is not a parsable JWT.</summary>
    private static JsonObject? JwtPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            var rem = (4 - padded.Length % 4) % 4;
            padded += new string('=', (4 - padded.Length % 4) % 4);
            return JsonNode.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded))) as JsonObject;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
