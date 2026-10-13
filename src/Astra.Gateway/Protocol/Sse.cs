using System.Text;

namespace Astra.Gateway.Protocol;

/// <summary>
/// Incremental server-sent-events parser (WHATWG rules: "event:" / "data:" fields, multi-line data joined with
/// "\n", blank line dispatches, ":" comments ignored, CR/LF/CRLF line endings). Feed raw UTF-8 chunks as they
/// arrive; complete events come out in order. Works on the bytes: the delimiters are ASCII, so lines are found
/// with a vectorized search and only an event's name and data ever become strings (one decode each).
/// </summary>
public sealed class SseParser
{
    private byte[] _line = new byte[256];
    private int _lineLength;
    private byte[] _data = new byte[1024];
    private int _dataLength;
    private string? _event;
    private bool _hasData;
    private bool _lastWasCr;

    public IReadOnlyList<SseEvent> Feed(ReadOnlySpan<byte> chunk)
    {
        List<SseEvent>? events = null;
        if (_lastWasCr && chunk.Length > 0)
        {
            // the LF of a CRLF split across chunks
            _lastWasCr = false;
            if (chunk[0] == (byte)'\n') chunk = chunk[1..];
        }
        while (true)
        {
            var end = chunk.IndexOfAny((byte)'\r', (byte)'\n');
            if (end < 0)
            {
                Append(ref _line, ref _lineLength, chunk);
                break;
            }
            if (_lineLength == 0)
            {
                ProcessLine(chunk[..end], ref events);
            }
            else
            {
                Append(ref _line, ref _lineLength, chunk[..end]);
                ProcessLine(_line.AsSpan(0, _lineLength), ref events);
                _lineLength = 0;
            }
            if (chunk[end] == (byte)'\r')
            {
                if (end + 1 == chunk.Length) _lastWasCr = true;
                else if (chunk[end + 1] == (byte)'\n') end++;
            }
            chunk = chunk[(end + 1)..];
        }
        return events ?? (IReadOnlyList<SseEvent>)[];
    }

    public IReadOnlyList<SseEvent> Feed(ReadOnlySpan<char> text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text)];
        Encoding.UTF8.GetBytes(text, bytes);
        return Feed(bytes);
    }

    /// <summary>End of stream: dispatches a final event that was not followed by a blank line.</summary>
    public IReadOnlyList<SseEvent> Flush()
    {
        List<SseEvent>? events = null;
        if (_lineLength > 0)
        {
            ProcessLine(_line.AsSpan(0, _lineLength), ref events);
            _lineLength = 0;
        }
        Dispatch(ref events);
        return events ?? (IReadOnlyList<SseEvent>)[];
    }

    /// <summary>Parses a complete SSE document (tests, fixtures).</summary>
    public static IReadOnlyList<SseEvent> ParseAll(string text)
    {
        var parser = new SseParser();
        var list = new List<SseEvent>(parser.Feed(text.AsSpan()));
        list.AddRange(parser.Flush());
        return list;
    }

    private void ProcessLine(ReadOnlySpan<byte> line, ref List<SseEvent>? events)
    {
        if (line.Length == 0)
        {
            Dispatch(ref events);
            return;
        }
        if (line[0] == (byte)':') return;
        var colon = line.IndexOf((byte)':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? [] : line[(colon + 1)..];
        if (value.Length > 0 && value[0] == (byte)' ') value = value[1..];
        if (field.SequenceEqual("data"u8))
        {
            if (_hasData) Append(ref _data, ref _dataLength, "\n"u8);
            Append(ref _data, ref _dataLength, value);
            _hasData = true;
        }
        else if (field.SequenceEqual("event"u8))
        {
            _event = Encoding.UTF8.GetString(value);
        }
    }

    private void Dispatch(ref List<SseEvent>? events)
    {
        if (_hasData)
            (events ??= []).Add(new SseEvent(string.IsNullOrEmpty(_event) ? null : _event, Encoding.UTF8.GetString(_data, 0, _dataLength)));
        _dataLength = 0;
        _hasData = false;
        _event = null;
    }

    private static void Append(ref byte[] buffer, ref int length, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        if (buffer.Length - length < bytes.Length) System.Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + bytes.Length));
        bytes.CopyTo(buffer.AsSpan(length));
        length += bytes.Length;
    }
}

public static class SseWriter
{
    /// <summary>Serializes one event ("event: x\ndata: …\n\n"); multi-line data becomes several data lines.</summary>
    public static string Format(SseEvent e)
    {
        var sb = new StringBuilder(e.Data.Length + (e.Event?.Length ?? 0) + 16);
        AppendTo(sb, e);
        return sb.ToString();
    }

    /// <summary><see cref="Format"/> appended to <paramref name="sb"/> (no string of its own).</summary>
    public static void AppendTo(StringBuilder sb, SseEvent e)
    {
        if (e.Event is not null) sb.Append("event: ").Append(e.Event).Append('\n');
        var data = e.Data.AsSpan();
        while (true)
        {
            var newline = data.IndexOf('\n');
            sb.Append("data: ").Append(newline < 0 ? data : data[..newline]).Append('\n');
            if (newline < 0) break;
            data = data[(newline + 1)..];
        }
        sb.Append('\n');
    }
}

/// <summary>
/// Writes a stream of JSON payloads as one JSON array ("[{…},{…}]"), draining incrementally so long
/// generations still flush chunk by chunk. This is the wire format of Gemini's
/// <c>:streamGenerateContent</c> when the client does not pass <c>?alt=sse</c>.
/// </summary>
public sealed class JsonArrayBody
{
    private readonly StringBuilder _pending = new();
    private bool _opened;
    private bool _first = true;

    public void Add(string json)
    {
        if (!_opened)
        {
            _pending.Append('[');
            _opened = true;
        }
        if (!_first) _pending.Append(',');
        _first = false;
        _pending.Append(json);
    }

    /// <summary>Buffered text since the last drain (the opening bracket rides on the first flush); null when empty.</summary>
    public string? Drain()
    {
        if (_pending.Length == 0) return null;
        var text = _pending.ToString();
        _pending.Clear();
        return text;
    }

    /// <summary>The closing bracket; null when nothing was ever added.</summary>
    public string? Close()
    {
        return _opened ? "]" : null;
    }
}

/// <summary>JSON output options for everything the gateway writes (non-ASCII text stays readable, not \uXXXX).</summary>
public static class GatewayJson
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly System.Text.Json.JsonWriterOptions WriterOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [ThreadStatic]
    private static System.Text.Json.Utf8JsonWriter? t_writer;

    // What an idle writer points at, so it does not keep the last output (a pooled buffer) alive.
    private static readonly System.Buffers.ArrayBufferWriter<byte> Detached = new(1);

    /// <summary>
    /// <c>node.ToJsonString(Options)</c> written as UTF-8 straight into <paramref name="output"/> — no UTF-16
    /// string in between. The writer is reused per thread (it holds no buffer of its own).
    /// </summary>
    public static void WriteTo(System.Text.Json.Nodes.JsonNode node, System.Buffers.IBufferWriter<byte> output)
    {
        var writer = t_writer;
        if (writer is null) t_writer = writer = new System.Text.Json.Utf8JsonWriter(output, WriterOptions);
        else writer.Reset(output);
        try
        {
            node.WriteTo(writer, Options);
            writer.Flush();
        }
        finally
        {
            writer.Reset(Detached);
        }
    }
}
