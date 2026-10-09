using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;
using Astra.Providers.Subscription;

namespace Astra.Gateway.Pipeline;

/// <summary>Shared helpers for subscription-backed providers (plan §5.4).</summary>
public static class SubscriptionSupport
{
    /// <summary>Catalog key of a provider: template id, else price_key, else the instance id.</summary>
    public static string ProviderKeyOf(Provider provider) => provider.TemplateId ?? provider.PriceKey ?? provider.Id;

    /// <summary>Catalog defaults merged with the instance's <c>settings_json.subscription_oauth</c> overrides.</summary>
    public static SubscriptionOAuthConfig EffectiveConfig(Provider provider) =>
        SubscriptionCatalog.Effective(
            SubscriptionCatalog.Find(ProviderKeyOf(provider))
            ?? new SubscriptionOAuthConfig { ProviderKey = ProviderKeyOf(provider), DisplayName = provider.Name },
            provider.Settings);

    /// <summary>Upstream auth scheme a subscription account acts as (settings key subscription.upstream_scheme).</summary>
    public static string UpstreamSchemeOf(Provider provider)
    {
        if (provider.Settings.TryGetPropertyValue("subscription", out var node) && node is System.Text.Json.Nodes.JsonObject o
            && o.TryGetPropertyValue("upstream_scheme", out var scheme) && scheme is System.Text.Json.Nodes.JsonValue v
            && v.TryGetValue<string>(out var s) && AuthSchemes.All.Contains(s))
            return s;
        return AuthSchemes.Bearer;
    }

    /// <summary>Catalog key of the Claude Pro/Max subscription (Anthropic OAuth).</summary>
    public const string ClaudeSubscriptionKey = "claude-subscription";

    /// <summary>Whether the provider is a Claude subscription (Anthropic OAuth tokens scoped to Claude Code).</summary>
    public static bool IsClaudeSubscription(Provider provider) =>
        provider.AuthScheme == AuthSchemes.OAuthSubscription && ProviderKeyOf(provider) == ClaudeSubscriptionKey;

    /// <summary>
    /// Which clients may use the subscription (settings key <c>subscription.client_policy</c>). The Claude subscription
    /// defaults to <see cref="ClientPolicies.ClaudeCodeOnly"/> — also for instances created before the setting existed;
    /// every other provider defaults to <see cref="ClientPolicies.Any"/>.
    /// </summary>
    public static string ClientPolicyOf(Provider provider) =>
        SubscriptionSetting(provider, "client_policy") is { } p && ClientPolicies.All.Contains(p) ? p
        : IsClaudeSubscription(provider) ? ClientPolicies.ClaudeCodeOnly : ClientPolicies.Any;

    /// <summary>Account switching mode (settings key <c>subscription.switch_mode</c>); defaults to manual.</summary>
    public static string SwitchModeOf(Provider provider) =>
        SubscriptionSetting(provider, "switch_mode") is { } m && SwitchModes.All.Contains(m) ? m : SwitchModes.Manual;

    /// <summary>
    /// Whether callers other than Claude Code present a Claude Code identity on a Claude subscription
    /// (settings key <c>subscription.mimic_claude_code</c>; default off — see <see cref="ClaudeCodeMimicry"/>).
    /// Anthropic ties subscription tokens to its own clients, so non-Haiku requests from other clients
    /// fail unless they carry this identity.
    /// </summary>
    public static bool MimicClaudeCodeOf(Provider provider) =>
        provider.Settings["subscription"] is System.Text.Json.Nodes.JsonObject o
        && o["mimic_claude_code"] is System.Text.Json.Nodes.JsonValue v
        && v.TryGetValue<bool>(out var on) && on;

    private static string? SubscriptionSetting(Provider provider, string key) =>
        provider.Settings["subscription"] is System.Text.Json.Nodes.JsonObject o && o[key] is System.Text.Json.Nodes.JsonValue v
        && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// Enforces <see cref="ClientPolicyOf"/>: a request not recognized as Claude Code is rejected (403, nothing leaves
    /// the machine) while the provider is limited to Claude Code.
    /// </summary>
    /// <exception cref="GatewayException">The provider only serves Claude Code and this caller is not Claude Code.</exception>
    public static void EnforceClientPolicy(Provider provider, bool isClaudeCode)
    {
        if (isClaudeCode || ClientPolicyOf(provider) != ClientPolicies.ClaudeCodeOnly) return;
        throw new GatewayException(403, "permission_error",
            $"提供商 {provider.Name} 仅允许 Claude Code 客户端使用（订阅账号的客户端限制）。如需让其他客户端使用，请在 Astra 的提供商详情页关闭「仅限 Claude Code」。");
    }

