using System.Text.Json.Nodes;

namespace Astra.Core.Models;

/// <summary>Lifecycle state of a subscription account's grant.</summary>
public static class AccountStatus
{
    public const string Active = "active";
    public const string Expired = "expired";   // refresh failed — user should re-login
    public const string Revoked = "revoked";   // invalid_grant — the grant itself is gone

    public static readonly IReadOnlyList<string> All = [Active, Expired, Revoked];
}

/// <summary>What kind of credential a subscription account holds (column <c>kind</c>).</summary>
public static class AccountKinds
{
    /// <summary>A login: access token plus a refreshable grant (or a long-lived token that simply expires).</summary>
    public const string OAuth = "oauth";

    /// <summary>A static API key the subscription's endpoint accepts: never refreshed, never expires.</summary>
    public const string ApiKey = "api_key";

    /// <summary>A plugin's sign-in: the credential stays in plugin-auth.json; <c>extra.plugin_key</c> names it there.</summary>
    public const string Plugin = "plugin";
}

/// <summary>How an account was added (column <c>source</c>; null for accounts older than the column).</summary>
public static class AccountSources
{
    public const string Login = "login";
    public const string Local = "local";
    public const string Token = "token";
    public const string Batch = "batch";

    /// <summary>Signed in through a provider plugin.</summary>
    public const string Plugin = "plugin";
}

/// <summary>
/// One OAuth subscription login behind a provider whose
/// <see cref="Provider.AuthScheme"/> is <see cref="AuthSchemes.OAuthSubscription"/> (plan §5.4).
/// Tokens are stored DataProtection-encrypted and never returned by the admin API.
/// </summary>
public sealed class ProviderAccount
{
    public string Id { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? AccountEmail { get; set; }

    /// <summary>Subscription plan when known (e.g. "claude_pro", "chatgpt_plus").</summary>
    public string? Plan { get; set; }

    public string? AccessTokenEnc { get; set; }
    public string? RefreshTokenEnc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public string Status { get; set; } = AccountStatus.Active;
    public DateTimeOffset? LastRefreshAtUtc { get; set; }

    /// <summary>Provider-specific extras (quota info, organization id, …).</summary>
    public JsonObject Extra { get; set; } = new();

    /// <summary>User switch: a disabled account is never selected for requests (its tokens are kept).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The provider's current account (at most one per provider), set by "switch to this account" or by automatic
    /// failover. Unset everywhere = the first usable account in <see cref="SortOrder"/>.
    /// </summary>
    public bool IsCurrent { get; set; }

    /// <summary>Failover order inside the provider (ascending).</summary>
    public int SortOrder { get; set; }

    /// <summary>Cooling down after a rate limit or auth rejection (automatic failover skips it); null = not cooling down.</summary>
    public DateTimeOffset? CooldownUntilUtc { get; set; }

    /// <summary>
    /// Why the account was last taken out of rotation. Auth rejections start with "401 " / "403 " so quota recovery
    /// does not mistake them for a rate-limit cooldown; the rest is display text.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary><see cref="AccountKinds"/> value.</summary>
    public string Kind { get; set; } = AccountKinds.OAuth;

    /// <summary><see cref="AccountSources"/> value; null when unknown.</summary>
    public string? Source { get; set; }

    /// <summary>
    /// Proxy for everything sent with this account (requests, refresh, quota); null = the provider's
    /// <see cref="Provider.HttpProxy"/>, else the system proxy.
    /// </summary>
    public string? HttpProxy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Persistence for <see cref="ProviderAccount"/> (implemented with Dapper in Astra.Data).</summary>
public interface IProviderAccountStore
{
    Task<IReadOnlyList<ProviderAccount>> ListAsync(string? providerId = null, CancellationToken ct = default);
    Task<ProviderAccount?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Active accounts whose access token expires within the given window (or has none).</summary>
    Task<IReadOnlyList<ProviderAccount>> ListExpiringAsync(TimeSpan within, CancellationToken ct = default);

    Task InsertAsync(ProviderAccount account, CancellationToken ct = default);

    /// <summary>
    /// Updates the login / token / extra fields; stamps UpdatedAt. The switching state (enabled, current, order,
    /// cooldown) has dedicated writers and is left untouched. Returns false when the row does not exist.
    /// </summary>
    Task<bool> UpdateAsync(ProviderAccount account, CancellationToken ct = default);

    /// <summary>Token rotation after a successful refresh (keeps the old refresh token when none is returned).</summary>
    Task<bool> UpdateTokensAsync(
        string id, string accessTokenEnc, string? refreshTokenEnc, DateTimeOffset expiresAtUtc, CancellationToken ct = default);

    Task<bool> SetStatusAsync(string id, string status, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
