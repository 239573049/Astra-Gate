using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Privacy;
using Astra.Core.Requests;
using Astra.Data;
using Astra.Data.Repositories;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

/// <summary>/api/requests, /api/stats and /api/settings.</summary>
public static class RequestEndpoints
{
    public sealed record RequestSummaryDto(
        string Id, DateTimeOffset StartedAtUtc, string? ClientKind, string? ProviderId, string? ProviderName,
        string InboundProtocol, string? UpstreamProtocol, bool Passthrough, string? RequestedModel, string? UpstreamModel,
        string? ResponseModel, string? SystemModelId, bool Stream, string Status, int? HttpStatus, long? TtftMs, long? TotalMs, double? OutputTps,
        long TotalInputTokens, long TotalOutputTokens, long CacheReadTokens, long CacheWriteTokens, long ReasoningTokens,
        long CostNanoUsd, string UsageSource,
        string? ReasoningEffort, string? ReasoningMode, long? ReasoningBudgetTokens,
        string? TokenId, string? TokenName);

    public sealed record UsageItemDto(
        string TokenType, long Tokens, bool IsPerCall, string UnitPrice, string? BaseUnitPrice, string TierApplied,
        string? PricedAs, List<string>? Multipliers, long CostNanoUsd, string? Note);

    public sealed record BodiesDto(string? ClientRequest, string? UpstreamRequest, string? UpstreamResponse, string? ClientResponse);

    public sealed record RequestDetailDto(
        string Id, DateTimeOffset StartedAtUtc, string? ClientKind, string? ProviderId, string? ProviderName,
        string InboundProtocol, string? UpstreamProtocol, bool Passthrough, string? RequestedModel, string? UpstreamModel,
        string? ResponseModel, string? SystemModelId, bool Stream, string Status, int? HttpStatus, long? TtftMs, long? TotalMs,
        long TotalInputTokens, long TotalOutputTokens, long CacheReadTokens, long CacheWriteTokens, long ReasoningTokens,
        long CostNanoUsd, string UsageSource,
        string? ServiceTier, string? ErrorType, string? ErrorMessage, string? UpstreamRequestId, long? TtfbMs,
        long? GenerationMs, double? OutputTps, JsonNode? UsageRaw, JsonNode? PricingSnapshot, string? PricingSource,
        string? PriceKey, List<BillingTraceStep> BillingTrace, string? BillingDescription, List<UsageItemDto> UsageItems,
        string? UserAgent, BodiesDto? Bodies, PrivacyReport? Privacy,
        string? ReasoningEffort, string? ReasoningMode, long? ReasoningBudgetTokens,
        string? TokenId, string? TokenName);

