namespace Astra.Core.Routing;

/// <summary>One option of the judge's question: a grade id and the words that describe it.</summary>
public sealed record JudgeChoice(string Id, string Description);

/// <summary>Facts about the conversation that are not text: how big it is, and whether the current request carries images.</summary>
public sealed record JudgeShape(int Messages, int Tools, bool HasImages);

/// <summary>What the judge reads: the current request, the user requests before it (oldest first), and the conversation's shape.</summary>
public sealed record JudgeState(string Request, IReadOnlyList<string> Earlier, JudgeShape Shape);

/// <summary>The judge's question: pick exactly one of <paramref name="Choices"/> (cheapest grade first) for <paramref name="State"/>.</summary>
public sealed record JudgeQuestion(string Instructions, IReadOnlyList<JudgeChoice> Choices, JudgeState State);

/// <summary>
/// The judge's answer. <paramref name="Confidence"/> is 0–1 and describes how concentrated the probability is on
/// <paramref name="Choice"/>, not how likely the choice is to be right. <paramref name="JudgeModel"/> is the model that
/// answered, as the service reports it (an alias resolves to a versioned id).
/// </summary>
public sealed record JudgeVerdict(
    string Choice, double Confidence, IReadOnlyDictionary<string, double> Probabilities,
    string? JudgeModel, int InputTokens, int OutputTokens);

/// <summary>
/// Smart routing's decision for one request to the virtual model, as the request log keeps it (<c>requests.routing_json</c>,
/// Json.Storage; plan §5.9) — so a request shows whether it went the judge's way without opening its trace.
/// </summary>
/// <param name="Grade">The grade the request was sent to; <paramref name="Model"/> is that grade's model.</param>
/// <param name="Source"><c>judge</c> (judged for this request), <c>pin</c> / <c>turn</c> (the session's or this turn's decision,
/// reused), <c>fallback</c> or <c>breaker</c> (the fallback grade).</param>
/// <param name="FallbackReason">Why the fallback grade was used (<c>timeout</c>, <c>low_confidence</c> …); a decision reused
/// from a turn that fell back keeps it. Null when the judge's grade was used.</param>
/// <param name="AdjustedFrom">The grade decided first, when this caller could not use it and the request went to
/// <paramref name="Grade"/> instead.</param>
/// <param name="JudgeMs">How long this request's own judge call took; null when it made none.</param>
/// <param name="AdjustedReason">Why the caller could not use <paramref name="AdjustedFrom"/> (a <see cref="GradeProblems"/> code).</param>
public sealed record RoutingReport(
    string Grade, string Model, string Source, string Mode,
    double? Confidence = null, string? FallbackReason = null, string? AdjustedFrom = null, long? JudgeMs = null,
    string? AdjustedReason = null);

/// <summary>Why a caller cannot use a smart-routing grade (plan §7 risk 7): the codes the console and the request log explain.</summary>
public static class GradeProblems
{
    /// <summary>The grade names a provider outside the caller's own, and the caller's token is limited to some clients.</summary>
    public const string TokenLimited = "token_limited";

    /// <summary>The grade's provider has been deleted.</summary>
    public const string ProviderMissing = "provider_missing";

    /// <summary>The grade's provider is switched off or has no address.</summary>
    public const string ProviderOff = "provider_off";

    /// <summary>The grade's provider is a subscription only Claude Code may use, and the caller is not Claude Code.</summary>
    public const string ClaudeCodeOnly = "claude_code_only";

    /// <summary>
    /// The grade's provider does not serve its model — not one of its enabled models, nor mapped onto one; for a grade that
    /// names no provider, none of the caller's providers does.
    /// </summary>
    public const string ModelNotServed = "model_not_served";

    public static readonly IReadOnlyList<string> All = [TokenLimited, ProviderMissing, ProviderOff, ClaudeCodeOnly, ModelNotServed];
}

/// <summary>Why a caller cannot use a grade: a <see cref="GradeProblems"/> code, and the grade's provider as the console names it.</summary>
public sealed record GradeProblem(string Code, string? ProviderName);
