
namespace Astra.Api.Contracts;



/// <summary>Admin API payload: smart routing judge.</summary>
public sealed record SmartRoutingJudgeDto(
    string Protocol, string? BaseUrl, string EffectiveBaseUrl, bool HasApiKey, string? ApiKeyMasked, string Model, int TimeoutMs, string? Instructions);

/// <param name="ProviderId">The provider the grade's requests go to; null while no usable provider serves its model.</param>
public sealed record SmartRoutingGradeDto(string Id, string Name, string Description, string Model, string? ProviderId);

/// <summary>Admin API payload: smart routing rules.</summary>
public sealed record SmartRoutingRulesDto(string Mode, int SessionTtlMinutes, string FallbackGrade, double MinConfidence, int ContextTurns, int MaxChars);

/// <param name="BreakerOpen">The judge failed several times in a row and is being left alone for a while.</param>
/// <param name="Pins">Sessions that currently hold a pinned grade.</param>
/// <param name="Recent">What the last fresh decisions came to.</param>
/// <param name="Unreachable">Callers moved to another grade lately because none of their providers serves a grade's model, the latest first.</param>
public sealed record SmartRoutingStatusDto(bool BreakerOpen, int Pins, RecentDecisionsDto Recent, List<UnreachableGradeDto> Unreachable);

/// <summary>
/// The last fresh decisions under the current settings (at most <see cref="RoutingActivity.Window"/>; a decision kept for
/// a session or turn is not counted again): how each ended — <c>judge</c>, or the reason the fallback grade was used.
/// </summary>
/// <param name="MedianJudgeMs">The median time of the judge calls that answered (confidently or not); null when none did.</param>
/// <param name="LastFallback">The reason of the latest decision that used the fallback grade; null when there was none.</param>
public sealed record RecentDecisionsDto(int Total, Dictionary<string, int> Outcomes, long? MedianJudgeMs, string? LastFallback, DateTimeOffset? LastFallbackAt);

/// <param name="Caller">A client kind, or a token's name for a direct call.</param>
/// <param name="UsedGrade">The grade the requests were given instead; null when the caller could use no grade.</param>
/// <param name="Reason">Why the caller cannot use the grade: a <see cref="GradeProblems"/> code.</param>
/// <param name="ProviderName">The grade's provider; null when it is gone or the grade names none.</param>
public sealed record UnreachableGradeDto(
    string Caller, string Grade, string Model, string? UsedGrade, int Count, DateTimeOffset LastAt, string Reason, string? ProviderName);

/// <summary>A judge dialect with the address and model a new judge of that dialect starts with.</summary>
public sealed record JudgeProtocolDto(string Id, string DefaultBaseUrl, string DefaultModel);

/// <param name="DefaultInstructions">The question used while <c>judge.instructions</c> is empty.</param>
public sealed record SmartRoutingDto(
    bool Enabled, string VirtualModel, SmartRoutingJudgeDto Judge, List<SmartRoutingGradeDto> Grades, SmartRoutingRulesDto Rules,
    SmartRoutingStatusDto Status, string DefaultInstructions, List<JudgeProtocolDto> Protocols);

/// <summary>Partial update: a null field is left as it is. <c>ApiKey</c> is write-only; "" clears it.</summary>
public sealed record SmartRoutingJudgeUpdate(
    string? Protocol = null, string? BaseUrl = null, string? ApiKey = null, string? Model = null, int? TimeoutMs = null, string? Instructions = null);

/// <param name="ProviderId">The provider <c>Model</c> was picked from; left out, the first usable provider serving the model.</param>
public sealed record SmartRoutingGradeUpdate(string? Id = null, string? Name = null, string? Description = null, string? Model = null, string? ProviderId = null);

/// <summary>Request body: smart routing rules update.</summary>
public sealed record SmartRoutingRulesUpdate(
    string? Mode = null, int? SessionTtlMinutes = null, string? FallbackGrade = null, double? MinConfidence = null, int? ContextTurns = null, int? MaxChars = null);

/// <summary>Partial update of the settings; <c>Grades</c>, when given, replaces the whole list.</summary>
public sealed record SmartRoutingUpdate(
    bool? Enabled = null, string? VirtualModel = null, SmartRoutingJudgeUpdate? Judge = null,
    List<SmartRoutingGradeUpdate>? Grades = null, SmartRoutingRulesUpdate? Rules = null);

/// <summary>A sample to judge, with the judge and grade settings as they are being edited (unsaved); what is left out is taken from the saved ones.</summary>
public sealed record SmartRoutingTestRequest(string? Text, SmartRoutingJudgeUpdate? Judge = null, List<SmartRoutingGradeUpdate>? Grades = null);

/// <param name="Ok">The judge answered. When it did not, <c>Failure</c> says how and <c>Message</c> what it said.</param>
/// <param name="Confident">The answer's confidence reaches <c>rules.minConfidence</c>: a request would be routed to <c>Model</c> rather than the fallback grade.</param>
/// <param name="LimitMs">How long a request would have waited for this answer: the judge's timeout, plus
/// <see cref="JudgeEndpoint.ConnectAllowance"/> when the call had to open a connection (<c>NewConnection</c>). The test
/// itself waits longer, so an answer slower than this is shown — a request would have used the fallback grade.</param>
public sealed record SmartRoutingTestDto(
    bool Ok, string? Grade, string? GradeName, string? Model, double? Confidence, bool? Confident, Dictionary<string, double>? Probabilities,
    string? JudgeModel, long LatencyMs, int InputTokens, int OutputTokens, string? Failure, string? Message, long LimitMs, bool NewConnection);

/// <summary>Admin API payload: routing model.</summary>
public sealed record RoutingModelDto(string Id, string Name, string ProviderId, string ProviderName);

/// <summary>Admin API payload: routing problem.</summary>
public sealed record RoutingProblemDto(string Code, string Message, string? Field);

/// <summary>The <c>details</c> of a refusal.</summary>
public sealed record RoutingProblemsDetails(List<RoutingProblemDto> Problems);