    public sealed record PageDto<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize);

    public sealed record StatsSummaryDto(string Range, decimal CostUsd, long Requests, double SuccessRate,
        long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, long ReasoningTokens, double? AvgTtftMs);

    public sealed record TimeseriesPointDto(string Bucket, string Key, decimal CostUsd, long Requests, long Tokens);

    /// <summary>InputTokens includes CacheReadTokens, so the hit rate is CacheReadTokens / InputTokens (as in the summary).</summary>
    public sealed record TopModelDto(string Model, decimal CostUsd, long Requests, long Tokens, long InputTokens, long CacheReadTokens);

    /// <summary>One local day of the activity heatmap; days without requests are absent.</summary>
    public sealed record DailyActivityDto(string Day, long Requests);

    public sealed record SettingsDto(
        string Locale, bool DebugBodies, int BodyRetentionDays, int? RequestRetentionDays, EffortBudgets EffortBudgets,
        int StreamIdleTimeoutSec, string UpdateChannel, bool UpdateAutoCheck,
        int Port, string Host, string DataDir, string GatewayBaseUrl, int QuotaAutoIntervalMinutes);

    public static void MapRequestEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/requests", async (HttpContext http, AstraDatabase db, CancellationToken ct) =>
        {
            var q = http.Request.Query;
            var query = new RequestQuery
            {
                From = ParseTime(q["from"]),
                To = ParseTime(q["to"]),
                ClientKind = Str(q["client"]),
                TokenId = Str(q["token"]),
                ProviderId = Str(q["provider"]),
                Model = Str(q["model"]),
                Status = Str(q["status"]),
                Page = int.TryParse(q["page"], out var page) ? Math.Max(1, page) : 1,
                PageSize = int.TryParse(q["pageSize"], out var size) ? Math.Clamp(size, 1, 500) : 50,
            };
            var result = await db.Requests.QueryAsync(query, ct);
            return Results.Ok(new PageDto<RequestSummaryDto>(result.Items.Select(ToSummary).ToList(), result.Total, result.Page, result.PageSize));
        });

        app.MapGet("/api/requests/{id}", async (string id, AstraDatabase db, BodyStore bodies, CancellationToken ct) =>
            await db.Requests.GetAsync(id, ct) is { } r ? Results.Ok(ToDetail(r, bodies)) : ModelEndpoints.NotFound(id));

        // Stats accept optional `client` (client kind) and `token` (token id) filters; omitted means every request.
        app.MapGet("/api/stats/summary", async (string? range, string? client, string? token, AstraDatabase db, CancellationToken ct) =>
        {
            var (name, from, to) = Range(range);
            var s = await db.Requests.SummaryAsync(from, to, Str(client), Str(token), ct);
            return Results.Ok(new StatsSummaryDto(name, Money.FromNanos(s.TotalCostNanoUsd), s.TotalRequests, s.SuccessRate,
                s.TotalInputTokens, s.TotalOutputTokens, s.TotalCacheReadTokens, s.TotalCacheWriteTokens, s.TotalReasoningTokens, s.AvgTtftMs));
        });

        // tzOffset: the viewer's UTC offset in minutes (e.g. 480 for UTC+8), so buckets follow local days/hours.
        app.MapGet("/api/stats/timeseries", async (string? range, string? groupBy, string? by, string? client, string? token, int? tzOffset,
            AstraDatabase db, CancellationToken ct) =>
        {
            var (_, from, to) = Range(range);
            var bucket = groupBy is "hour" ? "hour" : "day";
            var dim = by is "provider" or "client" or "token" ? by : "model";
            var offset = Math.Clamp(tzOffset ?? 0, -14 * 60, 14 * 60);
            var points = await db.Requests.TimeseriesAsync(from, to, bucket, dim, Str(client), offset, Str(token), ct);
            return Results.Ok(points.Select(p => new TimeseriesPointDto(p.Bucket, p.Label ?? p.GroupKey ?? "unknown",
                Money.FromNanos(p.TotalCostNanoUsd), p.Requests, p.TotalInputTokens + p.TotalOutputTokens)).ToList());
        });

        app.MapGet("/api/stats/top-models", async (string? range, int? limit, string? client, string? token, AstraDatabase db, CancellationToken ct) =>
        {
            var (_, from, to) = Range(range);
            var top = await db.Requests.TopModelsAsync(from, to, Math.Clamp(limit ?? 10, 1, 100), Str(client), Str(token), ct);
            return Results.Ok(top.Select(t => new TopModelDto(t.ModelId, Money.FromNanos(t.TotalCostNanoUsd), t.Requests,
                t.TotalInputTokens + t.TotalOutputTokens, t.TotalInputTokens, t.TotalCacheReadTokens)).ToList());
        });

        // Activity heatmap: request counts per local day over a fixed window ("days", 30-730), newest day inclusive.
        app.MapGet("/api/stats/activity-heatmap", async (int? days, string? client, string? token, int? tzOffset,
            AstraDatabase db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.Now;
            var startOfToday = new DateTimeOffset(now.Date, now.Offset);
            var window = Math.Clamp(days ?? 365, 30, 730);
            var offset = Math.Clamp(tzOffset ?? 0, -14 * 60, 14 * 60);
            var activity = await db.Requests.DailyActivityAsync(
                startOfToday.AddDays(-(window - 1)).ToUniversalTime(), Str(client), offset, Str(token), ct);
            return Results.Ok(activity.Select(a => new DailyActivityDto(a.Day, a.Requests)).ToList());
        });

        app.MapGet("/api/settings", (SettingsService settings, ServerOptions server, AstraPaths paths) =>
            Results.Ok(ToSettings(settings.Current, server, paths)));

        app.MapPatch("/api/settings", async (JsonObject patch, SettingsService settings, ServerOptions server, AstraPaths paths, CancellationToken ct) =>
        {
            string? error = null;
            var next = await settings.UpdateAsync(s =>
            {
                foreach (var (key, value) in patch)
                {
                    switch (key)
                    {
                        case "locale":
                            var locale = value?.GetValue<string>();
                            if (locale is "zh" or "en") s.Locale = locale;
                            else error = "locale must be zh or en";
                            break;
                        case "debugBodies": s.DebugBodies = value?.GetValue<bool>() ?? false; break;
                        case "bodyRetentionDays": s.BodyRetentionDays = Math.Clamp(value?.GetValue<int>() ?? 7, 1, 3650); break;
                        case "requestRetentionDays":
                            s.RequestRetentionDays = value is null ? null : Math.Clamp(value.GetValue<int>(), 1, 36500);
                            break;
                        case "effortBudgets":
                            if (value is not null) s.EffortBudgets = Json.DeserializeApi<EffortBudgets>(value) ?? s.EffortBudgets;
                            break;
                        case "streamIdleTimeoutSec": s.StreamIdleTimeoutSec = Math.Clamp(value?.GetValue<int>() ?? 300, 10, 3600); break;
                        case "updateChannel":
                            var channel = value?.GetValue<string>();
                            if (channel is "stable" or "beta") s.UpdateChannel = channel;
                            else error = "updateChannel must be stable or beta";
                            break;
                        case "updateAutoCheck": s.UpdateAutoCheck = value?.GetValue<bool>() ?? true; break;
                        case "updateFeedUrl":
                            // JSON null resets to the built-in feed; "" disables checks entirely.
                            var feedUrl = value?.GetValue<string>();
                            s.UpdateFeedUrl = feedUrl is null ? null : feedUrl.Trim() is { Length: 0 } ? "" : feedUrl.Trim();
                            break;
                        case "quotaAutoIntervalMinutes":
                            // 0 turns the background balance / quota refresh off; providers may override it.
                            s.QuotaAutoIntervalMinutes = Math.Clamp(value?.GetValue<int>() ?? 30, 0, 1440);
                            break;
                        // read-only fields are ignored (changed via CLI / config.json)
                    }
                }
            }, ct);
            return error is null ? Results.Ok(ToSettings(next, server, paths)) : ModelEndpoints.Bad(error);
        });
    }

    private static SettingsDto ToSettings(AppSettings s, ServerOptions server, AstraPaths paths) => new(
        s.Locale, s.DebugBodies, s.BodyRetentionDays, s.RequestRetentionDays, s.EffortBudgets, s.StreamIdleTimeoutSec,
        s.UpdateChannel, s.UpdateAutoCheck,
        server.Port, server.Host, paths.Root, server.GatewayBaseUrl, s.QuotaAutoIntervalMinutes);

    internal static RequestSummaryDto ToSummary(RequestRecord r) => new(
        r.Id, r.StartedAtUtc, r.ClientKind, r.ProviderId, r.ProviderName, r.InboundProtocol, r.UpstreamProtocol, r.Passthrough,
        r.RequestedModel, r.UpstreamModel, r.ResponseModel, r.SystemModelId, r.Stream, r.Status, r.HttpStatus, r.TtftMs, r.TotalMs, r.OutputTps,
        r.TotalInputTokens, r.TotalOutputTokens, r.CacheReadTokens, r.CacheWriteTokens, r.ReasoningTokens,
        r.CostNanoUsd, r.UsageSource,
        r.ReasoningEffort, r.ReasoningMode, r.ReasoningBudgetTokens,
        r.TokenId, r.TokenName);

    private static RequestDetailDto ToDetail(RequestRecord r, BodyStore bodies)
    {
        BodiesDto? captured = null;
        if (!string.IsNullOrEmpty(r.BodyRef))
        {
            captured = new BodiesDto(bodies.Read(r.BodyRef, BodyStore.ClientRequest), bodies.Read(r.BodyRef, BodyStore.UpstreamRequest),
                bodies.Read(r.BodyRef, BodyStore.UpstreamResponse), bodies.Read(r.BodyRef, BodyStore.ClientResponse));
        }
        return new RequestDetailDto(
            r.Id, r.StartedAtUtc, r.ClientKind, r.ProviderId, r.ProviderName, r.InboundProtocol, r.UpstreamProtocol, r.Passthrough,
            r.RequestedModel, r.UpstreamModel, r.ResponseModel, r.SystemModelId, r.Stream, r.Status, r.HttpStatus, r.TtftMs, r.TotalMs,
            r.TotalInputTokens, r.TotalOutputTokens, r.CacheReadTokens, r.CacheWriteTokens, r.ReasoningTokens,
            r.CostNanoUsd, r.UsageSource,
            r.ServiceTier, r.ErrorType, r.ErrorMessage, r.UpstreamRequestId, r.TtfbMs, r.GenerationMs, r.OutputTps,
            ParseNode(r.UsageRawJson), ParseNode(r.PricingSnapshotJson), r.PricingSource, r.PriceKey,
            r.BillingTraceJson is null ? [] : JsonSerializer.Deserialize(r.BillingTraceJson, JsonContexts.Info<List<BillingTraceStep>>(Json.Storage)) ?? [],
            r.BillingDescription,
            r.UsageItems.Select(i => new UsageItemDto(i.TokenType, i.Tokens, i.IsPerCall, i.UnitPrice, i.BaseUnitPrice, i.TierApplied,
                i.PricedAs, i.MultipliersJson is null ? null : JsonSerializer.Deserialize(i.MultipliersJson, JsonContexts.Info<List<string>>(Json.Storage)),
                i.CostNanoUsd, i.Note)).ToList(),
            r.UserAgent, captured, r.PrivacyJson is null ? null : Json.Deserialize<PrivacyReport>(r.PrivacyJson),
            r.ReasoningEffort, r.ReasoningMode, r.ReasoningBudgetTokens,
            r.TokenId, r.TokenName);
    }

    private static JsonNode? ParseNode(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json);
        }
    }

    internal static (string Name, DateTimeOffset From, DateTimeOffset To) Range(string? range)
    {
        var now = DateTimeOffset.Now;
        var startOfToday = new DateTimeOffset(now.Date, now.Offset);
        return range switch
        {
            "7d" => ("7d", startOfToday.AddDays(-6).ToUniversalTime(), now.ToUniversalTime()),
            "30d" => ("30d", startOfToday.AddDays(-29).ToUniversalTime(), now.ToUniversalTime()),
            "90d" => ("90d", startOfToday.AddDays(-89).ToUniversalTime(), now.ToUniversalTime()),
            "all" => ("all", new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), now.ToUniversalTime()),
            _ => ("today", startOfToday.ToUniversalTime(), now.ToUniversalTime()),
        };
    }

    internal static DateTimeOffset? ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUniversalTime()
            : null;

    internal static string? Str(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
