using Dapper;
using Astra.Core.Requests;

namespace Astra.Data.Repositories;

/// <summary>One conversation seen in the request log (.plan/observability.md §3.1): its calls, turns, errors and cost.</summary>
public sealed class TraceSessionSummary
{
    public string SessionKey { get; set; } = "";
    public string? ClientKind { get; set; }
    public DateTimeOffset FirstAtUtc { get; set; }
    public DateTimeOffset LastAtUtc { get; set; }
    public long Requests { get; set; }
    public long Turns { get; set; }
    public long Errors { get; set; }
    public long CostNanoUsd { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }

    /// <summary>Distinct providers the session's calls went to, comma-separated.</summary>
    public string? ProviderIds { get; set; }
}

/// <summary>One turn of a session: the calls between one user input and the next.</summary>
public sealed class TraceTurnSummary
{
    public string TurnId { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public long Requests { get; set; }
    public long Errors { get; set; }
    public long CostNanoUsd { get; set; }

    /// <summary>From the first call's start to the end of the call that finished last.</summary>
    public double DurationMs { get; set; }
}

/// <summary>Traffic of one client kind (direct calls: null) over a window.</summary>
public sealed class TraceClientStat
{
    public string? ClientKind { get; set; }
    public long Requests { get; set; }
    public long Errors { get; set; }
}

/// <summary>Traffic of one provider over a window, with its failovers (requests that took more than one attempt).</summary>
public sealed class TraceProviderStat
{
    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }
    public long Requests { get; set; }
    public long Errors { get; set; }
    public long Attempts { get; set; }
    public long RateLimited { get; set; }
    public long Failovers { get; set; }
}

/// <summary>A timing sample of one recent request: provider, first-byte time and when Astra first went upstream.</summary>
public sealed class TraceTimingSample
{
    public string? ProviderId { get; set; }
    public long? TtfbMs { get; set; }

    /// <summary>Microseconds from arrival to the first upstream attempt (Astra's own overhead); null when none was made.</summary>
    public long? FirstAttemptUs { get; set; }
}

/// <summary>Duration of one span of a recent request, for per-stage percentiles.</summary>
public sealed class TraceStageSample
{
    public string Name { get; set; } = "";
    public long DurationUs { get; set; }
}

/// <summary>The latest attempt an account served in a window, with its attributes (rate-limit snapshot).</summary>
public sealed class TraceAccountAttempt
{
    public string? ProviderId { get; set; }
    public string? AccountId { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public string? AttributesJson { get; set; }
}

/// <summary>Everything the topology view aggregates over one window.</summary>
public sealed class TraceTopologyData
{
    public List<TraceClientStat> Clients { get; init; } = [];
    public List<TraceProviderStat> Providers { get; init; } = [];
    public List<TraceTimingSample> Timings { get; init; } = [];
    public List<TraceStageSample> Stages { get; init; } = [];
    public List<TraceAccountAttempt> AccountAttempts { get; init; } = [];
}

/// <summary>
/// Read side of tracing (.plan/observability.md §4): spans of a request or a turn, sessions and their turns, and the
/// topology aggregates. Spans are written by <see cref="RequestRepository.InsertBatchAsync"/> with their request.
/// </summary>
public sealed class TraceRepository(SqliteConnectionFactory factory)
{
    /// <summary>Requests a topology window samples for percentiles: the most recent ones, so a busy day stays cheap.</summary>
    public const int SampleLimit = 5000;

    private const string SpanSelect = """
        SELECT s.request_id, s.span_id, s.parent_span_id, s.name, s.kind, s.start_us, s.duration_us, s.status,
               s.status_message, s.attributes_json, s.events_json
        FROM trace_spans s
        """;

    /// <summary>The spans of one request, in start order.</summary>
    public async Task<List<TraceSpan>> ListSpansAsync(string requestId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<TraceSpan>(
            $"{SpanSelect} WHERE s.request_id = @request_id ORDER BY s.start_us, s.span_id",
            new { request_id = requestId })).ToList();
    }

