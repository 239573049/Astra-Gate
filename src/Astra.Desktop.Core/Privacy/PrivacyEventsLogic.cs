using System.Globalization;
using Astra.Api.Contracts;
using Astra.Core.Privacy;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Settings;

namespace Astra.Desktop.Core.Privacy;

/// <summary>The four filters of the interception log (web <c>FilterId</c>).</summary>
public enum PrivacyFilter { Client, Category, Action, Blocked }

/// <summary>What happened to a request, for the outcome column.</summary>
public enum PrivacyOutcome { Blocked, Redacted, DryRun, Warned }

/// <summary>One row of the interception log table.</summary>
/// <param name="Id">The request id.</param>
/// <param name="Time">When it started.</param>
/// <param name="Client">The client's display name.</param>
/// <param name="Model">The model the client asked for ("" when unknown).</param>
/// <param name="Outcome">What the guard did.</param>
/// <param name="Hits">Total matches over all rules.</param>
/// <param name="Event">The event the row shows.</param>
public sealed record PrivacyEventRow(string Id, DateTimeOffset Time, string Client, string Model, PrivacyOutcome Outcome, int Hits, PrivacyEventDto Event);

/// <summary>
/// The interception log's filter state, query and rows (web/src/components/PrivacyEventLogs.tsx): the model search, the four filters,
/// the page. Filters and the search reset the page to 1 (like the web); the query string is what GET /api/privacy/events takes.
/// </summary>
public sealed class PrivacyEventFilters
{
    /// <summary>Rows per page.</summary>
    public const int PageSize = 50;

    private readonly Dictionary<PrivacyFilter, string> _values = new()
    {
        [PrivacyFilter.Client] = "",
        [PrivacyFilter.Category] = "",
        [PrivacyFilter.Action] = "",
        [PrivacyFilter.Blocked] = "",
    };

    /// <summary>The (debounced) model search.</summary>
    public string Model { get; private set; } = "";

    /// <summary>The current page, 1-based.</summary>
    public int Page { get; private set; } = 1;

    /// <summary>The value chosen for a filter ("" = none).</summary>
    public string this[PrivacyFilter filter] => _values[filter];

    /// <summary>Whether any filter is set.</summary>
    public bool Any => _values.Values.Any(v => v != "");

    /// <summary>Sets or clears one filter and goes back to page 1.</summary>
    public void Set(PrivacyFilter filter, string value)
    {
        _values[filter] = value;
        Page = 1;
    }

    /// <summary>Clears every filter (the search stays) and goes back to page 1.</summary>
    public void ClearAll()
    {
        foreach (var k in _values.Keys.ToArray()) _values[k] = "";
        Page = 1;
    }

    /// <summary>Changes the model search and goes back to page 1.</summary>
    public void SetModel(string model)
    {
        Model = model;
        Page = 1;
    }

    /// <summary>Goes to a page.</summary>
    public void GoTo(int page) => Page = Math.Max(1, page);

    /// <summary>The query key parts, so two states with the same filters share one cached request.</summary>
    public QueryKey Key => QueryKey.Of("privacy-events", Model, _values[PrivacyFilter.Client], _values[PrivacyFilter.Category],
        _values[PrivacyFilter.Action], _values[PrivacyFilter.Blocked], Page.ToString(CultureInfo.InvariantCulture));

    /// <summary>The request path with its query string (empty values left out).</summary>
    public string Path => "/api/privacy/events" + ApiClient.Qs(
        ("model", Model), ("client", _values[PrivacyFilter.Client]), ("category", _values[PrivacyFilter.Category]),
        ("action", _values[PrivacyFilter.Action]), ("blocked", _values[PrivacyFilter.Blocked]),
        ("page", Page), ("pageSize", PageSize));

    /// <summary>Whether the first page is shown, which is the only one that follows new events (polls every 5 s).</summary>
    public bool Live => Page == 1;

