using System.ComponentModel;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Tests.Api;

/// <summary>A clock tests move by hand; timers fire inside <see cref="Advance"/>.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
    private readonly List<ManualTimer> _timers = [];

    public override DateTimeOffset GetUtcNow() => _now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state, dueTime);
        lock (_timers) _timers.Add(t);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_timers) next = _timers.Where(t => t.Due is not null && t.Due <= target).OrderBy(t => t.Due).FirstOrDefault();
            if (next is null) break;
            _now = next.Due!.Value;
            next.Due = null;
            next.Fire();
        }
        _now = target;
    }

    public int Pending { get { lock (_timers) return _timers.Count(t => t.Due is not null); } }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTime _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTime owner, TimerCallback callback, object? state, TimeSpan due)
        {
            _owner = owner; _callback = callback; _state = state;
            Due = due == Timeout.InfiniteTimeSpan ? null : owner._now + due;
        }

        public DateTimeOffset? Due { get; set; }
        public void Fire() => _callback(_state);
        public bool Change(TimeSpan dueTime, TimeSpan period) { Due = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime; return true; }
        public void Dispose() { Due = null; lock (_owner._timers) _owner._timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal sealed class QueuedDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _q = new();
    public void Post(Action action) => _q.Enqueue(action);
    public int Pump() { var n = 0; while (_q.Count > 0) { _q.Dequeue()(); n++; } return n; }
}

public class QueryTests
{
    private static readonly TimeSpan Zero = TimeSpan.Zero;

    private static QueryOptions<int> Opts(TimeSpan? stale = null, TimeSpan? poll = null, int retries = 1) =>
        new() { StaleTime = stale ?? TimeSpan.FromSeconds(5), RefetchInterval = poll is null ? null : _ => poll, Retries = retries, RetryDelay = Zero };

    [Fact]
    public async Task Activate_fetches_once_and_raises_property_changes()
    {
        using var qc = new QueryClient();
        var calls = 0;
        var q = qc.Query<int>("n", _ => Task.FromResult(++calls), Opts());
        var changed = new List<string?>();
        ((INotifyPropertyChanged)q).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.False(q.HasData);
        using var obs = q.Activate();
        await q.RefetchAsync(); // joins nothing: the activation fetch already finished (inline fetcher), so this is a second, forced one
        Assert.Equal(2, calls);
        Assert.Equal(2, q.Data);
        Assert.Equal(QueryStatus.Success, q.Status);
        Assert.False(q.IsLoading);
        Assert.Contains("Data", changed);
        Assert.Contains("IsFetching", changed);
    }

