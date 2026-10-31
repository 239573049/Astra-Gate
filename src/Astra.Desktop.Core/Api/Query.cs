using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Astra.Core;

namespace Astra.Desktop.Core.Api;

/// <summary>
/// Runs a callback on the UI thread. Desktop.Core has no Avalonia reference, so the app injects
/// <c>Dispatcher.UIThread.Post</c>; <see cref="ImmediateDispatcher"/> (the default) runs inline, which is what tests and
/// headless use want.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> on the UI thread (inline when already there is fine).</summary>
    void Post(Action action);
}

/// <summary>Runs the callback on the calling thread.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    /// <summary>Shared instance.</summary>
    public static readonly ImmediateDispatcher Instance = new();

    /// <inheritdoc />
    public void Post(Action action) => action();
}

/// <summary>
/// A react-query key: an ordered list of string parts. <see cref="StartsWith"/> is react-query's prefix matching, which
/// <see cref="QueryClient.Invalidate(QueryKey)"/> uses (web: <c>invalidateQueries({ queryKey: ['requests'] })</c> hits every
/// <c>['requests', filter]</c>). Parameter objects must be flattened to strings by the caller.
/// </summary>
public readonly struct QueryKey : IEquatable<QueryKey>
{
    private readonly string[]? _parts;

    /// <summary>Creates a key.</summary>
    public QueryKey(params string[] parts) => _parts = parts;

    /// <summary>The parts (empty for <c>default</c>).</summary>
    public IReadOnlyList<string> Parts => _parts ?? [];

    /// <summary>Shorthand for the constructor.</summary>
    public static QueryKey Of(params string[] parts) => new(parts);

    /// <summary>True when <paramref name="prefix"/> equals the first parts of this key.</summary>
    public bool StartsWith(QueryKey prefix)
    {
        var a = Parts;
        var b = prefix.Parts;
        if (b.Count > a.Count) return false;
        for (var i = 0; i < b.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    /// <inheritdoc />
    public bool Equals(QueryKey other) => Parts.SequenceEqual(other.Parts);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is QueryKey k && Equals(k);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var p in Parts) h.Add(p);
        return h.ToHashCode();
    }

    /// <inheritdoc />
    public override string ToString() => string.Join('/', Parts);

    /// <summary>Implicit from a single part: <c>QueryKey k = "providers"</c>.</summary>
    public static implicit operator QueryKey(string part) => new(part);
}

/// <summary>Lifecycle of a query's data (react-query's <c>status</c>).</summary>
public enum QueryStatus
{
    /// <summary>Nothing fetched yet.</summary>
    Pending,

    /// <summary>The last fetch succeeded (or data was set).</summary>
    Success,

    /// <summary>The last fetch failed; <see cref="Query{T}.Data"/> still holds the last good value, as in react-query.</summary>
    Error,
}

/// <summary>Per-query behaviour; the defaults are the web QueryClient's (web/src/main.tsx: staleTime 5 s, retry once unless 4xx).</summary>
/// <typeparam name="T">The query's data type.</typeparam>
public sealed class QueryOptions<T>
{
    /// <summary>How long fetched data counts as fresh; an <see cref="Query{T}.Activate"/> within it does not refetch.</summary>
    public TimeSpan StaleTime { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Poll interval while the query has an active observer; null (or a null result) means no polling. Receives the latest data, like react-query's function form (web useTunnel).</summary>
    public Func<T?, TimeSpan?>? RefetchInterval { get; init; }

    /// <summary>Extra attempts after a failure (web default 1; 4xx answers are never retried, see <see cref="ShouldRetry"/>).</summary>
    public int Retries { get; init; } = 1;

    /// <summary>Wait between attempts (react-query: 1 s).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Whether an error may be retried; default: not a 4xx <see cref="ApiException"/> (web main.tsx).</summary>
    public Func<Exception, bool> ShouldRetry { get; init; } = e => e is not ApiException { IsClientError: true };

    /// <summary>
    /// react-query's <c>gcTime</c>, opt-in: once nobody has observed the query for this long its data is dropped and it leaves the
    /// cache (an activation after that fetches again). Null (the default) keeps it for the client's lifetime. Set it on queries keyed
    /// by an id whose answers are large — a request's detail or body — or the cache grows with every one ever opened.
    /// </summary>
    public TimeSpan? CacheTime { get; init; }

    /// <summary>
    /// react-query's structural sharing, opt-in: when this returns true for the held data and a new answer (a refetch, a poll or
    /// <see cref="Query{T}.SetData"/>), the held instance is kept and <see cref="Query{T}.Data"/> is not raised, so a view that redraws
    /// from it does nothing when a poll brings nothing new. <see cref="QueryData.JsonEqual{T}"/> compares API answers. Null (the
    /// default) raises <c>Data</c> for every answer.
    /// </summary>
    public Func<T, T, bool>? SameData { get; init; }
}

/// <summary>Helpers for <see cref="QueryOptions{T}.SameData"/>.</summary>
public static class QueryData
{
    /// <summary>
    /// Deep equality of two API answers through the JSON they serialize to (their types are the ones <see cref="ApiClient"/> reads them
    /// with, registered in <see cref="JsonContexts"/>); records alone compare their lists by reference, which every fetch replaces.
    /// </summary>
    public static bool JsonEqual<T>(T a, T b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        var info = JsonContexts.Info<T>(Json.Api);
        return JsonSerializer.SerializeToUtf8Bytes(a, info).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(b, info));
    }
}

