using System.Buffers;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Pipeline;

/// <summary>
/// A growable byte buffer rented from <see cref="ArrayPool{T}.Shared"/> and shared by reference count: the
/// array goes back to the pool when the last holder releases it — the owner (<see cref="Release"/>) and every
/// in-flight send through a <see cref="PooledContent"/>. A send the HTTP stack still runs after its request is
/// over therefore never reads an array another request has rented since. Large bodies stop being fresh
/// large-object-heap allocations that only a gen-2 collection frees.
/// </summary>
public sealed class PooledBuffer : IBufferWriter<byte>
{
    private byte[]? _array;
    private readonly bool _pooled;
    private int _refs = 1;

    /// <summary>An empty buffer with room for <paramref name="capacity"/> bytes.</summary>
    public PooledBuffer(int capacity)
    {
        _array = ArrayPool<byte>.Shared.Rent(Math.Max(capacity, 256));
        _pooled = true;
    }

    private PooledBuffer(byte[] array)
    {
        _array = array;
        Length = array.Length;
    }

    /// <summary>Wraps bytes someone else allocated (nothing goes back to the pool).</summary>
    public static PooledBuffer Wrap(byte[] bytes) => new(bytes);

    /// <summary>UTF-8 of <paramref name="text"/> in a pooled buffer.</summary>
    public static PooledBuffer FromString(string text)
    {
        var buffer = new PooledBuffer(Encoding.UTF8.GetByteCount(text));
        buffer.Length = Encoding.UTF8.GetBytes(text, buffer.Array);
        return buffer;
    }

    /// <summary>Bytes written so far.</summary>
    public int Length { get; private set; }

    public ReadOnlyMemory<byte> Memory => Array.AsMemory(0, Length);

    public ReadOnlySpan<byte> Span => Array.AsSpan(0, Length);

    /// <summary>A read-only stream over the bytes written so far (valid until <see cref="Release"/>).</summary>
    internal Stream AsStream() => new MemoryStream(Array, 0, Length, writable: false);

    private byte[] Array => _array ?? throw new ObjectDisposedException(nameof(PooledBuffer));

    public void Advance(int count) => Length += count;

    /// <summary>Drops the first <paramref name="count"/> bytes.</summary>
    public void DropPrefix(int count)
    {
        Array.AsSpan(count, Length - count).CopyTo(Array);
        Length -= count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return Array.AsMemory(Length);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return Array.AsSpan(Length);
    }

    private void Reserve(int sizeHint)
    {
        var array = Array;
        var needed = Math.Max(sizeHint, 1);
        if (array.Length - Length >= needed) return;
        var grown = ArrayPool<byte>.Shared.Rent((int)Math.Min(System.Array.MaxLength, Math.Max((long)array.Length * 2, (long)Length + needed)));
        array.AsSpan(0, Length).CopyTo(grown);
        _array = grown;
        if (_pooled) ArrayPool<byte>.Shared.Return(array);
    }

    /// <summary>Takes a reference for a send; false once the buffer is gone.</summary>
    internal bool TryAddRef()
    {
        while (true)
        {
            var refs = Volatile.Read(ref _refs);
            if (refs == 0) return false;
            if (Interlocked.CompareExchange(ref _refs, refs + 1, refs) == refs) return true;
        }
    }

    /// <summary>Drops one reference; the last one returns the array to the pool.</summary>
    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) != 0) return;
        var array = Interlocked.Exchange(ref _array, null);
        if (array is not null && _pooled) ArrayPool<byte>.Shared.Return(array);
    }
}

/// <summary>A JSON request body sent straight from a <see cref="PooledBuffer"/> (no copy, length known up front).</summary>
public sealed class PooledContent : HttpContent
{
    private readonly PooledBuffer _buffer;

    public PooledContent(PooledBuffer buffer)
    {
        _buffer = buffer;
        Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        if (!_buffer.TryAddRef()) throw new ObjectDisposedException(nameof(PooledContent));
        try
        {
            await stream.WriteAsync(_buffer.Memory, cancellationToken);
        }
        finally
        {
            _buffer.Release();
        }
    }

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        if (!_buffer.TryAddRef()) throw new ObjectDisposedException(nameof(PooledContent));
        try
        {
            stream.Write(_buffer.Span);
        }
        finally
        {
            _buffer.Release();
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _buffer.Length;
        return true;
    }
}

/// <summary>
/// A client request body, read once into a pooled UTF-8 buffer and parsed once (plan §6): the privacy guard
/// scans and redacts the very tree the pipeline then reads, and a body nothing rewrote goes upstream as the
/// bytes that came in. The tree reads its values out of the buffer, so it is only used until <see cref="Dispose"/>.
/// </summary>
public sealed class RequestBody : IDisposable
{
    private JsonDocument? _document;

    private RequestBody(PooledBuffer buffer) => Buffer = buffer;

    /// <summary>The body's bytes (a UTF-8 byte order mark dropped); sent as they are when nothing changed them.</summary>
    public PooledBuffer Buffer { get; private set; }

