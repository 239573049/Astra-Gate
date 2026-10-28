using System.Collections.Concurrent;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Tray;

/// <summary>Everything the tray panel shows (web components/tray/useTrayData.ts <c>TrayData</c>).</summary>
/// <param name="Settings">Server settings (the gateway address).</param>
/// <param name="Summary">Today's totals.</param>
/// <param name="Hourly">Today by hour (chart).</param>
/// <param name="Daily">Last 7 days by day (yesterday comparison); empty unless that section is on.</param>
/// <param name="Top">Today's top models.</param>
/// <param name="Plans">Subscription windows first, then API providers' balances / windows.</param>
/// <param name="HasSubscriptions">Some active subscription account exists (the quota block then explains a missing snapshot).</param>
/// <param name="Clients">Clients.</param>
/// <param name="Providers">Providers.</param>
/// <param name="UpdatedAt">When today's numbers were last fetched; null = never.</param>
/// <param name="Loading">Today's numbers are loading for the first time.</param>
/// <param name="Error">Today's numbers failed to load.</param>
public sealed record TrayData(
    SettingsDto? Settings, StatsSummaryDto? Summary, IReadOnlyList<TimeseriesPointDto> Hourly, IReadOnlyList<TimeseriesPointDto> Daily,
    IReadOnlyList<TopModelDto> Top, IReadOnlyList<TrayQuotaPlan> Plans, bool HasSubscriptions, IReadOnlyList<ClientInfoDto> Clients,
    IReadOnlyList<ProviderDto> Providers, DateTimeOffset? UpdatedAt, bool Loading, bool Error)
{
    /// <summary>Nothing loaded.</summary>
    public static TrayData Empty { get; } = new(null, null, [], [], [], [], false, [], [], null, false, false);
}

/// <summary>What the service fetches and polls (<c>useTrayData</c>'s options).</summary>
/// <param name="Prefs">Which sections (and title) need data.</param>
/// <param name="Enabled">The service runs (nothing is fetched otherwise; what was loaded stays).</param>
/// <param name="Background">Keep polling what the menu-bar title needs while the panel is not on screen.</param>
/// <param name="Live">The panel is on screen: every section polls and stale quota snapshots are refreshed from the upstream.</param>
/// <param name="TitleOnly">Fetch only what the menu-bar title needs (no section is shown).</param>
public sealed record TrayDataOptions(TrayPrefs Prefs, bool Enabled, bool Background, bool Live, bool TitleOnly = false);

/// <summary>Accounts whose quota was requested recently (shared by every panel in the app), so a failing probe is not retried in a loop.</summary>
public sealed class QuotaAttempts
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _at = new(StringComparer.Ordinal);

    /// <summary>The app-wide instance.</summary>
    public static QuotaAttempts Shared { get; } = new();

    /// <summary>Records an attempt for <paramref name="accountId"/> at <paramref name="now"/> unless one was made within <paramref name="window"/>; true = go ahead.</summary>
    public bool TryBegin(string accountId, DateTimeOffset now, TimeSpan window)
    {
        while (true)
        {
            if (_at.TryGetValue(accountId, out var last))
            {
                if (now - last < window) return false;
                if (_at.TryUpdate(accountId, now, last)) return true;
            }
            else if (_at.TryAdd(accountId, now)) return true;
        }
    }
}

/// <summary>
/// The tray panel's data (web components/tray/useTrayData.ts): several queries aggregated into one <see cref="TrayData"/>. Keys are the
/// pages' own (<see cref="StatsQueries"/>, <see cref="ListQueries"/>, the settings store), so the panel and the pages share one cache.
/// <list type="bullet">
/// <item>Polling is the service's own (15 s summary, 30 s chart / top models / clients, 60 s daily series / providers / accounts) because a
/// shared query keeps the options of whoever created it first. A hidden panel keeps polling only what the menu-bar title is computed from
/// (<see cref="TrayDataOptions.Background"/>), everything else waits until it is shown again.</item>
/// <item>Subscription quota snapshots older than 5 minutes are fetched again from the upstream (POST <c>/api/provider-accounts/{id}/quota</c>)
/// while the panel is on screen or the title shows quota — at most once per 5 minutes per account (<see cref="QuotaAttempts"/>) and never
/// retried on failure. API-key providers' balances come from the server's own background refresh through the providers query.</item>
/// </list>
/// Results arrive on the query client's UI dispatcher; <see cref="Changed"/> fires there.
/// </summary>
public sealed class TrayDataService : IDisposable
{
    /// <summary>Quota snapshots older than this are refetched from the upstream (<c>QUOTA_MAX_AGE_MS</c>).</summary>
    public static readonly TimeSpan QuotaMaxAge = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan SummaryEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ChartEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SlowEvery = TimeSpan.FromSeconds(60);

