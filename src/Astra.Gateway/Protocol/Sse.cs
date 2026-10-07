using System.Text;

namespace Astra.Gateway.Protocol;

/// <summary>
/// Incremental server-sent-events parser (WHATWG rules: "event:" / "data:" fields, multi-line data joined with
/// "\n", blank line dispatches, ":" comments ignored, CR/LF/CRLF line endings). Feed raw UTF-8 chunks as they
/// arrive; complete events come out in order.
/// </summary>
public sealed class SseParser
{
    private readonly Decoder _utf8 = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private readonly StringBuilder _data = new();
    private string? _event;
    private bool _hasData;
    private bool _lastWasCr;

    public IReadOnlyList<SseEvent> Feed(ReadOnlySpan<byte> chunk)
    {
        var chars = new char[_utf8.GetCharCount(chunk, flush: false)];
        _utf8.GetChars(chunk, chars, flush: false);
        return Feed(chars);
    }

    public IReadOnlyList<SseEvent> Feed(ReadOnlySpan<char> text)
    {
        var events = new List<SseEvent>();
        foreach (var c in text)
        {
            if (c == '\n' && _lastWasCr)
            {
                _lastWasCr = false;
                continue;
            }
            _lastWasCr = c == '\r';
            if (c is '\r' or '\n')
            {
                ProcessLine(_line.ToString(), events);
                _line.Clear();
            }
            else
            {
                _line.Append(c);
            }
        }
        return events;
    }

    /// <summary>End of stream: dispatches a final event that was not followed by a blank line.</summary>
    public IReadOnlyList<SseEvent> Flush()
    {
        var events = new List<SseEvent>();
        if (_line.Length > 0)
        {
            ProcessLine(_line.ToString(), events);
            _line.Clear();
        }
        Dispatch(events);
        return events;
    }

    /// <summary>Parses a complete SSE document (tests, fixtures).</summary>
    public static IReadOnlyList<SseEvent> ParseAll(string text)
    {
        var parser = new SseParser();
        var list = new List<SseEvent>(parser.Feed(text.AsSpan()));
        list.AddRange(parser.Flush());
        return list;
    }

    private void ProcessLine(string line, List<SseEvent> events)
    {
        if (line.Length == 0)
        {
            Dispatch(events);
            return;
        }
        if (line[0] == ':') return;
        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];
        switch (field)
        {
            case "event":
                _event = value;
                break;
            case "data":
                if (_hasData) _data.Append('\n');
                _data.Append(value);
                _hasData = true;
                break;
        }
    }

    private void Dispatch(List<SseEvent> events)
    {
        if (_hasData) events.Add(new SseEvent(string.IsNullOrEmpty(_event) ? null : _event, _data.ToString()));
        _data.Clear();
        _hasData = false;
        _event = null;
    }
}

public static class SseWriter
{
    /// <summary>Serializes one event ("event: x\ndata: …\n\n"); multi-line data becomes several data lines.</summary>
    public static string Format(SseEvent e)
    {
        var sb = new StringBuilder();
        if (e.Event is not null) sb.Append("event: ").Append(e.Event).Append('\n');
        foreach (var line in e.Data.Split('\n')) sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');
        return sb.ToString();
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
}
