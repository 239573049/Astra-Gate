using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Astra.Core;

/// <summary>
/// 所有出站访问（网关转发上游、订阅登录 / 额度、模型列表、更新检查、提供商测试）默认的走代理策略。
/// 设置里的代理模式（<see cref="Configure"/>）决定来源："custom" 用设置里填的代理，"direct" 一律直连；
/// "system"（默认）先看 <c>HTTPS_PROXY</c> / <c>HTTP_PROXY</c>（含小写，<c>NO_PROXY</c> 豁免），
/// 没有环境变量再读操作系统代理（macOS / Windows 的系统设置），最后才是直连。
/// 提供商自己配置了 <c>httpProxy</c> 时以它为准，不走这里。回环地址永远直连（本机 Ollama、LM Studio）。
/// </summary>
public static class SystemProxy
{
    public const string ModeSystem = "system";
    public const string ModeCustom = "custom";
    public const string ModeDirect = "direct";

    /// <summary>Null = "system" mode.</summary>
    private static volatile Override? _override;

    private sealed record Override(string Mode, WebProxy? Proxy, string? ChildUrl, string? Bypass);

    /// <summary>
    /// Applies the proxy chosen in settings. Takes effect on the next request, including on long-lived clients.
    /// "custom" without a valid URL behaves like "system".
    /// </summary>
    public static void Configure(string? mode, string? url, string? bypass, NetworkCredential? credentials)
    {
        _override = mode switch
        {
            ModeDirect => new Override(ModeDirect, null, null, null),
            ModeCustom when Uri.TryCreate(url, UriKind.Absolute, out var uri) => new Override(
                ModeCustom, BuildWebProxy(uri, bypass, credentials), ChildUrl(uri, credentials), bypass),
            _ => null,
        };
    }

    /// <summary>The effective mode: "system", "custom" or "direct".</summary>
    public static string Mode => _override?.Mode ?? ModeSystem;

    /// <summary>
    /// In "custom" mode: the proxy for child processes (credentials inline, as npm / Node expect) and its bypass list.
    /// </summary>
    public static (string Url, string? Bypass)? CustomForChildren =>
        _override is { Mode: ModeCustom, ChildUrl: { } url } o ? (url, o.Bypass) : null;

    /// <summary>
    /// 每个请求按目标地址实时解析的代理。长生命周期的 HttpClient（网关上游）用它，
    /// 这样服务启动后再开关系统代理 / 改环境变量也能生效，而不是启动那一刻定死。
    /// </summary>
    public static IWebProxy Dynamic { get; } = new DynamicProxy();

