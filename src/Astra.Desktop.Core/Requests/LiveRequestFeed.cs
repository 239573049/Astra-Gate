using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Requests;

/// <summary>
/// Whether the app's window is on screen. The web closes the live stream while its tab is hidden (the server records the live log
/// only while a stream is open); the desktop's equivalent is the window being hidden or minimized. The app implements it over its
/// window; tests use <see cref="ManualVisibility"/>.
/// </summary>
public interface IWindowVisibility
{
    /// <summary>True while the window is shown.</summary>
    bool IsVisible { get; }

    /// <summary>Raised (any thread) when <see cref="IsVisible"/> changes.</summary>
    event Action? Changed;
}

/// <summary>A visibility the owner sets by hand (the view sets it from its window; tests flip it).</summary>
public sealed class ManualVisibility : IWindowVisibility
{
    private volatile bool _visible = true;

    /// <inheritdoc />
    public bool IsVisible => _visible;

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>Sets the state and raises <see cref="Changed"/> when it differs.</summary>
    public void Set(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        Changed?.Invoke();
    }
}

/// <summary>
/// Keeps the live request stream open while started and fills a <see cref="LiveRequestStore"/> from it. Port of <c>connect()</c> in
/// <c>web/src/api/liveRequests.ts</c> on top of <see cref="ApiClient.StreamReconnectingAsync{T}"/> (which owns the reconnect backoff,
/// 1 s doubling to 15 s):
/// <list type="bullet">
/// <item>Hidden window: the connection is closed after <see cref="HiddenGrace"/> (a quick switch must not drop and resync the feed) and
/// nothing reconnects until the window is visible again; then <c>onReconnect</c> lets the list catch up with what was written meanwhile.</item>
/// <item>A dropped connection forgets its rows (their "persisted" event may never come; the snapshot on reconnect re-sends what is still in flight) and calls <c>onReconnect</c>.</item>
/// <item>"persisted" tells the page the stored row exists (<c>onPersisted</c>); the live copy is kept for <see cref="PersistedLinger"/> so cells do not blank before the list refetch lands.</item>
/// </list>
/// Every store mutation happens on the UI thread through the dispatcher, as the store is not thread safe.
/// </summary>
public sealed class LiveRequestFeed
{
    /// <summary>The live stream's path.</summary>
    public const string Path = "/api/requests/live";

    /// <summary>How long a hidden window keeps the stream open (web <c>HIDDEN_GRACE_MS</c>).</summary>
    public static readonly TimeSpan HiddenGrace = TimeSpan.FromSeconds(10);

    /// <summary>How long a persisted row stays in the store (web: 1500 ms).</summary>
    public static readonly TimeSpan PersistedLinger = TimeSpan.FromMilliseconds(1500);

    private readonly ApiClient _api;
    private readonly LiveRequestStore _store;
    private readonly IUiDispatcher _ui;
    private readonly IWindowVisibility _visibility;
    private readonly Action<string> _onPersisted;
    private readonly Action _onReconnect;
    private readonly TimeProvider _time;
    private readonly TimeSpan _hiddenGrace;
    private readonly TimeSpan _linger;
    private CancellationTokenSource? _stop;
    private Task? _run;

    /// <param name="api">The API client.</param>
    /// <param name="store">The rows' home.</param>
    /// <param name="ui">UI-thread marshalling for store changes and the callbacks.</param>
    /// <param name="visibility">Window visibility; null = always visible.</param>
    /// <param name="onPersisted">A request was stored (the page coalesces list / detail refetches).</param>
    /// <param name="onReconnect">The feed dropped or came back: refetch the list.</param>
    /// <param name="time">Clock for waits (tests inject a manual one).</param>
    /// <param name="hiddenGrace">Override of <see cref="HiddenGrace"/>.</param>
    /// <param name="linger">Override of <see cref="PersistedLinger"/>.</param>
    public LiveRequestFeed(ApiClient api, LiveRequestStore store, IUiDispatcher ui, IWindowVisibility? visibility, Action<string> onPersisted, Action onReconnect,
        TimeProvider? time = null, TimeSpan? hiddenGrace = null, TimeSpan? linger = null)
    {
        _api = api;
        _store = store;
        _ui = ui;
        _visibility = visibility ?? new ManualVisibility();
        _onPersisted = onPersisted;
        _onReconnect = onReconnect;
        _time = time ?? TimeProvider.System;
        _hiddenGrace = hiddenGrace ?? HiddenGrace;
        _linger = linger ?? PersistedLinger;
    }

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool Running => _stop is not null;

