using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Plugins.V2.Reply;

/// <summary>The upstream reply's bytes are not the framing the provider declared, or break a limit (design/plugin-v2 08 §2.2).</summary>
public sealed class ReplyFramingException(string message) : Exception(message);

/// <summary>
/// Cuts an upstream reply into the frames a provider's <c>transformResponse.frame</c> is called with (08 §2.2), one at a time and
/// without reading ahead — so a streaming reply reaches the plugin, and the client, as it arrives.
/// </summary>
public abstract class FrameReader(Stream input, int maxFrame, long maxTotal)
{
    protected Stream Input { get; } = input;
    protected int MaxFrame { get; } = maxFrame;
    private readonly long _maxTotal = maxTotal;
    private long _total;

    /// <summary>The next frame, or null at the end of the reply.</summary>
    public abstract ValueTask<JsonObject?> NextAsync(CancellationToken ct);

    /// <summary>Counts bytes against the reply cap and returns them.</summary>
    protected async ValueTask<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var n = await Input.ReadAsync(buffer.AsMemory(offset, count), ct);
        _total += n;
        if (_total > _maxTotal) throw new ReplyFramingException($"the reply is larger than {_maxTotal} bytes");
        return n;
    }

    /// <summary>A reader for <paramref name="framing"/> (<see cref="ReplyFramings"/>).</summary>
    public static FrameReader For(string framing, Stream input, int maxFrame, long maxTotal) => framing switch
    {
        ReplyFramings.Sse => new SseFrameReader(input, maxFrame, maxTotal),
        ReplyFramings.Ndjson => new NdjsonFrameReader(input, maxFrame, maxTotal),
        ReplyFramings.Json => new WholeJsonFrameReader(input, maxFrame, maxTotal),
        ReplyFramings.AwsEventStream => new EventStreamFrameReader(input, maxFrame, maxTotal),
        ReplyFramings.Connect => new ConnectFrameReader(input, maxFrame, maxTotal),
        _ => throw new ArgumentException($"unknown framing \"{framing}\""),
    };

    /// <summary>{ json } when <paramref name="text"/> parses as JSON, else { text }.</summary>
    internal static JsonObject Body(string text)
    {
        try
        {
            if (JsonNode.Parse(text) is { } j) return new JsonObject { ["json"] = j };
        }
        catch (JsonException)
        {
        }
        return new JsonObject { ["text"] = text };
    }

    /// <summary>Reads UTF-8 lines (\n, \r\n or \r) one at a time.</summary>
    protected sealed class LineSource(FrameReader owner)
    {
        private readonly byte[] _buf = new byte[16 * 1024];
        private int _pos, _len;
        private bool _skipLf, _eof;
        private readonly MemoryStream _line = new();

        /// <summary>The next line without its terminator; null at the end. A final line without a terminator is returned.</summary>
        public async ValueTask<string?> NextAsync(CancellationToken ct)
        {
            while (true)
            {
                if (_pos >= _len)
                {
                    if (_eof) break;
                    _len = await owner.ReadAsync(_buf, 0, _buf.Length, ct);
                    _pos = 0;
                    if (_len == 0)
                    {
                        _eof = true;
                        break;
                    }
                }
                while (_pos < _len)
                {
                    var b = _buf[_pos++];
                    if (_skipLf)
                    {
                        _skipLf = false;
                        if (b == (byte)'\n') continue;
                    }
                    if (b is (byte)'\n' or (byte)'\r')
                    {
                        _skipLf = b == (byte)'\r';
                        return Take();
                    }
                    if (_line.Length >= owner.MaxFrame) throw new ReplyFramingException($"a line is longer than {owner.MaxFrame} bytes");
                    _line.WriteByte(b);
                }
            }
            return _line.Length > 0 ? Take() : null;
        }

        private string Take()
        {
            var s = Encoding.UTF8.GetString(_line.GetBuffer(), 0, (int)_line.Length);
            _line.SetLength(0);
            return s;
        }
    }
}

/// <summary>Server-sent events as the HTML spec reads them: multi-line <c>data</c>, comments, CR / LF / CRLF, one space after the colon; frame { event?, data, id?, json? }.</summary>
public sealed class SseFrameReader(Stream input, int maxFrame, long maxTotal) : FrameReader(input, maxFrame, maxTotal)
{
    private LineSource? _lines;
    private bool _first = true;

