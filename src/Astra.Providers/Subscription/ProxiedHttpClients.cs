using System.Collections.Concurrent;
using System.Net;
using Astra.Core.Models;

namespace Astra.Providers.Subscription;

/// <summary>
/// Outbound clients for subscription traffic that must leave through an explicit proxy (the account's
/// <see cref="ProviderAccount.HttpProxy"/>, else the provider's <see cref="Provider.HttpProxy"/>). One pooled handler
/// per proxy URL; every <see cref="Create"/> returns a fresh client over it, so callers may dispose what they get
/// (the handler is never disposed with it). Without a proxy the named clients of the host apply (system proxy).
/// </summary>
public static class ProxiedHttpClients
{
    private static readonly ConcurrentDictionary<string, HttpMessageHandler> Handlers = new(StringComparer.Ordinal);

    /// <summary>Overrides the transport per proxy URL (tests); null = a real <see cref="SocketsHttpHandler"/>.</summary>
    public static Func<string, HttpMessageHandler>? HandlerFactory { get; set; }

    /// <summary>A client whose requests go through <paramref name="proxy"/>.</summary>
    public static HttpClient Create(string proxy, TimeSpan timeout)
    {
        var handler = HandlerFactory?.Invoke(proxy) ?? Handlers.GetOrAdd(proxy, url => new SocketsHttpHandler
        {
            Proxy = new WebProxy(url),
            UseProxy = true,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        });
        return new HttpClient(handler, disposeHandler: false) { Timeout = timeout };
    }

    /// <summary>The proxy an account's traffic uses: its own, else the provider's (carried by the config), else none.</summary>
    public static string? ProxyOf(ProviderAccount? account, SubscriptionOAuthConfig config) =>
        account?.HttpProxy is { Length: > 0 } own ? own
        : config.HttpProxy.Length > 0 ? config.HttpProxy
        : null;

    /// <summary>
    /// Validates a proxy URL typed by the user (http / https / socks5 with a host). Returns the trimmed value,
    /// null for empty input, or throws <see cref="ArgumentException"/> with a user-facing message.
    /// </summary>
    public static string? Normalize(string? proxy)
    {
        var value = proxy?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "socks5")
            || uri.Host.Length == 0)
            throw new ArgumentException("代理地址无效：需要 http://、https:// 或 socks5:// 开头的完整地址，例如 http://127.0.0.1:7890");
        return value;
    }
}
