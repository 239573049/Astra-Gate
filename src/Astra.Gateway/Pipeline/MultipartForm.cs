using System.Buffers;
using System.Net;
using System.Text;
using Astra.Gateway.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// As much of multipart/form-data as the Images API needs (issue #17): where each part's value lies in the bytes, so a
/// text field (the model, the prompt) can be read — and rewritten in place — while every other byte, the uploaded
/// images above all, goes upstream exactly as the client sent it.
/// </summary>
public static class MultipartForm
{
    /// <summary>A part: its form field name, its file name (null for a text field) and where its value lies.</summary>
    public readonly record struct Part(string? Name, string? FileName, int ValueStart, int ValueEnd)
    {
        public bool IsText => FileName is null;

        /// <summary>The value of a text field, as UTF-8 text.</summary>
        public string Text(ReadOnlySpan<byte> body) => Encoding.UTF8.GetString(body[ValueStart..ValueEnd]);
    }

    /// <summary>The boundary of a <c>multipart/form-data</c> content type; null for any other content type.</summary>
    public static string? BoundaryOf(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var media)
            || !media.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)) return null;
        var boundary = HeaderUtilities.RemoveQuotes(media.Boundary).ToString();
        // RFC 2046 §5.1.1: 1–70 characters.
        return boundary.Length is > 0 and <= 70 ? boundary : null;
    }

    /// <summary>The parts of a whole form body.</summary>
    /// <exception cref="ProtocolException">The body is not a complete multipart/form-data body.</exception>
    public static List<Part> Parse(ReadOnlySpan<byte> body, string boundary)
    {
        var scanner = new Scanner(boundary);
        var parts = new List<Part>();
        while (scanner.TryNext(body, out var part)) parts.Add(part);
        if (!scanner.Ended) throw Incomplete();
        return parts;
    }

    /// <summary>
    /// Reads <paramref name="body"/> only until the text field <paramref name="field"/> is complete: the bytes read so far
    /// (the caller owns them) and where that field lies in them — null when the form ended without it. Whatever follows
    /// — the uploads, when the field comes first, as the OpenAI SDKs and docs send it — is still unread.
    /// </summary>
    /// <exception cref="ProtocolException">The body is not a complete multipart/form-data body.</exception>
    /// <exception cref="GatewayException">More than <paramref name="maxBytes"/> come before the field.</exception>
    public static async Task<(PooledBuffer Read, Part? Field)> ReadUntilFieldAsync(
        Stream body, string boundary, string field, long maxBytes, CancellationToken ct)
    {
        var buffer = new PooledBuffer(16 * 1024);
        try
        {
            var scanner = new Scanner(boundary);
            while (true)
            {
                while (scanner.TryNext(buffer.Span, out var part))
                    if (part.IsText && part.Name == field) return (buffer, part);
                if (scanner.Ended) return (buffer, null);
                if (buffer.Length > maxBytes)
                    throw new GatewayException(413, "request_too_large", $"请求体在 {field} 字段之前就超过了 {maxBytes / (1024 * 1024)} MiB。");
                int read;
                try
                {
                    // At most one byte past the limit: enough to tell that it was passed.
                    var room = buffer.GetMemory(16 * 1024);
                    read = await body.ReadAsync(room[..(int)Math.Min(room.Length, maxBytes + 1 - buffer.Length)], ct);
                }
                catch (BadHttpRequestException e)
                {
                    // The server's own limits and framing, as RequestBody reports them.
                    throw new GatewayException(e.StatusCode, e.StatusCode == 413 ? "request_too_large" : "invalid_request_error", "请求体无效或过大。");
                }
                if (read == 0) throw Incomplete();
                buffer.Advance(read);
            }
        }
        catch
        {
            buffer.Release();
            throw;
        }
    }

    /// <summary><paramref name="body"/> with each range replaced by its value (UTF-8), in a new buffer the caller owns.</summary>
    public static PooledBuffer Splice(ReadOnlySpan<byte> body, IEnumerable<(int Start, int End, string Value)> replacements)
    {
        var result = new PooledBuffer(body.Length + 256);
        var at = 0;
        foreach (var (start, end, value) in replacements.OrderBy(r => r.Start))
        {
            result.Write(body[at..start]);
            Encoding.UTF8.GetBytes(value, result);
            at = end;
        }
        result.Write(body[at..]);
        return result;
    }

    private static ProtocolException Incomplete() => new("multipart/form-data 请求体不完整。");

    private static ProtocolException Malformed() => new("multipart/form-data 请求体格式无效。");

    /// <summary>
    /// Finds the parts in a body that may still be arriving: each call goes on from where the last one stopped and
    /// returns the next part whose value is complete, or false when more bytes are needed (or the closing delimiter came).
    /// </summary>
    private sealed class Scanner(string boundary)
    {
        private const int MaxHeaderBytes = 16 * 1024;
        private const int MaxLineBytes = 1024;

        // "\r\n--boundary": what ends a part's value; the first delimiter may come without the CRLF (no preamble).
        private readonly byte[] _delimiter = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        private State _state;
        private int _pos;
        private int _searchFrom;
        private string? _name;
        private string? _fileName;

        public bool Ended => _state == State.End;

        public bool TryNext(ReadOnlySpan<byte> data, out Part part)
        {
            part = default;
            while (true)
            {
                switch (_state)
                {
                    case State.Preamble:
                    {
                        var dashes = _delimiter.AsSpan(2);
                        if (data.Length < dashes.Length && dashes.StartsWith(data)) return false;
                        if (data.StartsWith(dashes))
                        {
                            _pos = dashes.Length;
                        }
                        else
                        {
                            var found = data[_searchFrom..].IndexOf(_delimiter);
                            if (found < 0)
                            {
                                _searchFrom = Math.Max(0, data.Length - _delimiter.Length + 1);
                                return false;
                            }
                            _pos = _searchFrom + found + _delimiter.Length;
                        }
                        _state = State.AfterDelimiter;
                        break;
                    }
                    case State.AfterDelimiter:
                    {
                        var rest = data[_pos..];
                        if (rest.Length < 2) return false;
                        if (rest[0] == (byte)'-' && rest[1] == (byte)'-')
                        {
                            _state = State.End;
                            return false;
                        }
                        var lineEnd = rest.IndexOf("\r\n"u8);
                        if (lineEnd < 0)
                        {
                            if (rest.Length > MaxLineBytes) throw Malformed();
                            return false;
                        }
                        // Only transport padding may follow a boundary on its line.
                        if (rest[..lineEnd].ContainsAnyExcept((byte)' ', (byte)'\t')) throw Malformed();
                        _pos += lineEnd + 2;
                        _state = State.Headers;
                        break;
                    }
                    case State.Headers:
                    {
                        var rest = data[_pos..];
                        if (rest.Length < 2) return false;
                        int headersEnd, valueStart;
                        if (rest.StartsWith("\r\n"u8))
                        {
                            headersEnd = _pos;
                            valueStart = _pos + 2;
                        }
                        else
                        {
                            var end = rest.IndexOf("\r\n\r\n"u8);
                            if (end < 0)
                            {
                                if (rest.Length > MaxHeaderBytes) throw Malformed();
                                return false;
                            }
                            headersEnd = _pos + end;
                            valueStart = headersEnd + 4;
                        }
                        ReadHeaders(data[_pos..headersEnd]);
                        _pos = _searchFrom = valueStart;
                        _state = State.Value;
                        break;
                    }
                    case State.Value:
                    {
                        var found = data[_searchFrom..].IndexOf(_delimiter);
                        if (found < 0)
                        {
                            _searchFrom = Math.Max(_pos, data.Length - _delimiter.Length + 1);
                            return false;
                        }
                        var end = _searchFrom + found;
                        part = new Part(_name, _fileName, _pos, end);
                        _pos = end + _delimiter.Length;
                        _state = State.AfterDelimiter;
                        return true;
                    }
                    default:
                        return false;
                }
            }
        }

        private void ReadHeaders(ReadOnlySpan<byte> headers)
        {
            _name = _fileName = null;
            foreach (var line in Encoding.UTF8.GetString(headers).Split("\r\n"))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || !line.AsSpan(0, colon).Trim().Equals(HeaderNames.ContentDisposition, StringComparison.OrdinalIgnoreCase)) continue;
                if (!ContentDispositionHeaderValue.TryParse(line[(colon + 1)..].Trim(), out var disposition)) throw Malformed();
                _name = HeaderUtilities.RemoveQuotes(disposition.Name).ToString();
                if (disposition.FileNameStar.HasValue || disposition.FileName.HasValue)
                    _fileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).ToString();
            }
        }

        private enum State
        {
            Preamble,
            AfterDelimiter,
            Headers,
            Value,
            End,
        }
    }
}

/// <summary>
/// A form upload sent on as it arrives (issue #17): the bytes already read (maybe with the model rewritten), then the
/// rest of the client's body. It can be sent once only — the client's stream is read as it goes out.
/// </summary>
public sealed class StreamedFormContent : HttpContent
{
    private readonly PooledBuffer _head;
    private readonly Stream _rest;
    private readonly long? _length;
    private int _sent;

    public StreamedFormContent(PooledBuffer head, Stream rest, long? length, string contentType)
    {
        _head = head;
        _rest = rest;
        _length = length;
        Headers.TryAddWithoutValidation(HeaderNames.ContentType, contentType);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _sent, 1) != 0) throw new InvalidOperationException("A streamed form upload can be sent once only.");
        if (!_head.TryAddRef()) throw new ObjectDisposedException(nameof(StreamedFormContent));
        try
        {
            await stream.WriteAsync(_head.Memory, cancellationToken);
        }
        finally
        {
            _head.Release();
        }
        await _rest.CopyToAsync(stream, cancellationToken);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length ?? 0;
        return _length is not null;
    }
}
