using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Astra.Desktop.Tests.Api;

/// <summary>A loopback HttpListener whose handler a test supplies; stands in for astra-server's admin API.</summary>
internal sealed class FakeServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeServer(Func<HttpListenerContext, Task> handler)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    Requests.Add(new Seen(ctx.Request.HttpMethod, ctx.Request.RawUrl ?? "", ctx.Request.Headers["X-Astra-Admin"], ctx.Request.Headers["Accept"], ReadBody(ctx)));
                    try { await handler(ctx); }
                    catch { /* client went away */ }
                    finally { try { ctx.Response.Close(); } catch { } }
                });
            }
        });
    }

    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";
    public System.Collections.Concurrent.ConcurrentBag<Seen> Requests { get; } = [];

    public sealed record Seen(string Method, string Url, string? Admin, string? Accept, string Body);

    private static string ReadBody(HttpListenerContext ctx)
    {
        if (!ctx.Request.HasEntityBody) return "";
        using var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static async Task Json(HttpListenerContext ctx, int status, string body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(body);
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    public static async Task Sse(HttpListenerContext ctx, params string[] chunks)
    {
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.SendChunked = true;
        foreach (var c in chunks)
        {
            var bytes = Encoding.UTF8.GetBytes(c);
            await ctx.Response.OutputStream.WriteAsync(bytes);
            await ctx.Response.OutputStream.FlushAsync();
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
