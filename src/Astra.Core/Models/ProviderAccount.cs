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

    /// <summary>Full update; stamps UpdatedAt. Returns false when the row does not exist.</summary>
    Task<bool> UpdateAsync(ProviderAccount account, CancellationToken ct = default);

    /// <summary>Token rotation after a successful refresh (keeps the old refresh token when none is returned).</summary>
    Task<bool> UpdateTokensAsync(
        string id, string accessTokenEnc, string? refreshTokenEnc, DateTimeOffset expiresAtUtc, CancellationToken ct = default);

    Task<bool> SetStatusAsync(string id, string status, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
