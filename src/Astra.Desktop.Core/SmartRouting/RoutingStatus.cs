using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;

namespace Astra.Desktop.Core.SmartRouting;

/// <summary>The switch row's state line: a dot tone and its text (web <c>StatusGroup</c>).</summary>
/// <param name="Tone">neutral (off), orange (breaker open) or green (on).</param>
/// <param name="Text">"Off", "The judge is paused", "On · 2 sessions pinned".</param>
public sealed record RoutingState(PluginTone Tone, string Text);

/// <summary>The warning that most decisions fell back.</summary>
/// <param name="Title">"Most requests used the fallback grade X".</param>
/// <param name="Hint">What to check for the commonest reason.</param>
/// <param name="Last">The latest fallback (reason and time), null when unknown.</param>
public sealed record FailingNotice(string Title, string Hint, string? Last);

/// <summary>A caller that was moved to another grade because none of its providers serves a grade's model.</summary>
/// <param name="Line">The sentence with the count and when.</param>
/// <param name="Fix">What to do about it, null for a reason this build does not know.</param>
public sealed record UnreachableNotice(string Line, string? Fix);

/// <summary>The "recent decisions" part of the status group.</summary>
/// <param name="Detail">"None yet" or "judged 18 of 20 · fallback (timeout) 2 · median 640 ms".</param>
/// <param name="Failing">The warning for mostly-failing decisions, null for none.</param>
/// <param name="Unreachable">The callers moved to another grade, the latest first.</param>
/// <param name="ShowUnreachableHint">At least one reason is unknown to this build: the generic hint follows.</param>
public sealed record RecentDecisionsModel(string Detail, FailingNotice? Failing, IReadOnlyList<UnreachableNotice> Unreachable, bool ShowUnreachableHint);

/// <summary>Computes what the status group shows from the saved settings and their live status.</summary>
public static class RoutingStatus
{
    /// <summary>The state under the switch.</summary>
    public static RoutingState StateOf(SmartRoutingDto saved)
    {
        if (!saved.Enabled) return new RoutingState(PluginTone.Neutral, Loc.T("smartRouting.state.off"));
        if (saved.Status.BreakerOpen) return new RoutingState(PluginTone.Orange, Loc.T("smartRouting.state.breaker"));
        var text = Loc.T("smartRouting.state.on");
        if (saved.Status.Pins > 0) text += " · " + Loc.T("smartRouting.state.pins", ("count", saved.Status.Pins));
        return new RoutingState(PluginTone.Green, text);
    }

