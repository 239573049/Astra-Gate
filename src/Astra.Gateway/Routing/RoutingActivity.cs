using Astra.Core.Routing;

namespace Astra.Gateway.Routing;

/// <summary>One fresh routing decision (not one reused from a pin or a turn): how it ended and what its judge call cost.</summary>
/// <param name="Outcome"><c>judge</c> when the judge's grade was used, else the decision's fallback reason (<c>timeout</c>, <c>breaker</c> …).</param>
/// <param name="JudgeMs">How long the judge call took; 0 when none was made.</param>
/// <param name="NewConnection">The judge call had to open a connection first.</param>
public sealed record RecentDecision(DateTimeOffset At, string Outcome, long JudgeMs, bool NewConnection);

/// <summary>A caller that asked for a grade it cannot use, why, and the grade it was given instead.</summary>
/// <param name="Caller">A client kind, or a token's name for a direct call.</param>
/// <param name="UsedGrade">The grade the requests were given; null when the caller could use no grade at all.</param>
/// <param name="Reason">Why it cannot use <paramref name="Grade"/> (a <see cref="GradeProblems"/> code).</param>
/// <param name="ProviderName">The grade's provider, as the console names it; null when it is gone or the grade names none.</param>
public sealed record UnreachableGrade(
    string Caller, string Grade, string Model, string? UsedGrade, int Count, DateTimeOffset LastAt, string Reason, string? ProviderName);

/// <summary>What <see cref="RoutingActivity"/> holds at one moment, for the current settings.</summary>
public sealed record RoutingActivitySnapshot(IReadOnlyList<RecentDecision> Decisions, IReadOnlyList<UnreachableGrade> Unreachable);

/// <summary>
/// What smart routing has been doing lately, so the console can show it (plan §5.9): the last fresh decisions — whether
/// the judge answered, how fast, why it fell back — and the callers moved to another grade because they cannot reach a
/// grade's model. A fallback is otherwise silent: the request still succeeds, on a grade nobody chose. In memory only (a
/// restart forgets it, like the pins), and only under the settings in force: saving new ones starts it afresh.
/// </summary>
public sealed class RoutingActivity(TimeProvider clock)
{
    /// <summary>How many fresh decisions are kept.</summary>
    public const int Window = 50;

    private const int MaxUnreachable = 20;

    /// <summary>A caller that stops asking for the grade it cannot reach is forgotten after this long.</summary>
    private static readonly TimeSpan UnreachableTtl = TimeSpan.FromHours(1);

    private readonly Lock _lock = new();
    private readonly Queue<RecentDecision> _decisions = new();
    private readonly Dictionary<(string Caller, string Grade), UnreachableGrade> _unreachable = new();
    private int _generation = -1;

    /// <summary>Records a decision that was made afresh under settings <paramref name="generation"/>.</summary>
    public void Decided(RoutingDecision decision, int generation)
    {
        var entry = new RecentDecision(clock.GetUtcNow(), decision.Source == RoutingSources.Judge ? RoutingSources.Judge : decision.FallbackReason ?? decision.Source,
            decision.JudgeMs, decision.JudgeNewConnection);
        lock (_lock)
        {
            if (!Use(generation)) return;
            _decisions.Enqueue(entry);
            while (_decisions.Count > Window) _decisions.Dequeue();
        }
    }

    /// <summary>Records that <paramref name="caller"/> cannot use <paramref name="wanted"/> and why, and the grade it was given (null: none).</summary>
    public void Unreachable(string caller, RoutingGrade wanted, RoutingGrade? used, GradeProblem problem, int generation)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (!Use(generation)) return;
            var key = (caller, wanted.Id);
            var seen = _unreachable.GetValueOrDefault(key);
            if (seen is null && _unreachable.Count >= MaxUnreachable)
                _unreachable.Remove(_unreachable.MinBy(e => e.Value.LastAt).Key);
            _unreachable[key] = new UnreachableGrade(caller, wanted.Id, wanted.Model, used?.Id, (seen?.Count ?? 0) + 1, now, problem.Code, problem.ProviderName);
        }
    }

    /// <summary>Records that <paramref name="caller"/> could use grade <paramref name="grade"/> as decided: a problem reported for it is over.</summary>
    public void Reached(string caller, string grade, int generation)
    {
        lock (_lock)
        {
            if (_generation == generation) _unreachable.Remove((caller, grade));
        }
    }

    /// <summary>What happened under settings <paramref name="generation"/>, oldest decision first; the most recent problems first.</summary>
    public RoutingActivitySnapshot Snapshot(int generation)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (_generation != generation) return new RoutingActivitySnapshot([], []);
            var unreachable = _unreachable.Values.Where(u => now - u.LastAt <= UnreachableTtl).OrderByDescending(u => u.LastAt).ToList();
            return new RoutingActivitySnapshot(_decisions.ToList(), unreachable);
        }
    }

    /// <summary>
    /// Starts afresh when the settings changed — what happened under the old ones does not describe the new ones — and
    /// refuses a late report from older settings (a judgement still running when they were replaced).
    /// </summary>
    private bool Use(int generation)
    {
        if (generation < _generation) return false;
        if (generation == _generation) return true;
        _generation = generation;
        _decisions.Clear();
        _unreachable.Clear();
        return true;
    }
}