    /// <summary>Usable for requests: enabled, grant alive, not cooling down after a rate limit.</summary>
    public static bool IsUsable(ProviderAccount account, DateTimeOffset now) =>
        account.Enabled && account.Status == AccountStatus.Active && !(account.CooldownUntilUtc > now);

    /// <summary>
    /// Plan §5.4 account selection over the provider's accounts (in failover order): the pinned account while usable,
    /// else the provider's current account while usable, else the first usable one. When none is usable the first
    /// enabled account is returned anyway (an expired one still gets its refresh attempt and a clear error); null only
    /// when every account is disabled or there is none.
    /// </summary>
    public static ProviderAccount? SelectAccount(IReadOnlyList<ProviderAccount> accounts, string? pinnedId, DateTimeOffset now) =>
        (pinnedId is null ? null : accounts.FirstOrDefault(a => a.Id == pinnedId && IsUsable(a, now)))
        ?? accounts.FirstOrDefault(a => a.IsCurrent && IsUsable(a, now))
        ?? accounts.FirstOrDefault(a => IsUsable(a, now))
        ?? accounts.FirstOrDefault(a => a.Enabled && a.Status == AccountStatus.Active)
        ?? accounts.FirstOrDefault(a => a.Enabled);

    /// <summary>
    /// Loads the provider, the account chosen by <see cref="SelectAccount"/> (a pinned account that expired, was revoked,
    /// disabled or is cooling down is skipped automatically) and the effective OAuth config.
    /// </summary>
    /// <exception cref="SubscriptionAuthException">Provider/account missing, or every account disabled.</exception>
    public static async Task<(Provider Provider, ProviderAccount Account, SubscriptionOAuthConfig Config)>
        RequireAccountAsync(AstraDatabase db, string providerId, string? accountId, CancellationToken ct = default)
    {
        var provider = await db.Providers.GetAsync(providerId, ct)
                       ?? throw new SubscriptionAuthException("", $"提供商 {providerId} 不存在");
        var all = await db.Accounts.ListAsync(providerId, ct);
        if (all.Count == 0)
            throw new SubscriptionAuthException(accountId ?? "", $"提供商 {provider.Name} 尚未登录订阅账号");
        var account = SelectAccount(all, accountId, DateTimeOffset.UtcNow)
                      ?? throw new SubscriptionAuthException(accountId ?? "", $"提供商 {provider.Name} 的订阅账号都已停用，请在 Astra 中启用至少一个账号");
        return (provider, account, EffectiveConfig(provider));
    }
}

/// <summary>Client policies of a subscription provider (settings key <c>subscription.client_policy</c>).</summary>
public static class ClientPolicies
{
    /// <summary>Only requests recognized by <see cref="ClaudeCodeDetector"/> are served.</summary>
    public const string ClaudeCodeOnly = "claude-code-only";

    /// <summary>Every client may use the subscription.</summary>
    public const string Any = "any";

    public static readonly IReadOnlyList<string> All = [ClaudeCodeOnly, Any];
}

/// <summary>Account switching modes of a subscription provider (settings key <c>subscription.switch_mode</c>).</summary>
public static class SwitchModes
{
    /// <summary>The current account changes only when the user switches (or it stops being usable).</summary>
    public const string Manual = "manual";

    /// <summary>A rate-limited (429) account cools down and the next usable account becomes current automatically.</summary>
    public const string Failover = "failover";

    public static readonly IReadOnlyList<string> All = [Manual, Failover];
}

/// <summary>Resolved upstream credentials for one provider instance.</summary>
/// <param name="AuthScheme">bearer | x-api-key | x-goog-api-key | query-key | none.</param>
public sealed record UpstreamAuth(
    string AuthScheme,
    string? HeaderName,
    string? HeaderValue,
    string? QueryName,
    string? QueryValue,
    /// <summary>Fixed header the upstream CLI client always sends (e.g. chatgpt-account-id).</summary>
    string? ExtraHeaderName = null,
    string? ExtraHeaderValue = null)
{
    public static UpstreamAuth None { get; } = new(AuthSchemes.None, null, null, null, null);

    /// <summary>The subscription account these credentials belong to; null for static API keys.</summary>
    public string? AccountId { get; init; }

    /// <summary>Applies the credentials (and any fixed client header) to an outgoing upstream request.</summary>
    public void Apply(HttpRequestMessage request)
    {
        if (ExtraHeaderName is not null && ExtraHeaderValue is not null)
        {
            request.Headers.Remove(ExtraHeaderName);
            request.Headers.TryAddWithoutValidation(ExtraHeaderName, ExtraHeaderValue);
        }
        if (HeaderName is not null && HeaderValue is not null)
        {
            request.Headers.Remove(HeaderName);
            request.Headers.TryAddWithoutValidation(HeaderName, HeaderValue);
        }
    }
}

