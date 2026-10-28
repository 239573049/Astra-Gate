using System.Text.Json.Nodes;

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

/// <summary>Admin API payload: trace account.</summary>
public sealed record TraceAccountDto(
    string Id, string Name, string Status, bool Enabled, DateTimeOffset? CooldownUntilUtc, DateTimeOffset? SeenAtUtc,
    string? RemainingRequests, string? RemainingTokens, string? LimitRequests);

/// <summary>Admin API payload: trace provider node.</summary>
public sealed record TraceProviderNodeDto(
    string ProviderId, string Name, string? Icon, string? TemplateId, long Requests, long Errors, long Attempts, long RateLimited,
    long Failovers, double PerMinute, long? TtfbP95Ms, List<TraceAccountDto> Accounts);

/// <summary>Admin API payload: trace topology.</summary>
public sealed record TraceTopologyDto(string Range, double WindowMinutes, TraceGatewayDto Gateway, List<TraceClientNodeDto> Clients,
    List<TraceProviderNodeDto> Providers);
