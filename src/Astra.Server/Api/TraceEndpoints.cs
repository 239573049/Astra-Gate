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

        // The activity feed beside the topology: the latest requests — why each was sent, where it went, what came back.
        // Requests in flight come from the live feed (their attempts and cause ride /api/requests/live).
        app.MapGet("/api/traces/activity", async (int? limit, string? client, AstraDatabase db, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 30, 1, 100);
            var clientKind = client is { Length: > 0 } ? client : null;
            var records = await db.Requests.ListRecentTracedAsync(take, clientKind, ct);
            var spans = (await db.Traces.ListRecentActivitySpansAsync(take, clientKind, ct)).ToLookup(x => x.RequestId);
            return Results.Ok(records.Select(r => ToActivity(r, spans[r.Id].ToList())).ToList());
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
                    Header(attributes, "x-ratelimit-limit-requests"), seen?.Attempts ?? 0, seen?.Errors ?? 0));
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

    /// <summary>
    /// One request of the activity feed, from its row and its root / attempt / response spans: the cause (root attributes and
    /// <c>tool_result</c> events), the attempts, and what came back (response events and attributes).
    /// </summary>
    internal static TraceActivityDto ToActivity(RequestRecord r, List<TraceSpan> spans)
    {
        var root = spans.Find(x => x.Name == TraceSpanNames.Request);
        var response = spans.Find(x => x.Name == TraceSpanNames.Response);
        var rootAttributes = Parse(root?.AttributesJson) as JsonObject;
        var responseAttributes = Parse(response?.AttributesJson) as JsonObject;
        var rootEvents = Parse(root?.EventsJson) as JsonArray;
        var responseEvents = (Parse(response?.EventsJson) as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

        RequestCause? cause = null;
        if (Str(rootAttributes, "astra.request.cause") is { } kind)
        {
            var results = (rootEvents?.OfType<JsonObject>() ?? []).Where(e => Str(e, "name") == "tool_result")
                .Select(e => e["attributes"] as JsonObject)
                .Select(a => new ToolResultInfo(Str(a, "name"), a?["error"] is JsonValue v && v.TryGetValue<bool>(out var error) ? error : null, Num(a, "chars") ?? 0))
                .ToList();
            cause = new RequestCause(kind, results, Str(rootAttributes, "astra.request.prompt"));
        }

        var attempts = spans.Where(x => x.Name == TraceSpanNames.Attempt).OrderBy(x => x.StartUs).Select(a =>
        {
            var attributes = Parse(a.AttributesJson) as JsonObject;
            var status = Num(attributes, "http.response.status_code") is { } code ? (int)code : (int?)null;
            return new RequestAttempt(Str(attributes, "astra.provider.id") ?? r.ProviderId, Str(attributes, "astra.provider.name") ?? r.ProviderName,
                Str(attributes, "astra.account.id"), status, Str(attributes, "astra.attempt.retry"), a.StartUs / 1000, (a.StartUs + a.DurationUs) / 1000,
                a.Status == TraceSpanStatus.Error && status is null ? a.StatusMessage : null);
        }).ToList();

        long? At(string name) => responseEvents.FirstOrDefault(e => Str(e, "name") == name) is { } e ? Num(e, "offset_us") : null;
        var reasoningMs = At("reasoning_start") is { } rs && At("reasoning_end") is { } re ? Math.Max(0, re - rs) / 1000 : (long?)null;
        var tools = responseEvents.Where(e => Str(e, "name") == "tool_call").Select(e => e["attributes"] as JsonObject)
            .Select(a => new TraceToolCallDto(Str(a, "name") ?? "", Str(a, "id"), Str(a, "arguments"))).ToList();
        // Without the events (a whole body, before content was kept) the names are still on the span.
        if (tools.Count == 0 && Str(responseAttributes, "astra.response.tool_names") is { } names)
            tools = names.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(n => new TraceToolCallDto(n, null, null)).ToList();

        return new TraceActivityDto(RequestEndpoints.ToSummary(r), r.Step, r.FinishReason, r.ErrorMessage, r.ToolCalls, cause, attempts, reasoningMs,
            Num(responseAttributes, "astra.response.reasoning_chars"), Num(responseAttributes, "astra.response.text_chars"),
            Str(responseAttributes, "astra.response.text"), tools);
    }

    private static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    private static long? Num(JsonObject? o, string key) =>
        o?[key] is JsonValue v && (v.TryGetValue<long>(out var n) || (v.TryGetValue<double>(out var d) && (n = (long)d) == d)) ? n : null;

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