    /// <summary>Opens the stream (and keeps it open). Idempotent.</summary>
    public void Start()
    {
        if (_stop is not null) return;
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _run = Task.Run(() => RunAsync(token));
    }

    /// <summary>Closes the stream and forgets its rows (web: the consumer count reached zero).</summary>
    public void Stop()
    {
        var stop = _stop;
        if (stop is null) return;
        _stop = null;
        stop.Cancel();
        _ui.Post(() => _store.Reset(false));
    }

    /// <summary>Completes when the run loop has ended after <see cref="Stop"/> (tests await it).</summary>
    public Task Completion => _run ?? Task.CompletedTask;

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // Hidden: stay disconnected until the window is visible again.
                if (!_visibility.IsVisible)
                {
                    await WaitVisibleAsync(stop).ConfigureAwait(false);
                    // Back in view: catch the list up with what was written while the feed was off.
                    _ui.Post(_onReconnect);
                }
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
                var watcher = WatchHiddenAsync(connection);
                try
                {
                    var options = new SseReconnectOptions
                    {
                        Time = _time,
                        OnOpen = () => _ui.Post(() => _store.SetConnected(true)),
                        // Rows of the dropped connection may never see their "persisted" event: forget them and let the list catch up.
                        OnDrop = _ => _ui.Post(() =>
                        {
                            _store.Reset(false);
                            _onReconnect();
                        }),
                        Gate = ct => WaitVisibleAsync(ct),
                    };
                    await foreach (var e in _api.StreamReconnectingAsync<RequestLiveEventDto>(Path, options, connection.Token).ConfigureAwait(false))
                        Handle(e);
                }
                finally
                {
                    await connection.CancelAsync().ConfigureAwait(false);
                    await watcher.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                // The hidden grace ran out: this connection was closed on purpose.
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (stop.IsCancellationRequested) return;
            _ui.Post(() =>
            {
                _store.Reset(false);
                _onReconnect();
            });
        }
    }

    private void Handle(RequestLiveEventDto e)
    {
        if (string.IsNullOrEmpty(e.Request?.Id)) return;
        var id = e.Request.Id;
        if (e.Type == "persisted")
        {
            _ui.Post(() => _onPersisted(id));
            // The stored row replaces this one once the list refetch lands; until then the cells keep showing it.
            _ = Task.Delay(_linger, _time).ContinueWith(_ => _ui.Post(() => _store.Delete(id)), TaskScheduler.Default);
        }
        else _ui.Post(() => _store.Set(e.Request));
    }

    /// <summary>Cancels <paramref name="connection"/> once the window has been hidden for the grace period.</summary>
    private async Task WatchHiddenAsync(CancellationTokenSource connection)
    {
        var ct = connection.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await WaitHiddenAsync(ct).ConfigureAwait(false);
                await Task.Delay(_hiddenGrace, _time, ct).ConfigureAwait(false);
                if (!_visibility.IsVisible)
                {
                    await connection.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task WaitVisibleAsync(CancellationToken ct) => WaitAsync(() => _visibility.IsVisible, ct);

    private Task WaitHiddenAsync(CancellationToken ct) => WaitAsync(() => !_visibility.IsVisible, ct);

    private async Task WaitAsync(Func<bool> condition, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            if (condition()) tcs.TrySetResult();
        }
        _visibility.Changed += OnChanged;
        try
        {
            if (condition()) return;
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _visibility.Changed -= OnChanged;
        }
    }
}
