using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Traces;

namespace Astra.Desktop.Pages.Traces;

/// <summary>
/// One query a traces view follows whose key changes with the view's params (range, client, turn): the current query is observed only while
/// the page is active, and with <c>keepPrevious</c> the last answer stays on screen while the next key loads (react-query
/// <c>placeholderData: (prev) =&gt; prev</c>).
/// </summary>
internal sealed class Watched<T> where T : class
{
    private readonly Action _changed;
    private readonly bool _keepPrevious;
    private Query<T>? _query;
    private IDisposable? _token;
    private bool _active;
    private T? _last;

    public Watched(Action changed, bool keepPrevious)
    {
        _changed = changed;
        _keepPrevious = keepPrevious;
    }

    public Query<T>? Query => _query;

    /// <summary>The data to show: the query's, else (keeping the previous) the last key's.</summary>
    public T? Data => _query is { HasData: true } q ? q.Data : _keepPrevious ? _last : null;

    /// <summary>Nothing to show yet and a fetch is running (web <c>isLoading &amp;&amp; !data</c>).</summary>
    public bool IsLoading => Data is null && (_query is null || _query.IsLoading || _query.Status == QueryStatus.Pending);

    /// <summary>The fetch failed and there is nothing to show.</summary>
    public bool IsError => Data is null && _query is { IsError: true };

    public Exception? Error => _query?.Error;

    public void Set(Query<T> query)
    {
        if (ReferenceEquals(query, _query)) return;
        if (_query is not null) _query.PropertyChanged -= OnChanged;
        _token?.Dispose();
        _token = null;
        _query = query;
        query.PropertyChanged += OnChanged;
        if (_active) _token = query.Activate();
        if (query.HasData) _last = query.Data;
        _changed();
    }

    /// <summary>Lets go of the query for good: it is shared and outlives the view model.</summary>
    public void Detach()
    {
        SetActive(false);
        if (_query is not null) _query.PropertyChanged -= OnChanged;
    }

    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active && _query is not null) _token = _query.Activate();
        else
        {
            _token?.Dispose();
            _token = null;
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_query is { HasData: true } q) _last = q.Data;
        _changed();
    }
}

/// <summary>What every traces view model gets from the page: the context, the live feed, brands, labels, clock and navigation.</summary>
public sealed class TraceViewServices
{
    internal TraceViewServices(Shell.PageContext ctx, LiveRequestStore live, Func<IReadOnlyDictionary<string, ProviderBrand>> brands, Action<TracesLocation, bool> go)
    {
        Ctx = ctx;
        Live = live;
        _brands = brands;
        _go = go;
    }

    private readonly Func<IReadOnlyDictionary<string, ProviderBrand>> _brands;
    private readonly Action<TracesLocation, bool> _go;

    /// <summary>The page context.</summary>
    public Shell.PageContext Ctx { get; }

    /// <summary>The live request feed's rows.</summary>
    public LiveRequestStore Live { get; }

    /// <summary>Provider brands by id (web <c>useProviderBrands</c>).</summary>
    public IReadOnlyDictionary<string, ProviderBrand> Brands => _brands();

    /// <summary>The zone dates are shown in; null = the machine's (the visual tests pin UTC).</summary>
    public TimeZoneInfo? TimeZone { get; set; }

    /// <summary>The trace text function (<c>t</c>).</summary>
    public TraceText T => TraceTexts.FromLoc;

    /// <summary>A client kind's display name (topology.tsx <c>labelOfClient</c>).</summary>
    public string ClientLabel(string? kind) => string.IsNullOrEmpty(kind) ? "" : RequestPresentation.ClientName(kind);

    /// <summary>Web <c>formatDateTime(iso, locale)</c>.</summary>
    public string DateTime(DateTimeOffset at) => RequestFormat.DateTime(at, Loc.Language, TimeZone);

    /// <summary>The page clock in Unix ms (<c>Date.now()</c>).</summary>
    public double NowMs() => Ctx.Clock.Now.ToUnixTimeMilliseconds();

