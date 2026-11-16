using System.Text;
using Astra.Gateway.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Astra.Gateway.Http;

/// <summary>
/// A request the gateway makes of its own pipeline on a caller's behalf: a Responses WebSocket's turn (<see cref="ResponsesSocket"/>)
/// and an Ollama chat (<see cref="OllamaEndpoints"/>) are each written as the HTTP request they stand for — the original's
/// headers (so the token, client, user agent and origin judge it as they judge a plain HTTP call), the new body and path — and run
/// through the ordinary handler (<c>GatewayPipeline.HandleAsync</c>); its answer is read back here and said in the caller's own
/// protocol. Nothing in the pipeline knows the difference, so token caps, routing, failover, the request log and the bill
/// are the HTTP route's own. The idea is magpie's (MIT): each response.create / Ollama chat is the POST it stands for.
/// </summary>
public sealed class InternalCall : Stream
{
    // Headers that describe the connection or the original body, not the request: never copied.
    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Content-Type", "Content-Length",
        "Content-Encoding", "Accept", "Accept-Encoding", "Expect",
    };

    private const int MaxBufferedBytes = 1024 * 1024;

    private readonly CallResponseFeature _response;
    private readonly Func<string, ValueTask>? _onEvent;
    private readonly SseParser _parser = new();
    private readonly MemoryStream _buffered = new();
    private bool _begun;

    private InternalCall(HttpContext origin, string path, ReadOnlyMemory<byte> body, CancellationToken ct, Func<string, ValueTask>? onEvent,
        Action<IHeaderDictionary>? edit)
    {
        _onEvent = onEvent;
        _response = new CallResponseFeature { Body = this };
        var request = new HttpRequestFeature
        {
            Method = HttpMethods.Post,
            Scheme = origin.Request.Scheme,
            Path = path,
            PathBase = "",
            QueryString = "",
            Body = new MemoryStream(body.ToArray(), writable: false),
            Headers = new HeaderDictionary(),
        };
        foreach (var (name, values) in origin.Request.Headers)
            if (!Skipped.Contains(name) && !name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
                request.Headers[name] = values;
        request.Headers.ContentType = "application/json";
        request.Headers.ContentLength = body.Length;
        request.Headers.Accept = "text/event-stream, application/json";
        edit?.Invoke(request.Headers);
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(request);
        features.Set<IHttpResponseFeature>(_response);
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(this));
        features.Set<IHttpConnectionFeature>(new HttpConnectionFeature
        {
            RemoteIpAddress = origin.Connection.RemoteIpAddress,
            RemotePort = origin.Connection.RemotePort,
            LocalIpAddress = origin.Connection.LocalIpAddress,
            LocalPort = origin.Connection.LocalPort,
            ConnectionId = origin.Connection.Id,
        });
        Context = new DefaultHttpContext(features) { RequestServices = origin.RequestServices, RequestAborted = ct };
        // What the host's security middleware decided (remote / network origin) travels with the request.
        foreach (var (key, value) in origin.Items) Context.Items[key] = value;
    }

    /// <summary>The request to hand to the handler; its response goes to this object.</summary>
    public DefaultHttpContext Context { get; }

    /// <summary>
    /// Writes <paramref name="origin"/>'s request again as <c>POST <paramref name="path"/></c> with <paramref name="body"/>. When the
    /// handler answers with an event stream (2xx), each event's data goes to <paramref name="onEvent"/> as it arrives; any other
    /// answer is kept whole (up to 1 MiB) as <see cref="BodyText"/>.
    /// </summary>
    public static InternalCall Create(HttpContext origin, string path, ReadOnlyMemory<byte> body, CancellationToken ct,
        Func<string, ValueTask>? onEvent = null, Action<IHeaderDictionary>? edit = null) =>
        new(origin, path, body, ct, onEvent, edit);

    /// <summary>The answer's status (200 until the handler sets one).</summary>
    public int Status => _response.StatusCode;

    /// <summary>Whether the answer was an event stream whose events went to the callback.</summary>
    public bool Streamed { get; private set; }

    /// <summary>The answer's headers.</summary>
    public IHeaderDictionary Headers => _response.Headers;

    /// <summary>The answer's body when it was not streamed.</summary>
    public string BodyText => Encoding.UTF8.GetString(_buffered.GetBuffer(), 0, (int)_buffered.Length);

    /// <summary>Finishes the answer: an event the stream ended in the middle of is delivered.</summary>
    public async ValueTask FinishAsync()
    {
        await Context.Response.BodyWriter.FlushAsync();
        if (Streamed && _onEvent is not null)
            foreach (var sse in _parser.Flush()) await _onEvent(sse.Data);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_begun)
        {
            _begun = true;
            _response.Started = true;
            Streamed = _onEvent is not null && Status / 100 == 2
                       && (Headers.ContentType.ToString()).StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase);
        }
        if (Streamed)
        {
            foreach (var sse in _parser.Feed(buffer.Span)) await _onEvent!(sse.Data);
        }
        else if (_buffered.Length < MaxBufferedBytes) _buffered.Write(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The gateway writes asynchronously.");

    public override void Flush()
    {
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>A response that says it has started once its first byte is written (the default feature never does).</summary>
    private sealed class CallResponseFeature : HttpResponseFeature
    {
        public bool Started { get; set; }

        public override bool HasStarted => Started;
    }
}
