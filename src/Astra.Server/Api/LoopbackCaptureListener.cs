using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Astra.Server.Api;

/// <summary>回调处理结果：<paramref name="Ok"/> 决定页面样式，<paramref name="Message"/> 是要显示的一句话。</summary>
public sealed record LoopbackCaptureResult(bool Ok, string Message);

/// <summary>
/// 在给定端口上临时接管一个 HTTP 回调端点（回一段提示页面，并把 <c>?code&amp;state</c> 交给上层）。
/// 存在的理由：codex-cli 的登录回调被上游钉死在 http://localhost:{1455|1457}/auth/callback
/// 这两个端口上（auth.openai.com 的 redirect URI 白名单），Astra 自己监听的是别的端口，
/// 所以在登录期间替 codex 收一下浏览器 302 回来的授权码。
/// 用裸 TcpListener 同时监听 IPv4 / IPv6 回环：HttpListener 的 localhost 前缀在 macOS 上只绑到
/// ::1，浏览器走 127.0.0.1 时会连不上。
/// </summary>
public sealed class LoopbackCaptureListener : IAsyncDisposable
{
    private readonly List<TcpListener> _sockets = [];
    private readonly CancellationTokenSource _cts = new();
    private readonly string _path;

    private LoopbackCaptureListener(
        int port, string path,
        Func<IReadOnlyDictionary<string, string>, Task<LoopbackCaptureResult>> onRequest,
        Action? onCompleted)
    {
        Port = port;
        _path = path;
        try
        {
            // 两个回环地址都必须绑上：localhost 解析到哪个由系统决定。
            foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
            {
                var listener = new TcpListener(address, port);
                listener.Start();
                _sockets.Add(listener);
                _ = Task.Run(() => AcceptAsync(listener, onRequest, onCompleted));
            }
        }
        catch (SocketException)
        {
            Stop();
            throw;
        }
    }

    /// <summary>The port actually claimed (one of the preferred ports).</summary>
    public int Port { get; }

    /// <summary>Starts listening on the first free port, returning null when none could be claimed.</summary>
    public static LoopbackCaptureListener? TryStart(
        IReadOnlyList<int> ports, string path,
        Func<IReadOnlyDictionary<string, string>, Task<LoopbackCaptureResult>> onRequest,
        ILogger logger, Action? onCompleted = null)
    {
        foreach (var port in ports.Distinct())
        {
            try
            {
                return new LoopbackCaptureListener(port, path, onRequest, onCompleted);
            }
            catch (SocketException e)
            {
                logger.LogInformation("Callback port {Port} is unavailable ({Message}); trying the next one", port, e.Message);
            }
        }
        return null;
    }

    private async Task AcceptAsync(
        TcpListener listener,
        Func<IReadOnlyDictionary<string, string>, Task<LoopbackCaptureResult>> onRequest,
        Action? onCompleted)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var result = new LoopbackCaptureResult(true, "授权完成，请返回 Astra。");
                    var handled = false;
                    try
                    {
                        var requestLine = await ReadRequestLineAsync(client, CancellationToken.None);
                        var target = requestLine?.Split(' ') is { Length: >= 2 } parts ? parts[1] : "";
                        var (path, query) = SplitTarget(target);
                        if (path == _path && query.Count > 0)
                        {
                            handled = true;
                            result = await onRequest(query);
                        }
                    }
                    catch (Exception e)
                    {
                        Console.Error.WriteLine($"[capture] callback failed: {e}");
                        result = new LoopbackCaptureResult(false, $"换取令牌失败：{e.Message}");
                    }
                    // 先把页面写回去（浏览器还停在回调页上），再让登录流程释放端口。
                    await WriteResponseAsync(client, result);
                    if (handled) onCompleted?.Invoke();
                }
            });
        }
    }

    /// <summary>Reads just the request line ("GET /auth/callback?… HTTP/1.1"); null when the peer vanished.</summary>
    private static async Task<string?> ReadRequestLineAsync(TcpClient client, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var total = 0;
        var stream = client.GetStream();
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) return null;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains('\n')) return text.Split('\n')[0].TrimEnd('\r');
        }
        return null;
    }

    private static (string Path, Dictionary<string, string> Query) SplitTarget(string target)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var mark = target.IndexOf('?');
        var path = mark < 0 ? target : target[..mark];
        if (mark < 0) return (path, query);
        foreach (var pair in target[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            query[Uri.UnescapeDataString(split[0])] = split.Length > 1 ? Uri.UnescapeDataString(split[1].Replace('+', ' ')) : "";
        }
        return (path, query);
    }

    private static async Task WriteResponseAsync(TcpClient client, LoopbackCaptureResult result)
    {
        var accent = result.Ok ? "#22c55e" : "#ef4444";
        var icon = result.Ok ? "✓" : "✕";
        var body = Encoding.UTF8.GetBytes($$"""
            <!doctype html><html lang="zh"><head><meta charset="utf-8"><title>Astra</title></head>
            <body style="font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;background:#0b0b0e;color:#e5e5e7;display:grid;place-items:center;height:100vh;margin:0">
            <div style="text-align:center;max-width:420px;padding:24px">
            <div style="width:56px;height:56px;border-radius:50%;background:{{accent}}22;color:{{accent}};font-size:28px;line-height:56px;margin:0 auto 16px">{{icon}}</div>
            <div style="font-size:17px;font-weight:600;margin-bottom:6px">Astra</div>
            <p style="font-size:14px;color:#a1a1aa;line-height:1.6;margin:0">{{result.Message}}</p>
            <p style="font-size:12px;color:#71717a;margin-top:18px">可以关闭此页面并返回 Astra。</p>
            </div></body></html>
            """);
        var header = Encoding.UTF8.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        try
        {
            var stream = client.GetStream();
            await stream.WriteAsync(header);
            await stream.WriteAsync(body);
            await stream.FlushAsync();
        }
        catch (Exception)
        {
            // 浏览器提前断开不影响已经完成的结果。
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    private void Stop()
    {
        // 可能被调用多次（构造失败回滚、登录结束、宿主关闭）：只做一次。
        if (_sockets.Count == 0) return;
        try { _cts.Cancel(); }
        catch (Exception) { /* already disposed */ }
        foreach (var socket in _sockets)
        {
            try { socket.Stop(); }
            catch (Exception) { /* already stopped */ }
        }
        _sockets.Clear();
    }
}