/// <summary>Non-generic face of a cached query, so the <see cref="QueryClient"/> can invalidate by key.</summary>
public abstract class QueryBase : INotifyPropertyChanged
{
    /// <summary>The cache key.</summary>
    public abstract QueryKey Key { get; }

    /// <summary>Marks the data stale and, while observed, refetches it (react-query <c>invalidateQueries</c>).</summary>
    public abstract void Invalidate();

    internal abstract void Shutdown();

    /// <summary>The fetch that is running right now, or null; completes after its result was applied on the UI thread.</summary>
    internal abstract Task? InFlight { get; }

    /// <summary>The client that hands this query out (it takes the query back after a <see cref="QueryOptions{T}.CacheTime"/> eviction).</summary>
    internal QueryClient? Owner { get; init; }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/>; call on the UI thread.</summary>
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One cached request with react-query's state machine: loading / error / data, stale time, polling while observed,
/// single-flight fetches, retry, invalidate. Created through <see cref="QueryClient.Query{T}"/> so two stores asking for the
/// same key share it (web: shared <c>queryKey</c>s).
/// <para>
/// View models bind to <see cref="Data"/> / <see cref="IsLoading"/> / <see cref="Error"/> through
/// <see cref="INotifyPropertyChanged"/>; all state changes are posted through the client's <see cref="IUiDispatcher"/>, so
/// property reads and events happen on the UI thread. A page calls <see cref="Activate"/> when shown and disposes the
/// token when hidden: polling runs only while at least one observer is active (react-query: mounted observers).
/// </para>
/// Differences from react-query: unobserved queries are only collected when they opt in (<see cref="QueryOptions{T}.CacheTime"/>), no
/// window-focus / reconnect refetch (the shell can call <see cref="RefetchAsync"/>), structural sharing only where a query opts in
/// (<see cref="QueryOptions{T}.SameData"/>; whole answers, not their parts), and
/// <see cref="QueryClient.Query{T}"/> ignores the options of a second caller (first one wins).
/// </summary>
/// <typeparam name="T">The data type.</typeparam>
public sealed class Query<T> : QueryBase
{
    private readonly Func<CancellationToken, Task<T>> _fetch;
    private readonly QueryOptions<T> _options;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _inflight;
    private int _observers;
    private bool _enabled = true;
    private bool _invalidated;
    private ITimer? _timer;
    private ITimer? _gcTimer;
    private bool _evicted;

    // UI-thread state (written only inside _ui.Post).
    private T? _data;
    private bool _hasData;
    private Exception? _error;
    private QueryStatus _status;
    private bool _isFetching;
    private DateTimeOffset? _updatedAt;

    internal Query(QueryKey key, Func<CancellationToken, Task<T>> fetch, QueryOptions<T> options, IUiDispatcher ui, TimeProvider time)
    {
        Key = key;
        _fetch = fetch;
        _options = options;
        _ui = ui;
        _time = time;
    }

    /// <inheritdoc />
    public override QueryKey Key { get; }

    internal override Task? InFlight
    {
        get
        {
            lock (_gate) return _inflight;
        }
    }

    /// <summary>The last successful result (kept across a failed refetch); default until the first success.</summary>
    public T? Data => _data;

