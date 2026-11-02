using Dapper;
using Astra.Core.Tokens;

namespace Astra.Data.Repositories;

/// <summary>One-time join codes of gateway tokens (table <c>token_invites</c>).</summary>
public sealed class TokenInviteRepository
{
    private const string Select = """
        SELECT id, token_id, code_hash, created_at, expires_at, used_at, used_ip, revoked_at
        FROM token_invites
        """;

    private readonly SqliteConnectionFactory _factory;

    public TokenInviteRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task InsertAsync(TokenInvite invite, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO token_invites(id, token_id, code_hash, created_at, expires_at)
            VALUES (@id, @token_id, @code_hash, @created_at, @expires_at)
            """, new
        {
            id = invite.Id,
            token_id = invite.TokenId,
            code_hash = invite.CodeHash,
            created_at = DateTimeOffsetHandler.ToStorage(invite.CreatedAt),
            expires_at = DateTimeOffsetHandler.ToStorage(invite.ExpiresAt),
        });
    }

    public async Task<TokenInvite?> FindByHashAsync(string codeHash, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TokenInvite>($"{Select} WHERE code_hash = @codeHash", new { codeHash });
    }

    /// <summary>Codes of a token that are still open, plus the ones used or revoked since <paramref name="since"/>; newest first.</summary>
    public async Task<IReadOnlyList<TokenInvite>> ListAsync(string tokenId, DateTimeOffset since, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<TokenInvite>($"""
            {Select} WHERE token_id = @tokenId AND created_at >= @since ORDER BY created_at DESC, id DESC
            """, new { tokenId, since = DateTimeOffsetHandler.ToStorage(since) })).ToList();
    }

    /// <summary>
    /// Marks the code used, but only if it is still open: one statement, so two simultaneous redeemers cannot both win.
    /// False = somebody else got there first (or it expired / was revoked in the meantime).
    /// </summary>
    public async Task<bool> RedeemAsync(string id, DateTimeOffset now, string? ip, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("""
            UPDATE token_invites SET used_at = @now, used_ip = @ip
            WHERE id = @id AND used_at IS NULL AND revoked_at IS NULL AND expires_at > @now
            """, new { id, now = DateTimeOffsetHandler.ToStorage(now), ip }) == 1;
    }

    /// <summary>Revokes a code that has not been used. False = no such open code of that token.</summary>
    public async Task<bool> RevokeAsync(string tokenId, string id, DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("""
            UPDATE token_invites SET revoked_at = @now
            WHERE id = @id AND token_id = @tokenId AND used_at IS NULL AND revoked_at IS NULL
            """, new { id, tokenId, now = DateTimeOffsetHandler.ToStorage(now) }) == 1;
    }

    /// <summary>Drops dead codes older than <paramref name="before"/> (housekeeping when a new one is made).</summary>
    public async Task PruneAsync(DateTimeOffset before, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM token_invites WHERE expires_at < @before",
            new { before = DateTimeOffsetHandler.ToStorage(before) });
    }
}