    /// <summary>Goes to another traces location: a new view is a step in the history (<c>navigate</c>); a param change replaces it.</summary>
    public void Go(TracesLocation location, bool push = true) => _go(location, push);

    /// <summary>Follows an inspector link (a web path).</summary>
    public void Open(TraceRoute route) => Go(TracesLocation.Parse(route.Path));
}

/// <summary>A traces view model: observed while the page is active, raising <see cref="Changed"/> when its view should redraw.</summary>
public abstract class TraceViewModel
{
    private readonly List<Action> _detach = [];

    /// <summary>The page's services.</summary>
    protected TraceViewModel(TraceViewServices services) => S = services;

    /// <summary>A <see cref="Watched{T}"/> that <see cref="Detach"/> lets go of.</summary>
    private protected Watched<T> Watch<T>(Action changed, bool keepPrevious) where T : class
    {
        var watched = new Watched<T>(changed, keepPrevious);
        _detach.Add(watched.Detach);
        return watched;
    }

    /// <summary>The page was dropped: unhooks every query this view model watched.</summary>
    public void Detach()
    {
        foreach (var detach in _detach) detach();
        _detach.Clear();
    }

    /// <summary>The page's services.</summary>
    public TraceViewServices S { get; }

    /// <summary>The view should redraw.</summary>
    public event Action? Changed;

    /// <summary>Raises <see cref="Changed"/>.</summary>
    protected void Raise() => Changed?.Invoke();

    /// <summary>Whether this view follows the live request feed (web <c>useLiveRequestFeed(true)</c>).</summary>
    public virtual bool UsesLiveFeed => false;

    /// <summary>The page became active / inactive.</summary>
    public abstract void SetActive(bool active);

    /// <summary>The live list changed.</summary>
    public virtual void OnLiveChanged() { }
}

/// <summary>
/// A · the whole traffic picture (topology.tsx <c>TopologyView</c>): clients → Astra → each provider's accounts, live — and beside it the
/// activity feed, one card per request (why it was sent, where it went, what came back). Hovering a card highlights its path.
/// </summary>
public sealed class TopologyViewModel : TraceViewModel
{
    private readonly Watched<TraceTopologyDto> _query;
    private readonly Watched<List<TraceActivityDto>> _activity;
    private readonly Watched<SettingsDto> _settings;
    private string _range = "live";
    private bool _savingContent;

    /// <summary>Creates the view model.</summary>
    public TopologyViewModel(TraceViewServices s) : base(s)
    {
        _query = Watch<TraceTopologyDto>(Raise, keepPrevious: true);
        _activity = Watch<List<TraceActivityDto>>(Raise, keepPrevious: true);
        _settings = Watch<SettingsDto>(Raise, keepPrevious: true);
        Load();
        _activity.Set(S.Ctx.Queries.Query(TraceQueries.ActivityKey(ActivityFeed.Limit),
            async ct => await S.Ctx.Api.GetAsync<List<TraceActivityDto>>(TraceQueries.ActivityPath(ActivityFeed.Limit), ct) ?? [],
            new QueryOptions<List<TraceActivityDto>> { RefetchInterval = _ => TraceQueries.ActivityInterval }));
        _settings.Set(S.Ctx.Settings.Settings);
    }

    /// <summary>Whether the activity feed is shown (the 活动流 toggle, remembered).</summary>
    public bool Rail { get; private set; } = TracePrefs.ActivityRail;

    /// <summary>The feed card under the pointer: its path is highlighted on the canvas.</summary>
    public ActivityItem? Hovered { get; private set; }

    /// <summary>The feed: stored requests with the live feed's running ones in their place, newest first.</summary>
    public IReadOnlyList<ActivityItem> Items => ActivityFeed.Items(_activity.Data ?? [], S.Live.List, S.NowMs());

