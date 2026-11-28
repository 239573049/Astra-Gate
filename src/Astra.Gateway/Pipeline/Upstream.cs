using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Astra.Core;
using Astra.Core.Models;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Pipeline;

/// <summary>The registered protocol codecs (one per <see cref="ApiProtocol"/>).</summary>
public sealed class CodecRegistry
{
    private readonly Dictionary<ApiProtocol, IProtocolCodec> _codecs;

    public CodecRegistry(IEnumerable<IProtocolCodec> codecs) => _codecs = codecs.ToDictionary(c => c.Protocol);

    public IProtocolCodec? Get(ApiProtocol protocol) => _codecs.GetValueOrDefault(protocol);

    public IProtocolCodec Require(ApiProtocol protocol) =>
        Get(protocol) ?? throw new InvalidOperationException($"No codec registered for {protocol.ToId()}");
}

/// <summary>Upstream request URLs per protocol (plan §6.1 / §6.5).</summary>
public static class UpstreamUrls
{
    /// <summary>Generation endpoint. Gemini puts the model and the streaming mode in the URL.</summary>
    public static string For(ProviderEndpoint endpoint, string model, bool stream)
    {
        if (endpoint.FullUrl) return endpoint.BaseUrl;
        return endpoint.Protocol switch
        {
            ApiProtocol.OpenAIChat => Append(endpoint.BaseUrl, "/chat/completions"),
            ApiProtocol.OpenAIResponses => Append(endpoint.BaseUrl, "/responses"),
            ApiProtocol.Anthropic => Append(endpoint.BaseUrl, AnthropicPrefix(endpoint.BaseUrl) + "/messages"),
            _ => Append(endpoint.BaseUrl, $"/models/{GeminiModel(model)}:{(stream ? "streamGenerateContent" : "generateContent")}")
                 + (stream ? "?alt=sse" : ""),
        };
    }

    /// <summary>Anthropic /v1/messages/count_tokens, Gemini :countTokens; null for the OpenAI protocols.</summary>
    public static string? CountTokens(ProviderEndpoint endpoint, string model) => endpoint.Protocol switch
    {
        ApiProtocol.Anthropic when !endpoint.FullUrl => Append(endpoint.BaseUrl, AnthropicPrefix(endpoint.BaseUrl) + "/messages/count_tokens"),
        ApiProtocol.Gemini when !endpoint.FullUrl => Append(endpoint.BaseUrl, $"/models/{GeminiModel(model)}:countTokens"),
        _ => null,
    };

    /// <summary>
    /// An OpenAI Images API address (<paramref name="path"/> = <c>images/generations</c> | <c>images/edits</c>; issue #17)
    /// beside an OpenAI endpoint. A complete address is taken apart at its <c>/chat/completions</c> or <c>/responses</c>
    /// (an Azure deployment keeps its <c>?api-version=</c>); null when it ends in neither.
    /// </summary>
    public static string? Images(ProviderEndpoint endpoint, string path)
    {
        if (endpoint.Protocol is not (ApiProtocol.OpenAIChat or ApiProtocol.OpenAIResponses)) return null;
        if (!endpoint.FullUrl) return Append(endpoint.BaseUrl, "/" + path);
        var uri = new Uri(endpoint.BaseUrl);
        var current = uri.AbsolutePath.TrimEnd('/');
        foreach (var suffix in (string[])["/chat/completions", "/responses"])
            if (current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return new UriBuilder(uri) { Path = current[..^suffix.Length] + "/" + path }.Uri.AbsoluteUri;
        return null;
    }

    /// <summary>A path beside the endpoint's base address (Gemini's <c>models/x:embedContent</c>); null for a full-URL endpoint.</summary>
    public static string? Beside(ProviderEndpoint endpoint, string path) => endpoint.FullUrl ? null : Append(endpoint.BaseUrl, "/" + path);

    /// <summary>Appends <c>key=value</c> to the query string (Gemini ?key= auth).</summary>
    public static string WithQuery(string url, string key, string value)
    {
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";
    }

    private static string GeminiModel(string model) =>
        Uri.EscapeDataString(model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model);

    // Anthropic base URLs are given either as https://api.anthropic.com or …/v1 (DeepSeek: …/anthropic).
    private static string AnthropicPrefix(string baseUrl) =>
        new Uri(baseUrl).AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal) ? "" : "/v1";

    private static string Append(string baseUrl, string path)
    {
        var uri = new Uri(baseUrl);
        var builder = new UriBuilder(uri) { Path = uri.AbsolutePath.TrimEnd('/') + "/" + path.TrimStart('/') };
        return builder.Uri.AbsoluteUri;
    }
}

/// <summary>
/// Long-lived upstream HttpClients, one per proxy setting, plus the smart-routing judge's own (<see cref="ForJudge"/>).
/// No overall timeout: streams can run for minutes; the pipeline enforces the connect timeout here and an idle timeout per read.
/// </summary>
public sealed class GatewayHttpClients : IDisposable
{
    /// <summary>The key of <see cref="ForJudge"/>'s client: no proxy URL looks like it.</summary>
    private const string JudgeKey = "\0judge";

    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    /// <summary>
    /// Set on a request sent through <see cref="ForJudge"/>: called when that request has to open a new connection
    /// before it can be sent, so its sender can allow for the handshake.
    /// </summary>
    public static readonly HttpRequestOptionsKey<Action> NewConnection = new("astra.new-connection");

    /// <summary>Overrides the transport (tests point the gateway at an in-process fake upstream).</summary>
    public Func<string?, HttpMessageHandler>? HandlerFactory { get; set; }

    public HttpClient For(Provider provider) => For(provider.HttpProxy);

