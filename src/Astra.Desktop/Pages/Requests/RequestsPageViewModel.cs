using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Requests;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Traces;
using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Requests;

/// <summary>
/// The request log (web <c>RequestsPage</c>): filters, the stored list with the live feed merged in on page 1, paging, the header numbers (total cost, live
/// RPM / TPM) and the open request detail. Everything that is not drawing lives in <c>Astra.Desktop.Core.Requests</c> (filters → query, live merge,
/// formatting); this class wires it to the shared query client and the page lifecycle.
/// <list type="bullet">
/// <item>Filters: the keyword and picks apply to the view at once but reach the server after a 250 ms quiet time (<see cref="Debounce{T}"/>, web <c>useDebounced</c>); any change returns to page 1.</item>
/// <item>The route parameter carries the web's address filters (<c>token=…&amp;model=…</c>, <see cref="RequestRouteParams"/>), read once.</item>
/// <item>Page 1 follows the live stream (<see cref="LiveRequestFeed"/>); polling every 5 s is only the fallback while the stream is down.</item>
/// <item>The previous page of rows stays on screen while the next one loads (web <c>placeholderData: (prev) =&gt; prev</c>).</item>
/// </list>
/// </summary>
public sealed class RequestsPageViewModel : PageViewModel
{
    private static readonly TimeSpan PollFallback = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RefetchCoalesce = TimeSpan.FromMilliseconds(250);

    private readonly PageContext _ctx;
    private readonly Debounce<string> _debouncedModel;
    private readonly Debounce<RequestFilterValues> _debouncedFilters;
    private readonly LiveRequestFeed _feed;
    private readonly Dictionary<string, RequestRowViewModel> _rows = new(StringComparer.Ordinal);
    private readonly HashSet<string> _persisted = new(StringComparer.Ordinal);
    private readonly RequestHeaderActions _header;
    private string _model;
    private RequestFilterValues _filters;
    private int _page = 1;
    private Query<RequestPageDto> _list = null!;
    private Query<RateStatsDto> _rate = null!;
    private IDisposable? _listWatch;
    private IDisposable? _rateWatch;
    private RequestPageDto? _shown;
    private RateStatsDto? _lastRate;
    private object? _lastListData;
    private Dictionary<string, RequestSummaryDto> _stored = new(StringComparer.Ordinal);
    private IReadOnlyList<RequestRowViewModel> _items = [];
    private int _liveOnlyCount;
    private ITimer? _refetchTimer;
    private RequestDetailViewModel? _detail;
    private bool _active;
    private bool _recomputeQueued;

    /// <summary>Creates the page; <paramref name="param"/> may carry the address filters.</summary>
    public RequestsPageViewModel(PageContext context, string? param) : base(AppRoute.Requests, param, context)
    {
        _ctx = context;
        var (model, filters) = RequestRouteParams.Parse(param);
        _model = model;
        _filters = filters;
        var ui = context.Queries.Dispatcher;
        var time = context.Queries.Time;
        _debouncedModel = new Debounce<string>(model.Trim(), ui, time: time);
        _debouncedFilters = new Debounce<RequestFilterValues>(filters, ui, time: time);
        _debouncedModel.Settled += _ => OnFiltersSettled();
        _debouncedFilters.Settled += _ => OnFiltersSettled();

        Live = new LiveRequestStore(ui);
        Live.ListChanged += OnLiveListChanged;
        Live.RowChanged += OnLiveRowChanged;
        _feed = new LiveRequestFeed(context.Api, Live, ui, Visibility, OnPersisted, ScheduleRefetch, time);

        ProvidersQuery = context.Queries.Query(QueryKey.Of("request-filters", "providers"),
            async ct => await context.Api.GetAsync<List<ProviderDto>>("/api/providers", ct) ?? []);
        ClientsQuery = context.Queries.Query(QueryKey.Of("request-filters", "clients"),
            async ct => await context.Api.GetAsync<List<ClientInfoDto>>("/api/clients", ct) ?? []);
        TunnelQuery = context.Queries.Query(QueryKey.Of("request-filters", "tunnel"),
            async ct => await context.Api.GetAsync<TunnelStatusDto>("/api/tunnel", ct) ?? throw new InvalidOperationException("empty answer"));
        Observe(ProvidersQuery);
        Observe(ClientsQuery);
        Observe(TunnelQuery);
        Observe(context.Tokens.Tokens);
        foreach (var q in new QueryBase[] { ProvidersQuery, ClientsQuery, TunnelQuery, context.Tokens.Tokens }) Subscribe(q, OnSourceChanged);
        OnDispose(() =>
        {
            if (_list is not null) _list.PropertyChanged -= OnListChanged;
            if (_rate is not null) _rate.PropertyChanged -= OnListChanged;
        });
        RebindQueries();
        TableContext = new RequestTableContext { Clock = context.Clock, Provider = ProviderById };
        _header = new RequestHeaderActions(Refresh);
        SetHeaderActions(_header);
        Recompute();
    }

