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

    [Fact]
    public async Task A_body_still_arriving_at_the_deadline_is_a_408()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new WaitingStream();
        var ex = await Assert.ThrowsAsync<GatewayException>(() =>
            RequestBody.ReadAsync(ctx.Request, 1 << 20, CancellationToken.None, TimeSpan.FromMilliseconds(50)));
        Assert.Equal(408, ex.Status);
    }

    [Fact]
    public async Task The_caller_leaving_cancels_a_read_with_a_deadline()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new WaitingStream();
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RequestBody.ReadAsync(ctx.Request, 1 << 20, caller.Token, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task A_read_cancelled_inside_the_stream_is_a_408_under_a_deadline()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new CancelledStream();
        var ex = await Assert.ThrowsAsync<GatewayException>(() =>
            RequestBody.ReadAsync(ctx.Request, 1 << 20, CancellationToken.None, TimeSpan.FromSeconds(30)));
        Assert.Equal(408, ex.Status);
    }

    [Theory]
    [InlineData(413, "request_too_large")]
    [InlineData(408, "timeout")]
    [InlineData(400, "invalid_request_error")]
    public async Task The_servers_own_body_rejection_keeps_its_status(int status, string type)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new RejectingStream(status);
        var ex = await Assert.ThrowsAsync<GatewayException>(() => RequestBody.ReadAsync(ctx.Request, 1 << 20, CancellationToken.None));
        Assert.Equal((status, type), (ex.Status, ex.Type));
    }

    private sealed class WaitingStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class CancelledStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new TaskCanceledException("Synthetic stream timeout", new TimeoutException()));
    }

    private sealed class RejectingStream(int status) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new BadHttpRequestException("rejected by the server", status));
    }
}