    /// <summary>True once <see cref="Data"/> holds a result (a legitimate null/empty result still counts).</summary>
    public bool HasData => _hasData;

    /// <summary>The last fetch's failure, cleared by the next success.</summary>
    public Exception? Error => _error;

    /// <summary>True when the last fetch failed.</summary>
    public bool IsError => _status == QueryStatus.Error;

    /// <summary>Pending / Success / Error.</summary>
    public QueryStatus Status => _status;

    /// <summary>A request is running (initial or refetch).</summary>
    public bool IsFetching => _isFetching;

    /// <summary>First load: fetching and nothing to show yet (react-query <c>isLoading</c>).</summary>
    public bool IsLoading => _isFetching && !_hasData;

    /// <summary>When <see cref="Data"/> was last set.</summary>
    public DateTimeOffset? DataUpdatedAt => _updatedAt;

    /// <summary>react-query's <c>enabled</c>: a disabled query neither fetches on activation nor polls. Enabling an observed query fetches it.</summary>
    public bool Enabled
    {
        get => Volatile.Read(ref _enabled);
        set
        {
            lock (_gate)
            {
                if (_enabled == value) return;
                _enabled = value;
            }
            if (value) OnObserved(); else StopTimer();
        }
    }

    /// <summary>Data is missing, was invalidated, or is older than <see cref="QueryOptions{T}.StaleTime"/>.</summary>
    public bool IsStale
    {
        get
        {
            lock (_gate)
            {
                var updated = _updatedAt;
                return _invalidated || !_hasData || updated is null || _time.GetUtcNow() - updated.Value >= _options.StaleTime;
            }
        }
    }

    /// <summary>Whether any observer is active.</summary>
    public bool IsObserved => Volatile.Read(ref _observers) > 0;

    /// <summary>
    /// Registers an observer (a mounted page): fetches when the data is stale and starts polling. Dispose the result when
    /// the page goes away; polling stops with the last observer.
    /// </summary>
    public IDisposable Activate()
    {
        bool readopt;
        lock (_gate)
        {
            _observers++;
            _gcTimer?.Dispose();
            _gcTimer = null;
            readopt = _evicted;
            _evicted = false;
        }
        if (readopt) Owner?.Readopt(this);
        OnObserved();
        return new Observer(this);
    }

    private void OnObserved()
    {
        if (!IsObserved || !Enabled) return;
        if (IsStale) _ = RefetchAsync();
        else SchedulePoll();
    }

    private void Release()
    {
        bool last;
        lock (_gate) last = --_observers == 0;
        if (!last) return;
        StopTimer();
        ScheduleEviction();
    }

