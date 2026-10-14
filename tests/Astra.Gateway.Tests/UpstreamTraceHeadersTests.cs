using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

/// <summary>
/// The upstream HttpClient must not forward W3C trace context. ASP.NET Core starts an Activity for every inbound request
/// (adopting the agent's traceparent and baggage), and a default SocketsHttpHandler injects both into the provider
/// request: a header the real CLIs never send (Claude Code relay / mimicry must look like the CLI) and the agent's
/// baggage handed to a third party. Uses the real handler — the integration fixture swaps it out.
/// </summary>
public class UpstreamTraceHeadersTests
{
    [Fact]
    public async Task Upstream_requests_carry_no_trace_context()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var received = AcceptOneAsync(listener);

        using var inbound = new Activity("inbound").SetParentId("00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        inbound.AddBaggage("user.email", "someone@example.com");
        inbound.Start();

        using var clients = new GatewayHttpClients();
        using var response = await clients.For((string?)null).PostAsync($"http://127.0.0.1:{port}/v1/messages", new StringContent("{}"));
        var headers = await received;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("traceparent:", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tracestate:", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("baggage:", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("correlation-context:", headers, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("request-id:", headers, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Accepts one HTTP/1.1 request, returns its head (request line + headers) and answers 200 with no body.</summary>
    private static async Task<string> AcceptOneAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var buffer = new byte[16 * 1024];
        var head = new StringBuilder();
        while (!head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0) break;
            head.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }
        await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
        return head.ToString();
    }
}
