using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>Compressed request bodies: zstd and friends from the client, and to providers that ask for them.</summary>
public class GatewayCompressionTests
{
    private const string Completion =
        """{"id":"resp_1","object":"response","created_at":1,"status":"completed","model":"gpt-5","output":[{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"OK"}]}],"usage":{"input_tokens":12,"output_tokens":3,"total_tokens":15}}""";

    private const string Body = """{"model":"gpt-5","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}]}""";

    /// <summary>Records the raw bytes and content headers the gateway sends upstream.</summary>
    private sealed class RawUpstream(Func<int, HttpStatusCode> status) : HttpMessageHandler
    {
        public List<(string? Encoding, byte[] Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            int n;
            lock (Requests)
            {
                Requests.Add((request.Content.Headers.ContentEncoding.FirstOrDefault(), bytes));
                n = Requests.Count;
            }
            return FakeUpstream.Json(Completion, status(n));
        }
    }

    private static async Task<(GatewayFixture Gw, RawUpstream Upstream)> StartAsync(string? compression, Func<int, HttpStatusCode>? status = null)
    {
        var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ => FakeUpstream.Json(Completion));
        var upstream = new RawUpstream(status ?? (_ => HttpStatusCode.OK));
        gw.Host.App.Services.GetRequiredService<GatewayHttpClients>().HandlerFactory = _ => upstream;
        if (compression is not null)
        {
            gw.Provider.Settings[UpstreamCompression.SettingKey] = compression;
            await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        }
        return (gw, upstream);
    }

    private static async Task<HttpResponseMessage> PostZstdAsync(GatewayFixture gw, string json)
    {
        using var zstd = new ZstdSharp.Compressor();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses")
        {
            Content = new ByteArrayContent(zstd.Wrap(Encoding.UTF8.GetBytes(json)).ToArray()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentEncoding.Add("zstd");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        return await gw.Host.Client.SendAsync(request);
    }

    private static string Unzstd(byte[] data)
    {
        using var zstd = new ZstdSharp.Decompressor();
        return Encoding.UTF8.GetString(zstd.Unwrap(data));
    }

    [Fact]
    public async Task Zstd_Client_Body_Reaches_A_Zstd_Provider_As_Zstd()
    {
        var (gw, upstream) = await StartAsync("zstd");
        await using var _ = gw;
        var response = await PostZstdAsync(gw, Body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (encoding, bytes) = Assert.Single(upstream.Requests);
        Assert.Equal("zstd", encoding);
        Assert.Equal("hi", JsonNode.Parse(Unzstd(bytes))!["input"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Providers_Without_The_Setting_Get_Plain_Json()
    {
        var (gw, upstream) = await StartAsync(null);
        await using var _ = gw;
        Assert.Equal(HttpStatusCode.OK, (await PostZstdAsync(gw, Body)).StatusCode);
        var (encoding, bytes) = Assert.Single(upstream.Requests);
        Assert.Null(encoding);
        Assert.Equal(Body, Encoding.UTF8.GetString(bytes));
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("deflate")]
    public async Task Other_Codings_Round_Trip(string coding)
    {
        var (gw, upstream) = await StartAsync(coding);
        await using var _ = gw;
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/responses", Body)).StatusCode);
        var (encoding, bytes) = Assert.Single(upstream.Requests);
        Assert.Equal(coding, encoding);
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.Headers.ContentEncoding = coding;
        using var decoded = await RequestBody.ReadAsync(ctx.Request, 1 << 20, CancellationToken.None);
        Assert.Equal(Body, decoded.Text());
    }

    [Fact]
    public async Task A_415_Is_Retried_Uncompressed()
    {
        var (gw, upstream) = await StartAsync("zstd", n => n == 1 ? HttpStatusCode.UnsupportedMediaType : HttpStatusCode.OK);
        await using var _ = gw;
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/responses", Body)).StatusCode);
        Assert.Equal(2, upstream.Requests.Count);
        Assert.Equal("zstd", upstream.Requests[0].Encoding);
        Assert.Null(upstream.Requests[1].Encoding);
        Assert.Equal(Body, Encoding.UTF8.GetString(upstream.Requests[1].Body));
    }

    [Fact]
    public async Task Unknown_Client_Coding_Is_415()
    {
        var (gw, upstream) = await StartAsync(null);
        await using var _ = gw;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/responses") { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        request.Content.Headers.ContentEncoding.Add("compress");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        var response = await gw.Host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(upstream.Requests);
    }

    [Theory]
    [InlineData("zstd", "gzip", "gzip")]
    [InlineData("zstd", "default", "zstd")]
    [InlineData("zstd", "none", null)]
    [InlineData(null, "zstd", null)]
    public async Task The_App_Setting_Repoints_Or_Turns_Off_Provider_Compression(string? provider, string app, string? expected)
    {
        var (gw, upstream) = await StartAsync(provider);
        await using var _ = gw;
        var (status, settings) = await gw.Host.SendAsync(HttpMethod.Patch, "/api/settings", new { requestCompression = app });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(app, settings!["requestCompression"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await gw.PostAsync("/v1/responses", Body)).StatusCode);
        Assert.Equal(expected, Assert.Single(upstream.Requests).Encoding);
    }

    [Fact]
    public async Task The_App_Setting_Rejects_Unknown_Codings()
    {
        var (gw, _) = await StartAsync(null);
        await using var __ = gw;
        var (status, _) = await gw.Host.SendAsync(HttpMethod.Patch, "/api/settings", new { requestCompression = "lz4" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("default", (await gw.Host.GetJsonAsync("/api/settings"))["requestCompression"]!.GetValue<string>());
    }
}
