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
/// Events delivered to subscribers together: at most one per request (its latest state). Every subscriber receives the
/// same instance, so <see cref="Format"/> serializes it once however many viewers are connected.
/// </summary>
public sealed class LiveRequestBatch(IReadOnlyList<LiveRequestEvent> events)
{
    private string? _formatted;

    /// <summary>The events, oldest request first.</summary>
    public IReadOnlyList<LiveRequestEvent> Events { get; } = events;

    /// <summary>
    /// The batch rendered by <paramref name="formatter"/>, computed by the first caller and shared after that — every
    /// caller must pass the same formatter.
    /// </summary>
    public string Format(Func<LiveRequestBatch, string> formatter) =>
        _formatted ?? Interlocked.CompareExchange(ref _formatted, formatter(this), null) ?? _formatted!;
}

/// <summary>
/// In-memory feed of in-flight gateway requests for the live request log. It records only while someone watches: with
/// no subscriber every call is a single volatile read and nothing is tracked, so the gateway pays nothing for a log
/// nobody has open. While subscribed, the pipeline announces a request the moment it arrives and as it progresses, and
/// <see cref="UsageWriter"/> announces it once persisted; a request already in flight when the first viewer arrived is
/// picked up at its next progress step. Changes are coalesced per request and delivered every
/// <see cref="FlushInterval"/> as one <see cref="LiveRequestBatch"/>, so a burst of traffic costs one frame per
/// interval rather than one per pipeline step. Nothing here is durable — the database stays the record of truth; a
/// request is dropped from the feed as soon as its row is written.
/// </summary>
public sealed class LiveRequestFeed
{
    /// <summary>Batches a subscriber may lag behind before it is disconnected (it reconnects and resyncs).</summary>
    private const int SubscriberBuffer = 256;

    private readonly ConcurrentDictionary<string, Entry> _inFlight = new();
    private readonly ConcurrentDictionary<string, LiveRequestEvent> _pending = new();
    private readonly Lock _subscribersLock = new();
    // Copy-on-write so the hot path reads it without a lock or an allocation.
    private volatile Channel<LiveRequestBatch>[] _subscribers = [];
    private int _flushScheduled;

    private sealed class Entry(RequestRecord record, bool finished = false)
    {
        public RequestRecord Record { get; } = record;
        public volatile bool Finished = finished;
    }

    /// <summary>How long changes are gathered before they go out as one batch.</summary>
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Whether anyone is watching — and so whether the feed records anything.</summary>
    public bool IsActive => _subscribers.Length > 0;

    /// <summary>A request arrived.</summary>
    public void Start(RequestRecord record)
    {
        if (!IsActive) return;
        _inFlight[record.Id] = new Entry(record);
        Publish(new LiveRequestEvent(LiveRequestEventTypes.Started, record, InFlight: true));
    }

    /// <summary>An in-flight request changed (fields were set on the record).</summary>
    public void Update(RequestRecord record)
    {
        if (!IsActive) return;
        // Not tracked: it started before anyone watched — adopt it now.
        var e = _inFlight.GetOrAdd(record.Id, static (_, r) => new Entry(r), record);
        if (!e.Finished) Publish(new LiveRequestEvent(LiveRequestEventTypes.Updated, record, InFlight: true));
    }

    /// <summary>The pipeline is done with the request: status, timing and cost are final, the row is queued for writing.</summary>
    public void Finish(RequestRecord record)
    {
        if (!IsActive) return;
        _inFlight.GetOrAdd(record.Id, static (_, r) => new Entry(r, finished: true), record).Finished = true;
        Publish(new LiveRequestEvent(LiveRequestEventTypes.Updated, record, InFlight: false));
    }

    /// <summary>The records are in the database (or failed to write); they leave the feed.</summary>
    public void Persisted(IEnumerable<RequestRecord> records)
    {
        var active = IsActive;
        foreach (var record in records)
        {
            // Lock-free lookup first: with nobody watching the map is empty, and TryRemove takes a lock.
            if (_inFlight.TryGetValue(record.Id, out _)) _inFlight.TryRemove(record.Id, out _);
            // Also for rows the feed never tracked: the viewer's list refetches on it.
            if (active) Publish(new LiveRequestEvent(LiveRequestEventTypes.Persisted, record, InFlight: false));
        }
    }