    /// <summary>Whether the window is shown; the view sets it, the live stream closes while it is not.</summary>
    public ManualVisibility Visibility { get; } = new();

    /// <summary>The in-flight rows of the live feed.</summary>
    public LiveRequestStore Live { get; }

    /// <summary>The providers (icons for the table, options for the filter).</summary>
    public Query<List<ProviderDto>> ProvidersQuery { get; }

    /// <summary>The clients (options for the filter).</summary>
    public Query<List<ClientInfoDto>> ClientsQuery { get; }

    /// <summary>Remote access status: the origin filter is offered once it is on.</summary>
    public Query<TunnelStatusDto> TunnelQuery { get; }

    /// <summary>What the table's cells need besides their rows.</summary>
    public RequestTableContext TableContext { get; }

    /// <summary>The state changed in a way the view shows (rows, counts, filter options, loading / error).</summary>
    public event Action? StateChanged;

    /// <summary>The detail sheet opened or closed.</summary>
    public event Action? DetailChanged;

    /// <summary>The <c>find</c> command: focus the keyword box.</summary>
    public event Action? FocusSearchRequested;

    /// <inheritdoc />
    public override string? Subtitle => _shown is null ? null : Loc.T("requests.total", ("count", TraceFormat.Tokens(_shown.Total + _liveOnlyCount)));

    /// <summary>The keyword as typed.</summary>
    public string Model => _model;

    /// <summary>The picks as the user has them (the server sees them after the debounce).</summary>
    public RequestFilterValues Filters => _filters;

    /// <summary>The page shown (1-based).</summary>
    public int Page => _page;

    /// <summary>Pages of the stored list.</summary>
    public int PageCount => RequestListMerge.PageCount(_shown?.Total);

    /// <summary>The rows: live-only rows first, then the stored page.</summary>
    public IReadOnlyList<RequestRowViewModel> Items => _items;

    /// <summary>Bumped whenever the set or order of <see cref="Items"/> changes (a row's own update does not).</summary>
    public int ItemsVersion { get; private set; }

    /// <summary>The stored list has never answered and is loading (web <c>requests.isLoading</c>, with no earlier page to show).</summary>
    public bool IsLoading => _shown is null && _list.IsLoading;

    /// <summary>The stored list's last fetch failed.</summary>
    public bool IsError => _list.IsError;

    /// <summary>The failure's message (web <c>errorText</c>).</summary>
    public string? ErrorMessage => _list.Error?.Message;

    /// <summary>There is an answer (the empty / table / paging branches show).</summary>
    public bool HasData => _shown is not null;

    /// <summary>The list is being refetched (the refresh button spins).</summary>
    public bool IsFetching => _list.IsFetching;

    /// <summary>The keyword or any filter is set.</summary>
    public bool HasFilters => RequestFilterCatalog.HasFilters(_model, _filters);

    /// <summary>The origin filter is offered: remote access is on now, or some row at hand came in through it.</summary>
    public bool ShowOrigin => TunnelQuery.Data?.Enabled == true || _items.Any(r => r.Summary.Remote);

    /// <summary>The open request's detail, or null.</summary>
    public RequestDetailViewModel? Detail => _detail;

    /// <summary>Id of the open request.</summary>
    public string? Selected => _detail?.Id;

    /// <summary>The stored list's current query (tests read its path).</summary>
    public RequestListQuery CurrentQuery => new(_debouncedModel.Value, _debouncedFilters.Value, _page);

    // ---------- user actions ----------

    /// <summary>The keyword box changed (web <c>onModelChange</c>).</summary>
    public void SetModel(string value)
    {
        if (value == _model) return;
        _model = value;
        _page = 1;
        _debouncedModel.Set(value.Trim());
        RebindIfPageChanged();
        Changed();
    }

    /// <summary>One filter's picks changed (web <c>setFilter</c>).</summary>
    public void SetFilter(RequestFilterId id, IEnumerable<string> values)
    {
        _filters = _filters.With(id, values);
        _page = 1;
        _debouncedFilters.Set(_filters);
        RebindIfPageChanged();
        Changed();
    }