    /// <summary>
    /// Builds the handler used by Astra's own outbound clients. Loopback and <c>NO_PROXY</c>
    /// entries always bypass the proxy, so local providers (Ollama, LM Studio) stay direct.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(TimeSpan connectTimeout) => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = connectTimeout,
        AutomaticDecompression = DecompressionMethods.All,
        UseProxy = true,
        Proxy = Dynamic,
    };

    /// <summary>The proxy to use for external hosts, or null for a direct connection.</summary>
    public static IWebProxy? Detect()
    {
        var source = Source();
        if (source is null) return null;
        try
        {
            var probe = new Uri("https://example.com/");
            if (source.IsBypassed(probe)) return null;
            var candidate = source.GetProxy(probe);
            if (candidate is not null && candidate != probe) return source;
        }
        catch (PlatformNotSupportedException)
        {
            // 该平台不支持读取系统代理：直连。
        }
        return null;
    }

    /// <summary>
    /// 子进程（npm、客户端自带的更新命令）该用的代理地址，形如 <c>http://127.0.0.1:10808</c>；直连时为 null。
    /// 子进程看不到 macOS / Windows 的系统代理设置，只认 <c>HTTPS_PROXY</c> 这类环境变量——从桌面应用启动的服务
    /// 没有这些变量，所以由调用方把这里的结果写进子进程环境。
    /// </summary>
    public static string? ProxyUrlFor(Uri target)
    {
        var proxy = Detect();
        if (proxy is null || proxy.IsBypassed(target)) return null;
        var uri = proxy.GetProxy(target);
        return uri is null || uri == target ? null : uri.GetLeftPart(UriPartial.Authority);
    }

    /// <summary>设置里的代理模式优先；"system" 时环境变量优先，其次操作系统代理；都没有则 null。</summary>
    private static IWebProxy? Source()
    {
        if (_override is { } configured) return configured.Proxy;

        // 环境变量优先：显式设置的进程环境最可信，也便于测试与容器环境。
        var env = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                  ?? Environment.GetEnvironmentVariable("https_proxy")
                  ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
                  ?? Environment.GetEnvironmentVariable("http_proxy")
                  ?? Environment.GetEnvironmentVariable("ALL_PROXY")
                  ?? Environment.GetEnvironmentVariable("all_proxy");
        if (!string.IsNullOrWhiteSpace(env) && Uri.TryCreate(env, UriKind.Absolute, out var envUri))
            return BuildWebProxy(envUri, Environment.GetEnvironmentVariable("NO_PROXY") ?? Environment.GetEnvironmentVariable("no_proxy"), null);

        // 环境没设就读系统代理（macOS: scutil / CFNetwork，Windows: WinINET）。
        try
        {
            return HttpClient.DefaultProxy;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static bool IsLocal(Uri target) =>
        target.IsLoopback || target.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || target.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 环境变量 / 设置形式的代理：回环与 <paramref name="noProxy"/> 里的 host 直连。
    /// URL 里的 <c>user:pass@</c> 会被拆成凭据——WebProxy 自己不读 userinfo。
    /// </summary>
    private static WebProxy BuildWebProxy(Uri proxyUri, string? noProxy, NetworkCredential? credentials)
    {
        if (credentials is null && proxyUri.UserInfo.Length > 0)
        {
            var parts = proxyUri.UserInfo.Split(':', 2);
            credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]), parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        proxyUri = new Uri(proxyUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));
        // BypassProxyOnLocal 只认"没有点的主机名"（localhost），127.0.0.1 / ::1 要显式列进来，
        // 否则本机提供商（Ollama、LM Studio）的请求会被送去代理。
        var bypass = new List<string> { "^localhost$", @"^127\.0\.0\.1$", @"^\[::1\]$", "^::1$" };
        foreach (var raw in (noProxy ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*" || raw.Length == 0) continue;
            var host = raw.StartsWith('[') ? raw : raw.Split(':')[0]; // host[:port]
            // WebProxy 拿 "scheme://host[:port]" 整串去匹配，所以正则必须带上 scheme 和可选端口；
            // NO_PROXY 惯例里 example.com 同时豁免其子域名；*.example.com 写法同义。
            bypass.Add($@"^[a-z]+://([^/]*\.)?{Regex.Escape(host.Trim('[', ']').TrimStart('*').TrimStart('.'))}(:\d+)?$");
        }
        return new WebProxy(proxyUri) { BypassProxyOnLocal = true, BypassList = [.. bypass], Credentials = credentials };
    }

    /// <summary>scheme://user:pass@host:port for child processes; npm / Node only read credentials from the URL.</summary>
    private static string ChildUrl(Uri proxyUri, NetworkCredential? credentials)
    {
        var server = proxyUri.GetComponents(UriComponents.HostAndPort, UriFormat.UriEscaped);
        return credentials is { UserName.Length: > 0 }
            ? $"{proxyUri.Scheme}://{Uri.EscapeDataString(credentials.UserName)}:{Uri.EscapeDataString(credentials.Password)}@{server}"
            : $"{proxyUri.Scheme}://{server}";
    }

    private sealed class DynamicProxy : IWebProxy
    {
        // SocketsHttpHandler reads Credentials once, when its connection pools are created, so this has to
        // delegate: a long-lived upstream client must see credentials configured (or changed) after that.
        public ICredentials? Credentials { get; set; } = new DynamicCredentials();

        public Uri? GetProxy(Uri destination)
        {
            if (IsLocal(destination)) return null;
            try
            {
                var source = Source();
                if (source is null || source.IsBypassed(destination)) return null;
                var proxy = source.GetProxy(destination);
                return proxy == destination ? null : proxy;
            }
            catch (PlatformNotSupportedException)
            {
                return null;
            }
        }

        public bool IsBypassed(Uri host) => GetProxy(host) is null;
    }

    private sealed class DynamicCredentials : ICredentials
    {
        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            try
            {
                return Source()?.Credentials?.GetCredential(uri, authType);
            }
            catch (PlatformNotSupportedException)
            {
                return null;
            }
        }
    }
}
