using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

/// <summary>
/// Upstream response decompression that respects explicit <c>Accept-Encoding: identity</c> while still decoding
/// compressed responses when an upstream ignores it (issue #56).
/// </summary>
public sealed class UpstreamDecompressionTests
{
    [Fact]
    public async Task Explicit_identity_reaches_the_wire_without_compressed_encodings()
    {
        var seen = new List<string?>();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var tcp = await listener.AcceptTcpClientAsync();
                await using var net = tcp.GetStream();
                var reqBuf = new byte[4096];
                var read = await net.ReadAsync(reqBuf);
                var text = Encoding.UTF8.GetString(reqBuf, 0, read);
                var header = text.Split("\r\n")
                    .FirstOrDefault(l => l.StartsWith("Accept-Encoding:", StringComparison.OrdinalIgnoreCase));
                seen.Add(header?.Split(':', 2)[1].Trim());
                await net.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray());
                await net.FlushAsync();
            }
        });

        using var handler = new UpstreamDecompressionHandler(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            UseProxy = false,
        });
        using var client = new HttpClient(handler);

        using (var streaming = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/stream"))
        {
            streaming.Headers.AcceptEncoding.ParseAdd("identity");
            using var res = await client.SendAsync(streaming);
            Assert.Equal("ok", await res.Content.ReadAsStringAsync());
        }

        using (var normal = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/json"))
        {
            using var res = await client.SendAsync(normal);
            Assert.Equal("ok", await res.Content.ReadAsStringAsync());
        }

        await server.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("identity", seen[0]);
        Assert.Contains("gzip", seen[1]);
        Assert.Contains("br", seen[1]);
        Assert.Contains("zstd", seen[1]);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    [InlineData("zstd")]
    public async Task Compressed_responses_are_decoded_even_when_identity_was_requested(string encoding)
    {
        const string payload = "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n";
        var compressed = Compress(Encoding.UTF8.GetBytes(payload), encoding);

        using var handler = new UpstreamDecompressionHandler(new StubHandler(_ =>
        {
            var content = new ByteArrayContent(compressed);
            content.Headers.ContentType = new("text/event-stream");
            content.Headers.ContentEncoding.Add(encoding);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://upstream.test/v1/chat/completions");
        request.Headers.AcceptEncoding.ParseAdd("identity");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Null(response.Content.Headers.ContentLength);
        Assert.Equal(payload, await response.Content.ReadAsStringAsync());
    }

    private static byte[] Compress(byte[] raw, string encoding)
    {
        using var ms = new MemoryStream();
        using (Stream compressor = encoding switch
               {
                   "gzip" => new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true),
                   "deflate" => new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true),
                   "br" => new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true),
                   "zstd" => new ZstandardStream(ms, CompressionLevel.Fastest, leaveOpen: true),
                   _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
               })
        {
            compressor.Write(raw);
        }
        return ms.ToArray();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