    /// <summary>Empties the keyword and every filter (web <c>clearFilters</c>).</summary>
    public void ClearFilters()
    {
        _model = "";
        _filters = RequestFilterValues.Empty;
        _page = 1;
        _debouncedModel.Set("");
        _debouncedFilters.Set(RequestFilterValues.Empty);
        RebindIfPageChanged();
        Changed();
    }

    /// <summary>Shows page <paramref name="page"/> (clamped to the pages there are).</summary>
    public void GoToPage(int page)
    {
        page = Math.Clamp(page, 1, Math.Max(1, PageCount));
        if (page == _page) return;
        _page = page;
        RebindQueries();
        SyncFeed();
        Changed();
    }

    /// <summary>The header's refresh button: refetch the list and the rate now.</summary>
    public void Refresh()
    {
        _ = _list.RefetchAsync();
        _ctx.Queries.Invalidate("stats", "rate");
    }

    /// <summary>Opens request <paramref name="id"/>'s detail sheet.</summary>
    public void Open(string id)
    {
        if (_detail?.Id == id) return;
        _detail?.Dispose();
        _detail = new RequestDetailViewModel(_ctx, id);
        DetailChanged?.Invoke();
    }

    /// <summary>Closes the detail sheet.</summary>
    public void CloseDetail()
    {
        if (_detail is null) return;
        _detail.Dispose();
        _detail = null;
        DetailChanged?.Invoke();
    }

    /// <summary>"查看链路": the request's span tree in the traces page (web <c>/traces/requests/:id</c>).</summary>
    public void OpenTrace(string id) => _ctx.Navigator.Push(nameof(AppRoute.Traces), TraceRoutes.Request(id).Path);

