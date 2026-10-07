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

    /// <summary>
    /// Loads the provider, the usable account (the pinned one while it is active; once expired or
    /// revoked it is disabled automatically and the first active account takes over) and the
    /// effective OAuth config.
    /// </summary>
    /// <exception cref="SubscriptionAuthException">Provider/account missing.</exception>
    public static async Task<(Provider Provider, ProviderAccount Account, SubscriptionOAuthConfig Config)>
        RequireAccountAsync(AstraDatabase db, string providerId, string? accountId, CancellationToken ct = default)
    {
        var provider = await db.Providers.GetAsync(providerId, ct)
                       ?? throw new SubscriptionAuthException("", $"提供商 {providerId} 不存在");
        var all = await db.Accounts.ListAsync(providerId, ct);
        var pinned = accountId is null ? null
            : await db.Accounts.GetAsync(accountId, ct) is { Status: AccountStatus.Active } active ? active : null;
        var account = pinned
                      ?? all.FirstOrDefault(a => a.Status == AccountStatus.Active)
                      ?? all.FirstOrDefault();
        if (account is null)
            throw new SubscriptionAuthException(accountId ?? "", $"提供商 {provider.Name} 尚未登录订阅账号");
        return (provider, account, EffectiveConfig(provider));
    }
}

/// <summary>Resolved upstream credentials for one provider instance.</summary>
/// <param name="AuthScheme">bearer | x-api-key | x-goog-api-key | query-key | none.</param>
public sealed record UpstreamAuth(
    string AuthScheme,
    string? HeaderName,
    string? HeaderValue,
    string? QueryName,
    string? QueryValue)
{
    public static UpstreamAuth None { get; } = new(AuthSchemes.None, null, null, null, null);
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
        return Build(SubscriptionSupport.UpstreamSchemeOf(provider), token);
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
        return Build(SubscriptionSupport.UpstreamSchemeOf(provider), protector.Unprotect(refreshed.AccessTokenEnc));
    }

    private static UpstreamAuth? BuildStatic(Provider provider, ISecretProtector protector) =>
        provider.ApiKeyEnc is null ? UpstreamAuth.None : Build(provider.AuthScheme, protector.Unprotect(provider.ApiKeyEnc));

    private static UpstreamAuth Build(string scheme, string secret) => scheme switch
    {
        AuthSchemes.Bearer => new UpstreamAuth(scheme, "Authorization", $"Bearer {secret}", null, null),
        AuthSchemes.XApiKey => new UpstreamAuth(scheme, "x-api-key", secret, null, null),
        AuthSchemes.XGoogApiKey => new UpstreamAuth(scheme, "x-goog-api-key", secret, null, null),
        AuthSchemes.QueryKey => new UpstreamAuth(scheme, null, null, "key", secret),
        _ => UpstreamAuth.None,
    };
}
