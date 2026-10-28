using Astra.Api.Contracts;
using Astra.Desktop.Components.Layout;
using Astra.Desktop.Components.Requests;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Overview;

/// <summary>
/// The dashboard (web <c>OverviewPage</c>): a client filter, a token filter and a range selector over an activity heatmap, six number
/// cards, a usage trend and a breakdown table of recent requests, providers, models and tokens. The page owns the filters; every
/// section watches its own query (<c>QueryHost</c>) and asks this model for the query of the current filters, so changing a filter
/// swaps queries instead of refilling data. The data shaping lives in <c>Astra.Desktop.Core.Overview</c>.
/// </summary>
public sealed class OverviewPage : PageViewModel
{
    /// <summary>The recent-requests table shows this many rows (web <c>pageSize: 20</c>).</summary>
    public const int RecentRows = 20;

    /// <summary>The heatmap's own window in days, whatever the range selector says (web <c>ACTIVITY_DAYS</c>).</summary>
    public const int ActivityDays = 365;

    private readonly PageContext _ctx;
    private readonly StatsQueries _stats;
    private readonly ListQueries _lists;
    private string _range = StatsRange.Today;
    private string? _client;
    private string _token = "";
    private TrendMeasure _measure = TrendMeasure.Requests;
    private string _section = "requests";
    private readonly ArcButton _refresh;
    private readonly Dictionary<QueryBase, Func<bool>> _statsQueries = [];
    private bool _fetching;
    private Query<List<ProviderDto>>? _providers;
    private RequestDetailViewModel? _detail;

    /// <summary>Creates the page over the app context.</summary>
    public OverviewPage(PageContext context, string? param) : base(AppRoute.Overview, param, context)
    {
        _ctx = context;
        var now = Now;
        _stats = new StatsQueries(context.Api, context.Queries, now.OffsetMinutes);
        _lists = new ListQueries(context.Api, context.Queries);
        Observe(context.Tokens.Tokens);
        Observe(context.Settings.Settings);
        Clients = _lists.Clients();
        Observe(Clients);
        var refresh = _refresh = new ArcButton
        {
            Variant = ButtonVariant.Ghost,
            Content = new LucideIcon { Kind = LucideIconKind.RefreshCw, Size = 16 },
            Width = 32,
            Height = 32,
            Padding = new Avalonia.Thickness(0),
        };
        Avalonia.Automation.AutomationProperties.SetName(refresh, Loc.T("common.refresh"));
        refresh.Click += (_, _) => Refresh();
        SetHeaderActions(new ActionGroup(8, refresh));
    }

    /// <summary>Filters and refresh changed: sections ask for their queries again.</summary>
    public event Action? FiltersChanged;

    /// <summary>The page's wall clock in the user's zone (the page clock; the visual tests freeze it).</summary>
    public LocalTime Now => LocalTime.Of(_ctx.Clock);

    /// <summary>The clients (the filter offers the installed ones).</summary>
    public Query<List<ClientInfoDto>> Clients { get; }

    /// <summary>The tokens (the token filter's options).</summary>
    public Query<List<TokenDto>> Tokens => _ctx.Tokens.Tokens;

    /// <summary>The server settings (the gateway address shown in the "no requests yet" state).</summary>
    public Query<SettingsDto> Settings => _ctx.Settings.Settings;

    /// <summary>today | 7d | 30d | 90d.</summary>
    public string Range => _range;

    /// <summary>The selected client kind, null for all.</summary>
    public string? ClientKind => _client;

    /// <summary>The selected token id, "" for all.</summary>
    public string Token => _token;

    /// <summary>The token id as the API takes it: null for all.</summary>
    public string? TokenId => _token.Length == 0 ? null : _token;

    /// <summary>The plotted measure.</summary>
    public TrendMeasure Measure => _measure;

    /// <summary>The breakdown tab: requests | providers | models | tokens.</summary>
    public string Section => _section;

    /// <summary>The name of the selected token for the empty state (falls back to its id).</summary>
    public string TokenName => Tokens.Data?.FirstOrDefault(t => t.Id == TokenId)?.Name ?? TokenId ?? "";

    /// <summary>Selects a range.</summary>
    public void SetRange(string range) => Change(() => _range = range);

    /// <summary>Selects a client ("all" = every client).</summary>
    public void SetClient(string client) => Change(() => _client = client == "all" ? null : client);

    /// <summary>Selects a token ("" = all).</summary>
    public void SetToken(string token) => Change(() => _token = token);

    /// <summary>Selects the plotted measure; only the chart reacts.</summary>
    public void SetMeasure(TrendMeasure measure)
    {
        _measure = measure;
        MeasureChanged?.Invoke();
    }

    /// <summary>The measure changed.</summary>
    public event Action? MeasureChanged;

    /// <summary>Selects the breakdown tab.</summary>
    public void SetSection(string section) => _section = section;

    /// <summary>The refresh button / menu command: stats and request lists are fetched again (web <c>invalidateQueries</c> ['stats'] and ['requests']).</summary>
    public void Refresh()
    {
        _ctx.Queries.Invalidate("stats");
        _ctx.Queries.Invalidate("requests");
    }