/// <summary>
/// Builds upstream credentials for a provider: static API keys are decrypted per scheme;
/// subscription providers go through <see cref="SubscriptionTokenService"/> with refresh-on-401 support.
/// </summary>
public sealed class UpstreamAuthResolver(AstraDatabase db, ISecretProtector protector, SubscriptionTokenService tokens)
{
    /// <exception cref="SubscriptionAuthException">The subscription account is missing or unusable.</exception>
    public async Task<UpstreamAuth?> ResolveAsync(string providerId, string? accountId, CancellationToken ct = default)
    {
        var provider = await db.Providers.GetAsync(providerId, ct);
        if (provider is null) return null;

        if (provider.AuthScheme != AuthSchemes.OAuthSubscription)
            return BuildStatic(provider, protector);

        var (_, account, config) = await SubscriptionSupport.RequireAccountAsync(db, providerId, accountId, ct);
        var token = await tokens.GetValidAccessTokenAsync(account, config, ct);
        return Build(SubscriptionSupport.UpstreamSchemeOf(provider), token, ChatGptAccountHeader(token)) with { AccountId = account.Id };
    }

    /// <summary>Upstream answered 401/403: force-refresh the account once and resolve again.</summary>
    public async Task<UpstreamAuth> ResolveAfterUnauthorizedAsync(string providerId, string? accountId, CancellationToken ct = default)
    {
        var (provider, account, config) = await SubscriptionSupport.RequireAccountAsync(db, providerId, accountId, ct);
        if (provider.AuthScheme != AuthSchemes.OAuthSubscription)
            return BuildStatic(provider, protector) ?? UpstreamAuth.None;
        var refreshed = await tokens.RefreshAfterUnauthorizedAsync(account, config, ct);
        if (refreshed.AccessTokenEnc is null)
            throw new SubscriptionAuthException(account.Id, "刷新后仍没有访问令牌，请重新登录");
        var accessToken = protector.Unprotect(refreshed.AccessTokenEnc);
        return Build(SubscriptionSupport.UpstreamSchemeOf(provider), accessToken, ChatGptAccountHeader(accessToken)) with { AccountId = account.Id };
    }

    /// <summary>
    /// Automatic failover (plan §5.4, switch mode <see cref="SwitchModes.Failover"/>): puts <paramref name="accountId"/>
    /// on cooldown until <paramref name="cooldownUntil"/> (null = leave its state alone, e.g. a grant that just died),
    /// then makes the first usable account not yet <paramref name="tried"/> the provider's current account.
    /// Returns that account's id, or null when nothing is left to fail over to.
    /// </summary>
    public async Task<string?> FailOverAsync(
        string providerId, string accountId, DateTimeOffset? cooldownUntil, string reason,
        IReadOnlyCollection<string> tried, CancellationToken ct = default)
    {
        if (cooldownUntil is not null) await db.Accounts.SetCooldownAsync(accountId, cooldownUntil, reason, ct);
        var now = DateTimeOffset.UtcNow;
        var next = (await db.Accounts.ListAsync(providerId, ct))
            .FirstOrDefault(a => a.Id != accountId && !tried.Contains(a.Id) && SubscriptionSupport.IsUsable(a, now));
        if (next is null) return null;
        await db.Accounts.SetCurrentAsync(providerId, next.Id, ct);
        return next.Id;
    }

    private static UpstreamAuth? BuildStatic(Provider provider, ISecretProtector protector) =>
        provider.ApiKeyEnc is null ? UpstreamAuth.None : Build(provider.AuthScheme, protector.Unprotect(provider.ApiKeyEnc));

    /// <summary>
    /// codex-cli 每个 ChatGPT 后端请求都带 <c>chatgpt-account-id</c>（取自令牌的
    /// <c>chatgpt_account_id</c> claim）；Astra 代它发请求时同样要带，否则后端可能选错工作区。
    /// </summary>
    private static (string? Name, string? Value) ChatGptAccountHeader(string token) =>
        SubscriptionTokenService.ChatGptAccountId(token) is { Length: > 0 } accountId
            ? ("chatgpt-account-id", accountId)
            : (null, null);

    private static UpstreamAuth Build(string scheme, string secret, (string? Name, string? Value) extra = default) => scheme switch
    {
        AuthSchemes.Bearer => new UpstreamAuth(scheme, "Authorization", $"Bearer {secret}", null, null, extra.Name, extra.Value),
        AuthSchemes.XApiKey => new UpstreamAuth(scheme, "x-api-key", secret, null, null, extra.Name, extra.Value),
        AuthSchemes.XGoogApiKey => new UpstreamAuth(scheme, "x-goog-api-key", secret, null, null, extra.Name, extra.Value),
        AuthSchemes.QueryKey => new UpstreamAuth(scheme, null, null, "key", secret, extra.Name, extra.Value),
        _ => UpstreamAuth.None,
    };
}