    /// <summary>Whether conversation content is recorded (the <c>traceContent</c> setting the feed's switch flips).</summary>
    public bool ContentOn => _settings.Data?.TraceContent ?? false;

    /// <summary>The content switch can be used: the settings are loaded and no change is being saved.</summary>
    public bool ContentReady => _settings.Data is not null && !_savingContent;

    /// <summary>A provider's and an account's display names, for the feed's route line.</summary>
    public (string Provider, string? Account) NameOf(string? providerId, string? accountId)
    {
        var p = providerId is null ? null : Data?.Providers.FirstOrDefault(x => x.ProviderId == providerId);
        var provider = (providerId is not null && S.Brands.TryGetValue(providerId, out var b) ? b.Name : p?.Name) ?? providerId ?? "—";
        var account = string.IsNullOrEmpty(accountId) ? null : p?.Accounts.FirstOrDefault(a => a.Id == accountId)?.Name ?? accountId[..Math.Min(8, accountId.Length)];
        return (provider, account);
    }

    /// <summary>Shows or hides the activity feed.</summary>
    public void ToggleRail()
    {
        Rail = !Rail;
        TracePrefs.ActivityRail = Rail;
        Hovered = null;
        Raise();
    }

    /// <summary>The pointer entered a feed card (null: left the feed).</summary>
    public void Hover(ActivityItem? item)
    {
        if (Hovered?.Id == item?.Id) return;
        Hovered = item;
        Raise();
    }

    /// <summary>Turns conversation content recording on or off (PATCH /api/settings; the answer replaces the cached settings).</summary>
    public async void SetContent(bool on)
    {
        if (_savingContent || _settings.Data is null || _settings.Data.TraceContent == on) return;
        _savingContent = true;
        Raise();
        try
        {
            var saved = await S.Ctx.Api.PatchAsync<System.Text.Json.Nodes.JsonObject, SettingsDto>("/api/settings", Core.Settings.JsonPatch.Of(("traceContent", on)));
            if (saved is not null) S.Ctx.Settings.Settings.SetData(saved);
        }
        catch (Exception)
        {
            // the switch shows the stored value again
        }
        finally
        {
            _savingContent = false;
            Raise();
        }
    }

    /// <summary>The clock moved on: a failure stops bouncing and a switch note leaves the hub on time.</summary>
    public void Tick() => Raise();

    /// <inheritdoc />
    public override bool UsesLiveFeed => true;

    /// <summary>The window: live / 1h / 24h.</summary>
    public string Range => _range;

    /// <summary>The selected node.</summary>
    public string? Selected { get; private set; }

    /// <summary>The aggregates.</summary>
    public TraceTopologyDto? Data => _query.Data;

    /// <summary>First load.</summary>
    public bool IsLoading => _query.IsLoading;

    /// <summary>Failed with nothing to show.</summary>
    public bool IsError => _query.IsError;

    /// <summary>The error.</summary>
    public Exception? Error => _query.Error;

    /// <summary>The window saw no traffic and nothing is in flight.</summary>
    public bool IsEmpty => TopologyPageLogic.IsEmpty(Data, S.Live.List);

    /// <summary>The canvas.</summary>
    public TraceGraph Graph => Data is { } d
        ? TopologyBuilder.Build(d, S.T, S.ClientLabel, S.Brands, S.Live.List, new TopologyOptions(S.NowMs(),
            Hovered is { } h ? new HighlightPath(h.Request.ClientKind, h.Attempts, h.Request.ProviderId, h.Request.AccountId) : null, _activity.Data))
        : TraceGraph.Empty;

    /// <summary>The in-flight chip's count.</summary>
    public int InFlight => TopologyPageLogic.InFlight(S.Live.Connected, S.Live.List, Data);

    /// <summary>The selected node's details.</summary>
    public InspectorModel? Inspector => Data is { } d && Selected is { } id ? Inspectors.ForTopologyNode(id, d, S.T, S.DateTime, S.ClientLabel, S.Brands) : null;