    /// <summary>The number of pages for <paramref name="total"/> events.</summary>
    public static int Pages(long total) => Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));

    /// <summary>The label of a filter.</summary>
    public static Msg Label(PrivacyFilter filter) => new(filter switch
    {
        PrivacyFilter.Client => "requests.col.client",
        PrivacyFilter.Category => "privacy.stats.col.category",
        PrivacyFilter.Action => "privacy.logs.col.action",
        _ => "privacy.logs.col.outcome",
    });

    /// <summary>The filter id the toolbar uses for a filter.</summary>
    public static string Id(PrivacyFilter filter) => filter switch
    {
        PrivacyFilter.Client => "client",
        PrivacyFilter.Category => "category",
        PrivacyFilter.Action => "action",
        _ => "blocked",
    };

    /// <summary>The filter for a toolbar id.</summary>
    public static PrivacyFilter FromId(string id) => id switch
    {
        "client" => PrivacyFilter.Client,
        "category" => PrivacyFilter.Category,
        "action" => PrivacyFilter.Action,
        _ => PrivacyFilter.Blocked,
    };

    /// <summary>The options of a filter: clients (kind, name), the guard's categories, the actions, blocked / passed.</summary>
    public static IReadOnlyList<Option> Options(PrivacyFilter filter, IEnumerable<(string Kind, string Name)> clients, IEnumerable<string> categories)
        => filter switch
        {
            PrivacyFilter.Client => clients.Select(c => new Option(c.Kind, c.Name)).ToList(),
            PrivacyFilter.Category => categories.Select(c => new Option(c, PrivacyMeta.CategoryLabel(c))).ToList(),
            PrivacyFilter.Action => PrivacyMeta.ActiveActions.Select(a => new Option(a, Core.I18n.Loc.T("settings.privacy.action." + a))).ToList(),
            _ => [new Option("true", Core.I18n.Loc.T("privacy.logs.blockedOnly")), new Option("false", Core.I18n.Loc.T("privacy.logs.passedOnly"))],
        };
}

/// <summary>Rows and cells of the interception log.</summary>
public static class PrivacyEventRows
{
    /// <summary>What the guard did to a request: blocked, redacted, only detected (dry run) or warned.</summary>
    public static PrivacyOutcome OutcomeOf(PrivacyEventDto e)
        => e.Blocked ? PrivacyOutcome.Blocked : e.Redactions > 0 ? PrivacyOutcome.Redacted : e.DryRun ? PrivacyOutcome.DryRun : PrivacyOutcome.Warned;

    /// <summary>The outcome badge: its text and tone.</summary>
    public static (Msg Text, string Tone) OutcomeBadge(PrivacyEventDto e) => OutcomeOf(e) switch
    {
        PrivacyOutcome.Blocked => (new Msg("privacy.logs.outcome.blocked"), "red"),
        PrivacyOutcome.Redacted => (Msg.Of("privacy.logs.outcome.redacted", ("count", e.Redactions)), "orange"),
        PrivacyOutcome.DryRun => (new Msg("privacy.logs.outcome.dryRun"), "neutral"),
        _ => (new Msg("settings.privacy.action.warn"), "neutral"),
    };

    /// <summary>The table rows of a page.</summary>
    public static IReadOnlyList<PrivacyEventRow> Rows(IEnumerable<PrivacyEventDto> items)
        => items.Select(e => new PrivacyEventRow(e.Id, e.StartedAtUtc, ClientNames.Of(e.ClientKind), e.RequestedModel ?? "", OutcomeOf(e),
            e.Hits.Sum(h => h.Count), e)).ToList();

    /// <summary>The hits cell: the first two hits, then "+N more".</summary>
    public static (IReadOnlyList<PrivacyHit> Shown, int More) HitsSummary(PrivacyEventDto e)
    {
        var shown = e.Hits.Take(2).ToList();
        return (shown, e.Hits.Count - shown.Count);
    }

    /// <summary>The hit's text: category and count.</summary>
    public static string HitText(PrivacyHit h) => $"{PrivacyMeta.CategoryLabel(h.Category)} × {h.Count}";

    /// <summary>The hit's masked samples (the log shows two, the tester all).</summary>
    public static string Samples(PrivacyHit h, int? max = null) => string.Join("  ", max is { } m ? h.Samples.Take(m) : h.Samples);

    /// <summary>The dry-run header badges: would block / would redact N / no hits.</summary>
    public static IReadOnlyList<(Msg Text, string Tone)> DryRunBadges(PrivacyDryRunResultDto r)
    {
        var list = new List<(Msg, string)>();
        if (r.Blocked) list.Add((new Msg("settings.privacy.wouldBlock"), "red"));
        if (r.WouldRedact > 0) list.Add((Msg.Of("settings.privacy.wouldRedact", ("count", r.WouldRedact)), "orange"));
        if (!r.Blocked && r.WouldRedact == 0) list.Add((new Msg("settings.privacy.noHits"), "green"));
        return list;
    }
}

/// <summary>The overview's statistics table (web <c>PrivacyOverview</c>).</summary>
public static class PrivacyStatsLogic
{
    /// <summary>The ranges, in the web's order.</summary>
    public static readonly string[] Ranges = ["today", "7d", "30d", "all"];

    /// <summary>The range the overview opens on.</summary>
    public const string DefaultRange = "7d";

    /// <summary>The category rows sorted by hits, descending (the table's default sort).</summary>
    public static IReadOnlyList<PrivacyCategoryStatDto> ByHits(IEnumerable<PrivacyCategoryStatDto> rows)
        => rows.OrderByDescending(r => r.Hits).ToList();
}