    private void ScheduleEviction()
    {
        lock (_gate)
        {
            if (_options.CacheTime is not { } after || _observers > 0 || _evicted || _lifetime.IsCancellationRequested) return;
            _gcTimer?.Dispose();
            _gcTimer = _time.CreateTimer(_ => Evict(), null, after, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private void Evict()
    {
        lock (_gate)
        {
            _gcTimer?.Dispose();
            _gcTimer = null;
            if (_observers > 0 || _evicted) return;
            // A fetch still running: try again after it (its completion would otherwise refill the dropped data).
            if (_inflight is not null)
            {
                _gcTimer = _time.CreateTimer(_ => Evict(), null, _options.CacheTime!.Value, System.Threading.Timeout.InfiniteTimeSpan);
                return;
            }
            _evicted = true;
        }
        Owner?.Forget(this);
        _ui.Post(() =>
        {
            lock (_gate)
                if (!_evicted) return;
            _data = default;
            _updatedAt = null;
            Set(ref _hasData, false, nameof(HasData), nameof(IsLoading));
            Set(ref _error, null, nameof(Error));
            Set(ref _status, QueryStatus.Pending, nameof(Status), nameof(IsError));
            Raise(nameof(Data));
            Raise(nameof(DataUpdatedAt));
        });
    }

    /// <summary>
    /// Fetches now (single-flight: a running fetch is joined). Never throws for a failed request, the failure lands in
    /// <see cref="Error"/>; the returned task completes after the state was applied on the UI thread.
    /// </summary>
    public Task RefetchAsync()
    {
        TaskCompletionSource tcs;
        lock (_gate)
        {
            if (_inflight is not null) return _inflight;
            if (_lifetime.IsCancellationRequested) return Task.CompletedTask;
            tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inflight = tcs.Task;
            _invalidated = false;
        }
        _ = RunAsync(tcs);
        return tcs.Task;
    }

    /// <inheritdoc />
    public override void Invalidate()
    {
        lock (_gate) _invalidated = true;
        if (IsObserved && Enabled) _ = RefetchAsync();
    }

    /// <summary>Replaces the data without fetching (react-query <c>setQueryData</c>, e.g. a mutation's answer); marks it fresh.</summary>
    public void SetData(T data)
    {
        var now = _time.GetUtcNow();
        _ui.Post(() =>
        {
            ApplySuccess(data, now);
        });
    }

    private async Task RunAsync(TaskCompletionSource tcs)
    {
        _ui.Post(() => Set(ref _isFetching, true, nameof(IsFetching), nameof(IsLoading)));
        T? result = default;
        Exception? failure = null;
        var token = _lifetime.Token;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    result = await _fetch(token).ConfigureAwait(false);
                    failure = null;
                    break;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    failure = null;
                    result = default;
                    token.ThrowIfCancellationRequested();
                }
                catch (Exception e)
                {
                    failure = e;
                    if (attempt >= _options.Retries || !_options.ShouldRetry(e)) break;
                    await Task.Delay(_options.RetryDelay, _time, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The client is gone: leave the state alone.
            lock (_gate) _inflight = null;
            tcs.TrySetResult();
            return;
        }

        var now = _time.GetUtcNow();
        _ui.Post(() =>
        {
            if (failure is null) ApplySuccess(result!, now);
            else
            {
                Set(ref _error, failure, nameof(Error));
                Set(ref _status, QueryStatus.Error, nameof(Status), nameof(IsError));
            }
            Set(ref _isFetching, false, nameof(IsFetching), nameof(IsLoading));
            bool again;
            lock (_gate)
            {
                _inflight = null;
                again = _invalidated && _observers > 0 && _enabled;
            }
            tcs.TrySetResult();
            if (again) _ = RefetchAsync(); else SchedulePoll();
        });
    }

    // Runs on the UI thread.
    private void ApplySuccess(T data, DateTimeOffset at)
    {
        // Structural sharing (opt-in): an answer equal to the held data keeps the held instance and raises no Data.
        var same = _hasData && _options.SameData is { } equal && equal(_data!, data);
        if (!same) _data = data;
        _updatedAt = at;
        Set(ref _hasData, true, nameof(HasData), nameof(IsLoading));
        Set(ref _error, null, nameof(Error));
        Set(ref _status, QueryStatus.Success, nameof(Status), nameof(IsError));
        if (!same) Raise(nameof(Data));
        Raise(nameof(DataUpdatedAt));
    }

    private void SchedulePoll()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_observers == 0 || !_enabled || _options.RefetchInterval is null || _lifetime.IsCancellationRequested) return;
            var every = _options.RefetchInterval(_data);
            if (every is null) return;
            _timer = _time.CreateTimer(_ => _ = RefetchAsync(), null, every.Value, System.Threading.Timeout.InfiniteTimeSpan);
        }
    }

    private void StopTimer()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    internal override void Shutdown()
    {
        _lifetime.Cancel();
        StopTimer();
        lock (_gate)
        {
            _gcTimer?.Dispose();
            _gcTimer = null;
        }
    }

    private void Set<TField>(ref TField field, TField value, params string[] names)
    {
        if (EqualityComparer<TField>.Default.Equals(field, value)) return;
        field = value;
        foreach (var n in names) Raise(n);
    }

    private sealed class Observer(Query<T> owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release();
        }
    }
}