    /// <summary>Picks the window.</summary>
    public void SetRange(string range)
    {
        if (_range == range) return;
        _range = range;
        Load();
    }

    /// <summary>Selects a node.</summary>
    public void Select(string? id)
    {
        Selected = id;
        Raise();
    }

    /// <inheritdoc />
    public override void SetActive(bool active)
    {
        _query.SetActive(active);
        _activity.SetActive(active);
        _settings.SetActive(active);
    }

    /// <inheritdoc />
    public override void OnLiveChanged() => Raise();

    private void Load()
    {
        var range = _range;
        _query.Set(S.Ctx.Queries.Query(TraceQueries.TopologyKey(range),
            async ct => await S.Ctx.Api.GetAsync<TraceTopologyDto>(TraceQueries.TopologyPath(range), ct) ?? throw new InvalidOperationException("empty answer"),
            new QueryOptions<TraceTopologyDto> { RefetchInterval = _ => TraceQueries.TopologyInterval(range) }));
    }
}

/// <summary>The sessions seen in a window, most recently active first (session.tsx <c>SessionsList</c>).</summary>
public sealed class SessionsListViewModel : TraceViewModel
{
    private readonly Watched<List<TraceSessionDto>> _query;
    private TracesLocation _location;

    /// <summary>Creates the view model at <paramref name="location"/> (its <c>range</c> / <c>client</c> params).</summary>
    public SessionsListViewModel(TraceViewServices s, TracesLocation location) : base(s)
    {
        _location = location;
        _query = Watch<List<TraceSessionDto>>(Raise, keepPrevious: true);
        Load();
    }

    /// <summary>The window (default 24h).</summary>
    public string Range => _location.Get("range") ?? "24h";

    /// <summary>The client filter.</summary>
    public string? Client => _location.Get("client");

    /// <summary>The sessions.</summary>
    public IReadOnlyList<TraceSessionDto>? Data => _query.Data;

    /// <summary>First load.</summary>
    public bool IsLoading => _query.IsLoading;

    /// <summary>Failed with nothing to show.</summary>
    public bool IsError => _query.IsError;

    /// <summary>The error.</summary>
    public Exception? Error => _query.Error;

    /// <summary>Sets a search param (<c>set(key, value)</c>, replacing the history entry).</summary>
    public void Set(string key, string? value)
    {
        _location = _location.With(key, value);
        S.Go(_location, push: false);
        Load();
    }

    /// <summary>Opens a session.</summary>
    public void Open(string sessionKey) => S.Open(TraceRoutes.Session(sessionKey));

    /// <inheritdoc />
    public override void SetActive(bool active) => _query.SetActive(active);

    private void Load()
    {
        var (range, client) = (Range, Client);
        _query.Set(S.Ctx.Queries.Query(TraceQueries.SessionsKey(range, client),
            async ct => await S.Ctx.Api.GetAsync<List<TraceSessionDto>>(TraceQueries.SessionsPath(range, client), ct) ?? [],
            new QueryOptions<List<TraceSessionDto>> { RefetchInterval = _ => TraceQueries.SessionsInterval }));
    }
}

/// <summary>
/// C / D · one session (session.tsx <c>SessionView</c>): its turns, and the selected turn as a canvas (structure) or a timeline (time), with
/// the calls in flight joining the newest turn live.
/// </summary>
public sealed class SessionViewModel : TraceViewModel
{
    private readonly Watched<TraceSessionDetailDto> _query;
    private TracesLocation _location;
    private IReadOnlyList<RequestSummaryDto> _pending = [];
    private (string? Turn, HashSet<string> Ids) _opened = (null, new HashSet<string>(StringComparer.Ordinal));
    private int _columns = 4;
    private SessionGraph? _graph;

    /// <summary>Creates the view model for <see cref="TracesLocation.SessionKey"/>.</summary>
    public SessionViewModel(TraceViewServices s, TracesLocation location) : base(s)
    {
        _location = location;
        Key = location.SessionKey!;
        _query = Watch<TraceSessionDetailDto>(OnData, keepPrevious: true);
        Load();
    }

