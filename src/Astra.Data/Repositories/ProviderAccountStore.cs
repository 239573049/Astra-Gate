using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using Astra.Core;
using Astra.Core.Models;

namespace Astra.Data.Repositories;

/// <summary>OAuth subscription accounts (table <c>provider_accounts</c>). Tokens are encrypted at rest.</summary>
public sealed class ProviderAccountStore(SqliteConnectionFactory factory) : IProviderAccountStore
{
    private const string AccountSelect = """
        SELECT id, provider_id, display_name, account_email, plan, access_token_enc, refresh_token_enc,
               expires_at_utc, status, last_refresh_at_utc, extra_json, created_at, updated_at
        FROM provider_accounts
        """;

    public async Task<IReadOnlyList<ProviderAccount>> ListAsync(string? providerId = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var rows = string.IsNullOrEmpty(providerId)
            ? await conn.QueryAsync<AccountRow>($"{AccountSelect} ORDER BY created_at, id")
            : await conn.QueryAsync<AccountRow>($"{AccountSelect} WHERE provider_id = @provider_id ORDER BY created_at, id",
                new { provider_id = providerId });
        return rows.Select(r => r.ToAccount()).ToList();
    }

    public async Task<ProviderAccount?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<AccountRow>($"{AccountSelect} WHERE id = @id", new { id });
        return row?.ToAccount();
    }

    public async Task<IReadOnlyList<ProviderAccount>> ListExpiringAsync(TimeSpan within, CancellationToken ct = default)
    {
        var horizon = DateTimeOffsetHandler.ToStorage(DateTimeOffset.UtcNow + within);
        await using var conn = await factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<AccountRow>($"""
            {AccountSelect}
            WHERE status = 'active' AND expires_at_utc IS NOT NULL AND expires_at_utc <= @horizon
            ORDER BY expires_at_utc
            """, new { horizon });
        return rows.Select(r => r.ToAccount()).ToList();
    }

    public async Task InsertAsync(ProviderAccount account, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (account.CreatedAt == default) account.CreatedAt = now;
        if (account.UpdatedAt == default) account.UpdatedAt = now;
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO provider_accounts(id, provider_id, display_name, account_email, plan, access_token_enc,
                                          refresh_token_enc, expires_at_utc, status, last_refresh_at_utc,
                                          extra_json, created_at, updated_at)
            VALUES (@id, @provider_id, @display_name, @account_email, @plan, @access_token_enc,
                    @refresh_token_enc, @expires_at_utc, @status, @last_refresh_at_utc,
                    @extra_json, @created_at, @updated_at)
            """, new
        {
            id = account.Id,
            provider_id = account.ProviderId,
            display_name = account.DisplayName,
            account_email = account.AccountEmail,
            plan = account.Plan,
            access_token_enc = account.AccessTokenEnc,
            refresh_token_enc = account.RefreshTokenEnc,
            expires_at_utc = account.ExpiresAtUtc is { } exp ? DateTimeOffsetHandler.ToStorage(exp) : null,
            status = account.Status,
            last_refresh_at_utc = account.LastRefreshAtUtc is { } lr ? DateTimeOffsetHandler.ToStorage(lr) : null,
            extra_json = Json.Serialize(account.Extra),
            created_at = DateTimeOffsetHandler.ToStorage(account.CreatedAt),
            updated_at = DateTimeOffsetHandler.ToStorage(account.UpdatedAt),
        });
    }

    public async Task<bool> UpdateAsync(ProviderAccount account, CancellationToken ct = default)
    {
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync("""
            UPDATE provider_accounts SET
                display_name = @display_name, account_email = @account_email, plan = @plan,
                access_token_enc = @access_token_enc, refresh_token_enc = @refresh_token_enc,
                expires_at_utc = @expires_at_utc, status = @status, last_refresh_at_utc = @last_refresh_at_utc,
                extra_json = @extra_json, updated_at = @updated_at
            WHERE id = @id
            """, new
        {
            id = account.Id,
            provider_id = account.ProviderId,
            display_name = account.DisplayName,
            account_email = account.AccountEmail,
            plan = account.Plan,
            access_token_enc = account.AccessTokenEnc,
            refresh_token_enc = account.RefreshTokenEnc,
            expires_at_utc = account.ExpiresAtUtc is { } exp ? DateTimeOffsetHandler.ToStorage(exp) : null,
            status = account.Status,
            last_refresh_at_utc = account.LastRefreshAtUtc is { } lr ? DateTimeOffsetHandler.ToStorage(lr) : null,
            extra_json = Json.Serialize(account.Extra),
            updated_at = DateTimeOffsetHandler.ToStorage(account.UpdatedAt),
        }) > 0;
    }

    public async Task<bool> UpdateTokensAsync(
        string id, string accessTokenEnc, string? refreshTokenEnc, DateTimeOffset expiresAtUtc, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync("""
            UPDATE provider_accounts SET
                access_token_enc = @access_token_enc,
                refresh_token_enc = COALESCE(@refresh_token_enc, refresh_token_enc),
                expires_at_utc = @expires_at_utc,
                status = 'active',
                last_refresh_at_utc = @now,
                updated_at = @now
            WHERE id = @id
            """, new
        {
            id,
            access_token_enc = accessTokenEnc,
            refresh_token_enc = refreshTokenEnc,
            expires_at_utc = DateTimeOffsetHandler.ToStorage(expiresAtUtc),
            now = DateTimeOffsetHandler.ToStorage(DateTimeOffset.UtcNow),
        }) > 0;
    }

    public async Task<bool> SetStatusAsync(string id, string status, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync("""
            UPDATE provider_accounts SET status = @status, updated_at = @now WHERE id = @id
            """, new { id, status, now = DateTimeOffsetHandler.ToStorage(DateTimeOffset.UtcNow) }) > 0;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM provider_accounts WHERE id = @id", new { id }) > 0;
    }
}

// Dapper.AOT only materializes rows into types it can see from outside the store class; nested
// private types are silently left on vanilla Dapper, which dies under Native AOT (see its FAQ).
internal sealed class AccountRow
{
    public string Id { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? AccountEmail { get; set; }
    public string? Plan { get; set; }
    public string? AccessTokenEnc { get; set; }
    public string? RefreshTokenEnc { get; set; }
    public string? ExpiresAtUtc { get; set; }
    public string Status { get; set; } = "";
    public string? LastRefreshAtUtc { get; set; }
    public string? ExtraJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ProviderAccount ToAccount() => new()
    {
        Id = Id,
        ProviderId = ProviderId,
        DisplayName = DisplayName,
        AccountEmail = AccountEmail,
        Plan = Plan,
        AccessTokenEnc = AccessTokenEnc,
        RefreshTokenEnc = RefreshTokenEnc,
        ExpiresAtUtc = ExpiresAtUtc is null ? null : DateTimeOffset.Parse(ExpiresAtUtc, CultureInfo.InvariantCulture),
        Status = Status,
        LastRefreshAtUtc = LastRefreshAtUtc is null
            ? null
            : DateTimeOffset.Parse(LastRefreshAtUtc, CultureInfo.InvariantCulture),
        Extra = Json.Deserialize<JsonObject>(ExtraJson) ?? new JsonObject(),
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
