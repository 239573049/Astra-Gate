using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;

namespace Astra.Gateway.Protocol;

// Hub-and-spoke protocol IR (plan §6.4). Every wire protocol implements one IProtocolCodec:
//   client request JSON ──DecodeRequest──▶ UnifiedRequest ──EncodeRequest──▶ upstream request JSON
//   upstream JSON / SSE ──IResponseDecoder──▶ UnifiedStreamEvent* ──IResponseEncoder──▶ client JSON / SSE
// Non-streaming responses go through the same event stream and are aggregated by the encoder.
// Same-protocol traffic normally takes the pass-through fast path; the decoder still runs alongside it to
// observe usage, first-token time and the finish reason.

// ---------------------------------------------------------------- request

public enum MessageRole
{
    User,
    Assistant,
}

/// <summary>One part of a message. Tool results live in User messages (Anthropic style).</summary>
public abstract record ContentPart;

public sealed record TextPart(string Text) : ContentPart;

/// <summary>An image given either by URL (http(s) or data:) or by inline base64 data.</summary>
public sealed record ImagePart(string? Url, string? MediaType, string? Base64Data, string? Detail = null) : ContentPart;

/// <summary>A document (PDF …) by URL, inline base64 data or an upstream file id.</summary>
public sealed record FilePart(string? Url, string? MediaType, string? Base64Data, string? FileName = null, string? FileId = null) : ContentPart;

/// <summary>An assistant tool call. <paramref name="ArgumentsJson"/> is JSON text (an object), "{}" when empty.</summary>
public sealed record ToolCallPart(string Id, string Name, string ArgumentsJson) : ContentPart;

/// <summary>The result of a tool call, sent back by the user side. Content holds TextPart / ImagePart only.</summary>
public sealed record ToolResultPart(string CallId, string? Name, IReadOnlyList<ContentPart> Content, bool IsError = false) : ContentPart;

/// <summary>
/// Reasoning / thinking content. Signatures and encrypted payloads are only valid for the protocol that
/// produced them (<paramref name="Origin"/>): encoders of another protocol drop them and keep the text
/// (lossy mapping, plan §6.4). Anthropic redacted_thinking travels as <paramref name="EncryptedContent"/>.
/// </summary>
public sealed record ReasoningPart(
    string? Text,
    string? Signature = null,
    string? EncryptedContent = null,
    ApiProtocol? Origin = null,
    string? Id = null) : ContentPart;

public sealed record UnifiedMessage(MessageRole Role, List<ContentPart> Parts);

public sealed class UnifiedTool
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }

    /// <summary>JSON schema of the arguments (function tools).</summary>
    public JsonNode? InputSchema { get; init; }

    public bool? Strict { get; init; }

    /// <summary>
    /// Non-function tool (Responses web_search / file_search, Anthropic server tools, Gemini googleSearch …):
    /// its type id. Only re-emitted to the protocol in <see cref="BuiltinOrigin"/>; other encoders drop it and
    /// add a warning.
    /// </summary>
    public string? BuiltinType { get; init; }

    public ApiProtocol? BuiltinOrigin { get; init; }

    /// <summary>Original JSON of a builtin tool, re-emitted verbatim to its own protocol.</summary>
    public JsonObject? BuiltinRaw { get; init; }
}

public enum ToolChoiceMode
{
    Auto,
    None,
    Required,

    /// <summary>Force one named function (<see cref="ToolChoice.Name"/>).</summary>
    Specific,
}

public sealed record ToolChoice(ToolChoiceMode Mode, string? Name = null);

public sealed class ReasoningConfig
{
    /// <summary>"minimal" | "low" | "medium" | "high" (OpenAI style), null when only a budget is known.</summary>
    public string? Effort { get; set; }

    /// <summary>Thinking budget (Anthropic budget_tokens / Gemini thinkingBudget).</summary>
    public int? BudgetTokens { get; set; }

    /// <summary>False = reasoning explicitly disabled (thinking.type=disabled, thinkingBudget=0, effort none).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Ask the upstream to return reasoning summaries / thoughts.</summary>
    public bool IncludeThoughts { get; set; }
}

public sealed class ResponseFormat
{
    /// <summary>"text" | "json_object" | "json_schema".</summary>
    public string Type { get; set; } = "text";

    public string? Name { get; set; }
    public JsonNode? Schema { get; set; }
    public bool? Strict { get; set; }
}

public sealed class UnifiedRequest
{
    /// <summary>Model name as requested by the client (also forwarded upstream unless the pipeline maps it).</summary>
    public string Model { get; set; } = "";

    /// <summary>System / developer instructions (TextPart only), in order.</summary>
    public List<ContentPart> System { get; set; } = [];

