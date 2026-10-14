using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Astra.Core.Requests;

namespace Astra.Gateway.Tracing;

/// <summary>
/// Records the spans of one gateway request (.plan/observability.md §2.1, §2.3): cheap timestamps on the monotonic clock
/// while the request runs, turned into <see cref="TraceSpan"/> rows once it is over. Not thread-safe: the pipeline owns it.
/// </summary>
public sealed class RequestTrace
{
    private readonly long _t0 = Stopwatch.GetTimestamp();
    private readonly List<TraceSpanBuilder> _spans = [];

    public RequestTrace()
    {
        Root = new TraceSpanBuilder(this, TraceSpanNames.Request, TraceSpanKinds.Server, null, _t0);
        _spans.Add(Root);
    }

    /// <summary>The <c>gateway.request</c> span: from arrival to the last byte written back.</summary>
    public TraceSpanBuilder Root { get; }

    /// <summary>Starts a span now, under <paramref name="parent"/> (the root by default).</summary>
    public TraceSpanBuilder Start(string name, string kind = TraceSpanKinds.Internal, TraceSpanBuilder? parent = null) =>
        StartAt(name, Stopwatch.GetTimestamp(), kind, parent);

    /// <summary>Starts a span that began at <paramref name="timestamp"/> (a <see cref="Stopwatch.GetTimestamp"/> value).</summary>
    public TraceSpanBuilder StartAt(string name, long timestamp, string kind = TraceSpanKinds.Internal, TraceSpanBuilder? parent = null)
    {
        var span = new TraceSpanBuilder(this, name, kind, parent ?? Root, timestamp);
        _spans.Add(span);
        return span;
    }

    /// <summary>The spans of the request, root first. Spans still open end with the root; the root ends now if it is open.</summary>
    public List<TraceSpan> Build(string requestId)
    {
        var end = Root.EndTimestamp ?? Stopwatch.GetTimestamp();
        Root.EndAt(end);
        var result = new List<TraceSpan>(_spans.Count);
        foreach (var span in _spans)
        {
            if (span.EndTimestamp is null)
            {
                // Cut short by an exception or a client that went away.
                span.EndAt(end);
                if (span.Status == TraceSpanStatus.Unset) span.Error("incomplete");
            }
            result.Add(span.ToSpan(requestId, _t0));
        }
        return result;
    }

    internal static long Micros(long fromTimestamp, long toTimestamp) =>
        (long)Stopwatch.GetElapsedTime(fromTimestamp, toTimestamp).TotalMicroseconds;
}

/// <summary>One span being recorded. Attributes are flat string / number / bool values.</summary>
public sealed class TraceSpanBuilder : IDisposable
{
    private readonly RequestTrace _trace;
    private readonly long _start;
    private List<KeyValuePair<string, object>>? _attributes;
    private List<(string Name, long Timestamp, KeyValuePair<string, object>[]? Attributes)>? _events;

    internal TraceSpanBuilder(RequestTrace trace, string name, string kind, TraceSpanBuilder? parent, long start)
    {
        _trace = trace;
        Name = name;
        Kind = kind;
        Parent = parent;
        _start = start;
        Id = ActivitySpanId.CreateRandom().ToHexString();
    }

    public string Id { get; }
    public string Name { get; }
    public string Kind { get; }
    public TraceSpanBuilder? Parent { get; }
    public long? EndTimestamp { get; private set; }
    public string Status { get; private set; } = TraceSpanStatus.Unset;
    public string? StatusMessage { get; private set; }

    /// <summary>Starts a child span now.</summary>
    public TraceSpanBuilder Child(string name, string kind = TraceSpanKinds.Internal) => _trace.Start(name, kind, this);

    public TraceSpanBuilder Set(string key, string? value)
    {
        if (value is not null) Add(key, value);
        return this;
    }

    public TraceSpanBuilder Set(string key, long? value)
    {
        if (value is { } v) Add(key, v);
        return this;
    }

    public TraceSpanBuilder Set(string key, double? value)
    {
        if (value is { } v && double.IsFinite(v)) Add(key, v);
        return this;
    }

    public TraceSpanBuilder Set(string key, bool? value)
    {
        if (value is { } v) Add(key, v);
        return this;
    }

    /// <summary>A moment within the span (e.g. the first token), with optional attributes.</summary>
    public TraceSpanBuilder Event(string name, params KeyValuePair<string, object>[] attributes) =>
        EventAt(name, Stopwatch.GetTimestamp(), attributes);

    public TraceSpanBuilder EventAt(string name, long timestamp, params KeyValuePair<string, object>[] attributes)
    {
        (_events ??= []).Add((name, timestamp, attributes.Length == 0 ? null : attributes));
        return this;
    }

    public TraceSpanBuilder Ok()
    {
        Status = TraceSpanStatus.Ok;
        StatusMessage = null;
        return this;
    }

    public TraceSpanBuilder Error(string? message)
    {
        Status = TraceSpanStatus.Error;
        StatusMessage = message is { Length: > 300 } ? message[..300] : message;
        return this;
    }

    /// <summary>Ends the span now (a second call is ignored).</summary>
    public void End() => EndAt(Stopwatch.GetTimestamp());

    public void EndAt(long timestamp) => EndTimestamp ??= timestamp;

    public void Dispose() => End();

    private void Add(string key, object value)
    {
        _attributes ??= [];
        for (var i = 0; i < _attributes.Count; i++)
        {
            if (_attributes[i].Key != key) continue;
            _attributes[i] = new(key, value);
            return;
        }
        _attributes.Add(new(key, value));
    }

    internal TraceSpan ToSpan(string requestId, long t0) => new()
    {
        RequestId = requestId,
        SpanId = Id,
        ParentSpanId = Parent?.Id,
        Name = Name,
        Kind = Kind,
        StartUs = Math.Max(0, RequestTrace.Micros(t0, _start)),
        DurationUs = Math.Max(0, RequestTrace.Micros(_start, EndTimestamp ?? _start)),
        Status = Status,
        StatusMessage = StatusMessage,
        AttributesJson = _attributes is null ? null : WriteObject(_attributes),
        EventsJson = _events is null ? null : WriteEvents(t0),
    };

    // Utf8JsonWriter keeps the JSON free of reflection (AOT) and of any serializer options.
    private string WriteEvents(long t0)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var (name, timestamp, attributes) in _events!)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteNumber("offset_us", Math.Max(0, RequestTrace.Micros(t0, timestamp)));
                if (attributes is not null)
                {
                    writer.WritePropertyName("attributes");
                    WriteValues(writer, attributes);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string WriteObject(IEnumerable<KeyValuePair<string, object>> values)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var writer = new Utf8JsonWriter(buffer)) WriteValues(writer, values);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteValues(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object>> values)
    {
        writer.WriteStartObject();
        foreach (var (key, value) in values)
        {
            switch (value)
            {
                case string s: writer.WriteString(key, s); break;
                case long l: writer.WriteNumber(key, l); break;
                case int i: writer.WriteNumber(key, i); break;
                case double d when double.IsFinite(d): writer.WriteNumber(key, d); break;
                case bool b: writer.WriteBoolean(key, b); break;
            }
        }
        writer.WriteEndObject();
    }
}