    public ReadOnlySpan<byte> Utf8 => Buffer.Span;

    /// <summary>
    /// Reads the whole body, undoing its <c>Content-Encoding</c> (zstd — codex-cli's default —, gzip, br, deflate,
    /// stacked in any order); a body over <paramref name="maxBytes"/>, compressed or decoded, is a 413.
    /// </summary>
    public static async Task<RequestBody> ReadAsync(HttpRequest request, long maxBytes, CancellationToken ct)
    {
        var encodings = ContentEncodings(request.Headers.ContentEncoding.ToString());
        var declared = request.ContentLength;
        if (declared > maxBytes) throw TooLarge(maxBytes);
        var buffer = await ReadAllAsync(request.Body, declared, maxBytes, ct);
        try
        {
            // Codings are listed in the order they were applied: undo the last first.
            for (var i = encodings.Count - 1; i >= 0; i--)
            {
                var decoded = await DecodeAsync(buffer, encodings[i], maxBytes, ct);
                buffer.Release();
                buffer = decoded;
            }
            // What StreamReader did with a byte order mark: drop it.
            if (buffer.Span.StartsWith(Encoding.UTF8.Preamble)) buffer.DropPrefix(Encoding.UTF8.Preamble.Length);
            return new RequestBody(buffer);
        }
        catch
        {
            buffer.Release();
            throw;
        }
    }

    private static async Task<PooledBuffer> ReadAllAsync(Stream source, long? declared, long maxBytes, CancellationToken ct)
    {
        // A declared length sizes the buffer once (+1: the read that sees the end needs room to ask).
        var buffer = new PooledBuffer(declared is { } length ? (int)length + 1 : 16 * 1024);
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.GetMemory(declared is null ? 8 * 1024 : 1), ct);
                if (read == 0) break;
                buffer.Advance(read);
                if (buffer.Length > maxBytes) throw TooLarge(maxBytes);
            }
            return buffer;
        }
        catch
        {
            buffer.Release();
            throw;
        }
    }

    /// <summary>The body's codings, in the order they were applied (<c>identity</c> dropped); an unknown one is a 415.</summary>
    internal static List<string> ContentEncodings(string header)
    {
        var encodings = new List<string>();
        foreach (var token in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var coding = token.ToLowerInvariant();
            switch (coding)
            {
                case "identity":
                    continue;
                case "zstd" or "gzip" or "x-gzip" or "br" or "deflate":
                    encodings.Add(coding);
                    break;
                default:
                    throw new GatewayException(415, "invalid_request_error",
                        $"不支持的请求体压缩格式 Content-Encoding: {token}（支持 zstd、gzip、br、deflate）。");
            }
        }
        return encodings;
    }

    private static async Task<PooledBuffer> DecodeAsync(PooledBuffer encoded, string coding, long maxBytes, CancellationToken ct)
    {
        var input = encoded.AsStream();
        await using Stream decoder = coding switch
        {
            "zstd" => new ZstdSharp.DecompressionStream(input),
            "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
            "br" => new BrotliStream(input, CompressionMode.Decompress),
            // HTTP's "deflate" is zlib-wrapped (RFC 9110 §8.4.1.2), but some clients send raw deflate: sniff the header.
            _ => IsZlibHeader(encoded.Span)
                ? new ZLibStream(input, CompressionMode.Decompress)
                : new DeflateStream(input, CompressionMode.Decompress),
        };
        try
        {
            return await ReadAllAsync(decoder, null, maxBytes, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or ZstdSharp.ZstdException)
        {
            throw new GatewayException(400, "invalid_request_error", $"请求体无法按 Content-Encoding: {coding} 解压。");
        }
    }

    private static bool IsZlibHeader(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0;

    private static GatewayException TooLarge(long maxBytes) =>
        new(413, "request_too_large", $"请求体超过 {maxBytes / (1024 * 1024)} MiB。");

    /// <summary>Swaps in a new body (a middleware's rewrite); a tree parsed before is no longer valid.</summary>
    public void Replace(byte[] bytes)
    {
        _document?.Dispose();
        _document = null;
        Buffer.Release();
        Buffer = PooledBuffer.Wrap(bytes);
    }

    /// <summary>
    /// The body as a JSON object (strict JSON, as the gateway always parsed it), or null when it is not one —
    /// invalid JSON or UTF-8, or another JSON value; the caller then takes the text path (<see cref="Text"/>),
    /// which reports those cases as it always did.
    /// </summary>
    public (JsonElement Root, JsonObject Object)? Parse()
    {
        try
        {
            _document = JsonDocument.Parse(Buffer.Memory);
        }
        catch (JsonException)
        {
            return null;
        }
        var root = _document.RootElement;
        return root.ValueKind == JsonValueKind.Object ? (root, JsonObject.Create(root)!) : null;
    }

    /// <summary>The body as text (invalid UTF-8 replaced, as StreamReader did).</summary>
    public string Text() => Encoding.UTF8.GetString(Utf8);

    public void Dispose()
    {
        _document?.Dispose();
        _document = null;
        Buffer.Release();
    }
}
