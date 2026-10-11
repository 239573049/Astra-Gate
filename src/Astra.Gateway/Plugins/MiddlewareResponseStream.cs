using Astra.Plugins.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Astra.Gateway.Plugins;

/// <summary>
/// The response body a request's middleware sees (magpie's middleware writer, internal/middleware/run.go):
/// a server-sent event stream goes to onEvent event by event, a whole JSON reply to onResponse, anything
/// else — or a compressed body — passes as it is. The mode is decided on the first write, from the
/// headers set by then. Installed as the request's <see cref="IHttpResponseBodyFeature"/>, so writes
/// through <c>Response.Body</c> and <c>Response.BodyWriter</c> both come here.
/// </summary>
public sealed class MiddlewareResponseStream : Stream
{
    private enum Mode
    {
        Unknown,
        Events,
        Whole,
        AsIs,
    }

    private readonly HttpResponse _response;
    private readonly Stream _inner;
    private readonly MiddlewareRun _run;
    private readonly IHttpResponseBodyFeature _original;
    private readonly MemoryStream _buffer = new();
    private Mode _mode;
    private bool _completed;

    private MiddlewareResponseStream(HttpContext ctx, IHttpResponseBodyFeature original, MiddlewareRun run)
    {
        _response = ctx.Response;
        _original = original;
        _inner = original.Stream;
        _run = run;
    }

    /// <summary>
    /// Puts the request's response body through <paramref name="run"/>'s onEvent / onResponse hooks; null
    /// (nothing installed) when no middleware has either.
    /// </summary>
    public static MiddlewareResponseStream? Install(HttpContext ctx, MiddlewareRun run)
    {
        if (!run.WantsEvents && !run.WantsResponse) return null;
        var original = ctx.Features.Get<IHttpResponseBodyFeature>()!;
        var stream = new MiddlewareResponseStream(ctx, original, run);
        ctx.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(stream, original));
        return stream;
    }

    /// <summary>
    /// Writes what is still held — a whole reply after its onResponse hooks, an unfinished last event as it
    /// is — and puts the original body back. Called once the pipeline has written everything.
    /// </summary>
    public async Task CompleteAsync(CancellationToken ct)
    {
        if (_completed) return;
        _completed = true;
        var feature = _response.HttpContext.Features.Get<IHttpResponseBodyFeature>();
        // anything the PipeWriter still holds reaches this stream first
        if (feature is StreamResponseBodyFeature wrapped && wrapped.Stream == this) await wrapped.Writer.FlushAsync(ct);
        _response.HttpContext.Features.Set(_original);
        switch (_mode)
        {
            case Mode.Whole:
                var body = _run.Response(_buffer.ToArray(), _response.StatusCode);
                await _inner.WriteAsync(body, ct);
                break;
            case Mode.Events when _buffer.Length > 0:
                await _inner.WriteAsync(_buffer.GetBuffer().AsMemory(0, (int)_buffer.Length), ct);
                break;
        }
        _buffer.SetLength(0);
        await _inner.FlushAsync(ct);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (_mode == Mode.Unknown) Decide(buffer.Span);
        switch (_mode)
        {
            case Mode.AsIs:
                await _inner.WriteAsync(buffer, ct);
                return;
            case Mode.Whole:
                _buffer.Write(buffer.Span);
                return;
        }
        _buffer.Write(buffer.Span);
        while (true)
        {
            var held = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);
            var (index, length) = SseEventFramer.EventEnd(held);
            if (index < 0) break;
            var ev = held[..(index + length)].ToArray();
            var rest = held[(index + length)..].ToArray();
            _buffer.SetLength(0);
            _buffer.Write(rest);
            if (_run.Event(ev) is { } output) await _inner.WriteAsync(output, ct);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken ct) => _mode == Mode.Whole ? Task.CompletedTask : _inner.FlushAsync(ct);

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    // A body that is encoded passes as it is; an event stream goes event by event; a JSON one whole.
    // A stream no middleware wants events of (or a whole reply none wants) passes as it is too.
    private void Decide(ReadOnlySpan<byte> first)
    {
        var encoding = _response.Headers.ContentEncoding.ToString();
        var type = _response.ContentType ?? "";
        _mode = encoding.Length > 0 && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase) ? Mode.AsIs
            : type.Contains("event-stream", StringComparison.OrdinalIgnoreCase) ? Mode.Events
            : type.Length == 0 && (first.StartsWith("event:"u8) || first.StartsWith("data:"u8)) ? Mode.Events
            : type.Contains("json", StringComparison.OrdinalIgnoreCase) ? Mode.Whole
            : Mode.AsIs;
        if ((_mode == Mode.Events && !_run.WantsEvents) || (_mode == Mode.Whole && !_run.WantsResponse)) _mode = Mode.AsIs;
        // an event stream may have flushed its headers before its first byte; a length set before then is wrong now
        if (_mode != Mode.AsIs && !_response.HasStarted) _response.ContentLength = null;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
