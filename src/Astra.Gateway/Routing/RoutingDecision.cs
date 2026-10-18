using Astra.Core.Routing;
using Astra.Gateway.Tracing;

namespace Astra.Gateway.Routing;

/// <summary>Where a routing decision came from (the <c>astra.route.source</c> span attribute).</summary>
public static class RoutingSources
{
    /// <summary>The judge was asked and answered confidently.</summary>
    public const string Judge = "judge";

    /// <summary>The session's pinned grade (the judge was asked on an earlier request).</summary>
    public const string Pin = "pin";

    /// <summary>The grade decided for this user turn, reused by a request that only answers tool calls.</summary>
    public const string Turn = "turn";

    /// <summary>The fallback grade: the judge failed, was not confident enough or had nothing to read. Never pinned.</summary>
    public const string Fallback = "fallback";

    /// <summary>The fallback grade because recent judgements kept failing and the judge is being left alone for a while.</summary>
    public const string Breaker = "breaker";
}

/// <summary>
/// Which grade — and so which model — a request to the virtual model goes to, and how that was decided (plan §5.4).
/// The judge fields describe the call this request made; a decision reused from a pin or a turn carries none.
/// </summary>
/// <param name="FallbackReason">Why the fallback grade was used: <c>no_text</c>, <c>breaker</c>, <c>low_confidence</c>, <c>not_configured</c>,
/// <c>error</c>, or the failure kind of the judge call (<c>timeout</c>, <c>network</c>, <c>auth</c>, <c>rate_limited</c>,
/// <c>overloaded</c>, <c>bad_request</c>, <c>bad_response</c>).</param>
/// <param name="AdjustedFrom">The grade decided first, when none of the caller's providers serves its model and the request
/// went to the nearest grade they do serve (<see cref="GradeId"/>); null when the request kept its grade.</param>
/// <param name="JudgeNewConnection">The judge call had to open a connection to the service first.</param>
/// <param name="ProviderId">The provider the grade names (<see cref="Astra.Core.Routing.RoutingGrade.ProviderId"/>).</param>
/// <param name="AdjustedReason">Why the caller could not use <paramref name="AdjustedFrom"/> (a <see cref="GradeProblems"/> code).</param>
public sealed record RoutingDecision(
    string GradeId, string Model, string Source, string Mode,
    double? Confidence = null, string? FallbackReason = null,
    string? JudgeProtocol = null, string? JudgeModel = null, long JudgeMs = 0, int JudgeInputTokens = 0, int JudgeOutputTokens = 0,
    string? AdjustedFrom = null, bool JudgeNewConnection = false, string? ProviderId = null, string? AdjustedReason = null)
{
    /// <summary>The same decision, served again from the pin or turn cache: it called no judge this time.</summary>
    public RoutingDecision Reused(string source, string mode) => this with
    {
        Source = source, Mode = mode, JudgeProtocol = null, JudgeModel = null, JudgeMs = 0, JudgeInputTokens = 0, JudgeOutputTokens = 0,
        JudgeNewConnection = false,
    };

    /// <summary>The decision as the request log keeps it (<c>requests.routing_json</c>): success, fallback or move at a glance.</summary>
    public RoutingReport Report() =>
        new(GradeId, Model, Source, Mode, Confidence, FallbackReason, AdjustedFrom, JudgeProtocol is null ? null : JudgeMs, AdjustedReason);

    /// <summary>Writes the decision on the span of the resolution it took part in (<c>astra.route.*</c>).</summary>
    public void Annotate(TraceSpanBuilder span)
    {
        span.Set("astra.route.mode", Mode).Set("astra.route.source", Source).Set("astra.route.grade", GradeId)
            .Set("astra.route.model", Model).Set("astra.route.confidence", Confidence).Set("astra.route.fallback_reason", FallbackReason)
            .Set("astra.route.adjusted_from", AdjustedFrom).Set("astra.route.adjusted_reason", AdjustedReason);
        if (JudgeProtocol is null) return;
        span.Set("astra.route.judge.protocol", JudgeProtocol).Set("astra.route.judge.model", JudgeModel).Set("astra.route.judge.ms", JudgeMs)
            .Set("astra.route.judge.input_tokens", JudgeInputTokens).Set("astra.route.judge.output_tokens", JudgeOutputTokens)
            .Set("astra.route.judge.new_connection", JudgeNewConnection ? true : null);
    }
}
