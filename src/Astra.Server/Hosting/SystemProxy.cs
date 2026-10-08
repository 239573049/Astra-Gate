using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Astra.Server.Hosting;

/// <summary>
/// 上游访问（订阅登录 / 额度 / 模型列表 / 更新检查 / 提供商测试）的走代理策略：
/// 先看 <c>HTTPS_PROXY</c> / <c>HTTP_PROXY</c>（含小写，<c>NO_PROXY</c> 豁免），
/// 没有环境变量再读操作系统代理（macOS / Windows 的系统设置），最后才是直连。
/// 网关自身的上游请求走每个提供商的 <c>httpProxy</c>，不走这里。
/// </summary>
public static class SystemProxy
{
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
        Proxy = Detect(),
    };

    /// <summary>The proxy to use, or null for a direct connection.</summary>
    public static IWebProxy? Detect()
    {
        // 环境变量优先：显式设置的进程环境最可信，也便于测试与容器环境。
        var env = Environment.GetEnvironmentVariable("HTTPS_PROXY")
                  ?? Environment.GetEnvironmentVariable("https_proxy")
                  ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
                  ?? Environment.GetEnvironmentVariable("http_proxy")
                  ?? Environment.GetEnvironmentVariable("ALL_PROXY")
                  ?? Environment.GetEnvironmentVariable("all_proxy");
        if (!string.IsNullOrWhiteSpace(env) && Uri.TryCreate(env, UriKind.Absolute, out var envUri))
            return BuildWebProxy(envUri);

        // 环境没设就读系统代理（macOS: scutil，Windows: WinINET）。
        try
        {
            var system = HttpClient.DefaultProxy;
            if (system is null) return null;
            var probe = new Uri("https://example.com/");
            if (system.IsBypassed(probe)) return null;
            var candidate = system.GetProxy(probe);
            if (candidate is not null && candidate != probe) return system;
        }
        catch (PlatformNotSupportedException)
        {
            // 该平台不支持读取系统代理：直连。
        }
        return null;
    }

    /// <summary>环境变量形式的代理：回环与 <c>NO_PROXY</c> 里的 host 直连。</summary>
    private static WebProxy BuildWebProxy(Uri proxyUri)
    {
        // BypassProxyOnLocal 只认"没有点的主机名"（localhost），127.0.0.1 / ::1 要显式列进来，
        // 否则本机提供商（Ollama、LM Studio）的请求会被送去代理。
        var bypass = new List<string> { "^localhost$", @"^127\.0\.0\.1$", @"^\[::1\]$", "^::1$" };
        var noProxy = Environment.GetEnvironmentVariable("NO_PROXY") ?? Environment.GetEnvironmentVariable("no_proxy") ?? "";
        foreach (var raw in noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*" || raw.Length == 0) continue;
            var host = raw.StartsWith('[') ? raw : raw.Split(':')[0]; // host[:port]
            bypass.Add($"^{Regex.Escape(host.Trim('[', ']'))}$");
        }
        return new WebProxy(proxyUri) { BypassProxyOnLocal = true, BypassList = [.. bypass] };
    }
}