    [Fact]
    public void Fresh_data_is_not_refetched_until_stale()
    {
        var time = new ManualTime();
        using var qc = new QueryClient(null, time);
        var calls = 0;
        var q = qc.Query<int>("n", _ => Task.FromResult(++calls), Opts(stale: TimeSpan.FromSeconds(5)));
        using (q.Activate()) { }
        using (q.Activate()) { }
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(6));
        Assert.True(q.IsStale);
        using (q.Activate()) { }
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Polls_only_while_observed()
    {
        var time = new ManualTime();
        using var qc = new QueryClient(null, time);
        var calls = 0;
        var q = qc.Polling("health", _ => Task.FromResult(++calls), TimeSpan.FromSeconds(10));
        var obs = q.Activate();
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, calls);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(3, calls);
        obs.Dispose();
        Assert.Equal(0, time.Pending);
        time.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Interval_can_depend_on_the_data()
    {
        var time = new ManualTime();
        using var qc = new QueryClient(null, time);
        var calls = 0;
        // like useTunnel: poll fast while "busy", slow otherwise
        var q = qc.Query<int>("t", _ => Task.FromResult(++calls), new QueryOptions<int> { RefetchInterval = d => d < 3 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(60) });
        using var obs = q.Activate();
        time.Advance(TimeSpan.FromSeconds(1));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, calls);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(3, calls);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(4, calls);
    }

    [Fact]
    public void Invalidate_by_prefix_refetches_observed_and_marks_others_stale()
    {
        using var qc = new QueryClient();
        int a = 0, b = 0, other = 0;
        var qa = qc.Query<int>(QueryKey.Of("requests", "p1"), _ => Task.FromResult(++a), Opts());
        var qb = qc.Query<int>(QueryKey.Of("requests", "p2"), _ => Task.FromResult(++b), Opts());
        var qo = qc.Query<int>("providers", _ => Task.FromResult(++other), Opts());
        using var obs = qa.Activate();
        _ = qb.RefetchAsync();
        _ = qo.RefetchAsync();
        Assert.False(qb.IsStale);
        qc.Invalidate("requests");
        Assert.Equal(2, a);              // observed: refetched now
        Assert.Equal(1, b);              // unobserved: only stale
        Assert.True(qb.IsStale);
        Assert.False(qo.IsStale);
        using var obs2 = qb.Activate();
        Assert.Equal(2, b);
    }

    [Fact]
    public async Task Failure_keeps_data_and_retries_once_unless_client_error()
    {
        using var qc = new QueryClient();
        var calls = 0;
        var q = qc.Query<int>("n", _ => ++calls == 1 ? throw new ApiException(500, "boom") : Task.FromResult(7), Opts());
        await q.RefetchAsync();
        Assert.Equal(2, calls);
        Assert.Equal(7, q.Data);

        var fails = 0;
        var bad = qc.Query<int>("bad", _ => { fails++; throw new ApiException(404, "nope"); }, Opts());
        await bad.RefetchAsync();
        Assert.Equal(1, fails);
        Assert.True(bad.IsError);
        Assert.Equal("nope", bad.Error!.Message);

        // data from an earlier success survives a later failed refetch
        var flip = 0;
        var keep = qc.Query<int>("keep", _ => ++flip == 1 ? Task.FromResult(5) : throw new ApiException(404, "gone"), Opts());
        await keep.RefetchAsync();
        await keep.RefetchAsync();
        Assert.Equal(5, keep.Data);
        Assert.True(keep.IsError);
        Assert.True(keep.HasData);
    }

    [Fact]
    public async Task Fetches_are_single_flight()
    {
        using var qc = new QueryClient();
        var gate = new TaskCompletionSource<int>();
        var calls = 0;
        var q = qc.Query<int>("n", _ => { calls++; return gate.Task; }, Opts());
        var t1 = q.RefetchAsync();
        var t2 = q.RefetchAsync();
        Assert.True(q.IsLoading);
        gate.SetResult(3);
        await Task.WhenAll(t1, t2);
        Assert.Equal(1, calls);
        Assert.Equal(3, q.Data);
    }

    [Fact]
    public async Task Invalidate_during_a_fetch_triggers_another()
    {
        using var qc = new QueryClient();
        var gates = new Queue<TaskCompletionSource<int>>([new(), new()]);
        var calls = 0;
        var q = qc.Query<int>("n", _ => { calls++; return gates.Peek().Task; }, Opts());
        using var obs = q.Activate();
        q.Invalidate();
        gates.Dequeue().SetResult(1);
        await Task.Delay(200);
        Assert.Equal(2, calls);
        gates.Peek().SetResult(2);
        await q.RefetchAsync();
        Assert.Equal(2, q.Data);
    }

    [Fact]
    public async Task State_changes_go_through_the_ui_dispatcher()
    {
        var ui = new QueuedDispatcher();
        using var qc = new QueryClient(ui);
        var q = qc.Query<int>("n", _ => Task.FromResult(9), Opts());
        var done = q.RefetchAsync();
        Assert.False(q.HasData);          // nothing applied until the UI thread runs it
        Assert.False(done.IsCompleted);
        ui.Pump();
        await done;
        Assert.True(q.HasData);
        Assert.Equal(9, q.Data);
    }

    [Fact]
    public async Task Disabled_query_does_not_fetch_until_enabled()
    {
        using var qc = new QueryClient();
        var calls = 0;
        var q = qc.Query<int>("n", _ => Task.FromResult(++calls), Opts());
        q.Enabled = false;
        using var obs = q.Activate();
        Assert.Equal(0, calls);
        q.Enabled = true; // observed: enabling fetches
        Assert.Equal(1, calls);
        Assert.Equal(1, q.Data);
    }

    [Fact]
    public void Same_key_shares_the_query_and_a_type_clash_throws()
    {
        using var qc = new QueryClient();
        var a = qc.Query<int>("k", _ => Task.FromResult(1));
        var b = qc.Query<int>("k", _ => Task.FromResult(2));
        Assert.Same(a, b);
        Assert.Throws<InvalidOperationException>(() => qc.Query<string>("k", _ => Task.FromResult("")));
        qc.SetData("k", 42);
        Assert.Equal(42, a.Data);
        Assert.True(QueryKey.Of("a", "b").StartsWith(QueryKey.Of("a")));
        Assert.False(QueryKey.Of("a").StartsWith(QueryKey.Of("a", "b")));
    }

    [Fact]
    public void Cache_time_drops_an_unobserved_query_and_its_data()
    {
        var time = new ManualTime();
        using var qc = new QueryClient(null, time);
        var calls = 0;
        var q = qc.Query<int>("body", _ => Task.FromResult(++calls), new QueryOptions<int> { CacheTime = TimeSpan.FromSeconds(30) });
        using (q.Activate()) { }
        Assert.True(q.HasData);
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.Same(q, qc.Find<int>("body"));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(qc.Find<int>("body"));
        Assert.False(q.HasData);
        Assert.Equal(QueryStatus.Pending, q.Status);
        // Asking again builds a fresh query that fetches.
        var again = qc.Query<int>("body", _ => Task.FromResult(++calls), new QueryOptions<int> { CacheTime = TimeSpan.FromSeconds(30) });
        Assert.NotSame(q, again);
        using (again.Activate()) Assert.Equal(2, again.Data);
    }

    [Fact]
    public void Cache_time_waits_while_observed_and_restarts_after_the_last_observer()
    {
        var time = new ManualTime();
        using var qc = new QueryClient(null, time);
        var q = qc.Query<int>("detail", _ => Task.FromResult(1), new QueryOptions<int> { CacheTime = TimeSpan.FromSeconds(10) });
        var obs = q.Activate();
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Same(q, qc.Find<int>("detail"));
        obs.Dispose();
        time.Advance(TimeSpan.FromSeconds(9));
        using (q.Activate()) time.Advance(TimeSpan.FromSeconds(30));
        Assert.Same(q, qc.Find<int>("detail"));
        Assert.True(q.HasData);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(qc.Find<int>("detail"));
    }

    [Fact]
    public void An_evicted_query_observed_again_returns_to_the_cache_and_refetches()
    {
        var time = new ManualTime();
        using var qc = new QueryClient(null, time);
        var calls = 0;
        var q = qc.Query<int>(QueryKey.Of("request", "r1"), _ => Task.FromResult(++calls), new QueryOptions<int> { CacheTime = TimeSpan.FromSeconds(10) });
        using (q.Activate()) { }
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Null(qc.Find<int>(QueryKey.Of("request", "r1")));
        // A holder that kept the instance watches it again: it is cached again (invalidation reaches it) and fetches.
        using var obs = q.Activate();
        Assert.Same(q, qc.Find<int>(QueryKey.Of("request", "r1")));
        Assert.Equal(2, calls);
        qc.Invalidate("request");
        Assert.Equal(3, calls);
    }

    [Fact]
    public void Without_cache_time_an_unobserved_query_stays()
    {
        var time = new ManualTime();
        using var qc = new QueryClient(null, time);
        var q = qc.Query<int>("providers", _ => Task.FromResult(1));
        using (q.Activate()) { }
        time.Advance(TimeSpan.FromDays(1));
        Assert.Same(q, qc.Find<int>("providers"));
        Assert.True(q.HasData);
    }

    [Fact]
    public async Task Stores_share_keys_with_the_web_hooks()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 200, c.Request.RawUrl == "/api/tokens" ? "[]" : """[{"id":"x"}]"""));
        using var api = new ApiClient(server.Url);
        using var qc = new QueryClient();
        var tokens = new TokensStore(qc, api);
        var providers = new ProvidersStore(qc, api);
        var clients = new ClientsStore(qc, api);
        await Task.WhenAll(tokens.Tokens.RefetchAsync(), providers.Providers.RefetchAsync(), clients.Clients.RefetchAsync());
        Assert.Empty(tokens.Tokens.Data!);
        Assert.Single(providers.Providers.Data!);
        Assert.Single(clients.Clients.Data!);
        Assert.Same(tokens.Tokens, new TokensStore(qc, api).Tokens);
    }
}
