using System.ComponentModel;
using System.Text.Json.Nodes;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Traces;
using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Traces;

/// <summary>
/// 链路追踪 (pages/TracesPage.tsx; .plan/observability.md §5): Agent → Astra → provider. The route parameter is the web path
/// (<see cref="TracesLocation"/>): <c>/traces</c> the topology, <c>/traces/sessions[/KEY][?turn=&amp;view=&amp;range=&amp;client=]</c> the
/// sessions, <c>/traces/requests[/ID]</c> the requests — the same paths <see cref="TraceRoutes"/> builds, so the Requests page's "open trace"
/// (<c>Push("Traces", TraceRoutes.Request(id).Path)</c>) and the inspector links land here unchanged. The header's segmented control switches
/// views (a new top-level page); opening a session or a request from a view pushes it, so the back button returns to the list.
/// <para>
/// The page owns the live request feed (web <c>useLiveRequestFeed(true)</c> of the topology and session views): it runs while one of those
/// views is shown and the window is visible; a persisted request refetches the open session, the topology's activity feed and that request's
/// trace (coalesced, 250 ms).
/// </para>
/// </summary>
public sealed class TracesPageViewModel : PageViewModel
{
    private static readonly TimeSpan RefetchCoalesce = TimeSpan.FromMilliseconds(250);
    private readonly PageContext _ctx;
    private readonly LiveRequestFeed _feed;
    private readonly HashSet<string> _persisted = new(StringComparer.Ordinal);
    private readonly ControlledSegmented _tabs;
    private Dictionary<string, ProviderBrand> _brands = new(StringComparer.Ordinal);
    private object? _brandsSource;
    private ITimer? _refetchTimer;
    private bool _active;

    /// <summary>Creates the page at <paramref name="param"/> (a web path; null = the topology).</summary>
    public TracesPageViewModel(PageContext context, string? param) : base(AppRoute.Traces, param, context)
    {
        _ctx = context;
        Location = TracesLocation.Parse(param);
        var ui = context.Queries.Dispatcher;
        Live = new LiveRequestStore(ui);
        _feed = new LiveRequestFeed(context.Api, Live, ui, Visibility, OnPersisted, ScheduleRefetch, context.Queries.Time);
        Services = new TraceViewServices(context, Live, Brands, Go);
        Observe(context.Providers.Providers);
        Subscribe(context.Providers.Providers, OnProviders);
        View = Location switch
        {
            { Tab: TraceRoutes.Tab.Sessions, SessionKey: not null } => new SessionViewModel(Services, Location),
            { Tab: TraceRoutes.Tab.Sessions } => new SessionsListViewModel(Services, Location),
            { Tab: TraceRoutes.Tab.Requests, RequestId: { } id } => new RequestTraceViewModel(Services, id),
            { Tab: TraceRoutes.Tab.Requests } => new RecentRequestsViewModel(Services),
            _ => new TopologyViewModel(Services),
        };
        Live.ListChanged += View.OnLiveChanged;
        OnDispose(View.Detach);
        _tabs = new ControlledSegmented(
            [("topology", Loc.T("traces.tab.topology")), ("sessions", Loc.T("traces.tab.sessions")), ("requests", Loc.T("traces.tab.requests"))],
            Location.Tab.ToString().ToLowerInvariant());
        Avalonia.Automation.AutomationProperties.SetName(_tabs, Loc.T("traces.tab.label"));
        _tabs.Chose += tab => context.Navigator.Navigate(nameof(AppRoute.Traces),
            TraceRoutes.PathOf(tab switch { "sessions" => TraceRoutes.Tab.Sessions, "requests" => TraceRoutes.Tab.Requests, _ => TraceRoutes.Tab.Topology }));
        SetHeaderActions(_tabs);
    }

    /// <summary>Where the page is (its last search params included).</summary>
    public TracesLocation Location { get; private set; }

    /// <summary>The view shown.</summary>
    public TraceViewModel View { get; }

    /// <summary>What the views share.</summary>
    public TraceViewServices Services { get; }

    /// <summary>The live feed's in-flight rows.</summary>
    public LiveRequestStore Live { get; }

    /// <summary>Whether the window is shown; the view sets it, the live stream closes while it is not.</summary>
    public ManualVisibility Visibility { get; } = new();

    /// <inheritdoc />
    public override string? Subtitle => Loc.T("traces.subtitle");

    /// <inheritdoc />
    /// <remarks>The views lay out their own scrolling (a canvas fills the height; web <c>overflow-hidden</c> + <c>flex-1</c>).</remarks>
    public override bool HostScrolls => false;

    /// <inheritdoc />
    protected override void OnActivated()
    {
        _active = true;
        View.SetActive(true);
        if (View.UsesLiveFeed) _feed.Start();
    }

    /// <inheritdoc />
    protected override void OnDeactivated()
    {
        _active = false;
        View.SetActive(false);
        _feed.Stop();
    }

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        _tabs.ItemsSource = new[] { ("topology", "traces.tab.topology"), ("sessions", "traces.tab.sessions"), ("requests", "traces.tab.requests") }
            .Select(x => new TKCode.Arc.SegmentedOption(x.Item1, Loc.T(x.Item2))).ToList();
        _tabs.SetValue(Location.Tab.ToString().ToLowerInvariant());
    }

    private void Go(TracesLocation location, bool push)
    {
        if (!push)
        {
            Location = location;
            return;
        }
        _ctx.Navigator.Push(nameof(AppRoute.Traces), location.ToParam());
    }

    private IReadOnlyDictionary<string, ProviderBrand> Brands()
    {
        var data = _ctx.Providers.Providers.Data;
        if (ReferenceEquals(data, _brandsSource)) return _brands;
        _brandsSource = data;
        _brands = new Dictionary<string, ProviderBrand>(StringComparer.Ordinal);
        foreach (var node in data ?? [])
        {
            if (node is not JsonObject p || p["id"]?.GetValue<string>() is not { } id) continue;
            _brands[id] = new ProviderBrand(p["name"]?.GetValue<string>() ?? id, Str(p["icon"]), Str(p["templateId"]));
        }
        return _brands;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private void OnProviders(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Core.Api.Query<JsonArray>.Data)) View.OnLiveChanged();
    }

    private void OnPersisted(string id)
    {
        _persisted.Add(id);
        ScheduleRefetch();
    }

    /// <summary>
    /// Coalesce bursts (the usage writer persists in batches) into one refetch: the request lists, an open trace session (it shows the stored
    /// call now rather than at its next poll) and the persisted requests' details and traces (liveRequests.ts <c>useLiveRequestFeed</c>).
    /// </summary>
    private void ScheduleRefetch()
    {
        if (_refetchTimer is not null || !_active) return;
        _refetchTimer = _ctx.Queries.Time.CreateTimer(_ => _ctx.Queries.Dispatcher.Post(FlushRefetch), null, RefetchCoalesce, Timeout.InfiniteTimeSpan);
    }

    private void FlushRefetch()
    {
        _refetchTimer?.Dispose();
        _refetchTimer = null;
        _ctx.Queries.Invalidate("requests");
        _ctx.Queries.Invalidate("traces", "session");
        // the topology's activity feed: the stored row (with its tool calls) takes over from the live one
        _ctx.Queries.Invalidate("traces", "activity");
        foreach (var id in _persisted)
        {
            _ctx.Queries.Invalidate("request", id);
            _ctx.Queries.Invalidate("traces", "request", id);
        }
        _persisted.Clear();
    }
}
