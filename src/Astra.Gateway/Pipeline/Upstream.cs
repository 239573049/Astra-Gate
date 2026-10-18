using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
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
        var handler = HandlerFactory?.Invoke(proxy) ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            // 提供商配了 httpProxy 就用它；没配则默认走系统代理（环境变量 > 操作系统设置，回环直连）。
            Proxy = proxy is null ? SystemProxy.Dynamic : new WebProxy(proxy),
            UseProxy = true,
            // ASP.NET Core runs every inbound request under an Activity that adopts the agent's traceparent and
            // baggage; the default propagator would copy both into the provider request (a header the real CLIs never
            // send, and the agent's baggage handed to a third party). Trace context stays inside Astra.
            ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
    }
}
