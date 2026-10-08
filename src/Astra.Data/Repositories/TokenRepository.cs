using Dapper;
using Astra.Core.Tokens;

namespace Astra.Data.Repositories;

/// <summary>
/// Gateway tokens (table <c>tokens</c>) and their lifetime usage counters (table <c>token_usage_totals</c>,
/// added to by <see cref="RequestRepository.InsertBatchAsync"/>).
/// </summary>
public sealed class TokenRepository
{
    private const string Select = """
        SELECT id, name, is_default, enabled, key_enc, key_hash, key_prefix, provider_id, account_id,
               created_at, updated_at, last_used_at
        FROM tokens
        """;

    private const string TotalsSelect = """
        SELECT token_id, requests, success_requests, cost_nanousd, input_tokens, output_tokens, cache_read_tokens,
               cache_write_tokens, reasoning_tokens, tps_output_tokens, tps_generation_ms
        FROM token_usage_totals
        """;

    private readonly SqliteConnectionFactory _factory;

    public TokenRepository(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>All tokens: the default token first, then by creation time.</summary>
    public async Task<IReadOnlyList<TokenRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<TokenRecord>($"{Select} ORDER BY is_default DESC, created_at, id")).ToList();
    }

    public async Task<TokenRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<TokenRecord>($"{Select} WHERE id = @id", new { id });
    }

    /// <summary>Candidates for a presented token (the caller verifies the hash).</summary>
    public async Task<IReadOnlyList<TokenRecord>> FindByPrefixAsync(string prefix, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<TokenRecord>($"{Select} WHERE key_prefix = @prefix", new { prefix })).ToList();
    }

    public async Task InsertAsync(TokenRecord token, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO tokens(id, name, is_default, enabled, key_enc, key_hash, key_prefix, provider_id, account_id,
                               created_at, updated_at, last_used_at)
            VALUES (@id, @name, @is_default, @enabled, @key_enc, @key_hash, @key_prefix, @provider_id, @account_id,
                    @created_at, @updated_at, @last_used_at)
            """, new
        {
            id = token.Id,
            name = token.Name,
            is_default = token.IsDefault,
            enabled = token.Enabled,
            key_enc = token.KeyEnc,
            key_hash = token.KeyHash,
            key_prefix = token.KeyPrefix,
            provider_id = token.ProviderId,
            account_id = token.AccountId,
            created_at = DateTimeOffsetHandler.ToStorage(token.CreatedAt),
            updated_at = DateTimeOffsetHandler.ToStorage(token.UpdatedAt),
            last_used_at = token.LastUsedAt is { } used ? DateTimeOffsetHandler.ToStorage(used) : null,
        });
    }

    /// <summary>Saves every editable column (name, enabled, key, direct-call provider); never touches last_used_at.</summary>
    public async Task<bool> UpdateAsync(TokenRecord token, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("""
            UPDATE tokens SET name = @name, enabled = @enabled, key_enc = @key_enc, key_hash = @key_hash,
                              key_prefix = @key_prefix, provider_id = @provider_id, account_id = @account_id,
                              updated_at = @updated_at
            WHERE id = @id
            """, new
        {
            id = token.Id,
            name = token.Name,
            enabled = token.Enabled,
            key_enc = token.KeyEnc,
            key_hash = token.KeyHash,
            key_prefix = token.KeyPrefix,
            provider_id = token.ProviderId,
            account_id = token.AccountId,
            updated_at = DateTimeOffsetHandler.ToStorage(token.UpdatedAt),
        }) > 0;
    }

    /// <summary>Deletes a token (its lifetime counters cascade). The default token is never deleted.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM tokens WHERE id = @id AND is_default = 0", new { id }) > 0;
    }

    /// <summary>Lifetime counters of every token that has any.</summary>
    public async Task<IReadOnlyList<TokenUsage>> ListTotalsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<TokenUsage>(TotalsSelect)).ToList();
    }

    /// <summary>Per-token usage from the request log within [from, to] (e.g. today).</summary>
    public async Task<IReadOnlyList<TokenUsage>> UsageAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<TokenUsage>("""
            SELECT token_id AS TokenId,
                   COUNT(*) AS Requests,
                   COALESCE(SUM(CASE WHEN status = 'success' THEN 1 ELSE 0 END), 0) AS SuccessRequests,
                   COALESCE(SUM(cost_nanousd), 0) AS CostNanoUsd,
                   COALESCE(SUM(total_input_tokens), 0) AS InputTokens,
                   COALESCE(SUM(total_output_tokens), 0) AS OutputTokens,
                   COALESCE(SUM(cache_read_tokens), 0) AS CacheReadTokens,
                   COALESCE(SUM(cache_write_tokens), 0) AS CacheWriteTokens,
                   COALESCE(SUM(reasoning_tokens), 0) AS ReasoningTokens,
                   COALESCE(SUM(CASE WHEN status = 'success' AND generation_ms > 0 THEN total_output_tokens ELSE 0 END), 0) AS TpsOutputTokens,
                   COALESCE(SUM(CASE WHEN status = 'success' AND generation_ms > 0 THEN generation_ms ELSE 0 END), 0) AS TpsGenerationMs
            FROM requests
            WHERE token_id IS NOT NULL AND started_at_utc >= @from AND started_at_utc <= @to
            GROUP BY token_id
            """, new { from = DateTimeOffsetHandler.ToStorage(from), to = DateTimeOffsetHandler.ToStorage(to) })).ToList();
    }
}