    /// <inheritdoc />
    public override bool UsesLiveFeed => true;

    /// <summary>The session key.</summary>
    public string Key { get; }

    /// <summary>The server's answer.</summary>
    public TraceSessionDetailDto? Data => _query.Data;

    /// <summary>First load.</summary>
    public bool IsLoading => _query.IsLoading;

    /// <summary>A real error (an unknown session is <see cref="IsNotFound"/>).</summary>
    public bool IsError => _query.IsError && !TraceQueries.IsNotFound(_query.Error);

    /// <summary>The error.</summary>
    public Exception? Error => _query.Error;

    /// <summary>Nothing to show: the session is gone.</summary>
    public bool IsNotFound => Data is null && !IsLoading && !IsError;

    /// <summary>The derived state (shown turn, its calls, pending calls, view).</summary>
    public SessionPageState State { get; private set; } = SessionPageLogic.Resolve(null, null, [], null);

    /// <summary>The canvas of the shown turn.</summary>
    public SessionGraph Graph => _graph ??= SessionBuilder.Build(State.Calls, State.Spans, S.T, S.Brands,
        new SessionOptions(_columns, true, Expanded, State.Pending));

    /// <summary>The time bar under the view.</summary>
    public SessionTimeBar TimeBar => TimeBarBuilder.ForSession(State.Calls, State.Spans, S.T);

    /// <summary>The selected call (or <c>tool:ID</c> / <c>turn</c>).</summary>
    public string? Selected { get; private set; }

    /// <summary>The anomaly the view was last moved to, and a counter so the same one moves again.</summary>
    public (string Id, int Seq)? Focus { get; private set; }

    /// <summary>The selected node's details.</summary>
    public InspectorModel? Inspector => Selected is { } id ? Inspectors.ForSessionNode(id, State.Calls, State.Spans, S.T, S.Brands) : null;

    /// <summary>The live calls of this session (the feed is newest first).</summary>
    public IReadOnlyList<RequestSummaryDto> Mine => Data is null ? [] : S.Live.List.Where(r => r.SessionKey == Key).ToList();

    /// <summary>The turn picker's entries.</summary>
    public IReadOnlyList<TurnOption> TurnOptions => Data is { } d ? SessionPageLogic.TurnOptions(d, State.LiveTurn, S.T, S.DateTime) : [];

    /// <summary>Folded groups the user opened in the shown turn.</summary>
    public IReadOnlySet<string> Expanded => _opened.Turn == State.TurnId ? _opened.Ids : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>What the agent is doing now (the status chip), or null.</summary>
    public AgentStatus? AgentStatus
    {
        get
        {
            var liveId = S.Live.List.FirstOrDefault(r => r.Status == "pending" && r.SessionKey == Key)?.Id;
            var live = liveId is null ? null : S.Live.Get(liveId);
            return TraceAnalysis.AgentStatusOf(live?.Status == "pending" ? live : null, State.LatestTurn ? State.Calls : [], State.Spans);
        }
    }

    /// <summary>The steps per row for a measured canvas width; redraws when it changes.</summary>
    public void SetWidth(double width)
    {
        var columns = SessionPageLogic.Columns(width);
        if (columns == _columns) return;
        _columns = columns;
        _graph = null;
        Raise();
    }

    /// <summary>Selects a node; a folded run opens instead of being selected.</summary>
    public void Select(string? id)
    {
        if (id is not null && id.StartsWith("group:", StringComparison.Ordinal))
        {
            Expand([id]);
            return;
        }
        Selected = id;
        Raise();
    }

    /// <summary>Selects a timeline row.</summary>
    public void SelectRow(string id)
    {
        Selected = id;
        Raise();
    }

    /// <summary>Opens folded groups.</summary>
    public void Expand(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(Expanded, StringComparer.Ordinal);
        set.UnionWith(ids);
        _opened = (State.TurnId, set);
        _graph = null;
        Raise();
    }

