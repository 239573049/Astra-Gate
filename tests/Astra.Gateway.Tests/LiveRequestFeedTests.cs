using Astra.Core.Requests;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

public class LiveRequestFeedTests
{
    private static LiveRequestFeed NewFeed() => new() { FlushInterval = TimeSpan.FromMilliseconds(20) };

    /// <summary>Starts a subscription and waits until it is registered (the first MoveNext blocks on an empty feed).</summary>
    private static async Task<(IAsyncEnumerator<LiveRequestBatch> Events, ValueTask<bool> Next)> SubscribeAsync(
        LiveRequestFeed feed, CancellationToken ct)
    {
        var events = feed.SubscribeAsync(ct).GetAsyncEnumerator(ct);
        var next = events.MoveNextAsync();
        while (!feed.IsActive) await Task.Delay(1, ct);
        return (events, next);
    }

    [Fact]
    public void Records_Nothing_While_Nobody_Watches()
    {
        var feed = NewFeed();
        var record = new RequestRecord { Id = "r1", StartedAtUtc = DateTimeOffset.UtcNow };
        feed.Start(record);
        feed.Update(record);
        feed.Finish(record);

        Assert.False(feed.IsActive);
        Assert.Null(feed.Find("r1"));
        Assert.Empty(feed.InFlightSnapshot());
        feed.Persisted([record]);
    }

    [Fact]
    public async Task Changes_Within_An_Interval_Are_Coalesced_To_The_Latest_State()
    {
        var feed = NewFeed();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (events, next) = await SubscribeAsync(feed, cts.Token);
        await using var _ = events;

        var record = new RequestRecord { Id = "r1", StartedAtUtc = DateTimeOffset.UtcNow };
        feed.Start(record);
        record.RequestedModel = "m";
        feed.Update(record);
        feed.Update(record);

        Assert.True(await next);
        var e = Assert.Single(events.Current.Events);
        Assert.Equal((LiveRequestEventTypes.Updated, true, "m"), (e.Type, e.InFlight, e.Record.RequestedModel));
        Assert.True(feed.Find("r1")!.InFlight);

        record.Status = RequestStatus.UpstreamError;
        feed.Finish(record);
        feed.Update(record); // after Finish: ignored
        feed.Persisted([record]);

        Assert.True(await events.MoveNextAsync());
        e = Assert.Single(events.Current.Events);
        Assert.Equal((LiveRequestEventTypes.Persisted, false), (e.Type, e.InFlight));
        Assert.Null(feed.Find("r1"));
        await cts.CancelAsync();
    }

    [Fact]
    public async Task A_New_Subscriber_Gets_The_In_Flight_Snapshot_First()
    {
        var feed = NewFeed();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (first, firstNext) = await SubscribeAsync(feed, cts.Token);
        await using var _ = first;

        var older = new RequestRecord { Id = "a", StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-2) };
        var newer = new RequestRecord { Id = "b", StartedAtUtc = DateTimeOffset.UtcNow };
        feed.Start(newer);
        feed.Start(older);
        feed.Finish(newer);
        Assert.True(await firstNext);
        Assert.Equal(["a", "b"], first.Current.Events.Select(e => e.Record.Id));

        await using var second = feed.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await second.MoveNextAsync());
        Assert.Equal([("a", true), ("b", false)], second.Current.Events.Select(e => (e.Record.Id, e.InFlight)));
        await cts.CancelAsync();
    }

    [Fact]
    public async Task A_Request_Started_Before_Anyone_Watched_Is_Adopted_At_Its_Next_Step()
    {
        var feed = NewFeed();
        var record = new RequestRecord { Id = "r1", StartedAtUtc = DateTimeOffset.UtcNow };
        feed.Start(record); // nobody watching: not tracked

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (events, next) = await SubscribeAsync(feed, cts.Token);
        await using var _ = events;
        feed.Update(record);

        Assert.True(await next);
        Assert.Equal("r1", Assert.Single(events.Current.Events).Record.Id);
        Assert.NotNull(feed.Find("r1"));
        await cts.CancelAsync();
    }

    [Fact]
    public async Task Every_Subscriber_Shares_One_Serialized_Batch_And_Recording_Stops_When_The_Last_Leaves()
    {
        var feed = NewFeed();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var (a, aNext) = await SubscribeAsync(feed, cts.Token);
        var (b, bNext) = await SubscribeAsync(feed, cts.Token);

        var record = new RequestRecord { Id = "r1", StartedAtUtc = DateTimeOffset.UtcNow };
        feed.Start(record);
        Assert.True(await aNext);
        Assert.True(await bNext);
        Assert.Same(a.Current, b.Current);
        var calls = 0;
        string Format(LiveRequestBatch batch) => $"{++calls}:{batch.Events.Count}";
        Assert.Equal("1:1", a.Current.Format(Format));
        Assert.Equal("1:1", b.Current.Format(Format));
        Assert.Equal(1, calls);

        await a.DisposeAsync();
        Assert.True(feed.IsActive);
        await b.DisposeAsync();
        Assert.False(feed.IsActive);
        Assert.Null(feed.Find("r1"));
    }
}
