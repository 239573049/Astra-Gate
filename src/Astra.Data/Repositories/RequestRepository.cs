using System.Globalization;
using Dapper;
using Astra.Core;
using Astra.Core.Requests;

namespace Astra.Data.Repositories;

/// <summary>Filter for <see cref="RequestRepository.QueryAsync"/> and <see cref="RequestRepository.RateAsync"/> (which ignores Page/PageSize).</summary>
public sealed record RequestQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? ClientKind { get; init; }
    public string? TokenId { get; init; }
    public string? ProviderId { get; init; }

    /// <summary>Literal contains-match (case-insensitive) over requested_model, upstream_model, system_model_id and response_model; LIKE wildcards in the term are escaped.</summary>
    public string? Model { get; init; }

    public string? Status { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>Paged result of a request-log query (newest first).</summary>
public sealed class RequestPage
{
    public IReadOnlyList<RequestRecord> Items { get; init; } = [];
    public long Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

/// <summary>
/// Aggregate over a short trailing window (the live RPM / TPM readout): request count, input+output tokens,
/// cache-read tokens and input tokens (the hit-rate denominator, cache reads included).
/// </summary>
public sealed record RateWindow(long Requests, long Tokens, long CacheReadTokens, long InputTokens);

/// <summary>Aggregated request/cost numbers for one time range.</summary>
public sealed class RequestSummary
{
    public long TotalRequests { get; init; }
    public long SuccessRequests { get; init; }
    public double SuccessRate { get; init; }
    public long TotalCostNanoUsd { get; init; }
    public long TotalInputTokens { get; init; }
    public long TotalOutputTokens { get; init; }
    public long TotalCacheReadTokens { get; init; }
    public long TotalCacheWriteTokens { get; init; }
    public long TotalReasoningTokens { get; init; }
    public double? AvgTtftMs { get; init; }
}

/// <summary>One timeseries bucket: "2026-10-06" (day) or "2026-10-06T12:00" (hour), in UTC shifted by the requested offset.</summary>
public sealed class TimeseriesPoint
{
    public string Bucket { get; init; } = "";
    public string? GroupKey { get; init; }

    /// <summary>Human label when available (provider name for by=provider).</summary>
    public string? Label { get; init; }

    public long Requests { get; init; }
    public long TotalCostNanoUsd { get; init; }
    public long TotalInputTokens { get; init; }
    public long TotalOutputTokens { get; init; }
}

/// <summary>One day of the activity heatmap: the local "yyyy-MM-dd" and how many requests landed on it.</summary>
public sealed class DailyActivityRow
{
    public string Day { get; init; } = "";
    public long Requests { get; init; }
}

/// <summary>Filter for privacy guard events (rows with a guard outcome in <c>privacy_json</c>).</summary>
public sealed record PrivacyEventQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? ClientKind { get; init; }
    public string? ProviderId { get; init; }

    /// <summary>Only rows whose hits include this category.</summary>
    public string? Category { get; init; }

    /// <summary>Only rows whose hits include this action (warn | block | redact).</summary>
    public string? Action { get; init; }

    /// <summary>true = blocked only, false = passed only, null = any.</summary>
    public bool? Blocked { get; init; }

    /// <summary>Matches requested_model, upstream_model or system_model_id.</summary>
    public string? Model { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
}

/// <summary>Light row of one privacy event; the report JSON is parsed by the caller.</summary>
public sealed record PrivacyEventRow(
    string Id, DateTimeOffset StartedAtUtc, string? ClientKind, string? ProviderId, string? ProviderName,
    string? RequestedModel, string Status, string PrivacyJson);

/// <summary>Paged result of a privacy-event query (newest first).</summary>
public sealed class PrivacyEventPage
{
    public IReadOnlyList<PrivacyEventRow> Items { get; init; } = [];
    public long Total { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

/// <summary>Per-category hit breakdown of the privacy guard.</summary>
public sealed record PrivacyCategoryStatRow(
    string Category, long Requests, long Hits, long WarnHits, long BlockHits, long RedactHits);

/// <summary>Aggregated privacy guard numbers for one time range.</summary>
public sealed class PrivacyEventStats
{
    public long Events { get; init; }
    public long Blocked { get; init; }
    public long DryRun { get; init; }
    public long Redactions { get; init; }
    public IReadOnlyList<PrivacyCategoryStatRow> Categories { get; init; } = [];
}

/// <summary>One row of the most expensive models in a range.</summary>
public sealed class TopModelStat
{
    public string ModelId { get; init; } = "";
    public long Requests { get; init; }
    public long TotalCostNanoUsd { get; init; }
    public long TotalInputTokens { get; init; }
    public long TotalOutputTokens { get; init; }
    /// <summary>Prompt-cache reads; already part of <see cref="TotalInputTokens"/>.</summary>
    public long TotalCacheReadTokens { get; init; }
}

/// <summary>Request log persistence: batch inserts plus paged queries, stats and retention cleanup.</summary>
public sealed class RequestRepository
{
    private const string RequestSelect = """
        SELECT id, started_at_utc, client_kind, token_id,
               COALESCE((SELECT t.name FROM tokens t WHERE t.id = requests.token_id), token_name) AS token_name,
               provider_id, provider_name, inbound_protocol, upstream_protocol,
               passthrough, requested_model, upstream_model, system_model_id, response_model, stream, service_tier, status,
               http_status, error_type, error_message, upstream_request_id, ttfb_ms, ttft_ms, total_ms,
               generation_ms, output_tps, total_input_tokens, total_output_tokens, cache_read_tokens, cache_write_tokens,
               reasoning_tokens, cost_nanousd, usage_source, usage_raw_json, pricing_snapshot_json, pricing_source, price_key, billing_trace_json,
               billing_description, privacy_json, body_ref, user_agent,
               reasoning_effort, reasoning_mode, reasoning_budget_tokens, account_id,
               (SELECT a.display_name FROM provider_accounts a WHERE a.id = requests.account_id) AS account_name
        FROM requests
        """;

    private const string ItemSelect = """
        SELECT id, request_id, token_type, tokens, is_per_call, unit_price, base_unit_price, tier_applied,
               priced_as, multipliers_json, cost_nanousd, note
        FROM request_usage_items
        """;

    private const string InsertRequestSql = """
        INSERT INTO requests(id, started_at_utc, client_kind, token_id, token_name, provider_id, provider_name, inbound_protocol,
                             upstream_protocol, passthrough, requested_model, upstream_model, system_model_id,
                             response_model,
                             stream, service_tier, status, http_status, error_type, error_message,
                             upstream_request_id, ttfb_ms, ttft_ms, total_ms, generation_ms, output_tps,
                             total_input_tokens, total_output_tokens, cache_read_tokens, cache_write_tokens,
                             reasoning_tokens, cost_nanousd, usage_source, usage_raw_json,
                             pricing_snapshot_json, pricing_source, price_key, billing_trace_json,
                             billing_description, privacy_json, body_ref, user_agent,
                             reasoning_effort, reasoning_mode, reasoning_budget_tokens, account_id)
        VALUES (@id, @started_at_utc, @client_kind, @token_id, @token_name, @provider_id, @provider_name, @inbound_protocol,
                @upstream_protocol, @passthrough, @requested_model, @upstream_model, @system_model_id,
                @response_model,
                @stream, @service_tier, @status, @http_status, @error_type, @error_message,
                @upstream_request_id, @ttfb_ms, @ttft_ms, @total_ms, @generation_ms, @output_tps,
                @total_input_tokens, @total_output_tokens, @cache_read_tokens, @cache_write_tokens,
                @reasoning_tokens, @cost_nanousd, @usage_source, @usage_raw_json,
                @pricing_snapshot_json, @pricing_source, @price_key, @billing_trace_json,
                @billing_description, @privacy_json, @body_ref, @user_agent,
                @reasoning_effort, @reasoning_mode, @reasoning_budget_tokens, @account_id)
        """;

    private const string InsertItemSql = """
        INSERT INTO request_usage_items(request_id, token_type, tokens, is_per_call, unit_price, base_unit_price,
                                        tier_applied, priced_as, multipliers_json, cost_nanousd, note)
        VALUES (@request_id, @token_type, @tokens, @is_per_call, @unit_price, @base_unit_price,
                @tier_applied, @priced_as, @multipliers_json, @cost_nanousd, @note)
        """;

    // Lifetime token counters (plan: tokens). Skipped when the token was deleted meanwhile (no row to attach to).
    private const string AddTokenTotalsSql = """
        INSERT INTO token_usage_totals(token_id, requests, success_requests, cost_nanousd, input_tokens, output_tokens,
                                       cache_read_tokens, cache_write_tokens, reasoning_tokens, tps_output_tokens, tps_generation_ms)
        SELECT @token_id, 1, @success, @cost_nanousd, @input_tokens, @output_tokens, @cache_read_tokens, @cache_write_tokens,
               @reasoning_tokens, @tps_output_tokens, @tps_generation_ms
        WHERE EXISTS (SELECT 1 FROM tokens WHERE id = @token_id)
        ON CONFLICT(token_id) DO UPDATE SET
            requests = requests + 1,
            success_requests = success_requests + excluded.success_requests,
            cost_nanousd = cost_nanousd + excluded.cost_nanousd,
            input_tokens = input_tokens + excluded.input_tokens,
            output_tokens = output_tokens + excluded.output_tokens,
            cache_read_tokens = cache_read_tokens + excluded.cache_read_tokens,
            cache_write_tokens = cache_write_tokens + excluded.cache_write_tokens,
            reasoning_tokens = reasoning_tokens + excluded.reasoning_tokens,
            tps_output_tokens = tps_output_tokens + excluded.tps_output_tokens,
            tps_generation_ms = tps_generation_ms + excluded.tps_generation_ms
        """;

    private const string TouchTokenSql = """
        UPDATE tokens SET last_used_at = MAX(COALESCE(last_used_at, ''), @used_at) WHERE id = @token_id
        """;

    // Filters are expressed against a fixed parameter shape (Dapper.AOT cannot read DynamicParameters):
    // a null/empty parameter means "no filter" for that predicate, matching the previous string-built
    // predicate list row-for-row. The SQL text below is constant; the parameter objects are anonymous.
    private const string RequestWhereSql = """
         WHERE 1=1
           AND (@from IS NULL OR started_at_utc >= @from)
           AND (@to IS NULL OR started_at_utc <= @to)
           AND (@client_kind IS NULL OR @client_kind = '' OR client_kind = @client_kind)
           AND (@token_id IS NULL OR @token_id = '' OR token_id = @token_id)
           AND (@provider_id IS NULL OR @provider_id = '' OR provider_id = @provider_id)
           AND (@model IS NULL OR (requested_model LIKE @model ESCAPE '\' OR upstream_model LIKE @model ESCAPE '\'
                OR system_model_id LIKE @model ESCAPE '\' OR response_model LIKE @model ESCAPE '\'))
           AND (@status IS NULL OR @status = '' OR status = @status)
        """;

    private const string PrivacyWhereSql = """
         WHERE privacy_json IS NOT NULL
           AND (@from IS NULL OR started_at_utc >= @from)
           AND (@to IS NULL OR started_at_utc <= @to)
           AND (@client_kind IS NULL OR @client_kind = '' OR client_kind = @client_kind)
           AND (@provider_id IS NULL OR @provider_id = '' OR provider_id = @provider_id)
           AND (@blocked IS NULL OR json_extract(privacy_json, '$.blocked') = @blocked)
           AND (@hit_category IS NULL OR @hit_category = '' OR EXISTS
                (SELECT 1 FROM json_each(json_extract(privacy_json, '$.hits')) je
                 WHERE json_extract(je.value, '$.category') = @hit_category))
           AND (@hit_action IS NULL OR @hit_action = '' OR EXISTS
                (SELECT 1 FROM json_each(json_extract(privacy_json, '$.hits')) je
                 WHERE json_extract(je.value, '$.action') = @hit_action))
           AND (@model IS NULL OR @model = ''
                OR (requested_model = @model OR upstream_model = @model OR system_model_id = @model))
        """;

    private readonly SqliteConnectionFactory _factory;

    public RequestRepository(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Inserts records and their usage items in ONE transaction (called by the background usage writer), adding
    /// each record to its token's lifetime counters in that same transaction. Fills missing record ids with fresh ULIDs.
    /// </summary>
    public async Task InsertBatchAsync(IReadOnlyList<RequestRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var record in records)
        {
            if (string.IsNullOrEmpty(record.Id)) record.Id = Ulid.NewUlid();
            await conn.ExecuteAsync(InsertRequestSql, new
            {
                id = record.Id,
                started_at_utc = DateTimeOffsetHandler.ToStorage(record.StartedAtUtc),
                client_kind = record.ClientKind,
                token_id = record.TokenId,
                token_name = record.TokenName,
                provider_id = record.ProviderId,
                provider_name = record.ProviderName,
                inbound_protocol = record.InboundProtocol,
                upstream_protocol = record.UpstreamProtocol,
                passthrough = record.Passthrough,
                requested_model = record.RequestedModel,
                upstream_model = record.UpstreamModel,
                system_model_id = record.SystemModelId,
                response_model = record.ResponseModel,
                stream = record.Stream,
                service_tier = record.ServiceTier,
                status = record.Status,
                http_status = record.HttpStatus,
                error_type = record.ErrorType,
                error_message = record.ErrorMessage,
                upstream_request_id = record.UpstreamRequestId,
                ttfb_ms = record.TtfbMs,
                ttft_ms = record.TtftMs,
                total_ms = record.TotalMs,
                generation_ms = record.GenerationMs,
                output_tps = record.OutputTps,
                total_input_tokens = record.TotalInputTokens,
                total_output_tokens = record.TotalOutputTokens,
                cache_read_tokens = record.CacheReadTokens,
                cache_write_tokens = record.CacheWriteTokens,
                reasoning_tokens = record.ReasoningTokens,
                cost_nanousd = record.CostNanoUsd,
                usage_source = record.UsageSource,
                usage_raw_json = record.UsageRawJson,
                pricing_snapshot_json = record.PricingSnapshotJson,
                pricing_source = record.PricingSource,
                price_key = record.PriceKey,
                billing_trace_json = record.BillingTraceJson,
                billing_description = record.BillingDescription,
                privacy_json = record.PrivacyJson,
                body_ref = record.BodyRef,
                user_agent = record.UserAgent,
                reasoning_effort = record.ReasoningEffort,
                reasoning_mode = record.ReasoningMode,
                reasoning_budget_tokens = record.ReasoningBudgetTokens,
                account_id = record.AccountId,
            }, transaction: tx);
            foreach (var item in record.UsageItems)
            {
                item.RequestId = record.Id;
                await conn.ExecuteAsync(InsertItemSql, new
                {
                    request_id = item.RequestId,
                    token_type = item.TokenType,
                    tokens = item.Tokens,
                    is_per_call = item.IsPerCall,
                    unit_price = item.UnitPrice,
                    base_unit_price = item.BaseUnitPrice,
                    tier_applied = item.TierApplied,
                    priced_as = item.PricedAs,
                    multipliers_json = item.MultipliersJson,
                    cost_nanousd = item.CostNanoUsd,
                    note = item.Note,
                }, transaction: tx);
            }
            if (record.TokenId is not null)
            {
                var timed = record.Status == RequestStatus.Success && record.GenerationMs > 0;
                await conn.ExecuteAsync(AddTokenTotalsSql, new
                {
                    token_id = record.TokenId,
                    success = record.Status == RequestStatus.Success ? 1 : 0,
                    cost_nanousd = record.CostNanoUsd,
                    input_tokens = record.TotalInputTokens,
                    output_tokens = record.TotalOutputTokens,
                    cache_read_tokens = record.CacheReadTokens,
                    cache_write_tokens = record.CacheWriteTokens,
                    reasoning_tokens = record.ReasoningTokens,
                    tps_output_tokens = timed ? record.TotalOutputTokens : 0,
                    tps_generation_ms = timed ? record.GenerationMs!.Value : 0,
                }, transaction: tx);
                await conn.ExecuteAsync(TouchTokenSql, new
                {
                    token_id = record.TokenId,
                    used_at = DateTimeOffsetHandler.ToStorage(record.StartedAtUtc),
                }, transaction: tx);
            }
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Paged privacy events (rows with a guard outcome), newest first, with total count.</summary>
    public async Task<PrivacyEventPage> QueryPrivacyEventsAsync(PrivacyEventQuery query, CancellationToken ct = default)
    {
        var filter = ToPrivacyFilter(query);
        var p = new
        {
            filter.from, filter.to, filter.client_kind, filter.provider_id,
            filter.blocked, filter.hit_category, filter.hit_action, filter.model,
        };
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        await using var conn = await _factory.OpenAsync(ct);
        var total = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM requests{PrivacyWhereSql}", p);
        var items = (await conn.QueryAsync<PrivacyEventRow>($"""
            SELECT id, started_at_utc, client_kind, provider_id, provider_name, requested_model, status, privacy_json
            FROM requests{PrivacyWhereSql} ORDER BY started_at_utc DESC, id DESC LIMIT @limit OFFSET @offset
            """, new
        {
            p.from, p.to, p.client_kind, p.provider_id,
            p.blocked, p.hit_category, p.hit_action, p.model,
            limit = pageSize,
            offset = (page - 1) * pageSize,
        })).ToList();
        return new PrivacyEventPage { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    /// <summary>Aggregated guard outcome counts plus a per-category hit breakdown for the same filters.</summary>
    public async Task<PrivacyEventStats> PrivacyEventStatsAsync(PrivacyEventQuery query, CancellationToken ct = default)
    {
        var filter = ToPrivacyFilter(query);
        var p = new
        {
            filter.from, filter.to, filter.client_kind, filter.provider_id,
            filter.blocked, filter.hit_category, filter.hit_action, filter.model,
        };

        await using var conn = await _factory.OpenAsync(ct);
        var totals = await conn.QuerySingleAsync<PrivacyTotalsRow>($"""
            SELECT COUNT(*) AS Events,
                   COALESCE(SUM(json_extract(privacy_json, '$.blocked')), 0) AS Blocked,
                   COALESCE(SUM(json_extract(privacy_json, '$.dry_run')), 0) AS DryRun,
                   COALESCE(SUM(json_extract(privacy_json, '$.redactions')), 0) AS Redactions
            FROM requests{PrivacyWhereSql}
            """, p);
        var categories = (await conn.QueryAsync<PrivacyCategoryRow>($"""
            SELECT json_extract(h.value, '$.category') AS Category,
                   COUNT(DISTINCT requests.id) AS Requests,
                   COALESCE(SUM(json_extract(h.value, '$.count')), 0) AS Hits,
                   COALESCE(SUM(CASE WHEN json_extract(h.value, '$.action') = 'warn' THEN json_extract(h.value, '$.count') ELSE 0 END), 0) AS WarnHits,
                   COALESCE(SUM(CASE WHEN json_extract(h.value, '$.action') = 'block' THEN json_extract(h.value, '$.count') ELSE 0 END), 0) AS BlockHits,
                   COALESCE(SUM(CASE WHEN json_extract(h.value, '$.action') = 'redact' THEN json_extract(h.value, '$.count') ELSE 0 END), 0) AS RedactHits
            FROM requests, json_each(json_extract(requests.privacy_json, '$.hits')) h{PrivacyWhereSql}
            GROUP BY Category
            ORDER BY Hits DESC
            """, p)).ToList();
        return new PrivacyEventStats
        {
            Events = totals.Events,
            Blocked = totals.Blocked,
            DryRun = totals.DryRun,
            Redactions = totals.Redactions,
            Categories = categories.Select(c => new PrivacyCategoryStatRow(
                c.Category ?? "", c.Requests, c.Hits, c.WarnHits, c.BlockHits, c.RedactHits)).ToList(),
        };
    }

    /// <summary>Fixed-shape filter values for <see cref="PrivacyWhereSql"/> (member names are the SQL parameter names); a member is null (or empty, for strings) exactly when the previous string-built predicate list omitted that filter.</summary>
    private sealed record PrivacyFilter(
        string? from, string? to, string? client_kind, string? provider_id,
        long? blocked, string? hit_category, string? hit_action, string? model);

    private static PrivacyFilter ToPrivacyFilter(PrivacyEventQuery query) => new(
        query.From is { } from ? DateTimeOffsetHandler.ToStorage(from) : null,
        query.To is { } to ? DateTimeOffsetHandler.ToStorage(to) : null,
        query.ClientKind,
        query.ProviderId,
        query.Blocked is { } blocked ? (long?)(blocked ? 1 : 0) : null,
        query.Category,
        query.Action,
        query.Model);

    /// <summary>Paged, newest-first query with total count.</summary>
    public async Task<RequestPage> QueryAsync(RequestQuery query, CancellationToken ct = default)
    {
        var p = new
        {
            from = query.From is { } from ? DateTimeOffsetHandler.ToStorage(from) : null,
            to = query.To is { } to ? DateTimeOffsetHandler.ToStorage(to) : null,
            client_kind = query.ClientKind,
            token_id = query.TokenId,
            provider_id = query.ProviderId,
            model = LikePattern(query.Model),
            status = query.Status,
        };
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        await using var conn = await _factory.OpenAsync(ct);
        var total = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM requests{RequestWhereSql}", p);
        var items = (await conn.QueryAsync<RequestRecord>(
            $"{RequestSelect}{RequestWhereSql} ORDER BY started_at_utc DESC, id DESC LIMIT @limit OFFSET @offset", new
            {
                p.from, p.to, p.client_kind, p.token_id, p.provider_id, p.model, p.status,
                limit = pageSize,
                offset = (page - 1) * pageSize,
            })).ToList();
        return new RequestPage { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    /// <summary>
    /// Aggregates a short trailing window (the live RPM / TPM readout) under the <see cref="RequestQuery"/>
    /// filters — same model contains-match and status handling as <see cref="QueryAsync"/>; Page/PageSize ignored.
    /// </summary>
    public async Task<RateWindow> RateAsync(RequestQuery query, CancellationToken ct = default)
    {
        var p = new
        {
            from = query.From is { } from ? DateTimeOffsetHandler.ToStorage(from) : null,
            to = query.To is { } to ? DateTimeOffsetHandler.ToStorage(to) : null,
            client_kind = query.ClientKind,
            token_id = query.TokenId,
            provider_id = query.ProviderId,
            model = LikePattern(query.Model),
            status = query.Status,
        };
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<RateRow>($"""
            SELECT COUNT(*) AS Requests,
                   COALESCE(SUM(total_input_tokens + total_output_tokens), 0) AS Tokens,
                   COALESCE(SUM(cache_read_tokens), 0) AS CacheReadTokens,
                   COALESCE(SUM(total_input_tokens), 0) AS InputTokens
            FROM requests{RequestWhereSql}
            """, p);
        return new RateWindow(row.Requests, row.Tokens, row.CacheReadTokens, row.InputTokens);
    }

    /// <summary>One request including its usage items; null when absent.</summary>
    public async Task<RequestRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var record = await conn.QuerySingleOrDefaultAsync<RequestRecord>($"{RequestSelect} WHERE id = @id", new { id });
        if (record is null) return null;
        record.UsageItems = (await conn.QueryAsync<RequestUsageItem>(
            $"{ItemSelect} WHERE request_id = @request_id ORDER BY id", new { request_id = id })).ToList();
        return record;
    }

    /// <summary>
    /// Cost, request count, success rate, token totals and average TTFT for a range;
    /// <paramref name="clientKind"/> / <paramref name="tokenId"/> restrict it to one client / token (null = every request).
    /// </summary>
    public async Task<RequestSummary> SummaryAsync(
        DateTimeOffset from, DateTimeOffset to, string? clientKind = null, string? tokenId = null, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleAsync<SummaryRow>($"""
            SELECT COUNT(*) AS TotalRequests,
                   COALESCE(SUM(CASE WHEN status = 'success' THEN 1 ELSE 0 END), 0) AS SuccessRequests,
                   COALESCE(SUM(cost_nanousd), 0) AS TotalCostNanoUsd,
                   COALESCE(SUM(total_input_tokens), 0) AS TotalInputTokens,
                   COALESCE(SUM(total_output_tokens), 0) AS TotalOutputTokens,
                   COALESCE(SUM(cache_read_tokens), 0) AS TotalCacheReadTokens,
                   COALESCE(SUM(cache_write_tokens), 0) AS TotalCacheWriteTokens,
                   COALESCE(SUM(reasoning_tokens), 0) AS TotalReasoningTokens,
                   AVG(ttft_ms) AS AvgTtftMs
            FROM requests
            WHERE started_at_utc >= @from AND started_at_utc <= @to{Filters(clientKind, tokenId)}
            """, new
        {
            from = DateTimeOffsetHandler.ToStorage(from),
            to = DateTimeOffsetHandler.ToStorage(to),
            client_kind = clientKind,
            token_id = tokenId,
        });
        return new RequestSummary
        {
            TotalRequests = row.TotalRequests,
            SuccessRequests = row.SuccessRequests,
            SuccessRate = row.TotalRequests == 0 ? 0 : (double)row.SuccessRequests / row.TotalRequests,
            TotalCostNanoUsd = row.TotalCostNanoUsd,
            TotalInputTokens = row.TotalInputTokens,
            TotalOutputTokens = row.TotalOutputTokens,
            TotalCacheReadTokens = row.TotalCacheReadTokens,
            TotalCacheWriteTokens = row.TotalCacheWriteTokens,
            TotalReasoningTokens = row.TotalReasoningTokens,
            AvgTtftMs = row.AvgTtftMs,
        };
    }

    /// <summary>
    /// Cost/usage buckets. <paramref name="groupBy"/> is "day" or "hour"; buckets are UTC wall time shifted by
    /// <paramref name="utcOffsetMinutes"/> (pass the viewer's offset to bucket by local days/hours).
    /// <paramref name="by"/> is "model", "provider", "client" or "token"; <paramref name="clientKind"/> /
    /// <paramref name="tokenId"/> restrict the rows to one client / token.
    /// </summary>
    public async Task<IReadOnlyList<TimeseriesPoint>> TimeseriesAsync(
        DateTimeOffset from, DateTimeOffset to, string groupBy = "day", string by = "model",
        string? clientKind = null, int utcOffsetMinutes = 0, string? tokenId = null, CancellationToken ct = default)
    {
        var bucket = groupBy.Trim().ToLowerInvariant() switch
        {
            "day" => "strftime('%Y-%m-%d', started_at_utc, @shift)",
            "hour" => "strftime('%Y-%m-%dT%H:00', started_at_utc, @shift)",
            _ => throw new ArgumentException("groupBy must be \"day\" or \"hour\".", nameof(groupBy)),
        };
        if (utcOffsetMinutes is < -14 * 60 or > 14 * 60)
            throw new ArgumentOutOfRangeException(nameof(utcOffsetMinutes), "UTC offset must be within ±14 hours.");
        var (keyExpr, labelExpr) = by.Trim().ToLowerInvariant() switch
        {
            "model" => ("COALESCE(system_model_id, requested_model)", "COALESCE(system_model_id, requested_model)"),
            "provider" => ("provider_id", "MAX(provider_name)"),
            "client" => ("client_kind", "client_kind"),
            // Current token name; the request-time snapshot once the token was deleted.
            "token" => ("token_id", "COALESCE((SELECT t.name FROM tokens t WHERE t.id = requests.token_id), MAX(token_name))"),
            _ => throw new ArgumentException("by must be \"model\", \"provider\", \"client\" or \"token\".", nameof(by)),
        };

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<TimeseriesRow>($"""
            SELECT {bucket} AS Bucket,
                   {keyExpr} AS GroupKey,
                   {labelExpr} AS Label,
                   COUNT(*) AS Requests,
                   COALESCE(SUM(cost_nanousd), 0) AS TotalCostNanoUsd,
                   COALESCE(SUM(total_input_tokens), 0) AS TotalInputTokens,
                   COALESCE(SUM(total_output_tokens), 0) AS TotalOutputTokens
            FROM requests
            WHERE started_at_utc >= @from AND started_at_utc <= @to{Filters(clientKind, tokenId)}
            GROUP BY Bucket, GroupKey
            ORDER BY Bucket
            """, new
        {
            from = DateTimeOffsetHandler.ToStorage(from),
            to = DateTimeOffsetHandler.ToStorage(to),
            client_kind = clientKind,
            token_id = tokenId,
            shift = string.Create(CultureInfo.InvariantCulture, $"{utcOffsetMinutes:+0;-0;+0} minutes"),
        });
        return rows.Select(r => new TimeseriesPoint
        {
            Bucket = r.Bucket,
            GroupKey = r.GroupKey,
            Label = r.Label,
            Requests = r.Requests,
            TotalCostNanoUsd = r.TotalCostNanoUsd,
            TotalInputTokens = r.TotalInputTokens,
            TotalOutputTokens = r.TotalOutputTokens,
        }).ToList();
    }

    /// <summary>
    /// Most expensive models in the range, ordered by cost descending; <paramref name="clientKind"/> /
    /// <paramref name="tokenId"/> restrict it to one client / token.
    /// </summary>
    public async Task<IReadOnlyList<TopModelStat>> TopModelsAsync(
        DateTimeOffset from, DateTimeOffset to, int limit = 10, string? clientKind = null, string? tokenId = null,
        CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<TopModelRow>($"""
            SELECT COALESCE(system_model_id, requested_model) AS ModelId,
                   COUNT(*) AS Requests,
                   COALESCE(SUM(cost_nanousd), 0) AS TotalCostNanoUsd,
                   COALESCE(SUM(total_input_tokens), 0) AS TotalInputTokens,
                   COALESCE(SUM(total_output_tokens), 0) AS TotalOutputTokens,
                   COALESCE(SUM(cache_read_tokens), 0) AS TotalCacheReadTokens
            FROM requests
            WHERE started_at_utc >= @from AND started_at_utc <= @to{Filters(clientKind, tokenId)}
            GROUP BY ModelId
            ORDER BY TotalCostNanoUsd DESC
            LIMIT @limit
            """, new
        {
            from = DateTimeOffsetHandler.ToStorage(from),
            to = DateTimeOffsetHandler.ToStorage(to),
            client_kind = clientKind,
            token_id = tokenId,
            limit,
        });
        return rows.Select(r => new TopModelStat
        {
            ModelId = r.ModelId ?? "",
            Requests = r.Requests,
            TotalCostNanoUsd = r.TotalCostNanoUsd,
            TotalInputTokens = r.TotalInputTokens,
            TotalOutputTokens = r.TotalOutputTokens,
            TotalCacheReadTokens = r.TotalCacheReadTokens,
        }).ToList();
    }

    /// <summary>
    /// Request count per local day since <paramref name="from"/>, for the overview's activity heatmap. Days are
    /// bucketed by the viewer's offset like <see cref="TimeseriesAsync"/> and days with no requests are absent;
    /// <paramref name="clientKind"/> / <paramref name="tokenId"/> restrict the rows to one client / token.
    /// </summary>
    public async Task<IReadOnlyList<DailyActivityRow>> DailyActivityAsync(
        DateTimeOffset from, string? clientKind = null, int utcOffsetMinutes = 0, string? tokenId = null,
        CancellationToken ct = default)
    {
        if (utcOffsetMinutes is < -14 * 60 or > 14 * 60)
            throw new ArgumentOutOfRangeException(nameof(utcOffsetMinutes), "UTC offset must be within ±14 hours.");

        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<DailyActivityRow>($"""
            SELECT strftime('%Y-%m-%d', started_at_utc, @shift) AS Day,
                   COUNT(*) AS Requests
            FROM requests
            WHERE started_at_utc >= @from{Filters(clientKind, tokenId)}
            GROUP BY Day
            ORDER BY Day
            """, new
        {
            from = DateTimeOffsetHandler.ToStorage(from),
            client_kind = clientKind,
            token_id = tokenId,
            shift = string.Create(CultureInfo.InvariantCulture, $"{utcOffsetMinutes:+0;-0;+0} minutes"),
        });
        return rows.Where(r => !string.IsNullOrEmpty(r.Day)).ToList();
    }

    /// <summary>Retention cleanup: deletes requests (and, via cascade, their usage items) started before the cutoff. Returns the number of requests removed.</summary>
    public async Task<long> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM requests WHERE started_at_utc < @cutoff",
            new { cutoff = DateTimeOffsetHandler.ToStorage(cutoff) });
    }

    /// <summary>
    /// Extra stats predicates for the optional client / token filters; the values are bound as <c>@client_kind</c> and
    /// <c>@token_id</c>.
    /// </summary>
    private static string Filters(string? clientKind, string? tokenId) =>
        (string.IsNullOrEmpty(clientKind) ? "" : " AND client_kind = @client_kind")
        + (string.IsNullOrEmpty(tokenId) ? "" : " AND token_id = @token_id");

    /// <summary>
    /// Literal contains-match for a model term: escape LIKE wildcards so the term never broadens, then wrap it
    /// in %...%. Null when absent/whitespace — @model IS NULL then means "no filter".
    /// </summary>
    private static string? LikePattern(string? term)
    {
        if (string.IsNullOrWhiteSpace(term)) return null;
        return $"%{term.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";
    }
}

// Dapper.AOT only materializes rows into types it can see from outside the repository class; nested
// private types are silently left on vanilla Dapper, which dies under Native AOT (see its FAQ).
internal sealed class PrivacyTotalsRow
{
    public long Events { get; set; }
    public long Blocked { get; set; }
    public long DryRun { get; set; }
    public long Redactions { get; set; }
}

internal sealed class PrivacyCategoryRow
{
    public string? Category { get; set; }
    public long Requests { get; set; }
    public long Hits { get; set; }
    public long WarnHits { get; set; }
    public long BlockHits { get; set; }
    public long RedactHits { get; set; }
}

internal sealed class SummaryRow
{
    public long TotalRequests { get; set; }
    public long SuccessRequests { get; set; }
    public long TotalCostNanoUsd { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalCacheReadTokens { get; set; }
    public long TotalCacheWriteTokens { get; set; }
    public long TotalReasoningTokens { get; set; }
    public double? AvgTtftMs { get; set; }
}

internal sealed class TimeseriesRow
{
    public string Bucket { get; set; } = "";
    public string? GroupKey { get; set; }
    public string? Label { get; set; }
    public long Requests { get; set; }
    public long TotalCostNanoUsd { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
}

internal sealed class TopModelRow
{
    public string? ModelId { get; set; }
    public long Requests { get; set; }
    public long TotalCostNanoUsd { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }
    public long TotalCacheReadTokens { get; set; }
}

internal sealed class RateRow
{
    public long Requests { get; set; }
    public long Tokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long InputTokens { get; set; }
}