/// <summary>
/// The query cache (react-query's <c>QueryClient</c>): hands out one <see cref="Query{T}"/> per key and invalidates by key
/// prefix. One instance per app, created by the host with the real UI dispatcher.
/// </summary>
public sealed class QueryClient : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<QueryKey, QueryBase> _queries = [];

    /// <param name="dispatcher">UI-thread marshalling; null runs callbacks inline.</param>
    /// <param name="time">Clock for staleness, polling and retry waits; tests inject a manual one.</param>
    public QueryClient(IUiDispatcher? dispatcher = null, TimeProvider? time = null)
    {
        Dispatcher = dispatcher ?? ImmediateDispatcher.Instance;
        Time = time ?? TimeProvider.System;
    }

    /// <summary>The injected UI dispatcher.</summary>
    public IUiDispatcher Dispatcher { get; }

    /// <summary>The injected clock.</summary>
    public TimeProvider Time { get; }

    /// <summary>
    /// The query cached under <paramref name="key"/>, created on first use with <paramref name="fetch"/> and
    /// <paramref name="options"/> (later callers' arguments are ignored). Throws when the key already holds another data type.
    /// </summary>
    public Query<T> Query<T>(QueryKey key, Func<CancellationToken, Task<T>> fetch, QueryOptions<T>? options = null)
    {
        lock (_gate)
        {
            if (_queries.TryGetValue(key, out var existing))
                return existing as Query<T> ?? throw new InvalidOperationException($"Query '{key}' is cached with a different data type.");
            var q = new Query<T>(key, fetch, options ?? new QueryOptions<T>(), Dispatcher, Time) { Owner = this };
            _queries[key] = q;
            return q;
        }
    }

    /// <summary>Drops an evicted query from the cache (unless its key has a newer query by now).</summary>
    internal void Forget(QueryBase query)
    {
        lock (_gate)
            if (_queries.TryGetValue(query.Key, out var cached) && ReferenceEquals(cached, query)) _queries.Remove(query.Key);
    }

    /// <summary>Takes an evicted query back that is observed again (a holder kept it), so invalidation reaches it.</summary>
    internal void Readopt(QueryBase query)
    {
        lock (_gate) _queries.TryAdd(query.Key, query);
    }

    /// <summary>
    /// A query that polls every <paramref name="interval"/> while observed (the <c>refetchInterval</c> hooks of
    /// web/src/api/hooks.ts: health 5 s, plugins 10 s, tunnel…). Shorthand for <see cref="Query{T}"/> with
    /// <see cref="QueryOptions{T}.RefetchInterval"/>.
    /// </summary>
    public Query<T> Polling<T>(QueryKey key, Func<CancellationToken, Task<T>> fetch, TimeSpan interval, TimeSpan? staleTime = null) =>
        Query(key, fetch, new QueryOptions<T> { RefetchInterval = _ => interval, StaleTime = staleTime ?? TimeSpan.FromSeconds(5) });

    /// <summary>The cached query for <paramref name="key"/> if it exists with that type.</summary>
    public Query<T>? Find<T>(QueryKey key)
    {
        lock (_gate) return _queries.TryGetValue(key, out var q) ? q as Query<T> : null;
    }

    /// <summary>Invalidates every query whose key starts with <paramref name="prefix"/> (react-query <c>invalidateQueries</c>).</summary>
    public void Invalidate(QueryKey prefix)
    {
        QueryBase[] hit;
        lock (_gate) hit = _queries.Values.Where(q => q.Key.StartsWith(prefix)).ToArray();
        foreach (var q in hit) q.Invalidate();
    }

    /// <summary>Invalidates by the parts of a key prefix.</summary>
    public void Invalidate(params string[] prefix) => Invalidate(new QueryKey(prefix));

    /// <summary>Sets the data of the query under <paramref name="key"/> if it exists (<c>setQueryData</c>).</summary>
    public void SetData<T>(QueryKey key, T data) => Find<T>(key)?.SetData(data);

    /// <summary>
    /// Completes when no query has a fetch in flight (and stays that way for one more turn of the dispatcher): the "page is settled"
    /// signal visual tests wait for (<see cref="Shell.IPageLoadState"/>). Polling queries refetch later; this is a snapshot, not a
    /// guarantee. Gives up (completes) after <paramref name="timeout"/> (default 10 s) so a hung request cannot hang a caller.
    /// </summary>
    public async Task WhenIdleAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var quiet = 0;
        while (quiet < 2 && clock.Elapsed < limit)
        {
            ct.ThrowIfCancellationRequested();
            Task[] running;
            lock (_gate) running = _queries.Values.Select(q => q.InFlight).Where(t => t is not null).Select(t => t!).ToArray();
            if (running.Length == 0)
            {
                quiet++;
                await Task.Delay(5, ct).ConfigureAwait(false);
                continue;
            }
            quiet = 0;
            await Task.WhenAny(Task.WhenAll(running), Task.Delay(50, ct)).ConfigureAwait(false);
        }
    }

    /// <summary>Stops every query's polling and cancels running fetches.</summary>
    public void Dispose()
    {
        QueryBase[] all;
        lock (_gate) all = _queries.Values.ToArray();
        foreach (var q in all) q.Shutdown();
    }
}
