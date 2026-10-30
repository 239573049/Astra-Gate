using System.Text.Json.Nodes;
using Astra.Core.Billing;
using Astra.Core.Privacy;
using Astra.Core.Requests;
using Astra.Core.Routing;

namespace Astra.Api.Contracts;



/// <summary>
/// Admin API payload: request summary. The live feed (<c>/api/requests/live</c>) adds what the 链路追踪 views follow while a
/// request runs: <c>Stage</c> / <c>Phases</c> (what the model is producing), <c>Attempts</c> (the upstream sends so far, the
/// last one possibly in flight) and <c>Cause</c> (user input or tool results); list queries leave them null.
/// </summary>
public sealed record RequestSummaryDto(
    string Id, DateTimeOffset StartedAtUtc, string? ClientKind, string? ProviderId, string? ProviderName,
    string InboundProtocol, string? UpstreamProtocol, bool Passthrough, string? RequestedModel, string? UpstreamModel,
    string? ResponseModel, string? SystemModelId, bool Stream, string Status, int? HttpStatus, long? TtftMs, long? TotalMs, double? OutputTps,
    long TotalInputTokens, long TotalOutputTokens, long CacheReadTokens, long CacheWriteTokens, long ReasoningTokens,
    long CostNanoUsd, string UsageSource,
    string? ReasoningEffort, string? ReasoningMode, long? ReasoningBudgetTokens,
    string? TokenId, string? TokenName, string? AccountId, string? AccountName, bool Remote, string? ClientIp,
    string? SessionKey = null, string? Stage = null, string? TurnId = null, string? CallKind = null, string? ParentRequestId = null,
    string? Phases = null, RoutingReport? Routing = null, List<RequestAttempt>? Attempts = null, RequestCause? Cause = null);

/// <summary>Admin API payload: usage item.</summary>
public sealed record UsageItemDto(
    string TokenType, long Tokens, bool IsPerCall, string UnitPrice, string? BaseUnitPrice, string TierApplied,
    string? PricedAs, List<string>? Multipliers, long CostNanoUsd, string? Note);

/// <summary>
/// Captured bodies, each cut to its first <see cref="PreviewLimit"/> bytes; <see cref="Sizes"/> holds every part's
/// full size, and GET /api/requests/{id}/bodies/{part} serves a whole part.
/// </summary>
public sealed record BodiesDto(string? ClientRequest, string? UpstreamRequest, string? UpstreamRequestHeaders,
    string? UpstreamResponse, string? ClientResponse, BodySizesDto Sizes, int PreviewLimit);

/// <summary>Full size in bytes of each captured part (null: not captured).</summary>
public sealed record BodySizesDto(long? ClientRequest, long? UpstreamRequest, long? UpstreamRequestHeaders,
    long? UpstreamResponse, long? ClientResponse);

/// <summary>Admin API payload: request detail.</summary>
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
    string? TokenId, string? TokenName, string? AccountId, string? AccountName, bool Remote, string? ClientIp,
    RoutingReport? Routing = null);

/// <summary>
/// One live request-log event (SSE <c>/api/requests/live</c>): "started" / "updated" carry the row as it stands
/// (status "pending" while in flight), "persisted" means list queries now return it.
/// </summary>
public sealed record RequestLiveEventDto(string Type, RequestSummaryDto Request);

/// <summary>Admin API payload: page.</summary>
public sealed record PageDto<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize);

/// <summary>A request-log page plus the summed cost of every matching request (all pages).</summary>
public sealed record RequestPageDto(IReadOnlyList<RequestSummaryDto> Items, long Total, int Page, int PageSize, long TotalCostNanoUsd);

/// <summary>Admin API payload: stats summary.</summary>
public sealed record StatsSummaryDto(string Range, decimal CostUsd, long Requests, double SuccessRate,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, long ReasoningTokens, double? AvgTtftMs);

/// <summary>Admin API payload: timeseries point.</summary>
public sealed record TimeseriesPointDto(string Bucket, string Key, decimal CostUsd, long Requests, long Tokens);

/// <summary>InputTokens includes CacheReadTokens, so the hit rate is CacheReadTokens / InputTokens (as in the summary).</summary>
public sealed record TopModelDto(string Model, decimal CostUsd, long Requests, long Tokens, long InputTokens, long CacheReadTokens);

/// <summary>One local day of the activity heatmap; days without requests are absent.</summary>
public sealed record DailyActivityDto(string Day, long Requests);

/// <summary>
/// One provider over a range: the requests it answered and provider-pool failovers away from it (its failed attempts)
/// and to it (requests it took over). <see cref="Name"/> is the provider's current name, else the logged one.
/// </summary>
public sealed record ProviderStatDto(
    string ProviderId, string? Name, long Requests, long SuccessRequests, double SuccessRate, decimal CostUsd,
    long InputTokens, long OutputTokens, double? AvgTtftMs, long FailoversOut, long FailoversIn);

/// <summary>
/// Live request-log rate over a trailing window, normalized to per minute: requests (including in-flight ones)
/// and tokens (completed requests only — usage settles when a request finishes), plus the window's cache-hit
/// ratio (null when the window has no input tokens).
/// </summary>
public sealed record RateStatsDto(double Rpm, double Tpm, double? CacheHitRate, int WindowSeconds);
