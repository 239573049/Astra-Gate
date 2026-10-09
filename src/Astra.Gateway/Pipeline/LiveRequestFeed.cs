using System.Collections.Concurrent;
using System.Threading.Channels;
using Astra.Core.Requests;

namespace Astra.Gateway.Pipeline;

/// <summary>Kind of a <see cref="LiveRequestEvent"/>.</summary>
public static class LiveRequestEventTypes
{
    /// <summary>A request arrived and is now in flight (nothing is persisted yet).</summary>
    public const string Started = "started";

    /// <summary>An in-flight request progressed (route resolved, upstream answered, first token, finished).</summary>
    public const string Updated = "updated";

    /// <summary>The record reached the database; list queries now return it.</summary>
    public const string Persisted = "persisted";
}

/// <summary>
/// One change to the live request log. <paramref name="InFlight"/> is true until the pipeline finished the request;
/// while it is, <see cref="RequestRecord.Status"/> is not meaningful yet (the record default is "success").
/// </summary>
public sealed record LiveRequestEvent(string Type, RequestRecord Record, bool InFlight);

/// <summary>
/// In-memory feed of in-flight gateway requests for the live request log: the pipeline announces a request the moment
/// it arrives and as it progresses, and <see cref="UsageWriter"/> announces it once persisted. Subscribers get the
/// current in-flight snapshot first, then every event. Nothing here is durable — the database stays the record of
/// truth; a request is dropped from the feed as soon as its row is written.
/// </summary>
public sealed class LiveRequestFeed
{
    private const int SubscriberBuffer = 512;

    private readonly ConcurrentDictionary<string, Entry> _inFlight = new();
    private readonly ConcurrentDictionary<Channel<LiveRequestEvent>, byte> _subscribers = new();

    private sealed class Entry(RequestRecord record)
    {
        public RequestRecord Record { get; } = record;
        public volatile bool Finished;
    }

    /// <summary>A request arrived.</summary>
    public void Start(RequestRecord record)
    {
        _inFlight[record.Id] = new Entry(record);
        Publish(new LiveRequestEvent(LiveRequestEventTypes.Started, record, InFlight: true));
    }

    /// <summary>An in-flight request changed (fields were set on the record).</summary>
    public void Update(RequestRecord record)
    {
        if (_inFlight.TryGetValue(record.Id, out var e) && !e.Finished)
            Publish(new LiveRequestEvent(LiveRequestEventTypes.Updated, record, InFlight: true));
    }

    /// <summary>The pipeline is done with the request: status, timing and cost are final, the row is queued for writing.</summary>
    public void Finish(RequestRecord record)
    {
        if (!_inFlight.TryGetValue(record.Id, out var e)) return;
        e.Finished = true;
        Publish(new LiveRequestEvent(LiveRequestEventTypes.Updated, record, InFlight: false));
    }

    /// <summary>The records are in the database (or failed to write); they leave the feed.</summary>
    public void Persisted(IEnumerable<RequestRecord> records)
    {
        foreach (var record in records)
        {
            _inFlight.TryRemove(record.Id, out _);
            Publish(new LiveRequestEvent(LiveRequestEventTypes.Persisted, record, InFlight: false));
        }
    }

    /// <summary>The in-flight (not yet persisted) record with this id, if any.</summary>
    public LiveRequestEvent? Find(string id) =>
        _inFlight.TryGetValue(id, out var e) ? new LiveRequestEvent(LiveRequestEventTypes.Updated, e.Record, !e.Finished) : null;

    /// <summary>
    /// Subscribes to the feed: yields the in-flight snapshot (oldest first), then live events until cancelled.
    /// A subscriber that falls more than <see cref="SubscriberBuffer"/> events behind loses the oldest ones.
    /// </summary>
    public async IAsyncEnumerable<LiveRequestEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<LiveRequestEvent>(new BoundedChannelOptions(SubscriberBuffer)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
        // Register before taking the snapshot so nothing that happens in between is missed (duplicates are harmless:
        // events carry the whole row and the client keys rows by id).
        _subscribers[channel] = 0;
        try
        {
            foreach (var e in _inFlight.Values.OrderBy(e => e.Record.StartedAtUtc).ToList())
                yield return new LiveRequestEvent(LiveRequestEventTypes.Started, e.Record, !e.Finished);
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                while (channel.Reader.TryRead(out var e)) yield return e;
            }
        }
        finally
        {
            _subscribers.TryRemove(channel, out _);
        }
    }

    private void Publish(LiveRequestEvent e)
    {
        foreach (var channel in _subscribers.Keys) channel.Writer.TryWrite(e);
    }
}
