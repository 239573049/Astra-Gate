using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Data;
using Astra.Data.Repositories;
using Astra.Gateway.Pipeline;

namespace Astra.Server.Api;

/// <summary>
/// /api/traces (.plan/observability.md §4): read-only views over the spans and agent links the gateway records —
/// one request's span tree, sessions and their turns, and the topology aggregates. Live particles reuse /api/requests/live.
/// </summary>
public static class TraceEndpoints
{
    /// <summary>A session whose last call is this recent counts as active.</summary>
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(2);

    public sealed record SpanDto(
        string RequestId, string SpanId, string? ParentSpanId, string Name, string Kind, long StartUs, long DurationUs,
        string Status, string? StatusMessage, JsonNode? Attributes, JsonNode? Events);

    /// <summary>One gateway call as the trace views show it: the request-log row plus its agent links.</summary>
    public sealed record TraceCallDto(
        string Id, DateTimeOffset StartedAtUtc, string? ClientKind, string? ProviderId, string? ProviderName, string? AccountId,
        string? AccountName, string InboundProtocol, string? UpstreamProtocol, bool Passthrough, bool Stream, string? RequestedModel,
        string? UpstreamModel, string? ResponseModel, string Status, int? HttpStatus, string? ErrorType, string? ErrorMessage,
        long? TtfbMs, long? TtftMs, long? TotalMs, long? GenerationMs, double? OutputTps, long TotalInputTokens, long TotalOutputTokens,
        long CacheReadTokens, long ReasoningTokens, long CostNanoUsd, string? UpstreamRequestId,
        string? TraceId, string? ParentSpanId, string? SessionKey, string? ChainKey, string? TurnId, string? ParentRequestId,
        int? Step, string? CallKind, string? LinkSource, string? FinishReason, int ToolCalls, string? ReasoningEffort, string? ReasoningMode);

    public sealed record TraceRequestDto(TraceCallDto Request, bool InFlight, List<SpanDto> Spans, TraceCallDto? Parent, List<TraceCallDto> Children);

    public sealed record TraceSessionDto(
        string SessionKey, string? ClientKind, DateTimeOffset FirstAtUtc, DateTimeOffset LastAtUtc, long Requests, long Turns,
        long Errors, long CostNanoUsd, long InputTokens, long OutputTokens, List<string> ProviderIds, bool Active);

    public sealed record TraceTurnDto(string TurnId, DateTimeOffset StartedAtUtc, long Requests, long Errors, long CostNanoUsd, double DurationMs);

    /// <summary>A session with its turns, and the calls and spans of one turn (the latest unless one is asked for).</summary>
    public sealed record TraceSessionDetailDto(TraceSessionDto Session, List<TraceTurnDto> Turns, string? TurnId, List<TraceCallDto> Calls, List<SpanDto> Spans);

    public sealed record TraceStageDto(string Name, long? P50Us, long? P95Us, int Samples);

    public sealed record TraceGatewayDto(long Requests, long Errors, int InFlight, long? OverheadP50Us, long? OverheadP95Us, long Failovers,
        List<TraceStageDto> Stages);

    public sealed record TraceClientNodeDto(string? ClientKind, long Requests, long Errors, double PerMinute, int ActiveSessions);

    public sealed record TraceAccountDto(
        string Id, string Name, string Status, bool Enabled, DateTimeOffset? CooldownUntilUtc, DateTimeOffset? SeenAtUtc,
        string? RemainingRequests, string? RemainingTokens, string? LimitRequests);

    public sealed record TraceProviderNodeDto(
        string ProviderId, string Name, string? Icon, string? TemplateId, long Requests, long Errors, long Attempts, long RateLimited,
        long Failovers, double PerMinute, long? TtfbP95Ms, List<TraceAccountDto> Accounts);

    public sealed record TraceTopologyDto(string Range, double WindowMinutes, TraceGatewayDto Gateway, List<TraceClientNodeDto> Clients,
        List<TraceProviderNodeDto> Providers);