    private readonly ApiClient _api;
    private readonly QueryClient _queries;
    private readonly IClock _clock;
    private readonly TimeProvider _time;
    private readonly QuotaAttempts _attempts;
    private readonly Query<SettingsDto> _settings;
    private readonly Query<StatsSummaryDto> _summary;
    private readonly Query<List<TimeseriesPointDto>> _hourly;
    private readonly Query<List<TimeseriesPointDto>> _daily;
    private readonly Query<List<TopModelDto>> _top;
    private readonly Query<List<ProviderDto>> _providers;
    private readonly Query<List<ClientInfoDto>> _clients;
    private readonly ListQueries _lists;
    private readonly Dictionary<QueryBase, Watch> _watches = [];
    private readonly Dictionary<string, Query<List<ProviderAccountDto>>> _accounts = new(StringComparer.Ordinal);
    private TrayDataOptions _options = new(TrayPrefs.Default, false, false, false);
    private string _accountKey = "";
    private bool _disposed;

    /// <param name="api">The admin API.</param>
    /// <param name="queries">The shared cache.</param>
    /// <param name="clock">The page clock (the chart's current hour, the quota age).</param>
    /// <param name="time">Polling timers; tests inject a manual one.</param>
    /// <param name="attempts">Quota attempts; <see cref="QuotaAttempts.Shared"/> by default.</param>
    public TrayDataService(ApiClient api, QueryClient queries, IClock clock, TimeProvider? time = null, QuotaAttempts? attempts = null)
    {
        _api = api;
        _queries = queries;
        _clock = clock;
        _time = time ?? queries.Time;
        _attempts = attempts ?? QuotaAttempts.Shared;
        var stats = new StatsQueries(api, queries, (int)LocalTime.ZoneOf(clock).TotalMinutes);
        _lists = new ListQueries(api, queries);
        _settings = new SettingsStore(queries, api).Settings;
        _summary = stats.Summary(StatsRange.Today, null, null);
        _hourly = stats.Timeseries(StatsRange.Today, "hour", "provider", null, null);
        _daily = stats.Timeseries(StatsRange.Days7, "day", "provider", null, null);
        _top = stats.TopModels(StatsRange.Today, null, 3, null);
        _providers = _lists.Providers();
        _clients = _lists.Clients();
    }

    /// <summary>The data changed (on the UI dispatcher).</summary>
    public event Action? Changed;

    /// <summary>The current options.</summary>
    public TrayDataOptions Options => _options;

    /// <summary>The queries observed right now (key "a/b/c") and their poll interval (null = fetched, not polled). For tests and diagnostics.</summary>
    public IReadOnlyDictionary<string, TimeSpan?> Observed => _watches.ToDictionary(w => w.Key.Key.ToString(), w => w.Value.Every, StringComparer.Ordinal);

    /// <summary>Sets what is fetched and polled; cheap when nothing changed.</summary>
    public void Configure(TrayDataOptions options)
    {
        if (_disposed) return;
        _options = options;
        Apply();
    }

    /// <summary>The panel's refresh button and every opening: today's numbers, providers, clients and accounts again (TrayPanelPage <c>refresh</c>).</summary>
    public void Refresh()
    {
        _queries.Invalidate("stats");
        _queries.Invalidate("providers");
        _queries.Invalidate("clients");
    }

    /// <summary>Everything the panel shows right now.</summary>
    public TrayData Snapshot()
    {
        var providers = _providers.Data ?? [];
        var rows = AccountRows();
        var plans = TrayMath.QuotaPlans(rows);
        plans.AddRange(TrayMath.ProviderQuotaPlans(providers));
        return new TrayData(
            _settings.Data, _summary.Data, _hourly.Data ?? [], _daily.Data ?? [], _top.Data ?? [], plans,
            rows.Any(r => r.Account.Status == "active"), _clients.Data ?? [], providers,
            _summary.HasData ? _summary.DataUpdatedAt : null, _summary.IsLoading, _summary.IsError);
    }

