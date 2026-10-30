using System.Text.Json.Nodes;
using Astra.Core.Requests;

namespace Astra.Api.Contracts;



/// <summary>Admin API payload: span.</summary>
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

/// <summary>Admin API payload: trace request.</summary>
public sealed record TraceRequestDto(TraceCallDto Request, bool InFlight, List<SpanDto> Spans, TraceCallDto? Parent, List<TraceCallDto> Children);

/// <summary>Admin API payload: trace session.</summary>
public sealed record TraceSessionDto(
    string SessionKey, string? ClientKind, DateTimeOffset FirstAtUtc, DateTimeOffset LastAtUtc, long Requests, long Turns,
    long Errors, long CostNanoUsd, long InputTokens, long OutputTokens, List<string> ProviderIds, bool Active);

/// <summary>Admin API payload: trace turn.</summary>
public sealed record TraceTurnDto(string TurnId, DateTimeOffset StartedAtUtc, long Requests, long Errors, long CostNanoUsd, double DurationMs);

/// <summary>A session with its turns, and the calls and spans of one turn (the latest unless one is asked for).</summary>
public sealed record TraceSessionDetailDto(TraceSessionDto Session, List<TraceTurnDto> Turns, string? TurnId, List<TraceCallDto> Calls, List<SpanDto> Spans);

/// <summary>Admin API payload: trace stage.</summary>
public sealed record TraceStageDto(string Name, long? P50Us, long? P95Us, int Samples);

/// <summary>Admin API payload: trace gateway.</summary>
public sealed record TraceGatewayDto(long Requests, long Errors, int InFlight, long? OverheadP50Us, long? OverheadP95Us, long Failovers,
    List<TraceStageDto> Stages);

/// <summary>Admin API payload: trace client node.</summary>
public sealed record TraceClientNodeDto(string? ClientKind, long Requests, long Errors, double PerMinute, int ActiveSessions);

/// <summary>
/// Admin API payload: trace account — one row of a provider's card on the topology canvas. <c>Attempts</c> / <c>Errors</c>
/// count the upstream attempts it made in the window and how many of them failed.
/// </summary>
public sealed record TraceAccountDto(
    string Id, string Name, string Status, bool Enabled, DateTimeOffset? CooldownUntilUtc, DateTimeOffset? SeenAtUtc,
    string? RemainingRequests, string? RemainingTokens, string? LimitRequests, long Attempts = 0, long Errors = 0);

/// <summary>Admin API payload: trace provider node.</summary>
public sealed record TraceProviderNodeDto(
    string ProviderId, string Name, string? Icon, string? TemplateId, long Requests, long Errors, long Attempts, long RateLimited,
    long Failovers, double PerMinute, long? TtfbP95Ms, List<TraceAccountDto> Accounts);

/// <summary>Admin API payload: trace topology.</summary>
public sealed record TraceTopologyDto(string Range, double WindowMinutes, TraceGatewayDto Gateway, List<TraceClientNodeDto> Clients,
    List<TraceProviderNodeDto> Providers);

/// <summary>
/// One tool call a response made, for the activity feed: its name and call id, and its arguments (JSON text, cut at 4 KB)
/// when the <c>traceContent</c> setting was on.
/// </summary>
public sealed record TraceToolCallDto(string Name, string? Id, string? Arguments);

/// <summary>
/// One request of the 链路追踪 activity feed (<c>GET /api/traces/activity</c>): why it was sent (<c>Cause</c>), the upstream
/// attempts it took (each account or provider it went to, failures and why it moved on) and what came back — thinking time
/// and size, the start of the reply and each tool call. <c>Cause.Prompt</c>, <c>Reply</c> and tool arguments are recorded
/// only while the <c>traceContent</c> setting is on.
/// </summary>
public sealed record TraceActivityDto(
    RequestSummaryDto Request, int? Step, string? FinishReason, string? ErrorMessage, int ToolCallCount, RequestCause? Cause,
    List<RequestAttempt> Attempts, long? ReasoningMs, long? ReasoningChars, long? TextChars, string? Reply, List<TraceToolCallDto> ToolCalls);
