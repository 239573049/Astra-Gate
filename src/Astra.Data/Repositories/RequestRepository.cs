using System.Globalization;
using System.Text;
using Dapper;
using Astra.Core;
using Astra.Core.Requests;

namespace Astra.Data.Repositories;

/// <summary>Filter for <see cref="RequestRepository.QueryAsync"/>.</summary>
public sealed record RequestQuery
{
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public string? ClientKind { get; init; }
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
        SELECT id, started_at_utc, client_kind, provider_id, provider_name, inbound_protocol, upstream_protocol,
               passthrough, requested_model, upstream_model, system_model_id, response_model, stream, service_tier, status,
               http_status, error_type, error_message, upstream_request_id, ttfb_ms, ttft_ms, total_ms,
               generation_ms, output_tps, total_input_tokens, total_output_tokens, cache_read_tokens, cache_write_tokens,
               reasoning_tokens, cost_nanousd, usage_source, usage_raw_json, pricing_snapshot_json, pricing_source, price_key, billing_trace_json,
               billing_description, privacy_json, body_ref, user_agent,
               reasoning_effort, reasoning_mode, reasoning_budget_tokens
        FROM requests
        """;

    private const string ItemSelect = """
        SELECT id, request_id, token_type, tokens, is_per_call, unit_price, base_unit_price, tier_applied,
               priced_as, multipliers_json, cost_nanousd, note
        FROM request_usage_items
        """;

    private const string InsertRequestSql = """
        INSERT INTO requests(id, started_at_utc, client_kind, provider_id, provider_name, inbound_protocol,
                             upstream_protocol, passthrough, requested_model, upstream_model, system_model_id,
                             response_model,
                             stream, service_tier, status, http_status, error_type, error_message,
                             upstream_request_id, ttfb_ms, ttft_ms, total_ms, generation_ms, output_tps,
                             total_input_tokens, total_output_tokens, cache_read_tokens, cache_write_tokens,
                             reasoning_tokens, cost_nanousd, usage_source, usage_raw_json,
                             pricing_snapshot_json, pricing_source, price_key, billing_trace_json,
                             billing_description, privacy_json, body_ref, user_agent,
                             reasoning_effort, reasoning_mode, reasoning_budget_tokens)
        VALUES (@id, @started_at_utc, @client_kind, @provider_id, @provider_name, @inbound_protocol,
                @upstream_protocol, @passthrough, @requested_model, @upstream_model, @system_model_id,
                @response_model,
                @stream, @service_tier, @status, @http_status, @error_type, @error_message,
                @upstream_request_id, @ttfb_ms, @ttft_ms, @total_ms, @generation_ms, @output_tps,
                @total_input_tokens, @total_output_tokens, @cache_read_tokens, @cache_write_tokens,
                @reasoning_tokens, @cost_nanousd, @usage_source, @usage_raw_json,
                @pricing_snapshot_json, @pricing_source, @price_key, @billing_trace_json,
                @billing_description, @privacy_json, @body_ref, @user_agent,
                @reasoning_effort, @reasoning_mode, @reasoning_budget_tokens)
        """;

    private const string InsertItemSql = """
        INSERT INTO request_usage_items(request_id, token_type, tokens, is_per_call, unit_price, base_unit_price,
                                        tier_applied, priced_as, multipliers_json, cost_nanousd, note)
        VALUES (@request_id, @token_type, @tokens, @is_per_call, @unit_price, @base_unit_price,
                @tier_applied, @priced_as, @multipliers_json, @cost_nanousd, @note)
        """;

    private readonly SqliteConnectionFactory _factory;

    public RequestRepository(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Inserts records and their usage items in ONE transaction (called by the background usage writer).
    /// Fills missing record ids with fresh ULIDs.
    /// </summary>
    public async Task InsertBatchAsync(IReadOnlyList<RequestRecord> records, CancellationToken ct = default)
    {
        if (records.Count == 0) return;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var record in records)
        {
            if (string.IsNullOrEmpty(record.Id)) record.Id = Ulid.NewUlid();
            await conn.ExecuteAsync(InsertRequestSql, RequestParams(record), transaction: tx);
            foreach (var item in record.UsageItems)
            {
                item.RequestId = record.Id;
                await conn.ExecuteAsync(InsertItemSql, ItemParams(item), transaction: tx);
            }
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Paged privacy events (rows with a guard outcome), newest first, with total count.</summary>
    public async Task<PrivacyEventPage> QueryPrivacyEventsAsync(PrivacyEventQuery query, CancellationToken ct = default)
    {
        var where = new StringBuilder();
        var p = new DynamicParameters();
        BuildPrivacyWhere(query, where, p);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        p.Add("limit", pageSize);
        p.Add("offset", (page - 1) * pageSize);

        await using var conn = await _factory.OpenAsync(ct);
        var total = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM requests{where}", p);
        var items = (await conn.QueryAsync<PrivacyEventRow>($"""
            SELECT id, started_at_utc, client_kind, provider_id, provider_name, requested_model, status, privacy_json
            FROM requests{where} ORDER BY started_at_utc DESC, id DESC LIMIT @limit OFFSET @offset
            """, p)).AsList();
        return new PrivacyEventPage { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    /// <summary>Aggregated guard outcome counts plus a per-category hit breakdown for the same filters.</summary>
    public async Task<PrivacyEventStats> PrivacyEventStatsAsync(PrivacyEventQuery query, CancellationToken ct = default)
    {
        var where = new StringBuilder();
        var p = new DynamicParameters();
        BuildPrivacyWhere(query, where, p);

        await using var conn = await _factory.OpenAsync(ct);
        var totals = await conn.QuerySingleAsync<PrivacyTotalsRow>($"""
            SELECT COUNT(*) AS Events,
                   COALESCE(SUM(json_extract(privacy_json, '$.blocked')), 0) AS Blocked,
                   COALESCE(SUM(json_extract(privacy_json, '$.dry_run')), 0) AS DryRun,
                   COALESCE(SUM(json_extract(privacy_json, '$.redactions')), 0) AS Redactions
            FROM requests{where}
            """, p);
        var categories = (await conn.QueryAsync<PrivacyCategoryRow>($"""
            SELECT json_extract(h.value, '$.category') AS Category,
                   COUNT(DISTINCT requests.id) AS Requests,
                   COALESCE(SUM(json_extract(h.value, '$.count')), 0) AS Hits,
                   COALESCE(SUM(CASE WHEN json_extract(h.value, '$.action') = 'warn' THEN json_extract(h.value, '$.count') ELSE 0 END), 0) AS WarnHits,
                   COALESCE(SUM(CASE WHEN json_extract(h.value, '$.action') = 'block' THEN json_extract(h.value, '$.count') ELSE 0 END), 0) AS BlockHits,
                   COALESCE(SUM(CASE WHEN json_extract(h.value, '$.action') = 'redact' THEN json_extract(h.value, '$.count') ELSE 0 END), 0) AS RedactHits
            FROM requests, json_each(json_extract(requests.privacy_json, '$.hits')) h{where}
            GROUP BY Category
            ORDER BY Hits DESC
            """, p)).AsList();
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

    /// <summary>WHERE clause over rows that have a guard outcome; every predicate runs in SQLite (JSON1).</summary>
    private static void BuildPrivacyWhere(PrivacyEventQuery query, StringBuilder where, DynamicParameters p)
    {
        where.Append(" WHERE privacy_json IS NOT NULL");
        if (query.From is { } from)
        {
            where.Append(" AND started_at_utc >= @from");
            p.Add("from", DateTimeOffsetHandler.ToStorage(from));
        }
        if (query.To is { } to)
        {
            where.Append(" AND started_at_utc <= @to");
            p.Add("to", DateTimeOffsetHandler.ToStorage(to));
        }
        if (!string.IsNullOrEmpty(query.ClientKind))
        {
            where.Append(" AND client_kind = @client_kind");
            p.Add("client_kind", query.ClientKind);
        }
        if (!string.IsNullOrEmpty(query.ProviderId))
        {
            where.Append(" AND provider_id = @provider_id");
            p.Add("provider_id", query.ProviderId);
        }
        if (query.Blocked is { } blocked)
        {
            where.Append(" AND json_extract(privacy_json, '$.blocked') = @blocked");
            p.Add("blocked", blocked ? 1 : 0);
        }
        if (!string.IsNullOrEmpty(query.Category))
        {
            where.Append(" AND EXISTS (SELECT 1 FROM json_each(json_extract(privacy_json, '$.hits')) je WHERE json_extract(je.value, '$.category') = @hit_category)");
            p.Add("hit_category", query.Category);
        }
        if (!string.IsNullOrEmpty(query.Action))
        {
            where.Append(" AND EXISTS (SELECT 1 FROM json_each(json_extract(privacy_json, '$.hits')) je WHERE json_extract(je.value, '$.action') = @hit_action)");
            p.Add("hit_action", query.Action);
        }
        if (!string.IsNullOrEmpty(query.Model))
        {
            where.Append(" AND (requested_model = @model OR upstream_model = @model OR system_model_id = @model)");
            p.Add("model", query.Model);
        }
    }

    /// <summary>Paged, newest-first query with total count.</summary>
    public async Task<RequestPage> QueryAsync(RequestQuery query, CancellationToken ct = default)
    {
        var where = new StringBuilder(" WHERE 1=1");
        var p = new DynamicParameters();
        if (query.From is { } from)
        {
            where.Append(" AND started_at_utc >= @from");
            p.Add("from", DateTimeOffsetHandler.ToStorage(from));
        }
        if (query.To is { } to)
        {
            where.Append(" AND started_at_utc <= @to");
            p.Add("to", DateTimeOffsetHandler.ToStorage(to));
        }
        if (!string.IsNullOrEmpty(query.ClientKind))
        {
            where.Append(" AND client_kind = @client_kind");
            p.Add("client_kind", query.ClientKind);
        }
        if (!string.IsNullOrEmpty(query.ProviderId))
        {
            where.Append(" AND provider_id = @provider_id");
            p.Add("provider_id", query.ProviderId);
        }
        if (!string.IsNullOrWhiteSpace(query.Model))
        {
            // Literal contains-match: escape LIKE wildcards so the term never broadens, then wrap it in %...%.
            var model = query.Model.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            where.Append("""
                 AND (requested_model LIKE @model ESCAPE '\' OR upstream_model LIKE @model ESCAPE '\'
                      OR system_model_id LIKE @model ESCAPE '\' OR response_model LIKE @model ESCAPE '\')
                """);
            p.Add("model", $"%{model}%");
        }
        if (!string.IsNullOrEmpty(query.Status))
        {
            where.Append(" AND status = @status");
            p.Add("status", query.Status);
        }

        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        p.Add("limit", pageSize);
        p.Add("offset", (page - 1) * pageSize);

        await using var conn = await _factory.OpenAsync(ct);
        var total = await conn.ExecuteScalarAsync<long>($"SELECT COUNT(*) FROM requests{where}", p);
        var items = (await conn.QueryAsync<RequestRecord>(
            $"{RequestSelect}{where} ORDER BY started_at_utc DESC, id DESC LIMIT @limit OFFSET @offset", p)).AsList();
        return new RequestPage { Items = items, Total = total, Page = page, PageSize = pageSize };
    }

    /// <summary>One request including its usage items; null when absent.</summary>
    public async Task<RequestRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var record = await conn.QuerySingleOrDefaultAsync<RequestRecord>($"{RequestSelect} WHERE id = @id", new { id });
        if (record is null) return null;
        record.UsageItems = (await conn.QueryAsync<RequestUsageItem>(
            $"{ItemSelect} WHERE request_id = @request_id ORDER BY id", new { request_id = id })).AsList();
        return record;
    }

    /// <summary>
    /// Cost, request count, success rate, token totals and average TTFT for a range;
    /// <paramref name="clientKind"/> restricts it to one client (null = every request).
    /// </summary>
    public async Task<RequestSummary> SummaryAsync(
        DateTimeOffset from, DateTimeOffset to, string? clientKind = null, CancellationToken ct = default)
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
            WHERE started_at_utc >= @from AND started_at_utc <= @to{ClientFilter(clientKind)}
            """, new
        {
            from = DateTimeOffsetHandler.ToStorage(from),
            to = DateTimeOffsetHandler.ToStorage(to),
            client_kind = clientKind,
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
    /// <paramref name="by"/> is "model", "provider" or "client"; <paramref name="clientKind"/> restricts the rows to one client.
    /// </summary>
    public async Task<IReadOnlyList<TimeseriesPoint>> TimeseriesAsync(
        DateTimeOffset from, DateTimeOffset to, string groupBy = "day", string by = "model",
        string? clientKind = null, int utcOffsetMinutes = 0, CancellationToken ct = default)
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
            _ => throw new ArgumentException("by must be \"model\", \"provider\" or \"client\".", nameof(by)),
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
            WHERE started_at_utc >= @from AND started_at_utc <= @to{ClientFilter(clientKind)}
            GROUP BY Bucket, GroupKey
            ORDER BY Bucket
            """, new
        {
            from = DateTimeOffsetHandler.ToStorage(from),
            to = DateTimeOffsetHandler.ToStorage(to),
            client_kind = clientKind,
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

    /// <summary>Most expensive models in the range, ordered by cost descending; <paramref name="clientKind"/> restricts it to one client.</summary>
    public async Task<IReadOnlyList<TopModelStat>> TopModelsAsync(
        DateTimeOffset from, DateTimeOffset to, int limit = 10, string? clientKind = null, CancellationToken ct = default)
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
            WHERE started_at_utc >= @from AND started_at_utc <= @to{ClientFilter(clientKind)}
            GROUP BY ModelId
            ORDER BY TotalCostNanoUsd DESC
            LIMIT @limit
            """, new
        {
            from = DateTimeOffsetHandler.ToStorage(from),
            to = DateTimeOffsetHandler.ToStorage(to),
            client_kind = clientKind,
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

    /// <summary>Retention cleanup: deletes requests (and, via cascade, their usage items) started before the cutoff. Returns the number of requests removed.</summary>
    public async Task<long> DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM requests WHERE started_at_utc < @cutoff",
            new { cutoff = DateTimeOffsetHandler.ToStorage(cutoff) });
    }

    /// <summary>Extra stats predicate for an optional client filter; the value is bound as <c>@client_kind</c>.</summary>
    private static string ClientFilter(string? clientKind) =>
        string.IsNullOrEmpty(clientKind) ? "" : " AND client_kind = @client_kind";

    private static object RequestParams(RequestRecord r) => new
    {
        id = r.Id,
        started_at_utc = DateTimeOffsetHandler.ToStorage(r.StartedAtUtc),
        client_kind = r.ClientKind,
        provider_id = r.ProviderId,
        provider_name = r.ProviderName,
        inbound_protocol = r.InboundProtocol,
        upstream_protocol = r.UpstreamProtocol,
        passthrough = r.Passthrough,
        requested_model = r.RequestedModel,
        upstream_model = r.UpstreamModel,
        system_model_id = r.SystemModelId,
        response_model = r.ResponseModel,
        stream = r.Stream,
        service_tier = r.ServiceTier,
        status = r.Status,
        http_status = r.HttpStatus,
        error_type = r.ErrorType,
        error_message = r.ErrorMessage,
        upstream_request_id = r.UpstreamRequestId,
        ttfb_ms = r.TtfbMs,
        ttft_ms = r.TtftMs,
        total_ms = r.TotalMs,
        generation_ms = r.GenerationMs,
        output_tps = r.OutputTps,
        total_input_tokens = r.TotalInputTokens,
        total_output_tokens = r.TotalOutputTokens,
        cache_read_tokens = r.CacheReadTokens,
        cache_write_tokens = r.CacheWriteTokens,
        reasoning_tokens = r.ReasoningTokens,
        cost_nanousd = r.CostNanoUsd,
        usage_source = r.UsageSource,
        usage_raw_json = r.UsageRawJson,
        pricing_snapshot_json = r.PricingSnapshotJson,
        pricing_source = r.PricingSource,
        price_key = r.PriceKey,
        billing_trace_json = r.BillingTraceJson,
        billing_description = r.BillingDescription,
        privacy_json = r.PrivacyJson,
        body_ref = r.BodyRef,
        user_agent = r.UserAgent,
        reasoning_effort = r.ReasoningEffort,
        reasoning_mode = r.ReasoningMode,
        reasoning_budget_tokens = r.ReasoningBudgetTokens,
    };

    private static object ItemParams(RequestUsageItem i) => new
    {
        request_id = i.RequestId,
        token_type = i.TokenType,
        tokens = i.Tokens,
        is_per_call = i.IsPerCall,
        unit_price = i.UnitPrice,
        base_unit_price = i.BaseUnitPrice,
        tier_applied = i.TierApplied,
        priced_as = i.PricedAs,
        multipliers_json = i.MultipliersJson,
        cost_nanousd = i.CostNanoUsd,
        note = i.Note,
    };

    private sealed class PrivacyTotalsRow
    {
        public long Events { get; set; }
        public long Blocked { get; set; }
        public long DryRun { get; set; }
        public long Redactions { get; set; }
    }

    private sealed class PrivacyCategoryRow
    {
        public string? Category { get; set; }
        public long Requests { get; set; }
        public long Hits { get; set; }
        public long WarnHits { get; set; }
        public long BlockHits { get; set; }
        public long RedactHits { get; set; }
    }

    private sealed class SummaryRow
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

    private sealed class TimeseriesRow
    {
        public string Bucket { get; set; } = "";
        public string? GroupKey { get; set; }
        public string? Label { get; set; }
        public long Requests { get; set; }
        public long TotalCostNanoUsd { get; set; }
        public long TotalInputTokens { get; set; }
        public long TotalOutputTokens { get; set; }
    }

    private sealed class TopModelRow
    {
        public string? ModelId { get; set; }
        public long Requests { get; set; }
        public long TotalCostNanoUsd { get; set; }
        public long TotalInputTokens { get; set; }
        public long TotalOutputTokens { get; set; }
        public long TotalCacheReadTokens { get; set; }
    }
}
