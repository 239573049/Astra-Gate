using System.Collections.Concurrent;
using System.Net;
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
/// Long-lived upstream HttpClients, one per proxy setting. No overall timeout: streams can run for minutes;
/// the pipeline enforces the connect timeout here and an idle timeout per read.
/// </summary>
public sealed class GatewayHttpClients : IDisposable
{
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new();

    /// <summary>Overrides the transport (tests point the gateway at an in-process fake upstream).</summary>
    public Func<string?, HttpMessageHandler>? HandlerFactory { get; set; }

    public HttpClient For(Provider provider) => _clients.GetOrAdd(provider.HttpProxy ?? "", key => Create(key.Length == 0 ? null : key));

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
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
    }
}