    /// <summary>Jumps to the next anomaly (selects it and centres the view on it).</summary>
    public void NextAnomaly()
    {
        var id = SessionPageLogic.NextAnomaly(Graph.Anomalies, Focus?.Id);
        if (id is null) return;
        Selected = id;
        Focus = (id, (Focus?.Seq ?? 0) + 1);
        Raise();
    }

    /// <summary>Shows another turn.</summary>
    public void SetTurn(string turn)
    {
        Selected = null;
        Set("turn", turn);
    }

    /// <summary>Shows the canvas or the timeline.</summary>
    public void SetView(string view) => Set("view", view);

    /// <summary>Back to the sessions list (the not-found state's link).</summary>
    public void Back() => S.Go(TracesLocation.Parse("/traces/sessions"));

    /// <summary>Opens a request's trace.</summary>
    public void OpenRequest(string id) => S.Open(TraceRoutes.Request(id));

    /// <inheritdoc />
    public override void SetActive(bool active) => _query.SetActive(active);

    /// <inheritdoc />
    public override void OnLiveChanged() => Recompute();

    private void Set(string key, string? value)
    {
        _location = _location.With(key, value);
        S.Go(_location, push: false);
        if (key == "turn") Load();
        else Recompute();
    }

    private void Load()
    {
        var turn = _location.Get("turn");
        var key = Key;
        _query.Set(S.Ctx.Queries.Query(TraceQueries.SessionKey(key, turn),
            async ct => await S.Ctx.Api.GetAsync<TraceSessionDetailDto>(TraceQueries.SessionPath(key, turn), ct) ?? throw new InvalidOperationException("empty answer"),
            new QueryOptions<TraceSessionDetailDto> { RefetchInterval = d => TraceQueries.SessionInterval(d?.Session.Active == true), CacheTime = TimeSpan.FromMinutes(2) }));
    }

    private void OnData()
    {
        _graph = null;
        Recompute();
    }

    private void Recompute()
    {
        var state = SessionPageLogic.Resolve(Data, _location.Get("turn"), Mine, _location.Get("view"));
        // the same pending list while it draws the same cards, so traffic elsewhere does not relayout the canvas
        if (!SessionPageLogic.SameCards(state.Pending, _pending)) _pending = state.Pending;
        else state = state with { Pending = _pending };
        if (!ReferenceEquals(state.Calls, State.Calls) || !ReferenceEquals(state.Pending, State.Pending) || state.TurnId != State.TurnId) _graph = null;
        State = state;
        Raise();
    }
}

/// <summary>The most recent requests, to pick one to trace (request.tsx <c>RecentRequests</c>).</summary>
public sealed class RecentRequestsViewModel : TraceViewModel
{
    private readonly Watched<RequestPageDto> _query;

    /// <summary>Creates the view model.</summary>
    public RecentRequestsViewModel(TraceViewServices s) : base(s)
    {
        _query = Watch<RequestPageDto>(Raise, keepPrevious: true);
        var q = new RequestListQuery("", RequestFilterValues.Empty, 1, TraceQueries.RecentPageSize);
        _query.Set(s.Ctx.Queries.Query(q.Key,
            async ct => await s.Ctx.Api.GetAsync<RequestPageDto>(q.Path, ct) ?? throw new InvalidOperationException("empty answer"),
            new QueryOptions<RequestPageDto> { RefetchInterval = _ => TimeSpan.FromSeconds(5) }));
    }

    /// <summary>The rows.</summary>
    public IReadOnlyList<RequestSummaryDto> Items => _query.Data?.Items ?? [];

    /// <summary>First load.</summary>
    public bool IsLoading => _query.IsLoading;

    /// <summary>Opens a request's trace.</summary>
    public void Open(string id) => S.Open(TraceRoutes.Request(id));

    /// <inheritdoc />
    public override void SetActive(bool active) => _query.SetActive(active);
}