    public override async ValueTask<JsonObject?> NextAsync(CancellationToken ct)
    {
        _lines ??= new LineSource(this);
        string? evt = null, id = null;
        StringBuilder? data = null;
        while (true)
        {
            var line = await _lines.NextAsync(ct);
            if (_first)
            {
                _first = false;
                if (line is not null && line.StartsWith('\uFEFF')) line = line[1..];
            }
            // the stream ended mid-event: a missing final blank line still delivers what arrived
            if (line is null) return data is null ? null : Frame(evt, id, data.ToString());
            if (line.Length == 0)
            {
                if (data is null)
                {
                    evt = id = null;
                    continue;
                }
                return Frame(evt, id, data.ToString());
            }
            if (line[0] == ':') continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            switch (field)
            {
                case "data":
                    if (data is null) data = new StringBuilder();
                    else data.Append('\n');
                    if (data.Length + value.Length > MaxFrame) throw new ReplyFramingException($"an event is larger than {MaxFrame} bytes");
                    data.Append(value);
                    break;
                case "event": evt = value; break;
                case "id": id = value; break;
            }
        }
    }

    private static JsonObject Frame(string? evt, string? id, string data)
    {
        var f = new JsonObject();
        if (!string.IsNullOrEmpty(evt)) f["event"] = evt;
        f["data"] = data;
        if (id is not null) f["id"] = id;
        try
        {
            if (JsonNode.Parse(data) is { } j) f["json"] = j;
        }
        catch (JsonException)
        {
        }
        return f;
    }
}

/// <summary>Newline-delimited JSON: one frame per non-empty line, { json } or (not JSON) { text }.</summary>
public sealed class NdjsonFrameReader(Stream input, int maxFrame, long maxTotal) : FrameReader(input, maxFrame, maxTotal)
{
    private LineSource? _lines;

    public override async ValueTask<JsonObject?> NextAsync(CancellationToken ct)
    {
        _lines ??= new LineSource(this);
        while (await _lines.NextAsync(ct) is { } line)
            if (line.Trim().Length > 0) return Body(line);
        return null;
    }
}

/// <summary>The whole reply as one frame, { json } or (not JSON) { text }.</summary>
public sealed class WholeJsonFrameReader(Stream input, int maxFrame, long maxTotal) : FrameReader(input, maxFrame, maxTotal)
{
    private bool _done;

    public override async ValueTask<JsonObject?> NextAsync(CancellationToken ct)
    {
        if (_done) return null;
        _done = true;
        var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await ReadAsync(buf, 0, buf.Length, ct)) > 0)
        {
            if (ms.Length + n > MaxFrame) throw new ReplyFramingException($"the reply body is larger than {MaxFrame} bytes");
            ms.Write(buf, 0, n);
        }
        return Body(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
    }
}

/// <summary>
/// Connect streaming (connectrpc.com/docs/protocol, "streaming"): each message is a flags byte, a 4-byte big-endian length and the
/// payload. A data message is the frame <c>{ flags, bytes }</c> (<c>bytes</c>: base64 — usually protobuf, which only the plugin can
/// read); the end-of-stream message (flag 0x02) is <c>{ flags, end: true, json | text }</c>, its payload being Connect's JSON trailer
/// (<c>{ error?, metadata? }</c>). Compressed messages (flag 0x01) are refused: the plugin should not ask for compression.
/// </summary>
public sealed class ConnectFrameReader(Stream input, int maxFrame, long maxTotal) : FrameReader(input, maxFrame, maxTotal)
{
    private bool _ended;

    public override async ValueTask<JsonObject?> NextAsync(CancellationToken ct)
    {
        if (_ended) return null;
        var head = new byte[5];
        var got = await FillAsync(head, ct);
        if (got == 0) return null;
        if (got < 5) throw new ReplyFramingException("the Connect stream ended inside a message header");
        var flags = head[0];
        if ((flags & 0x01) != 0) throw new ReplyFramingException("a compressed Connect message (flag 0x01): compression is not supported");
        var len = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(1));
        if (len > (uint)MaxFrame) throw new ReplyFramingException($"a Connect message is larger than {MaxFrame} bytes");
        var payload = new byte[len];
        if (await FillAsync(payload, ct) < payload.Length) throw new ReplyFramingException("the Connect stream ended inside a message");
        var frame = new JsonObject { ["flags"] = (int)flags };
        if ((flags & 0x02) == 0)
        {
            frame["bytes"] = Convert.ToBase64String(payload);
            return frame;
        }
        // end of stream: nothing after it counts
        _ended = true;
        frame["end"] = true;
        foreach (var (k, v) in Body(Encoding.UTF8.GetString(payload))) frame[k] = v?.DeepClone();
        return frame;
    }

    private async ValueTask<int> FillAsync(byte[] buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var n = await ReadAsync(buf, read, Math.Min(buf.Length - read, 64 * 1024), ct);
            if (n == 0) break;
            read += n;
        }
        return read;
    }
}