    public List<UnifiedMessage> Messages { get; set; } = [];
    public List<UnifiedTool> Tools { get; set; } = [];
    public ToolChoice? ToolChoice { get; set; }
    public bool? ParallelToolCalls { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public List<string>? Stop { get; set; }
    public int? MaxOutputTokens { get; set; }
    public ReasoningConfig? Reasoning { get; set; }
    public ResponseFormat? ResponseFormat { get; set; }
    public bool Stream { get; set; }
    public string? User { get; set; }
    public JsonObject? Metadata { get; set; }
    public string? ServiceTier { get; set; }

    /// <summary>Responses-only server-side conversation state; the pipeline rejects it for other upstreams.</summary>
    public string? PreviousResponseId { get; set; }

    /// <summary>
    /// Top-level fields of the client request the IR cannot express, keyed by the client protocol. Only the
    /// encoder of that same protocol merges them back (fields it already set win).
    /// </summary>
    public Dictionary<ApiProtocol, JsonObject> Extensions { get; set; } = new();

    /// <summary>Lossy-mapping notes collected while decoding / encoding (stored with the request log).</summary>
    public List<string> Warnings { get; set; } = [];
}

// ---------------------------------------------------------------- response events

public enum FinishReason
{
    Stop,
    Length,
    ToolCalls,
    ContentFilter,
    Error,
    Other,
}

public enum BlockKind
{
    Text,
    Reasoning,
    ToolCall,
}

/// <summary>
/// Normalized response event. Block indexes are assigned by the decoder: 0, 1, 2 … in order of appearance,
/// across all block kinds of one response. Every block is opened by <see cref="BlockStartEvent"/> before its
/// deltas and closed by <see cref="BlockStopEvent"/>. Order of a complete response:
/// MessageStart, (BlockStart, deltas…, BlockStop)*, Usage?, MessageStop — or Error at any point.
/// </summary>
public abstract record UnifiedStreamEvent;

public sealed record MessageStartEvent(string? Id, string? Model) : UnifiedStreamEvent;

/// <param name="ToolCallId">Tool blocks: the call id (decoders invent "call_&lt;ulid&gt;" when the upstream has none).</param>
/// <param name="ToolName">Tool blocks: the function name.</param>
public sealed record BlockStartEvent(int Index, BlockKind Kind, string? ToolCallId = null, string? ToolName = null) : UnifiedStreamEvent;

public sealed record TextDeltaEvent(int Index, string Text) : UnifiedStreamEvent;

/// <summary>Reasoning text and/or its signature / encrypted payload (they may arrive in separate events).</summary>
public sealed record ReasoningDeltaEvent(int Index, string? Text, string? Signature = null, string? EncryptedContent = null, ApiProtocol? Origin = null)
    : UnifiedStreamEvent;

/// <summary>A fragment of the tool call's JSON arguments; fragments concatenate to the full JSON text.</summary>
public sealed record ToolArgsDeltaEvent(int Index, string JsonFragment) : UnifiedStreamEvent;

public sealed record BlockStopEvent(int Index) : UnifiedStreamEvent;

/// <summary>
/// Timing signal for output with no portable IR content, such as a Responses custom tool call.
/// Encoders ignore it; pass-through still forwards the original payload unchanged.
/// </summary>
public sealed record OutputActivityEvent : UnifiedStreamEvent;

/// <summary>
/// Usage reported by the upstream. <paramref name="Raw"/> is the cumulative usage object in the upstream's own
/// shape (decoders merge partial reports, e.g. Anthropic message_start + message_delta); <paramref name="Usage"/> is
/// <c>UsageNormalizer.Normalize(Protocol, Raw)</c>. The last UsageEvent of a response wins.
/// OpenAI's <c>cache_write_tokens</c> extension has no TTL: decoders map it to 5m with a usage note;
/// encoders sum 5m and 1h writes into that field, losing the TTL split.
/// </summary>
public sealed record UsageEvent(ApiProtocol Protocol, JsonObject Raw, NormalizedUsage Usage) : UnifiedStreamEvent;

/// <param name="RawReason">The upstream's own finish reason string, for logs.</param>
public sealed record MessageStopEvent(FinishReason Reason, string? RawReason = null) : UnifiedStreamEvent;

/// <summary>An error (upstream error event mid-stream, or a gateway error). Status is an HTTP status code.</summary>
public sealed record ErrorEvent(int Status, string Type, string Message) : UnifiedStreamEvent;

// ---------------------------------------------------------------- codec contract

/// <summary>One server-sent event. <see cref="Event"/> is the "event:" field (null when absent).</summary>
public sealed record SseEvent(string? Event, string Data);

public sealed class RequestDecodeContext
{
    /// <summary>Model taken from the URL path (Gemini /v1beta/models/{model}:action), else null.</summary>
    public string? PathModel { get; init; }

