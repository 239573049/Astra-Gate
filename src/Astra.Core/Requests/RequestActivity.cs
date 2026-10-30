namespace Astra.Core.Requests;

/// <summary>
/// One upstream send of a request, as the 链路追踪 views show it (<c>upstream.attempt</c> spans; .plan/observability.md §2.3):
/// where it went, how it ended and why it was retried. Times are milliseconds from the request's arrival.
/// </summary>
/// <param name="ProviderId">The provider this attempt went to (a provider pool's attempts can go to several).</param>
/// <param name="ProviderName">That provider's name when the attempt was made.</param>
/// <param name="AccountId">The subscription account it used; null for an API key.</param>
/// <param name="Status">The upstream's HTTP status; null while the attempt is in flight or when the send threw.</param>
/// <param name="Retry">Why another attempt followed (<c>astra.attempt.retry</c>: rate_limited_failover, provider_failover, …); null for the last one.</param>
/// <param name="StartMs">When it started.</param>
/// <param name="EndMs">When it ended; null while in flight.</param>
/// <param name="Error">The failure, when it failed without a status (a refused connection, a sign-in that broke).</param>
public sealed record RequestAttempt(
    string? ProviderId, string? ProviderName, string? AccountId, int? Status, string? Retry, long StartMs, long? EndMs, string? Error);

/// <summary>What a request carries to the model: the user's own input or the results of tool calls (.plan/observability.md §3.2).</summary>
public static class RequestCauseKinds
{
    /// <summary>The last message is the user's input: a new turn, or a sub-agent / side call starting.</summary>
    public const string User = "user";

    /// <summary>The request hands back the results of the tool calls the previous response asked for.</summary>
    public const string ToolResult = "tool_result";
}

/// <summary>
/// Why a request was sent, read from the client's body after the privacy guard (<c>astra.request.cause</c> and the root
/// span's <c>tool_result</c> events). <paramref name="Prompt"/> is the start of the user's message, kept only while the
/// <see cref="AppSettings.TraceContent"/> setting is on.
/// </summary>
public sealed record RequestCause(string Kind, List<ToolResultInfo> ToolResults, string? Prompt);

/// <summary>One tool result a request carries: the tool's name (from the call it answers), whether it reports an error, and its size.</summary>
/// <param name="Name">The tool, from the matching call in the history; null when that call is not in the request.</param>
/// <param name="Error">True when the client marked it failed (Anthropic <c>is_error</c>, a Gemini <c>error</c>); null when the protocol has no such mark.</param>
/// <param name="Chars">Characters of text in the result.</param>
public sealed record ToolResultInfo(string? Name, bool? Error, long Chars);
