using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
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

    // ----- per-family probes (return null only for 401/403 so the recovery path kicks in) -----

    private async Task<JsonObject?> ProbeClaudeAsync(string accessToken, CancellationToken ct)
    {
        using var request = Build("https://api.anthropic.com/api/oauth/usage", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        var (status, payload) = await SendAsync(request, ct);
        if (IsUnauthorized(status)) return null;
        if (!IsSuccess(status)) throw new OAuthProtocolException($"http_{(int)status}", null);
        return NormalizeClaude(payload);
    }

    private async Task<JsonObject?> ProbeOpenAiAsync(string accessToken, CancellationToken ct)
    {
        var accountId = JwtClaim(accessToken, "chatgpt_account_id");
        foreach (var url in new[] { "https://chatgpt.com/backend-api/codex/usage", "https://chatgpt.com/backend-api/wham/usage" })
        {
            using var request = Build(url, accessToken);
            if (accountId is not null) request.Headers.TryAddWithoutValidation("chatgpt-account-id", accountId);
            request.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
            var (status, payload) = await SendAsync(request, ct);
            if (IsUnauthorized(status)) return null;
            if (status == HttpStatusCode.NotFound) continue; // older deployments serve the wham path only
            if (!IsSuccess(status)) throw new OAuthProtocolException($"http_{(int)status}", null);
            return NormalizeOpenAi(payload);
        }
        throw new OAuthProtocolException("http_404", "usage endpoint not found");
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
        var (status, payload) = await SendAsync(request, ct);
        if (IsUnauthorized(status)) return null;
        if (!IsSuccess(status)) throw new OAuthProtocolException($"http_{(int)status}", null);
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
            var (status, payload) = await SendAsync(request, ct);
            if (IsUnauthorized(status)) return null;
            if (!IsSuccess(status)) throw new OAuthProtocolException($"http_{(int)status}", null);
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

    private async Task<(HttpStatusCode Status, JsonObject? Payload)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var client = httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return (response.StatusCode, JsonNode.Parse(body) as JsonObject);
        }
        catch (System.Text.Json.JsonException)
        {
            return (response.StatusCode, null);
        }
    }

    private static bool IsUnauthorized(HttpStatusCode status) => status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and < 300;

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
        var limits = payload?["rate_limits"] as JsonObject;
        if (OpenAiWindow(limits, "primary_window") is { } session) quota["session"] = session;
        if (OpenAiWindow(limits, "secondary_window") is { } weekly) quota["weekly"] = weekly;
        return quota;
    }

    private JsonObject? OpenAiWindow(JsonObject? limits, string key)
    {
        if (limits?[key] is not JsonObject window) return null;
        if (Pct(window["used_percent"], null) is not { } used) return null;
        var resetsAt = window["resets_in_seconds"] is JsonValue v && v.TryGetValue<double>(out var seconds)
            ? _clock.GetUtcNow().AddSeconds(seconds)
            : default(DateTimeOffset?);
        var node = WindowNode(used, resetsAt?.ToString("o"));
        if (window["window_minutes"] is JsonValue m && m.TryGetValue<double>(out var minutes))
            node["windowMinutes"] = (int)minutes;
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
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            var rem = (4 - padded.Length % 4) % 4;
            padded += rem switch { 2 => "==", 3 => "=", _ => "" };
            return JsonNode.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded)))?[claim] is JsonValue v
                   && v.TryGetValue<string>(out var value) ? value : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