    /// <summary>The in-flight (not yet persisted) record with this id, if any (only while the feed is recording).</summary>
    public LiveRequestEvent? Find(string id) =>
        _inFlight.TryGetValue(id, out var e) ? new LiveRequestEvent(LiveRequestEventTypes.Updated, e.Record, !e.Finished) : null;

    /// <summary>
    /// The in-flight (not yet persisted) records, oldest first — a cheap snapshot for aggregates (live RPM). Empty while
    /// nobody watches the live log.
    /// </summary>
    public IReadOnlyList<RequestRecord> InFlightSnapshot() =>
        _inFlight.IsEmpty ? [] : _inFlight.Values.OrderBy(e => e.Record.StartedAtUtc).Select(e => e.Record).ToList();

    /// <summary>
    /// Subscribes to the feed (starting the recording if this is the first viewer): yields the in-flight snapshot
    /// (oldest first, omitted when empty), then a batch per <see cref="FlushInterval"/> with changes until cancelled.
    /// A subscriber that falls more than <see cref="SubscriberBuffer"/> batches behind is ended rather than silently
    /// losing events (a missed "persisted" would leave a row stuck in flight); its client reconnects and resyncs.
    /// </summary>
    public async IAsyncEnumerable<LiveRequestBatch> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<LiveRequestBatch>(new BoundedChannelOptions(SubscriberBuffer)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        // Register before taking the snapshot so nothing that happens in between is missed (duplicates are harmless:
        // events carry the whole row and the client keys rows by id).
        lock (_subscribersLock) _subscribers = [.. _subscribers, channel];
        try
        {
            var snapshot = _inFlight.Values
                .OrderBy(e => e.Record.StartedAtUtc)
                .Select(e => new LiveRequestEvent(LiveRequestEventTypes.Started, e.Record, !e.Finished))
                .ToList();
            if (snapshot.Count > 0) yield return new LiveRequestBatch(snapshot);
            while (await channel.Reader.WaitToReadAsync(ct))
            {
                while (channel.Reader.TryRead(out var batch)) yield return batch;
            }
        }
        finally
        {
            lock (_subscribersLock)
            {
                _subscribers = Array.FindAll(_subscribers, c => c != channel);
                // The last viewer left: stop recording. Rows still tracked drop out when persisted anyway.
                if (_subscribers.Length == 0)
                {
                    _inFlight.Clear();
                    _pending.Clear();
                }
            }
        }
    }

    private void Publish(LiveRequestEvent e)
    {
        // Latest state wins: a request that changed several times within one interval goes out once.
        _pending[e.Record.Id] = e;
        if (Interlocked.Exchange(ref _flushScheduled, 1) == 0) _ = FlushLaterAsync();
    }

    private async Task FlushLaterAsync()
    {
        await Task.Delay(FlushInterval).ConfigureAwait(false);
        // Reset before draining: a change landing mid-drain schedules the next flush instead of being stranded.
        Volatile.Write(ref _flushScheduled, 0);
        Flush();
    }

    private void Flush()
    {
        // Enumerating the map itself is lock-free (Keys / Count would take every lock).
        var events = new List<LiveRequestEvent>();
        foreach (var (id, _) in _pending)
        {
            if (_pending.TryRemove(id, out var e)) events.Add(e);
        }
        if (events.Count == 0) return;
        events.Sort(static (a, b) =>
        {
            var byStart = a.Record.StartedAtUtc.CompareTo(b.Record.StartedAtUtc);
            return byStart != 0 ? byStart : string.CompareOrdinal(a.Record.Id, b.Record.Id);
        });
        var batch = new LiveRequestBatch(events);
        foreach (var channel in _subscribers)
        {
            // Full: the viewer cannot keep up — end its stream (it resyncs on reconnect) instead of dropping events.
            if (!channel.Writer.TryWrite(batch)) channel.Writer.TryComplete();
        }
    }
}
