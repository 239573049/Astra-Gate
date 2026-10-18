using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Requests;
using Astra.Core.Routing;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Tracing;
using Microsoft.Extensions.Logging;

namespace Astra.Gateway.Routing;

/// <summary>What the router is asked about: the caller, the request as the client sent it (already guarded), and where it sits in its agent's loop.</summary>
/// <param name="TokenId">The calling token: a session id is only unique among one token's requests.</param>
/// <param name="Body">The client's body in its own protocol, after the privacy guard.</param>
public sealed record RoutingInput(string TokenId, ApiProtocol Inbound, JsonObject Body, TraceLink Link)
{
    /// <summary>Who is asking, as the console names it: a client kind, or the token's name for a direct call.</summary>
    public string? Caller { get; init; }

    /// <summary>
    /// Why this caller cannot use a grade — reach the provider it names, which serves its model
    /// (<see cref="GatewayRouter.GradeUseAsync"/>) — or null when it can; the delegate itself null = not checked. A grade
    /// the caller cannot use is replaced by the nearest one it can (<see cref="RoutingDecision.AdjustedFrom"/>).
    /// </summary>
    public Func<RoutingGrade, CancellationToken, Task<GradeProblem?>>? ProblemOf { get; init; }
}

/// <summary>
/// Decides which grade — and so which model — a request to the virtual model goes to (plan §5.4). When it is asked
/// depends on <see cref="RoutingRules.Mode"/>: once per session, or once per user turn. A decision is kept in memory
/// (a session's pin; a turn's grade for the requests that only answer its tool calls) and dropped when the settings
/// change. A judge that fails, is not confident enough or has nothing to read never fails the request: the fallback
/// grade is used, and a fallback is never pinned. Nor is a session held on the lowest grade: a session whose turns so far
/// were all judged lowest is judged again on its next turn. A grade whose model the caller cannot reach gives way to the
/// nearest one it can. What the router did lately is kept for the console (<see cref="Activity"/>).
/// </summary>
public sealed class SmartRouter(
    SmartRoutingService settings, RoutingJudgeRegistry judges, ISecretProtector secrets, ILogger<SmartRouter> logger,
    TimeProvider? timeProvider = null)
{
    private const int PinCapacity = 10_000;
    private const int TurnCapacity = 4_096;
    private static readonly TimeSpan TurnTtl = TimeSpan.FromMinutes(30);
    private const int BreakerThreshold = 3;
    private static readonly TimeSpan BreakerCooldown = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly DecisionStore _pins = new(timeProvider ?? TimeProvider.System, PinCapacity);
    private readonly DecisionStore _turns = new(timeProvider ?? TimeProvider.System, TurnCapacity);
    private readonly Breaker _breaker = new(timeProvider ?? TimeProvider.System);
    private readonly RoutingActivity _activity = new(timeProvider ?? TimeProvider.System);
    private readonly ConcurrentDictionary<string, Lazy<Task<RoutingDecision>>> _flights = new(StringComparer.Ordinal);

    /// <summary>Sessions currently holding a pinned grade.</summary>
    public int PinCount => _pins.Count;

    /// <summary>Whether the judge is being left alone because it kept failing.</summary>
    public bool BreakerOpen => _breaker.IsOpen;

    /// <summary>The last fresh decisions and the callers moved off a grade they cannot reach, under the current settings.</summary>
    public RoutingActivitySnapshot Activity => _activity.Snapshot(settings.Snapshot.Generation);

    /// <summary>Whether <paramref name="model"/> is the virtual model, switched on or not.</summary>
    public bool IsVirtual(string model) => settings.IsVirtual(model);

    /// <summary>The virtual model's id while smart routing is on — what the model lists offer clients; null while it is off.</summary>
    public string? AdvertisedModel => settings.Current is { Enabled: true } s ? s.VirtualModel : null;

    /// <summary>
    /// The model of the fallback grade, with no judging at all: for the places that need only a model (token counting).
    /// </summary>
    /// <exception cref="GatewayException">Smart routing is switched off.</exception>
    public RoutedModel FallbackModel(string virtualModel)
    {
        var s = settings.Current;
        if (!s.Enabled) throw Disabled(s.VirtualModel);
        var grade = FallbackGradeOf(s);
        return new RoutedModel(virtualModel, grade.Model, grade.Id, grade.ProviderId);
    }

    /// <exception cref="GatewayException">Smart routing is switched off.</exception>
    public async Task<RoutingDecision> DecideAsync(RoutingInput input, CancellationToken ct)
    {
        var snapshot = settings.Snapshot;
        var s = snapshot.Settings;
        if (!s.Enabled) throw Disabled(s.VirtualModel);
        return await ReachableAsync(snapshot, await GradeAsync(snapshot, input, ct), input, ct);
    }

    /// <summary>The grade of this request: a session's or turn's decision when there is one to keep, else a fresh one.</summary>
    private async Task<RoutingDecision> GradeAsync(SmartRoutingSnapshot snapshot, RoutingInput input, CancellationToken ct)
    {
        var s = snapshot.Settings;
        var rules = s.Rules;
        var generation = snapshot.Generation;

        // A Responses request chained by previous_response_id continues state the first provider holds: it must not move.
        var chained = input.Inbound == ApiProtocol.OpenAIResponses
                      && input.Body["previous_response_id"] is JsonValue previous && previous.TryGetValue<string>(out var previousId) && previousId.Length > 0;
        var sessionKey = input.Link.SessionKey is { } session ? $"{input.TokenId}\n{session}" : null;
        // A side call — another chain of the session that is no sub-agent of the main turn (TraceLinker) — is a task of its
        // own: it takes nothing from the main turn's decision, leaves none in its place and never pins the session.
        // A sub-agent shares the main turn's id and so follows its model.
        var sideCall = input.Link.CallKind == TraceCallKinds.Aux;
        var turnKey = sideCall ? $"{input.TokenId}\n{input.Link.TurnId}\n{input.Link.ChainKey}" : $"{input.TokenId}\n{input.Link.TurnId}";
        var newTurn = input.Link.LinkSource == TraceLinkSources.NewTurn;

        var pinned = sessionKey is not null && (rules.Mode == RoutingModes.Session || chained) ? _pins.Get(sessionKey, generation, slide: true) : null;
        if (chained && pinned is not null) return pinned.Reused(RoutingSources.Pin, rules.Mode);
        // A request that only answers tool calls belongs to a turn already decided: no model change in the middle of it.
        if (!newTurn && _turns.Get(turnKey, generation, slide: false) is { } turn) return turn.Reused(RoutingSources.Turn, rules.Mode);
        // The session keeps its grade — unless that is the lowest one. A session whose turns so far were all judged lowest
        // has asked only for small things (a greeting, "1+1"): its next turn is judged again, so the real task that often
        // follows is not held on the cheapest model for the rest of the session.
        if (pinned is not null && (!newTurn || pinned.GradeId != s.Grades[0].Id)) return pinned.Reused(RoutingSources.Pin, rules.Mode);

        // Concurrent first requests of one session share one judgement; a turn has no such thing to share.
        var flightKey = rules.Mode == RoutingModes.Session && sessionKey is not null ? sessionKey : turnKey;
        var decision = await DecideFreshAsync(s, generation, input, flightKey, ct);
        // The turn keeps even a fallback (its later requests must not switch); only a confident judgement pins the session.
        _turns.Set(turnKey, decision, generation, TurnTtl);
        if (decision.Source == RoutingSources.Judge && sessionKey is not null && !sideCall)
            _pins.Set(sessionKey, decision, generation, TimeSpan.FromMinutes(rules.SessionTtlMinutes));
        return decision;
    }

    private async Task<RoutingDecision> DecideFreshAsync(SmartRoutingSettings s, int generation, RoutingInput input, string flightKey, CancellationToken ct)
    {
        var ask = AskExtractor.Extract(input.Inbound, input.Body, s.Rules.ContextTurns, s.Rules.MaxChars);
        if (ask is null) return Recorded(Fallback(s, "no_text", RoutingSources.Fallback), generation);
        if (_breaker.IsOpen) return Recorded(Fallback(s, "breaker", RoutingSources.Breaker), generation);
        var flight = _flights.GetOrAdd(flightKey, key => new Lazy<Task<RoutingDecision>>(() => JudgeAsync(s, generation, ask, key)));
        // The judgement does not belong to the request that started it: it is not cancelled when that client goes away.
        return await flight.Value.WaitAsync(ct);
    }

    /// <summary>One judgement, shared by the requests waiting for it and recorded once.</summary>
    private async Task<RoutingDecision> JudgeAsync(SmartRoutingSettings s, int generation, Ask ask, string flightKey)
    {
        try
        {
            return Recorded(await AskJudgeAsync(s, ask), generation);
        }
        finally
        {
            _flights.TryRemove(flightKey, out _);
        }
    }

    private async Task<RoutingDecision> AskJudgeAsync(SmartRoutingSettings s, Ask ask)
    {
        var protocol = s.Judge.Protocol;
        var started = _clock.GetTimestamp();
        var connecting = false;
        try
        {
            if (string.IsNullOrEmpty(s.Judge.ApiKeyProtected)) return Fallback(s, "not_configured", RoutingSources.Fallback);
            string key;
            try
            {
                key = secrets.Unprotect(s.Judge.ApiKeyProtected);
            }
            catch (CryptographicException)
            {
                return Fallback(s, "not_configured", RoutingSources.Fallback);
            }
            var question = QuestionFor(s, ask);
            var endpoint = new JudgeEndpoint(s.Judge.EffectiveBaseUrl, key, s.Judge.Model, TimeSpan.FromMilliseconds(s.Judge.TimeoutMs))
            {
                OnNewConnection = () => connecting = true,
            };

            JudgeVerdict verdict;
            try
            {
                verdict = await judges.Require(protocol).JudgeAsync(endpoint, question, CancellationToken.None);
            }
            catch (RoutingJudgeException e)
            {
                _breaker.Failure();
                // The kind and the status only: the message may quote what the service said.
                logger.LogWarning("Smart routing: the {Protocol} judge failed ({Kind}, HTTP {Status}, new connection: {NewConnection}); using the fallback grade",
                    protocol, e.Kind, e.Status, connecting);
                return WithJudge(Fallback(s, FailureName(e.Kind), RoutingSources.Fallback), protocol, null, started, 0, 0, connecting);
            }

            _breaker.Success();
            var grade = s.Grades.First(g => g.Id == verdict.Choice);
            if (verdict.Confidence < s.Rules.MinConfidence)
                return WithJudge(Fallback(s, "low_confidence", RoutingSources.Fallback) with { Confidence = verdict.Confidence },
                    protocol, verdict.JudgeModel, started, verdict.InputTokens, verdict.OutputTokens, connecting);
            return WithJudge(new RoutingDecision(grade.Id, grade.Model, RoutingSources.Judge, s.Rules.Mode, verdict.Confidence, ProviderId: grade.ProviderId),
                protocol, verdict.JudgeModel, started, verdict.InputTokens, verdict.OutputTokens, connecting);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Smart routing: unexpected failure while judging; using the fallback grade");
            return Fallback(s, "error", RoutingSources.Fallback);
        }
    }

    private RoutingDecision Recorded(RoutingDecision decision, int generation)
    {
        _activity.Decided(decision, generation);
        return decision;
    }

    /// <summary>
    /// The decision as this caller can use it (plan §7 risk 7). A grade's requests go to the provider it names, but not
    /// every caller may go there (a token limited to some clients keeps to its own providers), and a provider can stop
    /// serving the model; sending the request anyway would reach one that answers "no such model". Such a decision moves to
    /// the nearest grade the caller can use — a stronger one first: the judge named the cheapest grade that does the work
    /// well, so a weaker one is the last resort — and the move is recorded with its reason (<see cref="RoutingDecision.AdjustedFrom"/>,
    /// <see cref="Activity"/>). When it can use none of them, the decision stands, and that is recorded too. The session
    /// and turn keep the decision from before the move, so a change of provider or token counts at once.
    /// </summary>
    private async Task<RoutingDecision> ReachableAsync(SmartRoutingSnapshot snapshot, RoutingDecision decision, RoutingInput input, CancellationToken ct)
    {
        var grades = snapshot.Settings.Grades;
        var at = grades.FindIndex(g => g.Id == decision.GradeId);
        if (input.ProblemOf is not { } problemOf || at < 0) return decision;
        var caller = input.Caller ?? input.TokenId;
        if (await problemOf(grades[at], ct) is not { } problem)
        {
            _activity.Reached(caller, decision.GradeId, snapshot.Generation);
            return decision;
        }
        foreach (var i in Enumerable.Range(at + 1, grades.Count - at - 1).Concat(Enumerable.Range(0, at).Reverse()))
        {
            if (await problemOf(grades[i], ct) is not null) continue;
            _activity.Unreachable(caller, grades[at], grades[i], problem, snapshot.Generation);
            return decision with
            {
                GradeId = grades[i].Id, Model = grades[i].Model, ProviderId = grades[i].ProviderId, AdjustedFrom = decision.GradeId,
                AdjustedReason = problem.Code,
            };
        }
        _activity.Unreachable(caller, grades[at], null, problem, snapshot.Generation);
        return decision;
    }

    /// <summary>The question the judge is asked about <paramref name="ask"/> under <paramref name="s"/>: the grades are its options, cheapest first.</summary>
    public static JudgeQuestion QuestionFor(SmartRoutingSettings s, Ask ask) => new(
        s.Judge.Instructions ?? SmartRoutingSettings.DefaultInstructions,
        s.Grades.Select(g => new JudgeChoice(g.Id, g.Description)).ToList(),
        new JudgeState(ask.Request, ask.Earlier, ask.Shape));

    private RoutingDecision WithJudge(
        RoutingDecision decision, string protocol, string? judgeModel, long started, int inputTokens, int outputTokens, bool newConnection) =>
        decision with
        {
            JudgeProtocol = protocol, JudgeModel = judgeModel, JudgeMs = (long)_clock.GetElapsedTime(started).TotalMilliseconds,
            JudgeInputTokens = inputTokens, JudgeOutputTokens = outputTokens, JudgeNewConnection = newConnection,
        };

    private static RoutingDecision Fallback(SmartRoutingSettings s, string reason, string source)
    {
        var grade = FallbackGradeOf(s);
        return new RoutingDecision(grade.Id, grade.Model, source, s.Rules.Mode, FallbackReason: reason, ProviderId: grade.ProviderId);
    }

    private static RoutingGrade FallbackGradeOf(SmartRoutingSettings s) =>
        s.Grades.FirstOrDefault(g => g.Id == s.Rules.FallbackGrade) ?? s.Grades[s.Grades.Count / 2];

    /// <summary>A failure kind as it is recorded and shown: <c>timeout</c>, <c>rate_limited</c>, <c>bad_response</c> …</summary>
    public static string FailureName(JudgeFailure kind) => kind switch
    {
        JudgeFailure.Timeout => "timeout",
        JudgeFailure.Network => "network",
        JudgeFailure.Auth => "auth",
        JudgeFailure.RateLimited => "rate_limited",
        JudgeFailure.Overloaded => "overloaded",
        JudgeFailure.BadRequest => "bad_request",
        _ => "bad_response",
    };

    private static GatewayException Disabled(string model) =>
        new(400, "invalid_request_error", $"智能路由未启用：模型 {model} 需要先在 Astra 的「智能路由」页面完成配置并启用。");

    /// <summary>
    /// Leaves a judge alone for a while once it has failed several times in a row, so a service that is down costs the
    /// first requests a timeout and not every request. After the cooldown the next failure closes it again at once.
    /// </summary>
    private sealed class Breaker(TimeProvider clock)
    {
        private readonly Lock _lock = new();
        private int _failures;
        private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

        public bool IsOpen
        {
            get
            {
                lock (_lock) return clock.GetUtcNow() < _openUntil;
            }
        }

        public void Success()
        {
            lock (_lock)
            {
                _failures = 0;
                _openUntil = DateTimeOffset.MinValue;
            }
        }

        public void Failure()
        {
            lock (_lock)
            {
                if (++_failures < BreakerThreshold) return;
                _openUntil = clock.GetUtcNow() + BreakerCooldown;
                _failures = BreakerThreshold - 1;
            }
        }
    }
}
