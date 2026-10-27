using System.IO.Compression;
using System.Net;
using System.Text;

namespace Astra.Server.IntegrationTests;

/// <summary>The relay reads request bodies with the server's reader, under its own limits.</summary>
public sealed class RelayBodyTests
{
    private const int Limit = 16 * 1024;

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    [InlineData("deflate")]
    [InlineData("raw-deflate")]
    [InlineData("zstd")]
    [InlineData("gzip, br")]
    public async Task Request_compression_is_undone_before_anything_goes_upstream(string encoding)
    {
        await using var relay = await RelayHost.StartAsync();
        using var content = Compressed(Encoding.UTF8.GetBytes(RelayHost.ChatBody), encoding);
        using var response = await relay.SendAsync(content: content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RelayHost.ChatBody, relay.Upstream.Requests.Last().Text);
        Assert.False(relay.Upstream.Requests.Last().Headers.ContainsKey("Content-Encoding"));
    }

    [Theory]
    [InlineData("/v1/chat/completions", false, false)]
    [InlineData("/v1/chat/completions", true, false)]
    [InlineData("/v1/chat/completions", true, true)]
    [InlineData("/v1/embeddings", false, false)]
    [InlineData("/v1/embeddings", true, true)]
    public async Task Exact_decoded_limit_passes_and_one_more_byte_is_413(string path, bool compressed, bool chunked)
    {
        await using var relay = await RelayHost.StartAsync(configure: o => o.MaxRequestBodyBytes = Limit);
        foreach (var size in new[] { Limit, Limit + 1 })
        {
            var bytes = Encoding.UTF8.GetBytes(RelayHost.ChatBody + new string(' ', size - Encoding.UTF8.GetByteCount(RelayHost.ChatBody)));
            using var encoded = compressed ? Compressed(bytes, "gzip") : new ByteArrayContent(bytes);
            using HttpContent? content = chunked ? new UnknownLengthContent(await encoded.ReadAsByteArrayAsync()) : null;
            if (compressed && content is not null) content.Headers.ContentEncoding.Add("gzip");
            using var response = await relay.SendAsync(path, content: content ?? encoded);
            Assert.Equal(size == Limit ? HttpStatusCode.OK : HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        Assert.Single(relay.Upstream.Requests, request => request.Body.Length > 0);
    }

    [Theory]
    [InlineData("/v1/chat/completions", "not json", null, 400)]
    [InlineData("/v1/messages", "[]", null, 400)]
    [InlineData("/v1/embeddings", "{}", null, 400)]
    [InlineData("/v1/messages/count_tokens", """{"model":12}""", null, 400)]
    [InlineData("/v1/responses", "broken gzip", "gzip", 400)]
    [InlineData("/v1/embeddings", "unsupported", "unknown", 415)]
    public async Task Invalid_bodies_are_explicit_errors(string path, string body, string? encoding, int status)
    {
        await using var relay = await RelayHost.StartAsync();
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (encoding is not null) content.Headers.ContentEncoding.Add(encoding);
        using var response = await relay.SendAsync(path, content: content);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Empty(relay.Upstream.Requests);
    }

    private static ByteArrayContent Compressed(byte[] bytes, string encoding)
    {
        foreach (var codec in encoding.Split(", ", StringSplitOptions.TrimEntries))
        {
            if (codec == "zstd")
            {
                using var compressor = new ZstdSharp.Compressor();
                bytes = compressor.Wrap(bytes).ToArray();
            }
            else
            {
                using var target = new MemoryStream();
                using (Stream encoder = codec switch
                {
                    "gzip" => new GZipStream(target, CompressionLevel.Fastest, leaveOpen: true),
                    "br" => new BrotliStream(target, CompressionLevel.Fastest, leaveOpen: true),
                    "deflate" => new ZLibStream(target, CompressionLevel.Fastest, leaveOpen: true),
                    "raw-deflate" => new DeflateStream(target, CompressionLevel.Fastest, leaveOpen: true),
                    _ => throw new ArgumentOutOfRangeException(nameof(encoding)),
                }) encoder.Write(bytes);
                bytes = target.ToArray();
            }
        }
        var content = new ByteArrayContent(bytes);
        foreach (var codec in encoding.Replace("raw-deflate", "deflate", StringComparison.Ordinal).Split(", "))
            content.Headers.ContentEncoding.Add(codec);
        return content;
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