    public static void MapTraceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/traces/requests/{id}", async (string id, AstraDatabase db, LiveRequestFeed live, CancellationToken ct) =>
        {
            if (await db.Requests.GetAsync(id, ct) is { } record)
            {
                var spans = (await db.Traces.ListSpansAsync(id, ct)).Select(ToSpan).ToList();
                var parent = record.ParentRequestId is { } parentId && await db.Requests.GetAsync(parentId, ct) is { } p ? ToCall(p) : null;
                var children = (await db.Requests.ListChildrenAsync(id, ct)).Select(ToCall).ToList();
                return Results.Ok(new TraceRequestDto(ToCall(record), false, spans, parent, children));
            }
            // Still being served: its spans are written with the row, once it is over.
            if (live.Find(id) is { InFlight: true } e)
                return Results.Ok(new TraceRequestDto(ToCall(e.Record) with { Status = RequestEndpoints.PendingStatus }, true, [], null, []));
            return ModelEndpoints.NotFound(id);
        });

        app.MapGet("/api/traces/sessions", async (string? range, string? client, int? limit, AstraDatabase db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var sessions = await db.Traces.ListSessionsAsync(now - TraceStats.Window(range ?? "24h"), client is { Length: > 0 } ? client : null,
                limit ?? 100, ct);
            return Results.Ok(sessions.Select(s => ToSession(s, now)).ToList());
        });

        app.MapGet("/api/traces/sessions/{key}", async (string key, string? turn, AstraDatabase db, CancellationToken ct) =>
        {
            if (await db.Traces.GetSessionAsync(key, ct) is not { } session) return ModelEndpoints.NotFound(key);
            var turns = (await db.Traces.ListTurnsAsync(key, ct))
                .Select(t => new TraceTurnDto(t.TurnId, t.StartedAtUtc, t.Requests, t.Errors, t.CostNanoUsd, Math.Max(0, t.DurationMs))).ToList();
            var selected = turn is { Length: > 0 } && turns.Any(t => t.TurnId == turn) ? turn : turns.LastOrDefault()?.TurnId;
            List<TraceCallDto> calls = [];
            List<SpanDto> spans = [];
            if (selected is not null)
            {
                calls = (await db.Requests.ListByTurnAsync(selected, ct)).Select(ToCall).ToList();
                spans = (await db.Traces.ListTurnSpansAsync(selected, ct)).Select(ToSpan).ToList();
            }
            return Results.Ok(new TraceSessionDetailDto(ToSession(session, DateTimeOffset.UtcNow), turns, selected, calls, spans));
        });

        app.MapGet("/api/traces/topology", async (string? range, AstraDatabase db, LiveRequestFeed live, CancellationToken ct) =>
        {
            var id = range is "live" or "1h" or "24h" ? range : "live";
            var window = TraceStats.Window(id);
            var now = DateTimeOffset.UtcNow;
            var data = await db.Traces.TopologyAsync(now - window, ct);
            var providers = (await db.Providers.ListAsync(ct)).ToDictionary(p => p.Id);
            var active = await db.Traces.ListSessionsAsync(now - ActiveWindow, null, 500, ct);
            return Results.Ok(await TopologyAsync(db, data, providers, active, live.InFlightSnapshot().Count, id, window, ct));
        });
    }

    private static async Task<TraceTopologyDto> TopologyAsync(AstraDatabase db, TraceTopologyData data, Dictionary<string, Provider> providers,
        List<TraceSessionSummary> active, int inFlight, string range, TimeSpan window, CancellationToken ct)
    {
        var minutes = window.TotalMinutes;
        var clients = data.Clients
            .Select(c => new TraceClientNodeDto(c.ClientKind, c.Requests, c.Errors, Math.Round(c.Requests / minutes, 2),
                active.Count(s => s.ClientKind == c.ClientKind)))
            .OrderByDescending(c => c.Requests).ToList();

        var nodes = new List<TraceProviderNodeDto>();
        foreach (var stat in data.Providers.OrderByDescending(p => p.Requests))
        {
            if (stat.ProviderId is not { } providerId) continue;
            providers.TryGetValue(providerId, out var provider);
            var ttfb = data.Timings.Where(t => t.ProviderId == providerId && t.TtfbMs is not null).Select(t => t.TtfbMs!.Value).ToList();
            var accounts = new List<TraceAccountDto>();
            foreach (var account in await db.Accounts.ListAsync(providerId, ct))
            {
                var seen = data.AccountAttempts.FirstOrDefault(a => a.ProviderId == providerId && a.AccountId == account.Id);
                var attributes = seen?.AttributesJson is { } json ? JsonNode.Parse(json) as JsonObject : null;
                accounts.Add(new TraceAccountDto(account.Id, account.DisplayName, AccountState(account), account.Enabled,
                    account.CooldownUntilUtc > DateTimeOffset.UtcNow ? account.CooldownUntilUtc : null, seen?.AtUtc,
                    Header(attributes, "x-ratelimit-remaining-requests", "anthropic-ratelimit-requests-remaining"),
                    Header(attributes, "x-ratelimit-remaining-tokens", "anthropic-ratelimit-tokens-remaining"),
                    Header(attributes, "x-ratelimit-limit-requests")));
            }
            nodes.Add(new TraceProviderNodeDto(providerId, provider?.Name ?? stat.ProviderName ?? providerId, provider?.Icon, provider?.TemplateId,
                stat.Requests, stat.Errors, stat.Attempts, stat.RateLimited, stat.Failovers, Math.Round(stat.Requests / minutes, 2),
                TraceStats.Percentile(ttfb, 95), accounts));
        }

        var overhead = data.Timings.Where(t => t.FirstAttemptUs is not null).Select(t => t.FirstAttemptUs!.Value).ToList();
        string[] stageOrder = [TraceSpanNames.Route, TraceSpanNames.Middleware, TraceSpanNames.Privacy, TraceSpanNames.Resolve, TraceSpanNames.Encode];
        var stages = stageOrder.Select(name =>
        {
            var samples = data.Stages.Where(s => s.Name == name).Select(s => s.DurationUs).ToList();
            return new TraceStageDto(name, TraceStats.Percentile(samples, 50), TraceStats.Percentile(samples, 95), samples.Count);
        }).ToList();
        var gateway = new TraceGatewayDto(data.Clients.Sum(c => c.Requests), data.Clients.Sum(c => c.Errors), inFlight,
            TraceStats.Percentile(overhead, 50), TraceStats.Percentile(overhead, 95), data.Providers.Sum(p => p.Failovers), stages);
        return new TraceTopologyDto(range, minutes, gateway, clients, nodes);
    }

    private static string AccountState(ProviderAccount account) =>
        !account.Enabled ? "disabled"
        : account.Status != AccountStatus.Active ? account.Status
        : account.CooldownUntilUtc > DateTimeOffset.UtcNow ? "cooldown"
        : "ok";

    private static string? Header(JsonObject? attributes, params string[] names)
    {
        if (attributes is null) return null;
        foreach (var name in names)
            if (attributes["astra.ratelimit." + name] is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        return null;
    }

    private static TraceSessionDto ToSession(TraceSessionSummary s, DateTimeOffset now) => new(
        s.SessionKey, s.ClientKind, s.FirstAtUtc, s.LastAtUtc, s.Requests, s.Turns, s.Errors, s.CostNanoUsd, s.InputTokens, s.OutputTokens,
        s.ProviderIds is { Length: > 0 } ids ? [.. ids.Split(',', StringSplitOptions.RemoveEmptyEntries)] : [],
        now - s.LastAtUtc < ActiveWindow);

    internal static TraceCallDto ToCall(RequestRecord r) => new(
        r.Id, r.StartedAtUtc, r.ClientKind, r.ProviderId, r.ProviderName, r.AccountId, r.AccountName, r.InboundProtocol, r.UpstreamProtocol,
        r.Passthrough, r.Stream, r.RequestedModel, r.UpstreamModel, r.ResponseModel, r.Status, r.HttpStatus, r.ErrorType, r.ErrorMessage,
        r.TtfbMs, r.TtftMs, r.TotalMs, r.GenerationMs, r.OutputTps, r.TotalInputTokens, r.TotalOutputTokens, r.CacheReadTokens,
        r.ReasoningTokens, r.CostNanoUsd, r.UpstreamRequestId, r.TraceId, r.ParentSpanId, r.SessionKey, r.ChainKey, r.TurnId,
        r.ParentRequestId, r.Step, r.CallKind, r.LinkSource, r.FinishReason, r.ToolCalls, r.ReasoningEffort, r.ReasoningMode);

    private static SpanDto ToSpan(TraceSpan s) => new(
        s.RequestId, s.SpanId, s.ParentSpanId, s.Name, s.Kind, s.StartUs, s.DurationUs, s.Status, s.StatusMessage,
        Parse(s.AttributesJson), Parse(s.EventsJson));

    private static JsonNode? Parse(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