    /// <summary>The menu-bar title for the current prefs and data.</summary>
    public TrayTitleText Title(bool running) => TrayTitle.Compute(_options.Prefs.Title, running, _summary.Data, Snapshot().Plans);

    /// <summary>Points <paramref name="kind"/> at <paramref name="providerId"/> (web <c>useSetBinding</c>); refreshes the lists on success.</summary>
    public async Task SetBindingAsync(string kind, string providerId)
    {
        await _api.PutAsync<BindingInput, ClientInfoDto>("/api/clients/" + kind + "/binding", new BindingInput(providerId, null));
        _queries.Invalidate("clients");
        _queries.Invalidate("providers");
    }

    /// <summary>Stops every observation and timer.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var w in _watches.Values) w.Dispose();
        _watches.Clear();
    }

    private bool TitleSummary => _options.Prefs.Title is not TrayTitleMode.None and not TrayTitleMode.Quota;

    private bool TitleQuota => _options.Prefs.Title is TrayTitleMode.Quota or TrayTitleMode.Alerts;

    private bool On(TraySection s) => !_options.TitleOnly && _options.Prefs.Sections.TryGetValue(s, out var on) && on;

    private bool NeedQuota => On(TraySection.Quota) || TitleQuota;

    private bool _applying;
    private bool _applyAgain;

    // Re-entrant safe: activating a query can report its state synchronously (an inline dispatcher), which comes back here.
    private void Apply()
    {
        if (_applying)
        {
            _applyAgain = true;
            return;
        }
        _applying = true;
        try
        {
            do
            {
                _applyAgain = false;
                ApplyOnce();
            } while (_applyAgain && !_disposed);
        }
        finally
        {
            _applying = false;
        }
    }

    private void ApplyOnce()
    {
        var o = _options;
        var e = o.Enabled;
        // Polls while on screen; a hidden panel only for what the title is computed from (refetchIntervalInBackground).
        TimeSpan? Poll(TimeSpan every, bool forTitle) => o.Live || (o.Background && forTitle) ? every : null;

        Set(_settings, e, null);
        Set(_summary, e, Poll(SummaryEvery, TitleSummary));
        Set(_hourly, e && On(TraySection.Chart), Poll(ChartEvery, false));
        Set(_daily, e && On(TraySection.CompareYesterday), Poll(SlowEvery, false));
        Set(_top, e && On(TraySection.TopModel), Poll(ChartEvery, false));
        Set(_providers, e && (NeedQuota || On(TraySection.Clients)), Poll(SlowEvery, TitleQuota));
        Set(_clients, e && On(TraySection.Clients), Poll(ChartEvery, false));

        var subscription = SubscriptionProviders().Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in subscription)
            if (!_accounts.ContainsKey(id)) _accounts[id] = _lists.Accounts(id);
        foreach (var (id, q) in _accounts.ToList())
            Set(q, e && NeedQuota && subscription.Contains(id), Poll(SlowEvery, TitleQuota));
        RefreshStaleQuota();
    }

    private void Set(QueryBase query, bool needed, TimeSpan? poll)
    {
        if (!needed)
        {
            if (_watches.Remove(query, out var gone)) gone.Dispose();
            return;
        }
        if (!_watches.TryGetValue(query, out var w))
        {
            w = new Watch(this, query);
            _watches[query] = w;
        }
        w.SetPoll(poll);
    }

    private IEnumerable<ProviderDto> SubscriptionProviders()
        => (_providers.Data ?? []).Where(p => p.Enabled && p.AuthScheme == "oauth-subscription");

    private List<(ProviderDto Provider, ProviderAccountDto Account)> AccountRows()
    {
        var rows = new List<(ProviderDto, ProviderAccountDto)>();
        foreach (var p in SubscriptionProviders())
            if (_accounts.TryGetValue(p.Id, out var q) && q.Data is { } list)
                rows.AddRange(list.Select(a => (p, a)));
        return rows;
    }

    /// <summary>Stale quota snapshots: ask the upstream again, at most once per 5 minutes per account; a failure is left alone.</summary>
    private void RefreshStaleQuota()
    {
        var rows = AccountRows();
        var key = string.Join('|', rows.Select(r => $"{r.Account.Id}:{r.Account.Status}:{FetchedAt(r.Account)}"));
        var active = _options.Enabled && NeedQuota && (_options.Live || TitleQuota);
        var changed = key != _accountKey;
        _accountKey = key;
        if (!active || (!changed && rows.Count == 0)) return;
        var now = _clock.Now;
        foreach (var (provider, account) in rows)
        {
            if (account.Status != "active") continue;
            if (FetchedAt(account) is { } at && now - at < QuotaMaxAge) continue;
            if (!_attempts.TryBegin(account.Id, now, QuotaMaxAge)) continue;
            _ = RefreshQuotaAsync(provider.Id, account.Id);
        }
    }

    private async Task RefreshQuotaAsync(string providerId, string accountId)
    {
        try
        {
            var account = await _api.PostAsync<ProviderAccountDto>("/api/provider-accounts/" + ApiClient.Enc(accountId) + "/quota");
            _queries.Invalidate(new QueryKey("providers", account?.ProviderId ?? providerId, "accounts"));
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            // No retry: the next attempt is allowed after QuotaMaxAge (useTrayData's onError: () => {}).
            _queries.Invalidate(new QueryKey("providers", providerId, "accounts"));
        }
    }

    private static DateTimeOffset? FetchedAt(ProviderAccountDto a)
        => a.Quota is System.Text.Json.Nodes.JsonObject q && q["fetchedAtUtc"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s)
           && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    private void OnQueryChanged(QueryBase query, string? property)
    {
        if (_disposed || property is not ("Data" or "Status" or "IsFetching")) return;
        // New providers can bring new subscription accounts; new accounts can bring stale snapshots.
        if (property == "Data" && (query == _providers || query is Query<List<ProviderAccountDto>>)) Apply();
        Changed?.Invoke();
    }

    /// <summary>One observed query with its own poll timer.</summary>
    private sealed class Watch : IDisposable
    {
        private readonly TrayDataService _owner;
        private readonly QueryBase _query;
        private readonly IDisposable _token;
        private ITimer? _timer;
        private TimeSpan? _every;

        public Watch(TrayDataService owner, QueryBase query)
        {
            _owner = owner;
            _query = query;
            _query.PropertyChanged += OnChanged;
            _token = Activate(query);
        }

        private static IDisposable Activate(QueryBase q) => q switch
        {
            Query<SettingsDto> s => s.Activate(),
            Query<StatsSummaryDto> s => s.Activate(),
            Query<List<TimeseriesPointDto>> s => s.Activate(),
            Query<List<TopModelDto>> s => s.Activate(),
            Query<List<ProviderDto>> s => s.Activate(),
            Query<List<ClientInfoDto>> s => s.Activate(),
            Query<List<ProviderAccountDto>> s => s.Activate(),
            _ => throw new InvalidOperationException("unexpected query type"),
        };

        private Task Refetch() => _query switch
        {
            Query<SettingsDto> s => s.RefetchAsync(),
            Query<StatsSummaryDto> s => s.RefetchAsync(),
            Query<List<TimeseriesPointDto>> s => s.RefetchAsync(),
            Query<List<TopModelDto>> s => s.RefetchAsync(),
            Query<List<ProviderDto>> s => s.RefetchAsync(),
            Query<List<ClientInfoDto>> s => s.RefetchAsync(),
            Query<List<ProviderAccountDto>> s => s.RefetchAsync(),
            _ => Task.CompletedTask,
        };

        public void SetPoll(TimeSpan? every)
        {
            if (every == _every) return;
            _every = every;
            _timer?.Dispose();
            // One-shot, re-armed on every tick: the same on a real clock and on a test's manual one.
            _timer = every is { } e ? _owner._time.CreateTimer(_ => Tick(e), null, e, Timeout.InfiniteTimeSpan) : null;
        }

        public TimeSpan? Every => _every;

        private void Tick(TimeSpan every)
        {
            _ = Refetch();
            if (_every == every) _timer?.Change(every, Timeout.InfiniteTimeSpan);
        }

        private void OnChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => _owner.OnQueryChanged(_query, e.PropertyName);

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
            _query.PropertyChanged -= OnChanged;
            _token.Dispose();
        }
    }
}