    /// <summary>The spans of every request of one turn, request by request in start order.</summary>
    public async Task<List<TraceSpan>> ListTurnSpansAsync(string turnId, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<TraceSpan>(
            $"{SpanSelect} JOIN requests r ON r.id = s.request_id WHERE r.turn_id = @turn_id ORDER BY r.started_at_utc, r.id, s.start_us, s.span_id",
            new { turn_id = turnId })).ToList();
    }

    /// <summary>Sessions active since <paramref name="from"/>, most recently active first.</summary>
    public async Task<List<TraceSessionSummary>> ListSessionsAsync(DateTimeOffset from, string? clientKind, int limit, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<TraceSessionSummary>("""
            SELECT session_key AS SessionKey, MIN(client_kind) AS ClientKind,
                   MIN(started_at_utc) AS FirstAtUtc, MAX(started_at_utc) AS LastAtUtc,
                   COUNT(*) AS Requests, COUNT(DISTINCT turn_id) AS Turns,
                   COALESCE(SUM(CASE WHEN status <> 'success' THEN 1 ELSE 0 END), 0) AS Errors,
                   COALESCE(SUM(cost_nanousd), 0) AS CostNanoUsd,
                   COALESCE(SUM(total_input_tokens), 0) AS InputTokens,
                   COALESCE(SUM(total_output_tokens), 0) AS OutputTokens,
                   GROUP_CONCAT(DISTINCT provider_id) AS ProviderIds
            FROM requests
            WHERE session_key IS NOT NULL AND started_at_utc >= @from
              AND (@client_kind IS NULL OR client_kind = @client_kind)
            GROUP BY session_key
            ORDER BY LastAtUtc DESC
            LIMIT @limit
            """, new { from = DateTimeOffsetHandler.ToStorage(from), client_kind = clientKind, limit = Math.Clamp(limit, 1, 500) })).ToList();
    }

    /// <summary>One session's summary over its whole retained history; null when no request carries the key.</summary>
    public async Task<TraceSessionSummary?> GetSessionAsync(string sessionKey, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<TraceSessionSummary>("""
            SELECT session_key AS SessionKey, MIN(client_kind) AS ClientKind,
                   MIN(started_at_utc) AS FirstAtUtc, MAX(started_at_utc) AS LastAtUtc,
                   COUNT(*) AS Requests, COUNT(DISTINCT turn_id) AS Turns,
                   COALESCE(SUM(CASE WHEN status <> 'success' THEN 1 ELSE 0 END), 0) AS Errors,
                   COALESCE(SUM(cost_nanousd), 0) AS CostNanoUsd,
                   COALESCE(SUM(total_input_tokens), 0) AS InputTokens,
                   COALESCE(SUM(total_output_tokens), 0) AS OutputTokens,
                   GROUP_CONCAT(DISTINCT provider_id) AS ProviderIds
            FROM requests
            WHERE session_key = @session_key
            GROUP BY session_key
            """, new { session_key = sessionKey });
        return row;
    }

    /// <summary>The turns of one session, oldest first.</summary>
    public async Task<List<TraceTurnSummary>> ListTurnsAsync(string sessionKey, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<TraceTurnSummary>("""
            SELECT turn_id AS TurnId, MIN(started_at_utc) AS StartedAtUtc, COUNT(*) AS Requests,
                   COALESCE(SUM(CASE WHEN status <> 'success' THEN 1 ELSE 0 END), 0) AS Errors,
                   COALESCE(SUM(cost_nanousd), 0) AS CostNanoUsd,
                   (MAX(julianday(started_at_utc) * 86400000.0 + COALESCE(total_ms, 0))
                    - MIN(julianday(started_at_utc) * 86400000.0)) AS DurationMs
            FROM requests
            WHERE session_key = @session_key AND turn_id IS NOT NULL
            GROUP BY turn_id
            ORDER BY StartedAtUtc, TurnId
            """, new { session_key = sessionKey })).ToList();
    }

    /// <summary>Traffic, failovers, timing and per-stage samples since <paramref name="from"/> for the topology view.</summary>
    public async Task<TraceTopologyData> TopologyAsync(DateTimeOffset from, CancellationToken ct = default)
    {
        var p = new { from = DateTimeOffsetHandler.ToStorage(from), limit = SampleLimit };
        await using var conn = await factory.OpenAsync(ct);
        var clients = (await conn.QueryAsync<TraceClientStat>("""
            SELECT client_kind AS ClientKind, COUNT(*) AS Requests,
                   COALESCE(SUM(CASE WHEN status <> 'success' THEN 1 ELSE 0 END), 0) AS Errors
            FROM requests WHERE started_at_utc >= @from
            GROUP BY client_kind
            """, p)).ToList();
        var providers = (await conn.QueryAsync<TraceProviderStat>("""
            SELECT r.provider_id AS ProviderId, MAX(r.provider_name) AS ProviderName, COUNT(*) AS Requests,
                   COALESCE(SUM(CASE WHEN r.status <> 'success' THEN 1 ELSE 0 END), 0) AS Errors,
                   COALESCE(SUM(a.attempts), 0) AS Attempts,
                   COALESCE(SUM(a.rate_limited), 0) AS RateLimited,
                   COALESCE(SUM(CASE WHEN a.attempts > 1 THEN 1 ELSE 0 END), 0) AS Failovers
            FROM requests r
            LEFT JOIN (
                SELECT request_id, COUNT(*) AS attempts,
                       SUM(CASE WHEN json_extract(attributes_json, '$."http.response.status_code"') = 429 THEN 1 ELSE 0 END) AS rate_limited
                FROM trace_spans WHERE name = 'upstream.attempt' GROUP BY request_id
            ) a ON a.request_id = r.id
            WHERE r.started_at_utc >= @from AND r.provider_id IS NOT NULL
            GROUP BY r.provider_id
            """, p)).ToList();
        var timings = (await conn.QueryAsync<TraceTimingSample>("""
            SELECT r.provider_id AS ProviderId, r.ttfb_ms AS TtfbMs,
                   (SELECT MIN(s.start_us) FROM trace_spans s WHERE s.request_id = r.id AND s.name = 'upstream.attempt') AS FirstAttemptUs
            FROM requests r
            WHERE r.started_at_utc >= @from AND r.trace_id IS NOT NULL
            ORDER BY r.started_at_utc DESC
            LIMIT @limit
            """, p)).ToList();
        var stages = (await conn.QueryAsync<TraceStageSample>("""
            SELECT s.name AS Name, s.duration_us AS DurationUs
            FROM trace_spans s
            WHERE s.request_id IN (SELECT id FROM requests WHERE started_at_utc >= @from AND trace_id IS NOT NULL
                                   ORDER BY started_at_utc DESC LIMIT @limit)
              AND s.name IN ('gateway.route', 'gateway.middleware', 'gateway.privacy', 'gateway.resolve', 'gateway.encode')
            """, p)).ToList();
        var accounts = (await conn.QueryAsync<TraceAccountAttempt>("""
            SELECT r.provider_id AS ProviderId,
                   json_extract(s.attributes_json, '$."astra.account.id"') AS AccountId,
                   MAX(r.started_at_utc) AS AtUtc,
                   s.attributes_json AS AttributesJson
            FROM trace_spans s JOIN requests r ON r.id = s.request_id
            WHERE s.name = 'upstream.attempt' AND r.started_at_utc >= @from
              AND json_extract(s.attributes_json, '$."astra.account.id"') IS NOT NULL
            GROUP BY r.provider_id, json_extract(s.attributes_json, '$."astra.account.id"')
            """, p)).ToList();
        return new TraceTopologyData { Clients = clients, Providers = providers, Timings = timings, Stages = stages, AccountAttempts = accounts };
    }
}