    /// <summary>Streaming decided by the URL (Gemini streamGenerateContent), else null = read it from the body.</summary>
    public bool? PathStream { get; init; }
}

public sealed class RequestEncodeContext
{
    /// <summary>Model id to send upstream.</summary>
    public string UpstreamModel { get; init; } = "";

    /// <summary>Effective model's max output tokens; Anthropic falls back to it (then 8192) when the IR has none.</summary>
    public int? DefaultMaxOutputTokens { get; init; }

    public EffortBudgets EffortBudgets { get; init; } = new();

    /// <summary>Anthropic upstream: add 5m cache_control breakpoints to system, tools, and the last two message blocks (provider setting).</summary>
    public bool AutoCacheControl { get; init; } = true;
}

public sealed class ResponseDecodeContext
{
    public bool Stream { get; init; }

    /// <summary>Model of the request (used when the upstream response omits it).</summary>
    public string Model { get; init; } = "";
}

public sealed class ResponseEncodeContext
{
    public bool Stream { get; init; }

    /// <summary>Model name echoed to the client (what the client asked for).</summary>
    public string Model { get; init; } = "";

    /// <summary>Response id to expose to the client (encoders add their protocol prefix if it has none).</summary>
    public string ResponseId { get; init; } = "";

    public DateTimeOffset Created { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Chat streaming: the client asked for stream_options.include_usage (send the final usage chunk).</summary>
    public bool IncludeUsage { get; init; } = true;

    /// <summary>The decoded client request (Responses echoes parts of it back).</summary>
    public UnifiedRequest? Request { get; init; }
}

/// <summary>Converts upstream output (JSON body or SSE events) into IR events. Stateful, one per response.</summary>
public interface IResponseDecoder
{
    /// <summary>Last model id declared in the upstream response; null when omitted, never the request fallback.</summary>
    string? ResponseModel { get; }

    /// <summary>Streaming: one upstream SSE event (the "[DONE]" sentinel included) → zero or more IR events.</summary>
    IEnumerable<UnifiedStreamEvent> DecodeSse(SseEvent sse);

    /// <summary>Non-streaming: the whole upstream JSON body → the complete IR event sequence.</summary>
    IEnumerable<UnifiedStreamEvent> DecodeJson(JsonObject body);

    /// <summary>End of the upstream stream: close open blocks, emit a MessageStop if none was seen.</summary>
    IEnumerable<UnifiedStreamEvent> Complete();

    /// <summary>
    /// The complete upstream response object, when the decoder can reconstruct one (Responses decoder:
    /// a non-streaming body, or the terminal <c>response.completed|failed</c> event). Callers that must
    /// answer a non-streaming client with a single JSON body use this instead of re-encoding.
    /// </summary>
    JsonObject? FinalResponse => null;
}

/// <summary>Converts IR events into the client protocol. Stateful, one per response.</summary>
public interface IResponseEncoder
{
    /// <summary>Streaming: SSE frames to write for this event (may be empty). Non-streaming callers ignore them.</summary>
    IReadOnlyList<SseEvent> OnEvent(UnifiedStreamEvent e);

    /// <summary>Non-streaming: the complete client JSON body aggregated from every event passed to OnEvent.</summary>
    JsonObject BuildJson();
}

/// <summary>One wire protocol. Implementations are stateless singletons.</summary>
public interface IProtocolCodec
{
    ApiProtocol Protocol { get; }

    /// <summary>Client request body → IR. Throws <see cref="ProtocolException"/> (400) on invalid input.</summary>
    UnifiedRequest DecodeRequest(JsonObject body, RequestDecodeContext ctx);

    /// <summary>IR → upstream request body (without the model when the protocol carries it in the URL).</summary>
    JsonObject EncodeRequest(UnifiedRequest request, RequestEncodeContext ctx);

    IResponseDecoder CreateResponseDecoder(ResponseDecodeContext ctx);
    IResponseEncoder CreateResponseEncoder(ResponseEncodeContext ctx);

    /// <summary>Error body in this protocol's shape (sent to clients of this protocol).</summary>
    JsonObject EncodeError(int status, string type, string message);

    /// <summary>Best-effort parse of an upstream error body of this protocol → (type, message).</summary>
    (string Type, string Message) DecodeError(int status, string body);
}

/// <summary>Invalid client input for a protocol; mapped to an HTTP 400 in the client's error format.</summary>
public sealed class ProtocolException(string message, int status = 400, string type = "invalid_request_error") : Exception(message)
{
    public int Status { get; } = status;
    public string Type { get; } = type;
}
