using System.IO.Compression;
using System.Text;
using Astra.Gateway.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Tests;

public class RequestBodyTests
{
    private const string Json = """{"model":"gpt-5","input":"你好"}""";

    private static HttpRequest Request(byte[] body, string? encoding)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(body);
        ctx.Request.ContentLength = body.Length;
        if (encoding is not null) ctx.Request.Headers.ContentEncoding = encoding;
        return ctx.Request;
    }

    private static byte[] Compress(byte[] data, string coding)
    {
        if (coding == "zstd")
        {
            using var zstd = new ZstdSharp.Compressor();
            return zstd.Wrap(data).ToArray();
        }
        using var output = new MemoryStream();
        using (Stream z = coding switch
        {
            "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "deflate" => new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "raw-deflate" => new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(coding)),
        })
            z.Write(data);
        return output.ToArray();
    }

    private static async Task<string> ReadText(byte[] body, string? encoding, long maxBytes = 1 << 20)
    {
        using var read = await RequestBody.ReadAsync(Request(body, encoding), maxBytes, CancellationToken.None);
        return read.Text();
    }

    [Theory]
    [InlineData("zstd")]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("deflate")]
    public async Task Decodes_each_content_encoding(string coding)
    {
        Assert.Equal(Json, await ReadText(Compress(Encoding.UTF8.GetBytes(Json), coding), coding));
    }

    [Fact]
    public async Task Accepts_raw_deflate_and_header_casing()
    {
        Assert.Equal(Json, await ReadText(Compress(Encoding.UTF8.GetBytes(Json), "raw-deflate"), "Deflate"));
        Assert.Equal(Json, await ReadText(Compress(Encoding.UTF8.GetBytes(Json), "gzip"), "X-GZIP"));
    }

    [Fact]
    public async Task Undoes_stacked_codings_last_first()
    {
        var body = Compress(Compress(Encoding.UTF8.GetBytes(Json), "gzip"), "zstd");
        Assert.Equal(Json, await ReadText(body, "gzip, identity, zstd"));
    }

    [Fact]
    public async Task Plain_and_identity_bodies_are_untouched()
    {
        Assert.Equal(Json, await ReadText(Encoding.UTF8.GetBytes(Json), null));
        Assert.Equal(Json, await ReadText(Encoding.UTF8.GetBytes(Json), "identity"));
    }

    [Fact]
    public async Task Unknown_coding_is_415()
    {
        var ex = await Assert.ThrowsAsync<GatewayException>(() => ReadText(Encoding.UTF8.GetBytes(Json), "compress"));
        Assert.Equal(415, ex.Status);
    }

    [Fact]
    public async Task Corrupt_zstd_is_400()
    {
        var ex = await Assert.ThrowsAsync<GatewayException>(() => ReadText(Encoding.UTF8.GetBytes(Json), "zstd"));
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public async Task Decoded_size_is_capped()
    {
        // A small body that inflates past the limit (a compression bomb) is a 413, not an unbounded buffer.
        var bomb = Compress(new byte[4 << 20], "zstd");
        Assert.True(bomb.Length < 1 << 20);
        var ex = await Assert.ThrowsAsync<GatewayException>(() => ReadText(bomb, "zstd", maxBytes: 1 << 20));
        Assert.Equal(413, ex.Status);
    }
}
