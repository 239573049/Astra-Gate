using System.Net;
using System.Net.Sockets;
using System.Text;
using Astra.Server.Api;
using Microsoft.Extensions.Logging.Abstractions;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// codex 登录要占用的回环接收器：必须两个回环地址都可连（浏览器可能走 ::1 或 127.0.0.1），
/// 收到 code 后回调上层，用完释放端口。
/// </summary>
public class LoopbackCaptureListenerTests
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task Captures_The_Code_On_Both_Loopback_Addresses_And_Releases_The_Port()
    {
        var port = FreePort();
        var seen = new List<string>();
        await using var listener = LoopbackCaptureListener.TryStart([port], "/auth/callback", query =>
        {
            seen.Add(query["code"]);
            return Task.FromResult(new LoopbackCaptureResult(true, "换码完成"));
        }, NullLogger.Instance);
        Assert.NotNull(listener);
        Assert.Equal(port, listener.Port);

        foreach (var host in new[] { "127.0.0.1", "[::1]" })
        {
            var body = await Request(host, port, "/auth/callback?code=code-" + host.Trim('[', ']') + "&state=s1");
            Assert.Contains("换码完成", body);
        }
        Assert.Equal(["code-127.0.0.1", "code-::1"], seen);

        // 不是回调路径就不触发换码，但照样回页面。
        var other = await Request("127.0.0.1", port, "/favicon.ico");
        Assert.Contains("请返回 Astra", other);
        Assert.Equal(2, seen.Count);

        await listener.DisposeAsync();
        using var probe = new TcpListener(IPAddress.Loopback, port);
        probe.Start(); // 端口已释放
        probe.Stop();
    }

    private static async Task<string> Request(string host, int port, string pathAndQuery)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port);
        var request = $"GET {pathAndQuery} HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request));
        var buffer = new byte[8192];
        var total = 0;
        int read;
        while ((read = await client.GetStream().ReadAsync(buffer.AsMemory(total))) > 0) total += read;
        return Encoding.UTF8.GetString(buffer, 0, total);
    }
}