/// <summary>B · one request (request.tsx <c>RequestTraceView</c>): the request leg through Astra to the provider, and the response leg back.</summary>
public sealed class RequestTraceViewModel : TraceViewModel
{
    private readonly Watched<TraceRequestDto> _query;

    /// <summary>Creates the view model for request <paramref name="id"/>.</summary>
    public RequestTraceViewModel(TraceViewServices s, string id) : base(s)
    {
        Id = id;
        _query = Watch<TraceRequestDto>(Raise, keepPrevious: false);
        _query.Set(s.Ctx.Queries.Query(TraceQueries.RequestKey(id),
            async ct => await s.Ctx.Api.GetAsync<TraceRequestDto>(TraceQueries.RequestPath(id), ct) ?? throw new InvalidOperationException("empty answer"),
            new QueryOptions<TraceRequestDto> { RefetchInterval = d => TraceQueries.RequestInterval(d?.InFlight == true), CacheTime = TimeSpan.FromMinutes(2) }));
    }

    /// <summary>The request id.</summary>
    public string Id { get; }

    /// <summary>The trace.</summary>
    public TraceRequestDto? Data => _query.Data;

    /// <summary>First load.</summary>
    public bool IsLoading => _query.IsLoading;

    /// <summary>A real error (an unknown request is <see cref="IsNotFound"/>).</summary>
    public bool IsError => _query.IsError && !TraceQueries.IsNotFound(_query.Error);

    /// <summary>The error.</summary>
    public Exception? Error => _query.Error;

    /// <summary>Nothing to show: the request is gone.</summary>
    public bool IsNotFound => Data is null && !IsLoading && !IsError;

    /// <summary>The provider's brand.</summary>
    public ProviderBrand? Brand => Data?.Request.ProviderId is { } p && S.Brands.TryGetValue(p, out var b) ? b : null;

    /// <summary>The canvas.</summary>
    public TraceGraph Graph => Data is { } d ? RequestGraphBuilder.Build(d, S.T, S.ClientLabel, Brand) : TraceGraph.Empty;

    /// <summary>The time bar.</summary>
    public TimeBar? TimeBar => Data is { } d ? TimeBarBuilder.ForRequest(d.Spans, S.T) : null;

    /// <summary>The smart-routing decision, if the request was routed.</summary>
    public RouteDecision? Decision => Data is { } d ? TraceAnalysis.RouteDecisionOf(d.Spans) : null;

    /// <summary>Upstream attempts.</summary>
    public int Attempts => Data?.Spans.Count(s => s.Name == Astra.Core.Requests.TraceSpanNames.Attempt) ?? 0;

    /// <summary>The selected node.</summary>
    public string? Selected { get; private set; }

    /// <summary>The selected node's details.</summary>
    public InspectorModel? Inspector => Data is { } d && Selected is { } id ? Inspectors.ForRequestNode(id, d, S.T, S.DateTime, S.ClientLabel, Brand) : null;

    /// <summary>Whether the request log's detail sheet is open (<c>logOpen</c>).</summary>
    public bool LogOpen { get; private set; }

    /// <summary>Selects a node.</summary>
    public void Select(string? id)
    {
        Selected = id;
        Raise();
    }

    /// <summary>Opens / closes the request log's detail sheet.</summary>
    public void SetLogOpen(bool open)
    {
        if (LogOpen == open) return;
        LogOpen = open;
        Raise();
    }

    /// <summary>Opens the session this request belongs to, at its turn.</summary>
    public void OpenSession()
    {
        if (Data?.Request is { SessionKey: { Length: > 0 } key } r) S.Open(TraceRoutes.Session(key, r.TurnId));
    }

    /// <summary>Back to the recent requests (the not-found state's link).</summary>
    public void Back() => S.Go(TracesLocation.Parse("/traces/requests"));

    /// <inheritdoc />
    public override void SetActive(bool active) => _query.SetActive(active);
}
