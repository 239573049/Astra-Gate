using Dapper;
using Astra.Core;

namespace Astra.Data.Repositories;

/// <summary>
/// One request a Claude client (Claude Desktop / Claude Code) made on its own direct connection and
/// reported to us afterwards (row of the <c>claude_direct_requests</c> table). This is client telemetry,
/// not gateway traffic: it never passes through the pipeline and is not part of <c>requests</c>.
/// Money is integer nano-USD.
/// </summary>
public sealed class ClaudeDirectRequest
{
    /// <summary>The client's own event id; the primary key, so a replayed upload is ignored.</summary>
    public string Id { get; set; } = "";

    /// <summary>Profile the request belongs to (groups the sessions of one client profile).</summary>
    public string ProfileId { get; set; } = "";

    /// <summary>Session the request was made in.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>Upstream account the client used; null when the client reported none.</summary>
    public string? AccountUuid { get; set; }

    public string Model { get; set; } = "";

    /// <summary>When the request was made, in UTC.</summary>
    public DateTimeOffset OccurredAtUtc { get; set; }

    /// <summary>Outcome reported by the client (e.g. <c>success</c> / <c>error</c>), verbatim.</summary>
    public string Status { get; set; } = "";

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheCreationTokens { get; set; }

    /// <summary>Wall-clock duration the client measured; null when unmeasured.</summary>
    public long? DurationMs { get; set; }

    /// <summary>The client's own cost estimate in nano-USD; null when it reported none — never derived here.</summary>
    public long? EstimatedCostNanoUsd { get; set; }
}

/// <summary>
/// Persistence for the Claude clients' direct-connection telemetry (<c>claude_direct_requests</c>),
/// separate from the gateway's <c>requests</c> log. Rows arrive in uploads, so inserts deduplicate on
/// the client event id and retention is a plain age cutoff.
/// </summary>
public sealed class ClaudeDirectRequestRepository
{
    private const string Select = """
        SELECT id, profile_id, session_id, account_uuid, model, occurred_at_utc, status,
               input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
               duration_ms, estimated_cost_nanousd
        FROM claude_direct_requests
        """;

    /// <summary>Cap on <see cref="ListAsync"/>; also the largest page a caller can ask for.</summary>
    public const int MaxListLimit = 200;

    // The client replays its uploads, so an id already stored is left exactly as it was recorded.
    private const string InsertSql = """
        INSERT INTO claude_direct_requests(id, profile_id, session_id, account_uuid, model, occurred_at_utc, status,
                                           input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens,
                                           duration_ms, estimated_cost_nanousd)
        VALUES (@id, @profile_id, @session_id, @account_uuid, @model, @occurred_at_utc, @status,
                @input_tokens, @output_tokens, @cache_read_tokens, @cache_creation_tokens,
                @duration_ms, @estimated_cost_nanousd)
        ON CONFLICT(id) DO NOTHING
        """;

    private readonly SqliteConnectionFactory _factory;

    public ClaudeDirectRequestRepository(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Persists one upload in a single transaction. Records whose id is already stored are skipped
    /// (<c>ON CONFLICT(id) DO NOTHING</c>), which makes a replayed upload idempotent.
    /// </summary>
    public async Task InsertAsync(IReadOnlyList<ClaudeDirectRequest> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var record in records)
        {
            await conn.ExecuteAsync(InsertSql, new
            {
                id = record.Id,
                profile_id = record.ProfileId,
                session_id = record.SessionId,
                account_uuid = record.AccountUuid,
                model = record.Model,
                occurred_at_utc = DateTimeOffsetHandler.ToStorage(record.OccurredAtUtc),
                status = record.Status,
                input_tokens = record.InputTokens,
                output_tokens = record.OutputTokens,
                cache_read_tokens = record.CacheReadTokens,
                cache_creation_tokens = record.CacheCreationTokens,
                duration_ms = record.DurationMs,
                estimated_cost_nanousd = record.EstimatedCostNanoUsd,
            }, transaction: tx);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Most recent requests of one profile, newest first. <paramref name="limit"/> is clamped to 1..<see cref="MaxListLimit"/>.</summary>
    public async Task<List<ClaudeDirectRequest>> ListAsync(
        string profileId, int limit = 100, CancellationToken ct = default)
    {
        var take = Math.Clamp(limit, 1, MaxListLimit);
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<ClaudeDirectRequest>(
            $"{Select} WHERE profile_id = @profile_id ORDER BY occurred_at_utc DESC, id DESC LIMIT @limit",
            new { profile_id = profileId, limit = take })).ToList();
    }

    /// <summary>Retention cleanup: deletes rows that occurred before <paramref name="before"/>.</summary>
    public async Task DeleteOlderThanAsync(DateTimeOffset before, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM claude_direct_requests WHERE occurred_at_utc < @before",
            new { before = DateTimeOffsetHandler.ToStorage(before) });
    }
}