    /// <inheritdoc />
    public override bool OnMenuCommand(string command)
    {
        if (command != MenuCommands.Find) return false;
        FocusSearchRequested?.Invoke();
        return true;
    }

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        Changed();
    }

    // ---------- lifecycle ----------

    /// <inheritdoc />
    protected override void OnActivated()
    {
        _active = true;
        _listWatch = _list.Activate();
        _rateWatch = _rate.Activate();
        SyncFeed();
        Recompute();
    }

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        _active = false;
        _listWatch?.Dispose();
        _rateWatch?.Dispose();
        _listWatch = _rateWatch = null;
        _feed.Stop();
        _refetchTimer?.Dispose();
        _refetchTimer = null;
        CloseDetail();
    }

    // ---------- queries ----------

    private void RebindIfPageChanged()
    {
        // The query key follows the settled filters and the page; a page reset alone can change it.
        RebindQueries();
        SyncFeed();
    }

    private void OnFiltersSettled()
    {
        RebindQueries();
        Changed();
    }

    /// <summary>
    /// Points the page at the queries of the settled filters and the current page. Queries are shared by key through the client's cache; the
    /// old ones are released and the new ones watched while the page is active.
    /// </summary>
    private void RebindQueries()
    {
        var q = CurrentQuery;
        var page = q.Page;
        var list = _ctx.Queries.Query(q.Key, async ct => await _ctx.Api.GetAsync<RequestPageDto>(q.Path, ct) ?? throw new InvalidOperationException("empty answer"),
            new QueryOptions<RequestPageDto>
            {
                // Polling is only the fallback for when the live stream is down (page 1); the feed's persisted frames refetch otherwise.
                RefetchInterval = _ => RequestListMerge.PollsList(page, Live.Connected) ? PollFallback : null,
            });
        var (ratePath, rateKey) = q.Rate();
        var rate = _ctx.Queries.Query(rateKey, async ct => await _ctx.Api.GetAsync<RateStatsDto>(ratePath, ct) ?? throw new InvalidOperationException("empty answer"),
            new QueryOptions<RateStatsDto> { RefetchInterval = _ => PollFallback });
        if (!ReferenceEquals(list, _list))
        {
            if (_list is not null) _list.PropertyChanged -= OnListChanged;
            _listWatch?.Dispose();
            _list = list;
            _list.PropertyChanged += OnListChanged;
            if (_active) _listWatch = _list.Activate();
        }
        if (!ReferenceEquals(rate, _rate))
        {
            if (_rate is not null) _rate.PropertyChanged -= OnListChanged;
            _rateWatch?.Dispose();
            _rate = rate;
            _rate.PropertyChanged += OnListChanged;
            if (_active) _rateWatch = _rate.Activate();
        }
    }

    private void OnListChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Shows(e)) QueueRecompute();
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Shows(e)) QueueRecompute();
    }

    // One fetch raises up to eight notifications; these three never change what the page shows.
    private static bool Shows(PropertyChangedEventArgs e)
        => e.PropertyName is not (nameof(Query<object>.IsFetching) or nameof(Query<object>.DataUpdatedAt) or nameof(Query<object>.Status));

    // The rest of one fetch's notifications (and those of the other queries answering in the same turn) become one recompute: each
    // recompute redraws the view, and the table is the page's heaviest part.
    private void QueueRecompute()
    {
        if (_recomputeQueued) return;
        _recomputeQueued = true;
        _ctx.Queries.Dispatcher.Post(() =>
        {
            _recomputeQueued = false;
            Recompute();
        });
    }

    // ---------- live feed ----------

    /// <summary>The feed runs while the page is shown and on page 1 (web <c>useLiveRequestFeed(page === 1)</c>).</summary>
    private void SyncFeed()
    {
        if (_active && _page == 1) _feed.Start();
        else _feed.Stop();
    }

    private void OnLiveListChanged()
    {
        // The stream going up or down changes whether the list polls: restart its cycle.
        Recompute();
    }

    private void OnLiveRowChanged(string id)
    {
        if (!_rows.TryGetValue(id, out var row)) return;
        row.Update(RequestListMerge.Freshest(_stored.GetValueOrDefault(id) ?? row.Summary, Live.Get(id)));
    }

    private void OnPersisted(string id)
    {
        _persisted.Add(id);
        ScheduleRefetch();
    }

    /// <summary>
    /// A persisted request (or a dropped / returned feed) refetches the list and the detail of what was persisted, coalesced: the usage writer persists
    /// in batches. An open trace session shows the stored call now rather than at its next poll.
    /// </summary>
    private void ScheduleRefetch()
    {
        if (_refetchTimer is not null) return;
        _refetchTimer = _ctx.Queries.Time.CreateTimer(_ => _ctx.Queries.Dispatcher.Post(FlushRefetch), null, RefetchCoalesce, Timeout.InfiniteTimeSpan);
    }

    private void FlushRefetch()
    {
        _refetchTimer?.Dispose();
        _refetchTimer = null;
        _ctx.Queries.Invalidate("requests");
        _ctx.Queries.Invalidate("traces", "session");
        foreach (var id in _persisted)
        {
            _ctx.Queries.Invalidate("request", id);
            _ctx.Queries.Invalidate("traces", "request", id);
        }
        _persisted.Clear();
    }

    // ---------- derived state ----------

    private void Changed()
    {
        Recompute();
    }

    /// <summary>Recomputes the shown page, the merged rows and the header numbers, and tells the view.</summary>
    private void Recompute()
    {
        _recomputeQueued = false;
        if (_list.HasData && !ReferenceEquals(_list.Data, _lastListData))
        {
            _lastListData = _list.Data;
            _shown = _list.Data;
            _stored = _shown!.Items.ToDictionary(r => r.Id, StringComparer.Ordinal);
        }
        if (_rate.HasData) _lastRate = _rate.Data;
        var liveOnly = RequestListMerge.LiveOnly(_page, _shown?.Items, Live.List, _debouncedModel.Value, _debouncedFilters.Value);
        _liveOnlyCount = liveOnly.Count;
        var merged = RequestListMerge.Items(liveOnly, _shown?.Items);
        var rows = new List<RequestRowViewModel>(merged.Count);
        foreach (var summary in merged)
        {
            var fresh = RequestListMerge.Freshest(summary, Live.Get(summary.Id));
            if (_rows.TryGetValue(summary.Id, out var row)) row.Update(fresh);
            else _rows[summary.Id] = row = new RequestRowViewModel(fresh);
            rows.Add(row);
        }
        foreach (var gone in _rows.Keys.Where(id => !merged.Any(r => r.Id == id)).ToList()) _rows.Remove(gone);
        if (!rows.SequenceEqual(_items))
        {
            _items = rows;
            ItemsVersion++;
        }
        _header.Update(_shown?.TotalCostNanoUsd, _lastRate, _list.IsFetching);
        OnPropertyChanged(nameof(Subtitle));
        StateChanged?.Invoke();
    }

    private ProviderDto? ProviderById(string? id)
        => id is null ? null : ProvidersQuery.Data?.FirstOrDefault(p => p.Id == id);
}
