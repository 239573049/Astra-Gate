using System.Collections.Concurrent;
using Astra.Core.Clients;
using Astra.Core.Models;

namespace Astra.Providers.Subscription;

/// <summary>
/// Keeps one subscription account usable: returns a valid access token (refreshing shortly before
/// expiry), rotates tokens after refreshes, and reacts to upstream 401s with a forced refresh.
/// Refreshes are serialized per account.
/// </summary>
public sealed class SubscriptionTokenService(
    IProviderAccountStore accounts,
    ISecretProtector protector,
    IHttpClientFactory httpClientFactory,
    TimeProvider? timeProvider = null)
{
    /// <summary>Refresh this long before expiry so in-flight requests never use a dying token.</summary>
    public static readonly TimeSpan RefreshLead = TimeSpan.FromSeconds(60);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    private OAuthClient Client { get; } = new(httpClientFactory);

    /// <summary>
    /// Returns a decrypted access token that is good for at least <see cref="RefreshLead"/>,
    /// refreshing first when needed. Throws <see cref="SubscriptionAuthException"/> when the
    /// account cannot be made usable.
    /// </summary>
    public async Task<string> GetValidAccessTokenAsync(ProviderAccount account, SubscriptionOAuthConfig config, CancellationToken ct = default)
    {
        if (IsFresh(account))
        {
            if (account.AccessTokenEnc is null)
                throw new SubscriptionAuthException(account.Id, "账号没有访问令牌，请重新登录");
            return protector.Unprotect(account.AccessTokenEnc);
        }
        var refreshed = await RefreshAsync(account, config, force: false, ct);
        if (refreshed.AccessTokenEnc is null)
            throw new SubscriptionAuthException(refreshed.Id, "刷新后仍没有访问令牌，请重新登录");
        return protector.Unprotect(refreshed.AccessTokenEnc);
    }

    /// <summary>
    /// Refreshes the account's tokens (serialized per account). When <paramref name="force"/> is false
    /// and another caller refreshed in the meantime, the fresh account is returned without a second call.
    /// On <c>invalid_grant</c> the account is marked revoked; on other OAuth errors it is marked expired.
    /// Transient (network) failures change nothing.
    /// </summary>
    public async Task<ProviderAccount> RefreshAsync(ProviderAccount account, SubscriptionOAuthConfig config, bool force, CancellationToken ct = default)
    {
        var gate = _locks.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var current = await accounts.GetAsync(account.Id, ct) ?? account;
            if (!force && IsFresh(current)) return current;

            if (current.RefreshTokenEnc is null)
            {
                await accounts.SetStatusAsync(current.Id, AccountStatus.Expired, ct);
                throw new SubscriptionAuthException(current.Id, "账号没有刷新令牌，请重新登录");
            }

            string refreshToken;
            try
            {
                refreshToken = protector.Unprotect(current.RefreshTokenEnc);
            }
            catch (Exception e)
            {
                await accounts.SetStatusAsync(current.Id, AccountStatus.Expired, ct);
                throw new SubscriptionAuthException(current.Id, $"刷新令牌无法解密（{e.Message}），请重新登录");
            }

            // ZCode 形态：没有标准 refresh 端点，"刷新" = 用存下的 OAuth token 重跑业务登录
            // （与 NextCoWork issuers/zcode.ts 的 refresh 语义一致）。业务 JWT 的过期时间从
            // exp claim 折算；解不出就给保守的一小时。
            if (config.Style == "zcode")
            {
                try
                {
                    var apiToken = await Client.ZcodeBusinessLoginAsync(config, refreshToken, ct);
                    await accounts.UpdateTokensAsync(
                        current.Id,
                        protector.Protect(apiToken),
                        current.RefreshTokenEnc,
                        JwtExpiresAt(apiToken, _clock.GetUtcNow()) ?? _clock.GetUtcNow().AddHours(1),
                        ct);
                    return await accounts.GetAsync(current.Id, ct) ?? current;
                }
                catch (OAuthProtocolException e)
                {
                    // 2007 = OAuth 授权令牌已失效（逆向实测）——授权本身没了，判 revoked。
                    await accounts.SetStatusAsync(current.Id,
                        e.Error == "zcode_2007" ? AccountStatus.Revoked : AccountStatus.Expired, ct);
                    throw new SubscriptionAuthException(current.Id, $"业务令牌刷新失败（{e.Error}），请重新登录");
                }
            }

            OAuthClient.TokenResult token;
            try
            {
                token = await Client.RefreshAsync(config, refreshToken, ct);
            }
            catch (OAuthProtocolException e)
            {
                var status = e.IsInvalidGrant ? AccountStatus.Revoked : AccountStatus.Expired;
                await accounts.SetStatusAsync(current.Id, status, ct);
                throw new SubscriptionAuthException(current.Id, $"刷新失败（{e.Error}），请在 Astra 中重新登录");
            }

            await accounts.UpdateTokensAsync(
                current.Id,
                protector.Protect(token.AccessToken),
                token.RefreshToken is null ? null : protector.Protect(token.RefreshToken),
                ExpiresAt(token, _clock.GetUtcNow()),
                ct);

            var updated = await accounts.GetAsync(current.Id, ct);
            return updated ?? current;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Upstream answered 401/403: force a refresh once, then let the caller retry the request.</summary>
    public Task<ProviderAccount> RefreshAfterUnauthorizedAsync(
        ProviderAccount account, SubscriptionOAuthConfig config, CancellationToken ct = default) =>
        RefreshAsync(account, config, force: true, ct);

    private bool IsFresh(ProviderAccount account) =>
        account.Status == AccountStatus.Active
        && account.AccessTokenEnc is not null
        && account.ExpiresAtUtc is { } expires
        && expires - _clock.GetUtcNow() > RefreshLead;

    /// <summary>Expiry of a token result; providers without expires_in get a conservative 1 hour.</summary>
    public static DateTimeOffset ExpiresAt(OAuthClient.TokenResult token, DateTimeOffset now) =>
        token.ExpiresIn is { } seconds ? now + TimeSpan.FromSeconds(seconds) : now + TimeSpan.FromHours(1);

    /// <summary>Seconds until a JWT's `exp` claim (min 60); null when the claim is absent or unparsable.</summary>
    public static int? ExpiresInSeconds(string jwt, DateTimeOffset now)
    {
        if (JwtExpiresAt(jwt, now) is not { } at) return null;
        return Math.Max(60, (int)(at - now).TotalSeconds);
    }

    /// <summary>Best-effort `exp` (epoch seconds) claim of a JWT payload; null when absent or unparsable.</summary>
    private static DateTimeOffset? JwtExpiresAt(string token, DateTimeOffset now)
    {
        var parts = token.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            var rem = (4 - padded.Length % 4) % 4;
            padded += rem switch { 2 => "==", 3 => "=", _ => "" };
            var json = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded)));
            return json?["exp"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<double>(out var seconds) && seconds > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
