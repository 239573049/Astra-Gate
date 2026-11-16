using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Astra.Gateway.ClaudeBridge;

/// <summary>
/// Where the MCP helper a <c>claude</c> run starts hands each tool call back to the gateway (magpie's <c>POST /_magpie/claude-mcp/&lt;token&gt;</c>, MIT).
/// It is a listener of its own on the loopback interface, not a route of the web host: the helper needs no gateway key and no session, the
/// host may be bound to any address or sit behind a tunnel, and the port that drifts at startup does not matter. The path is a per-run
/// 24-byte random token, written to a file of the run's own (0600) so it is never on a command line; a wrong, spent or unknown token is
/// a 404. Only <c>POST</c> with a JSON body is understood, one request a connection.
/// </summary>
public sealed class ClaudeCallbackServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 32 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;
    private Task? _loop;

    /// <summary>Answers a call: (token, tool_call_id, tool name, arguments) → the content to give <c>claude</c>; null = the token is not a live run's.</summary>
    public Func<string, string, string, JsonNode?, CancellationToken, Task<(JsonArray Content, bool IsError)?>>? Handler { get; set; }

    /// <summary>The address (with no token) the helper posts to; starts listening on the first ask.</summary>
    public string BaseUrl
    {
        get
        {
            lock (_gate)
            {
                if (_listener is null)
                {
                    _listener = new TcpListener(IPAddress.Loopback, 0);
                    _listener.Start(64);
                    _loop = Task.Run(() => AcceptLoopAsync(_listener));
                }
                return $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var (path, body) = await ReadRequestAsync(stream, linked.Token);
            if (path is null)
            {
                await WriteAsync(stream, 400, new JsonObject { ["error"] = "bad request" });
                return;
            }
            var token = path.Trim('/');
            JsonObject? call;
            try
            {
                call = JsonNode.Parse(body) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                call = null;
            }
            var id = call?["tool_call_id"]?.GetValue<string>();
            var name = call?["name"]?.GetValue<string>();
            if (call is null || id is null || name is null || Handler is null)
            {
                await WriteAsync(stream, 400, new JsonObject { ["error"] = "bad request" });
                return;
            }
            // the helper's connection ends when claude gives the call up (its process is killed): stop waiting for a result nobody reads
            var watcher = WatchAsync(client, linked);
            var answer = await Handler(token, id, name, call["arguments"], linked.Token);
            if (answer is null)
            {
                await WriteAsync(stream, 404, new JsonObject { ["error"] = "no such run" });
                return;
            }
            await WriteAsync(stream, 200, new JsonObject { ["content"] = answer.Value.Content, ["is_error"] = answer.Value.IsError });
            linked.Cancel();
            await watcher;
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static async Task WatchAsync(TcpClient client, CancellationTokenSource cts)
    {
        try
        {
            var buffer = new byte[1];
            // nothing more is sent on this connection: a read that returns means the other end closed it
            if (await client.GetStream().ReadAsync(buffer, cts.Token) == 0) await cts.CancelAsync();
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private static async Task<(string? Path, string Body)> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new List<byte>();
        var one = new byte[4096];
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(buffer)) < 0)
        {
            if (buffer.Count > MaxHeaderBytes) return (null, "");
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) return (null, "");
            buffer.AddRange(one.AsSpan(0, n).ToArray());
        }
        var head = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray());
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2 || first[0] != "POST") return (null, "");
        var length = 0;
        foreach (var line in lines.Skip(1))
            if (line.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase) && !int.TryParse(line["content-length:".Length..].Trim(), out length)) return (null, "");
        if (length is < 0 or > MaxBodyBytes) return (null, "");
        var body = buffer.GetRange(headerEnd + 4, buffer.Count - headerEnd - 4);
        while (body.Count < length)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) return (null, "");
            body.AddRange(one.AsSpan(0, n).ToArray());
        }
        return (first[1], Encoding.UTF8.GetString(body.GetRange(0, length).ToArray()));
    }

    private static int IndexOfHeaderEnd(List<byte> b)
    {
        for (var i = 0; i + 3 < b.Count; i++)
            if (b[i] == '\r' && b[i + 1] == '\n' && b[i + 2] == '\r' && b[i + 3] == '\n') return i;
        return -1;
    }

    private static async Task WriteAsync(NetworkStream stream, int status, JsonObject body)
    {
        var payload = Encoding.UTF8.GetBytes(body.ToJsonString());
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate) _listener?.Stop();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _stop.Dispose();
    }
}
