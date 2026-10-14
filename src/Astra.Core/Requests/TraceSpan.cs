namespace Astra.Core.Requests;

/// <summary>
/// One span of a gateway request (row of <c>trace_spans</c>; .plan/observability.md §2.1). Times are microseconds from
/// the moment the request arrived, on the monotonic clock; the trace id is the request's <see cref="RequestRecord.TraceId"/>.
/// </summary>
public sealed class TraceSpan
{
    public string RequestId { get; set; } = "";

    /// <summary>16 hex characters, unique within the request.</summary>
    public string SpanId { get; set; } = "";

    /// <summary>Null for the request's root span.</summary>
    public string? ParentSpanId { get; set; }

    /// <summary>One of <see cref="TraceSpanNames"/>.</summary>
    public string Name { get; set; } = "";

    /// <summary>server | internal | client (OTel SpanKind).</summary>
    public string Kind { get; set; } = TraceSpanKinds.Internal;

    public long StartUs { get; set; }
    public long DurationUs { get; set; }

    /// <summary>ok | error | unset.</summary>
    public string Status { get; set; } = TraceSpanStatus.Unset;

    public string? StatusMessage { get; set; }

    /// <summary>Flat attribute object (string / number / bool values), keys <c>astra.*</c> and OTel <c>gen_ai.*</c>.</summary>
    public string? AttributesJson { get; set; }

    /// <summary><c>[{ "name", "offset_us", "attributes" }]</c>, e.g. the first token.</summary>
    public string? EventsJson { get; set; }
}

/// <summary>Span names (.plan/observability.md §2.3).</summary>
public static class TraceSpanNames
{
    public const string Request = "gateway.request";
    public const string Route = "gateway.route";
    public const string Body = "gateway.body";
    public const string Middleware = "gateway.middleware";
    public const string Privacy = "gateway.privacy";
    public const string Resolve = "gateway.resolve";
    public const string Encode = "gateway.encode";
    public const string Attempt = "upstream.attempt";
    public const string Response = "gateway.response";
}

public static class TraceSpanKinds
{
    public const string Server = "server";
    public const string Internal = "internal";
    public const string Client = "client";
}

public static class TraceSpanStatus
{
    public const string Ok = "ok";
    public const string Error = "error";
    public const string Unset = "unset";
}

/// <summary>What kind of agent call a request is (.plan/observability.md §3.2).</summary>
public static class TraceCallKinds
{
    /// <summary>The main agent's loop.</summary>
    public const string Main = "main";

    /// <summary>A sub-agent started from one of the main agent's tool calls.</summary>
    public const string Sidechain = "sidechain";

    /// <summary>A side call in the same session (title, quota check…).</summary>
    public const string Aux = "aux";
}

/// <summary>How a request was linked to the call before it (.plan/observability.md §3.2).</summary>
public static class TraceLinkSources
{
    /// <summary>The start of a turn: the last message is the user's.</summary>
    public const string NewTurn = "new_turn";

    /// <summary>Exact: the request answers tool calls of a known response.</summary>
    public const string ToolCall = "tool_call";

    /// <summary>Fallback: tool results whose call is unknown (e.g. after a restart) — the session's latest call.</summary>
    public const string Session = "session";

    /// <summary>Heuristic: a sub-agent or side call attached by session and timing.</summary>
    public const string Inferred = "inferred";
}