    /// <summary>
    /// What the last fresh decisions came to. A fallback never fails a request, so without this nobody sees it: the request just lands
    /// on the fallback grade (web <c>RecentDecisions</c>). <paramref name="now"/> decides what "today" is for the times.
    /// </summary>
    public static RecentDecisionsModel RecentOf(SmartRoutingDto saved, DateTimeOffset now)
    {
        var locale = Loc.Language;
        var recent = saved.Status.Recent ?? new RecentDecisionsDto(0, [], null, null, null);
        var unreachable = saved.Status.Unreachable ?? [];
        string GradeName(string id) => saved.Grades.FirstOrDefault(g => g.Id == id) is { } g ? (g.Name.Length > 0 ? g.Name : g.Id) : id;

        recent.Outcomes.TryGetValue("judge", out var judged);
        var fallbacks = recent.Outcomes.Where(kv => kv.Key != "judge").OrderByDescending(kv => kv.Value).ToList();
        var fell = recent.Total - judged;
        var parts = new List<string> { Loc.T("smartRouting.recent.judged", ("count", judged), ("total", recent.Total)) };
        parts.AddRange(fallbacks.Select(kv => Loc.T("smartRouting.recent.fallback", ("reason", RoutingTexts.ReasonText(kv.Key)), ("count", kv.Value))));
        if (recent.MedianJudgeMs is { } median) parts.Add(Loc.T("smartRouting.recent.median", ("ms", median)));
        var detail = recent.Total == 0 ? Loc.T("smartRouting.recent.none") : string.Join(" · ", parts);

        FailingNotice? failing = null;
        if (fell >= 2 && fell * 2 > recent.Total)
        {
            var commonest = fallbacks.FirstOrDefault().Key;
            var hintKey = "smartRouting.recent.hint." + commonest;
            var last = recent.LastFallbackAt is { } at
                ? Loc.T("smartRouting.recent.last", ("reason", RoutingTexts.ReasonText(recent.LastFallback)), ("time", PluginFormat.Time(at, locale, now)))
                : null;
            failing = new FailingNotice(
                Loc.T("smartRouting.recent.failing", ("grade", GradeName(saved.Rules.FallbackGrade))),
                Loc.T(commonest is not null && Loc.Has(hintKey) ? hintKey : "smartRouting.recent.hint.other"), last);
        }

        var items = new List<UnreachableNotice>();
        foreach (var u in unreachable)
        {
            var reasonKey = RoutingTexts.UnusableKey(u.Reason);
            var when = new (string, object?)[] { ("count", u.Count), ("time", PluginFormat.Time(u.LastAt, locale, now)) };
            var where = string.IsNullOrEmpty(u.ProviderName) ? u.Model : Loc.T("smartRouting.unreachable.where", ("provider", u.ProviderName), ("model", u.Model));
            var head = Loc.T("smartRouting.unreachable.item",
                ("caller", RoutingTexts.ClientName(u.Caller)), ("grade", GradeName(u.Grade)), ("where", where), ("reason", Loc.T(reasonKey ?? "smartRouting.unusable.unknown")));
            var tail = string.IsNullOrEmpty(u.UsedGrade)
                ? Loc.T("smartRouting.unreachable.none", when)
                : Loc.T("smartRouting.unreachable.used", [.. when, ("used", GradeName(u.UsedGrade))]);
            items.Add(new UnreachableNotice($"{head} {tail}", reasonKey is null ? null : Loc.T(reasonKey + ".fix")));
        }
        return new RecentDecisionsModel(detail, failing, items, unreachable.Any(u => RoutingTexts.UnusableKey(u.Reason) is null));
    }
}

/// <summary>One option of a grade's model picker.</summary>
/// <param name="Value">The choice (<see cref="JudgePresets.ChoiceOf"/>), empty for none.</param>
/// <param name="Label">What the list shows.</param>
/// <param name="Detail">The provider's name when the model id is unique.</param>
public sealed record ModelChoice(string Value, string Label, string? Detail);

/// <summary>The models a grade can be given (web <c>GradesGroup</c>).</summary>
public static class RoutingModels
{
    /// <summary>
    /// A choice is a model on one provider: the same id on two providers is two choices (and then says which provider it is in its own
    /// label, since a short list shows no detail).
    /// </summary>
    public static List<ModelChoice> Choices(IReadOnlyList<RoutingModelDto> served)
    {
        var shared = served.GroupBy(m => m.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return served.Select(m => new ModelChoice(
            JudgePresets.ChoiceOf(m.ProviderId, m.Id),
            shared.Contains(m.Id) ? $"{m.Id} · {m.ProviderName}" : m.Id,
            shared.Contains(m.Id) ? null : m.ProviderName)).ToList();
    }

    /// <summary>The options for one grade: the choices, led by the grade's own model when no provider serves it any more (web <c>options</c>).</summary>
    public static List<ModelChoice> OptionsFor(IReadOnlyList<ModelChoice> choices, GradeDraft grade)
    {
        var current = JudgePresets.ChoiceOf(grade.ProviderId, grade.Model);
        if (grade.Model.Length > 0 && choices.All(o => o.Value != current))
            return [new ModelChoice(current, Loc.T("smartRouting.grade.unavailable", ("model", grade.Model)), null), .. choices];
        return [.. choices];
    }
}
