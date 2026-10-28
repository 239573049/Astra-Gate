using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Tests.Api;
using static Astra.Desktop.Tests.Requests.RequestTestData;

namespace Astra.Desktop.Tests.Requests;

/// <summary>The live feed against a fake SSE server: merge into the store, persisted, reconnect, and pausing while the window is hidden.</summary>
public class LiveRequestFeedTests
{
    private static async Task Until(Func<bool> condition, int ms = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    private sealed class Harness : IDisposable
    {
        public readonly LiveRequestStore Store = new();
        public readonly List<string> Persisted = [];
        public int Reconnects;
        public readonly ManualVisibility Visibility = new();
        public readonly ApiClient Api;
        public readonly LiveRequestFeed Feed;

        public Harness(FakeServer server, TimeSpan? grace = null, TimeSpan? linger = null)
        {
            Api = new ApiClient(server.Url);
            Feed = new LiveRequestFeed(Api, Store, ImmediateDispatcher.Instance, Visibility,
                id => { lock (Persisted) Persisted.Add(id); }, () => Interlocked.Increment(ref Reconnects),
                hiddenGrace: grace ?? TimeSpan.FromMilliseconds(50), linger: linger ?? TimeSpan.FromMilliseconds(30));
        }

        public void Dispose()
        {
            Feed.Stop();
            Api.Dispose();
        }
    }

    [Fact]
    public async Task Started_and_updated_frames_fill_and_update_the_store()
    {
        using var server = new FakeServer(c => FakeServer.Sse(c, Frame("started", Row("r1", "pending")), Frame("started", Row("r2", "pending", "2026-10-26T11:58:00Z")),
            Frame("updated", Row("r1", "success")), ": keep\n\n").ContinueWith(_ => Task.Delay(3000)).Unwrap());
        using var h = new Harness(server);
        var rowChanges = new List<string>();
        h.Store.RowChanged += rowChanges.Add;
        h.Feed.Start();
        await Until(() => h.Store.Get("r1")?.Status == "success");

        Assert.True(h.Store.Connected);
        // Newest first by start time, whatever the arrival order.
        Assert.Equal(["r2", "r1"], h.Store.List.Select(r => r.Id));
        // r1's status change is a filtered-on field, so the list was rebuilt; a TTFT-only update would not rebuild but still notifies the row.
        Assert.Contains("r1", rowChanges);
    }

    [Fact]
    public async Task Persisted_frame_tells_the_page_and_drops_the_row_after_the_linger()
    {
        using var server = new FakeServer(c => FakeServer.Sse(c, Frame("started", Row("r1", "pending")), Frame("persisted", Row("r1", "success"))).ContinueWith(_ => Task.Delay(3000)).Unwrap());
        using var h = new Harness(server);
        h.Feed.Start();
        await Until(() => h.Persisted.Contains("r1"));
        // The live copy stays a moment so cells do not blank before the list refetch lands, then leaves.
        await Until(() => h.Store.Get("r1") is null);
        Assert.Empty(h.Store.List);
    }

    [Fact]
    public async Task A_dropped_connection_forgets_its_rows_asks_for_a_refetch_and_reconnects_with_a_fresh_snapshot()
    {
        var hits = 0;
        using var server = new FakeServer(async c =>
        {
            if (Interlocked.Increment(ref hits) == 1) await FakeServer.Sse(c, Frame("started", Row("old", "pending"))); // then the server closes
            else await FakeServer.Sse(c, Frame("started", Row("new", "pending"))).ContinueWith(_ => Task.Delay(3000)).Unwrap();
        });
        using var h = new Harness(server);
        h.Feed.Start();
        await Until(() => h.Store.Get("new") is not null);

        Assert.Null(h.Store.Get("old"));
        Assert.True(h.Reconnects >= 1);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task A_refusing_server_leaves_the_store_disconnected_so_the_page_polls()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 503, """{"error":"starting"}"""));
        using var h = new Harness(server);
        h.Feed.Start();
        await Until(() => h.Reconnects >= 1);
        Assert.False(h.Store.Connected);
        Assert.Empty(h.Store.List);
    }

    [Fact]
    public async Task A_hidden_window_closes_the_stream_after_the_grace_and_reopens_it_when_visible_again()
    {
        using var server = new FakeServer(c => FakeServer.Sse(c, Frame("started", Row("r1", "pending"))).ContinueWith(_ => Task.Delay(5000)).Unwrap());
        using var h = new Harness(server);
        h.Feed.Start();
        await Until(() => h.Store.Connected);
        Assert.Single(server.Requests);

        h.Visibility.Set(false);
        await Until(() => !h.Store.Connected && h.Store.Get("r1") is null);
        var reconnectsWhileHidden = h.Reconnects;
        await Task.Delay(300);
        // Nothing reconnects while hidden.
        Assert.Single(server.Requests);

        h.Visibility.Set(true);
        await Until(() => h.Store.Connected && server.Requests.Count == 2);
        // Coming back in view: the list catches up with what was written while the feed was off.
        Assert.True(h.Reconnects > reconnectsWhileHidden);
        Assert.NotNull(h.Store.Get("r1"));
    }

    [Fact]
    public async Task A_quick_hide_within_the_grace_keeps_the_connection()
    {
        using var server = new FakeServer(c => FakeServer.Sse(c, Frame("started", Row("r1", "pending"))).ContinueWith(_ => Task.Delay(5000)).Unwrap());
        using var h = new Harness(server, grace: TimeSpan.FromMilliseconds(600));
        h.Feed.Start();
        await Until(() => h.Store.Get("r1") is not null);
        h.Visibility.Set(false);
        await Task.Delay(100);
        h.Visibility.Set(true);
        await Task.Delay(800);
        Assert.Single(server.Requests);
        Assert.NotNull(h.Store.Get("r1"));
        Assert.True(h.Store.Connected);
    }

    [Fact]
    public async Task Stop_closes_the_feed_and_clears_the_store()
    {
        using var server = new FakeServer(c => FakeServer.Sse(c, Frame("started", Row("r1", "pending"))).ContinueWith(_ => Task.Delay(5000)).Unwrap());
        using var h = new Harness(server);
        h.Feed.Start();
        await Until(() => h.Store.Get("r1") is not null);
        h.Feed.Stop();
        await h.Feed.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(h.Feed.Running);
        Assert.Empty(h.Store.List);
        Assert.False(h.Store.Connected);
    }

    [Fact]
    public void Store_notifies_the_list_only_for_membership_or_filtered_fields()
    {
        var store = new LiveRequestStore();
        var lists = 0;
        store.ListChanged += () => lists++;
        store.Set(Row("a", "pending"));
        Assert.Equal(1, lists);
        // A progress update (TTFT, cost) leaves the list alone...
        store.Set(Row("a", "pending") with { TtftMs = 999, CostNanoUsd = 5 });
        Assert.Equal(1, lists);
        // ...a status change is a filtered-on field.
        store.Set(Row("a", "success"));
        Assert.Equal(2, lists);
        store.Delete("a");
        Assert.Equal(3, lists);
        store.Delete("a");
        Assert.Equal(3, lists);
        store.SetConnected(true);
        Assert.Equal(4, lists);
        store.SetConnected(true);
        Assert.Equal(4, lists);
    }

    [Fact]
    public void Ties_on_the_start_time_keep_the_newer_id_first()
    {
        var store = new LiveRequestStore();
        store.Set(Row("01A", "pending", "2026-10-26T11:57:00Z"));
        store.Set(Row("01B", "pending", "2026-10-26T11:57:00Z"));
        Assert.Equal(["01B", "01A"], store.List.Select(r => r.Id));
    }
}
