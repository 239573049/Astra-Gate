using Astra.Core.Requests;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

public class LiveRequestFeedTests
{
    [Fact]
    public async Task Subscriber_Sees_A_Request_Arrive_Progress_Finish_And_Persist_In_Order()
    {
        var feed = new LiveRequestFeed();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = feed.SubscribeAsync(cts.Token).GetAsyncEnumerator();
        var next = events.MoveNextAsync(); // registers the subscriber (empty snapshot)

        var record = new RequestRecord { Id = "r1", StartedAtUtc = DateTimeOffset.UtcNow };
        feed.Start(record);
        record.RequestedModel = "m";
        feed.Update(record);
        record.Status = RequestStatus.UpstreamError;
        feed.Finish(record);
        feed.Update(record); // after Finish: ignored
        feed.Persisted([record]);

        var seen = new List<(string, bool)>();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(await next);
            seen.Add((events.Current.Type, events.Current.InFlight));
            next = events.MoveNextAsync();
        }
        Assert.Equal(
        [
            (LiveRequestEventTypes.Started, true),
            (LiveRequestEventTypes.Updated, true),
            (LiveRequestEventTypes.Updated, false),
            (LiveRequestEventTypes.Persisted, false),
        ], seen);
        Assert.Null(feed.Find("r1"));
        await cts.CancelAsync();
    }

    [Fact]
    public async Task A_New_Subscriber_Gets_The_In_Flight_Snapshot_First()
    {
        var feed = new LiveRequestFeed();
        var older = new RequestRecord { Id = "a", StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-2) };
        var newer = new RequestRecord { Id = "b", StartedAtUtc = DateTimeOffset.UtcNow };
        feed.Start(newer);
        feed.Start(older);
        feed.Finish(newer);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var events = feed.SubscribeAsync(cts.Token).GetAsyncEnumerator();
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(("a", true), (events.Current.Record.Id, events.Current.InFlight));
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(("b", false), (events.Current.Record.Id, events.Current.InFlight));
        Assert.True(feed.Find("a")!.InFlight);
        await cts.CancelAsync();
    }
}
