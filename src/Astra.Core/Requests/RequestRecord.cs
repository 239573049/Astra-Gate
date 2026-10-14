namespace Astra.Core.Requests;

public static class RequestStatus
{
    public const string Success = "success";
    public const string UpstreamError = "upstream_error";
    public const string GatewayError = "gateway_error";
    public const string ClientCancelled = "client_cancelled";

    /// <summary>Rejected by the privacy guard (a category with action "block" matched).</summary>
    public const string Blocked = "blocked";
}

/// <summary>One gateway request (row of the <c>requests</c> table). Money is integer nano-USD.</summary>
public sealed class RequestRecord
{
    public string Id { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public string? ClientKind { get; set; }

    /// <summary>Token that authenticated the request; null when authentication failed.</summary>
    public string? TokenId { get; set; }

    /// <summary>
    /// Token name: written as a snapshot at request time; queries return the token's current name and fall back to the
    /// snapshot once the token was deleted.
    /// </summary>
    public string? TokenName { get; set; }

    public string? ProviderId { get; set; }
    public string? ProviderName { get; set; }

    /// <summary>Subscription account that served the request (after any failover); null for API-key providers.</summary>
    public string? AccountId { get; set; }

    /// <summary>The account's current display name (query-time join, not stored); null once the account is deleted.</summary>
    public string? AccountName { get; set; }
    public string InboundProtocol { get; set; } = "";
    public string? UpstreamProtocol { get; set; }
    public bool Passthrough { get; set; }
    public string? RequestedModel { get; set; }
    public string? UpstreamModel { get; set; }

    /// <summary>Model ID declared by the upstream's ORIGINAL response itself; null when the response declares none (and for rows logged before this was tracked). Never falls back to the requested or sent model.</summary>
    public string? ResponseModel { get; set; }
    public string? SystemModelId { get; set; }
    public bool Stream { get; set; }

    /// <summary>
    /// Reasoning effort the client explicitly set, verbatim (Chat <c>reasoning_effort</c>, Responses
    /// <c>reasoning.effort</c> incl. "none", Anthropic <c>output_config.effort</c>, Gemini
    /// <c>thinkingLevel</c>). Null when the client set nothing or the row predates the field — never
    /// derived from a budget, token count or provider default.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Explicit reasoning mode from the client: Anthropic <c>thinking.type</c> ("adaptive" | "enabled" |
    /// "disabled"), or Gemini <c>thinkingBudget</c> semantics ("auto" for -1, "disabled" for 0). Null when unset.
    /// </summary>
    public string? ReasoningMode { get; set; }

    /// <summary>
    /// Thinking budget the client explicitly set (Anthropic <c>thinking.budget_tokens</c>, Gemini
    /// <c>thinkingBudget</c> &gt; 0), kept as the raw value — never mapped to an effort level.
    /// </summary>
    public long? ReasoningBudgetTokens { get; set; }

    public string? ServiceTier { get; set; }
    public string Status { get; set; } = RequestStatus.Success;
    public int? HttpStatus { get; set; }
    public string? ErrorType { get; set; }
    public string? ErrorMessage { get; set; }
    public string? UpstreamRequestId { get; set; }
    public long? TtfbMs { get; set; }
    public long? TtftMs { get; set; }
    public long? TotalMs { get; set; }
    public long? GenerationMs { get; set; }
    public double? OutputTps { get; set; }
    public long TotalInputTokens { get; set; }
    public long TotalOutputTokens { get; set; }

    /// <summary>Part of <see cref="TotalInputTokens"/> served from the prompt cache (cache hits).</summary>
    public long CacheReadTokens { get; set; }

    /// <summary>Part of <see cref="TotalInputTokens"/> written to the prompt cache (5m + 1h).</summary>
    public long CacheWriteTokens { get; set; }

    /// <summary>Part of <see cref="TotalOutputTokens"/> spent on reasoning / thinking.</summary>
    public long ReasoningTokens { get; set; }
    public long CostNanoUsd { get; set; }

    /// <summary>"reported" | "missing".</summary>
    public string UsageSource { get; set; } = "missing";

    public string? UsageRawJson { get; set; }
    public string? PricingSnapshotJson { get; set; }

    /// <summary>"system_default" | "provider_price" | "provider_override" | "none".</summary>
    public string? PricingSource { get; set; }

    public string? PriceKey { get; set; }
    public string? BillingTraceJson { get; set; }
    public string? BillingDescription { get; set; }

    /// <summary>Privacy guard outcome (hits / actions / dry-run). Originals of redacted values are never stored.</summary>
    public string? PrivacyJson { get; set; }

    public string? BodyRef { get; set; }
    public string? UserAgent { get; set; }

    /// <summary>Reached Astra through Cloudflare (remote access tunnel) rather than from this machine or its network.</summary>
    public bool Remote { get; set; }

    /// <summary>The caller's address when it isn't this machine (the visitor's, for tunnel requests); null for local callers.</summary>
    public string? ClientIp { get; set; }

    // ---- tracing (.plan/observability.md §2.2); all null for rows logged before tracing

    /// <summary>32-hex trace id shared by every call of one agent turn (the inbound traceparent's, when it sent one).</summary>
    public string? TraceId { get; set; }

    /// <summary>The span id of the inbound traceparent: the agent-side parent of this request's root span.</summary>
    public string? ParentSpanId { get; set; }

    /// <summary>The conversation this call belongs to (.plan/observability.md §3.1): a session id, or a hash.</summary>
    public string? SessionKey { get; set; }

    /// <summary>Fingerprint of the call chain (system + first user message): tells a sub-agent or side call from the main agent.</summary>
    public string? ChainKey { get; set; }

    /// <summary>Id of the request that opened this turn (this request's own id when it did).</summary>
    public string? TurnId { get; set; }

    /// <summary>The call this one continues (whose tool calls it answers, or inferred); null at the start of a turn.</summary>
    public string? ParentRequestId { get; set; }

    /// <summary>1-based index of this model call within its turn.</summary>
    public int? Step { get; set; }

    /// <summary><see cref="TraceCallKinds"/>: main agent, sub-agent (sidechain) or auxiliary call.</summary>
    public string? CallKind { get; set; }

    /// <summary><see cref="TraceLinkSources"/>: how <see cref="ParentRequestId"/> was found.</summary>
    public string? LinkSource { get; set; }

    /// <summary>Why the response ended: stop | length | tool_calls | content_filter | error | other.</summary>
    public string? FinishReason { get; set; }

    /// <summary>Tool calls the response asked for.</summary>
    public int ToolCalls { get; set; }

    /// <summary>
    /// What the model is producing right now — <c>thinking</c>, <c>writing</c> or <c>tool:&lt;name&gt;</c> — for the live
    /// request feed only (.plan/observability.md §5); never stored. Null before the first output and once it is over.
    /// </summary>
    public string? LiveStage { get; set; }

    /// <summary>
    /// When each of those stages began, as <c>stage@ms</c> since the request arrived, comma-separated in order — so the
    /// live views can colour a call in flight by phase, even when opened halfway through. Live feed only; never stored.
    /// </summary>
    public string? LivePhases { get; set; }

    public List<RequestUsageItem> UsageItems { get; set; } = [];

    /// <summary>The request's spans (root first), written with the row.</summary>
    public List<TraceSpan> Spans { get; set; } = [];
}

/// <summary>One token-type line of a request's bill (row of <c>request_usage_items</c>).</summary>
public sealed class RequestUsageItem
{
    public long Id { get; set; }
    public string RequestId { get; set; } = "";
    public string TokenType { get; set; } = "";
    public long Tokens { get; set; }
    public bool IsPerCall { get; set; }

    /// <summary>Unit price after multipliers (USD per 1M tokens, or per call), as an invariant decimal string.</summary>
    public string UnitPrice { get; set; } = "0";

    public string? BaseUnitPrice { get; set; }
    public string TierApplied { get; set; } = "base";
    public string? PricedAs { get; set; }
    public string? MultipliersJson { get; set; }
    public long CostNanoUsd { get; set; }
    public string? Note { get; set; }
}