    /// <summary>The client for one proxy URL (a subscription account's own proxy, or the provider's); null = system proxy.</summary>
    public HttpClient For(string? proxy) => _clients.GetOrAdd(proxy ?? "", key => Create(key.Length == 0 ? null : key));

    /// <summary>
    /// The smart-routing judge's client (system proxy, like <c>For(null)</c>). A judgement is made once per session or
    /// turn, often minutes apart, and must answer within about two seconds — less than opening a connection can take
    /// (a TLS handshake through a local proxy took 1–5 s when measured). So its connections stay pooled for an hour
    /// instead of two minutes, HTTP/2 (when the service offers it) lets concurrent judgements share one, and keep-alive
    /// pings keep an idle one alive — and notice one that died, where an idle HTTP/1.1 connection was seen to hang
    /// silently. A request that still has to connect first is told so through <see cref="NewConnection"/>.
    /// </summary>
    public HttpClient ForJudge() => _clients.GetOrAdd(JudgeKey, _ => new HttpClient(HandlerFactory?.Invoke(null) ?? new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionIdleTimeout = TimeSpan.FromHours(1),
        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
        KeepAlivePingTimeout = TimeSpan.FromSeconds(15),
        KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
        Proxy = SystemProxy.Dynamic,
        UseProxy = true,
        ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
        ConnectCallback = ConnectAsync,
    }) { Timeout = Timeout.InfiniteTimeSpan });

    /// <summary>The handler's own way of connecting (a dual-mode TCP socket without Nagle), after telling the request that needs it.</summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        if (context.InitialRequestMessage.Options.TryGetValue(NewConnection, out var notify)) notify();
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private HttpClient Create(string? proxy)
    {
        HttpMessageHandler handler = HandlerFactory?.Invoke(proxy) ?? new UpstreamDecompressionHandler(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            // Issue #56: SocketsHttpHandler's AutomaticDecompression unconditionally appends gzip/deflate/br/zstd
            // even when a streaming request explicitly asks for Accept-Encoding: identity. Keep it off here and let
            // UpstreamDecompressionHandler advertise compression only when the request did not set Accept-Encoding,
            // while still decoding any compressed response an upstream sends back anyway.
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            // 提供商配了 httpProxy 就用它；没配则默认走系统代理（环境变量 > 操作系统设置，回环直连）。
            Proxy = proxy is null ? SystemProxy.Dynamic : new WebProxy(proxy),
            UseProxy = true,
            // ASP.NET Core runs every inbound request under an Activity that adopts the agent's traceparent and
            // baggage; the default propagator would copy both into the provider request (a header the real CLIs never
            // send, and the agent's baggage handed to a third party). Trace context stays inside Astra.
            ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
        });
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
    }
}

/// <summary>
/// Upstream response decompression that respects an explicit <c>Accept-Encoding</c> header (issue #56).
/// Unlike <see cref="SocketsHttpHandler.AutomaticDecompression"/>, which appends <c>gzip, deflate, br, zstd</c>
/// even on top of <c>Accept-Encoding: identity</c>, this handler only advertises compressed encodings when the
/// caller left <c>Accept-Encoding</c> unset — and still decompresses any <c>Content-Encoding</c> an upstream
/// sends in spite of <c>identity</c>.
/// </summary>
public sealed class UpstreamDecompressionHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private static readonly StringWithQualityHeaderValue[] DefaultEncodings =
    [
        new("gzip"),
        new("deflate"),
        new("br"),
        new("zstd"),
    ];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.AcceptEncoding.Count == 0)
        {
            foreach (var encoding in DefaultEncodings)
                request.Headers.AcceptEncoding.Add(encoding);
        }
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        DecompressIfNeeded(response);
        return response;
    }

    /// <summary>Wraps <paramref name="response"/>'s content in streaming decoders when <c>Content-Encoding</c> is present.</summary>
    public static void DecompressIfNeeded(HttpResponseMessage response)
    {
        var encodings = response.Content.Headers.ContentEncoding;
        if (encodings.Count == 0) return;

        var remaining = encodings.ToList();
        var toDecode = new List<string>();
        for (var i = remaining.Count - 1; i >= 0; i--)
        {
            var token = remaining[i].Trim().ToLowerInvariant();
            if (token == "identity")
            {
                remaining.RemoveAt(i);
                continue;
            }
            if (token is "gzip" or "x-gzip" or "deflate" or "br" or "zstd")
            {
                toDecode.Add(token);
                remaining.RemoveAt(i);
                continue;
            }
            break;
        }
        if (toDecode.Count == 0) return;
        response.Content = new DecompressedHttpContent(response.Content, toDecode, remaining);
    }

    private sealed class DecompressedHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly List<string> _encodings;

        public DecompressedHttpContent(HttpContent inner, List<string> encodings, List<string> remaining)
        {
            _inner = inner;
            _encodings = encodings;
            foreach (var header in inner.Headers)
            {
                if (header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
                    || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    continue;
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            foreach (var encoding in remaining)
                Headers.ContentEncoding.Add(encoding);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            var stream = await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return Wrap(stream);
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
            Wrap(_inner.ReadAsStream(cancellationToken));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var decoded = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await decoded.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        private Stream Wrap(Stream stream)
        {
            var current = stream;
            try
            {
                foreach (var encoding in _encodings)
                {
                    current = encoding switch
                    {
                        "gzip" or "x-gzip" => new GZipStream(current, CompressionMode.Decompress),
                        "deflate" => new ZLibStream(current, CompressionMode.Decompress),
                        "br" => new BrotliStream(current, CompressionMode.Decompress),
                        "zstd" => new ZstandardStream(current, CompressionMode.Decompress),
                        _ => current,
                    };
                }
                return current;
            }
            catch
            {
                current.Dispose();
                throw;
            }
        }
    }
}