/// <summary>
/// AWS event stream (application/vnd.amazon.eventstream): prelude (total length, headers length, CRC32), headers, payload, message CRC32.
/// Frame { headers: { ":event-type": …, … }, json | text, bytes? } — <c>bytes</c> (base64) only for a payload that is not UTF-8 text.
/// </summary>
public sealed class EventStreamFrameReader(Stream input, int maxFrame, long maxTotal) : FrameReader(input, maxFrame, maxTotal)
{
    public override async ValueTask<JsonObject?> NextAsync(CancellationToken ct)
    {
        var prelude = new byte[12];
        var got = await FillAsync(prelude, ct);
        if (got == 0) return null;
        if (got < 12) throw new ReplyFramingException("the event stream ended inside a message prelude");
        if (Crc32(prelude.AsSpan(0, 8)) != BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(8)))
            throw new ReplyFramingException("event stream prelude CRC mismatch");
        var total = BinaryPrimitives.ReadUInt32BigEndian(prelude);
        var headersLen = BinaryPrimitives.ReadUInt32BigEndian(prelude.AsSpan(4));
        if (total < 16 || headersLen > total - 16) throw new ReplyFramingException("event stream message lengths are inconsistent");
        if (total > (uint)MaxFrame) throw new ReplyFramingException($"an event stream message is larger than {MaxFrame} bytes");
        var msg = new byte[total];
        prelude.CopyTo(msg, 0);
        var rest = msg.AsMemory(12);
        if (await FillAsync(rest, ct) < rest.Length) throw new ReplyFramingException("the event stream ended inside a message");
        if (Crc32(msg.AsSpan(0, (int)total - 4)) != BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan((int)total - 4)))
            throw new ReplyFramingException("event stream message CRC mismatch");

        var headers = ParseHeaders(msg.AsSpan(12, (int)headersLen));
        var payload = msg.AsSpan(12 + (int)headersLen, (int)(total - 16 - headersLen));
        var frame = new JsonObject { ["headers"] = headers };
        if (payload.Length == 0) return frame;
        string? text = null;
        try
        {
            text = new UTF8Encoding(false, true).GetString(payload);
        }
        catch (ArgumentException)
        {
        }
        if (text is null) frame["bytes"] = Convert.ToBase64String(payload);
        else foreach (var (k, v) in Body(text)) frame[k] = v?.DeepClone();
        return frame;
    }

    private async ValueTask<int> FillAsync(byte[] buf, CancellationToken ct) => await FillAsync(buf.AsMemory(), ct);

    private async ValueTask<int> FillAsync(Memory<byte> buf, CancellationToken ct)
    {
        var read = 0;
        while (read < buf.Length)
        {
            var arr = new byte[Math.Min(buf.Length - read, 64 * 1024)];
            var n = await ReadAsync(arr, 0, arr.Length, ct);
            if (n == 0) break;
            arr.AsMemory(0, n).CopyTo(buf[read..]);
            read += n;
        }
        return read;
    }

    private static JsonObject ParseHeaders(ReadOnlySpan<byte> s)
    {
        var o = new JsonObject();
        var i = 0;
        try
        {
            while (i < s.Length)
            {
                int nameLen = s[i++];
                var name = Encoding.UTF8.GetString(s.Slice(i, nameLen));
                i += nameLen;
                var type = s[i++];
                JsonNode? v;
                switch (type)
                {
                    case 0: v = true; break;
                    case 1: v = false; break;
                    case 2: v = (sbyte)s[i]; i += 1; break;
                    case 3: v = BinaryPrimitives.ReadInt16BigEndian(s[i..]); i += 2; break;
                    case 4: v = BinaryPrimitives.ReadInt32BigEndian(s[i..]); i += 4; break;
                    case 5: v = BinaryPrimitives.ReadInt64BigEndian(s[i..]); i += 8; break;
                    case 8: v = BinaryPrimitives.ReadInt64BigEndian(s[i..]); i += 8; break; // timestamp: ms since the epoch
                    case 6:
                    case 7:
                    {
                        int len = BinaryPrimitives.ReadUInt16BigEndian(s[i..]);
                        i += 2;
                        var bytes = s.Slice(i, len);
                        i += len;
                        v = type == 7 ? Encoding.UTF8.GetString(bytes) : Convert.ToBase64String(bytes);
                        break;
                    }
                    case 9:
                        v = new Guid(s.Slice(i, 16), bigEndian: true).ToString();
                        i += 16;
                        break;
                    default: throw new ReplyFramingException($"event stream header \"{name}\" has unknown type {type}");
                }
                o[name] = v;
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ReplyFramingException("event stream headers are cut short");
        }
        return o;
    }

    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    /// <summary>CRC-32 (IEEE), as the event stream uses it.</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