    /// <summary>Opens the request log (the "view all" link).</summary>
    public void OpenRequests() => _ctx.Navigator.Navigate("Requests");

    /// <summary>The open request's detail (the sheet over the page), or null.</summary>
    public RequestDetailViewModel? Detail => _detail;

    /// <summary>The detail sheet opened or closed.</summary>
    public event Action? DetailChanged;

    /// <summary>What the detail sheet's cells need: the page clock and the provider list (icons).</summary>
    public RequestTableContext TableContext => new() { Clock = _ctx.Clock, Provider = id => id is null ? null : _providers?.Data?.FirstOrDefault(p => p.Id == id) };

    /// <summary>
    /// A recent-requests row: the request's detail opens in a sheet over the dashboard (web <c>RequestDetailSheet</c> inside
    /// <c>Breakdown</c>), not in the request log. The provider list behind its icons is watched from the first opening on.
    /// </summary>
    public void OpenRequest(string id)
    {
        if (_providers is null)
        {
            _providers = _lists.Providers();
            Observe(_providers);
        }
        _detail?.Dispose();
        _detail = new RequestDetailViewModel(_ctx, id);
        DetailChanged?.Invoke();
    }

    /// <summary>The sheet closed: the detail stops being watched (web <c>gcTime: 0</c>).</summary>
    public void CloseDetail()
    {
        if (_detail is null) return;
        _detail.Dispose();
        _detail = null;
        DetailChanged?.Invoke();
    }

    /// <summary>"查看链路" in the detail sheet: the request's span tree.</summary>
    public void OpenTrace(string id)
    {
        CloseDetail();
        _ctx.Navigator.Push(nameof(AppRoute.Traces), Astra.Desktop.Core.Traces.TraceRoutes.Request(id).Path);
    }

    /// <summary>Whether a stats query is fetching (web <c>useIsFetching(['stats'])</c>: the refresh icon turns).</summary>
    public bool IsFetching => _fetching;

    /// <summary>The summary of the current filters.</summary>
    public Query<StatsSummaryDto> Summary() => Track(_stats.Summary(_range, _client, TokenId));

    /// <summary>The per-provider series (the chart sums it; the provider tab reuses it).</summary>
    public Query<List<TimeseriesPointDto>> ByProvider() => Track(_stats.Timeseries(_range, StatsRange.GroupBy(_range), "provider", _client, TokenId));

    /// <summary>The per-token series of the tokens tab.</summary>
    public Query<List<TimeseriesPointDto>> ByToken() => Track(_stats.Timeseries(_range, StatsRange.GroupBy(_range), "token", _client, TokenId));

    /// <summary>The heatmap's year of daily counts.</summary>
    public Query<List<DailyActivityDto>> Activity() => Track(_stats.Activity(ActivityDays, _client, TokenId));

    /// <summary>The top models.</summary>
    public Query<List<TopModelDto>> TopModels() => Track(_stats.TopModels(_range, _client, 20, TokenId));

    /// <summary>The latest requests of the current filters (<c>from</c> = the range's start).</summary>
    public Query<RequestPageDto> Requests()
    {
        var from = StatsMath.RangeStart(_range, Now);
        // The range's local midnight as an instant (web: rangeStart(range)?.toISOString()).
        var iso = from is { } d
            ? new DateTimeOffset(d, TimeSpan.FromMinutes(Now.OffsetMinutes)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return _stats.Requests(new RequestListQuery(_client, TokenId, iso, 1, RecentRows));
    }

    /// <inheritdoc />
    public override async Task WhenSettledAsync(CancellationToken ct = default)
    {
        // Sections create their dependent queries as data arrives: wait until a quiet moment survives a couple of turns.
        for (var i = 0; i < 3; i++)
        {
            await _ctx.Queries.WhenIdleAsync(ct: ct);
            await Task.Delay(40, ct);
        }
    }

    /// <inheritdoc />
    protected override void OnDeactivated() => CloseDetail();

    /// <summary>Follows a stats query's fetching state for the refresh icon; the page's stats queries are the web's ['stats'] prefix.</summary>
    private Query<T> Track<T>(Query<T> query)
    {
        if (_statsQueries.TryAdd(query, () => query.IsFetching))
            Subscribe(query, (_, e) =>
            {
                if (e.PropertyName == nameof(Query<T>.IsFetching)) Avalonia.Threading.Dispatcher.UIThread.Post(UpdateFetching);
            });
        UpdateFetching();
        return query;
    }

    private void UpdateFetching()
    {
        var fetching = _statsQueries.Values.Any(f => f());
        if (fetching == _fetching) return;
        _fetching = fetching;
        _refresh.Content = fetching
            ? new SpinningIcon { Kind = LucideIconKind.RefreshCw, Size = 16 }
            : new LucideIcon { Kind = LucideIconKind.RefreshCw, Size = 16 };
        OnPropertyChanged(nameof(IsFetching));
    }

    private void Change(Action apply)
    {
        apply();
        FiltersChanged?.Invoke();
    }
}
