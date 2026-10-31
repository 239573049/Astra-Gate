using System.Text;
using Astra.Core;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Tests;

/// <summary>The pieces of the Images API forwarding (issue #17): the form reader, the upstream address, the usage.</summary>
public class ImagesTests
{
    private const string Boundary = "XyZ--b0und4ry";

    // A preamble, a file before the model (its bytes hold CRLFs, dashes and a near-boundary), a part without headers'
    // optional extras, transport padding after a delimiter, and an epilogue.
    private static readonly byte[] Form = Encoding.Latin1.GetBytes(
        "preamble ignored\r\n"
        + $"--{Boundary}\r\n"
        + "Content-Disposition: form-data; name=\"image[]\"; filename=\"a.png\"\r\nContent-Type: image/png\r\n\r\n"
        + "\u0089PNG\r\n\u001a\n--XyZ--b0und4r\r\n\r\n--ÿ\u0000"
        + $"\r\n--{Boundary}  \r\n"
        + "content-disposition: form-data; name=model\r\n\r\n"
        + "gpt-image-1"
        + $"\r\n--{Boundary}\r\n"
        + "Content-Disposition: form-data; name=\"prompt\"\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n"
        + "a fox"
        + $"\r\n--{Boundary}--\r\nepilogue");

    /// <summary>Hands out one byte per read: every part boundary falls between two reads somewhere.</summary>
    private sealed class Trickle(byte[] data) : Stream
    {
        private int _at;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (_at >= data.Length || buffer.IsEmpty) return 0;
            buffer[0] = data[_at++];
            return 1;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromResult(Read(buffer.Span));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("multipart/form-data; boundary=abc", "abc")]
    [InlineData("multipart/form-data; boundary=\"----a b\"", "----a b")]
    [InlineData("Multipart/Form-Data; charset=utf-8; boundary=x", "x")]
    [InlineData("application/json", null)]
    [InlineData("multipart/mixed; boundary=abc", null)]
    [InlineData("multipart/form-data", null)]
    [InlineData(null, null)]
    public void The_boundary_comes_from_a_form_content_type_only(string? contentType, string? boundary) =>
        Assert.Equal(boundary, MultipartForm.BoundaryOf(contentType));

    [Fact]
    public void A_whole_form_parses_into_its_parts()
    {
        var parts = MultipartForm.Parse(Form, Boundary);
        Assert.Equal(["image[]", "model", "prompt"], parts.Select(p => p.Name));
        Assert.Equal("a.png", parts[0].FileName);
        Assert.False(parts[0].IsText);
        Assert.Equal("\u0089PNG\r\n\u001a\n--XyZ--b0und4r\r\n\r\n--ÿ\u0000", Encoding.Latin1.GetString(Form[parts[0].ValueStart..parts[0].ValueEnd]));
        Assert.Equal("gpt-image-1", parts[1].Text(Form));
        Assert.Equal("a fox", parts[2].Text(Form));
    }

    [Fact]
    public async Task The_model_is_found_however_the_bytes_arrive_and_nothing_after_it_is_read()
    {
        var stream = new Trickle(Form);
        var (read, field) = await MultipartForm.ReadUntilFieldAsync(stream, Boundary, "model", 1 << 20, CancellationToken.None);
        try
        {
            Assert.NotNull(field);
            Assert.Equal("gpt-image-1", field.Value.Text(read.Span));
            Assert.Equal(Form[..read.Length], read.Span.ToArray());
            // The part's end is known once its delimiter has arrived: the prompt is still unread.
            Assert.True(read.Length < Encoding.Latin1.GetString(Form).IndexOf("a fox", StringComparison.Ordinal));
            Assert.Equal(read.Length, stream.Position);
        }
        finally
        {
            read.Release();
        }
    }

    [Fact]
    public async Task A_form_without_the_field_reads_to_its_end_and_says_so()
    {
        var (read, field) = await MultipartForm.ReadUntilFieldAsync(new Trickle(Form), Boundary, "quality", 1 << 20, CancellationToken.None);
        Assert.Null(field);
        read.Release();
    }

    [Fact]
    public async Task Broken_or_oversized_forms_are_refused()
    {
        var cut = Form[..(Form.Length / 2)];
        await Assert.ThrowsAsync<ProtocolException>(() =>
            MultipartForm.ReadUntilFieldAsync(new MemoryStream(cut), Boundary, "quality", 1 << 20, CancellationToken.None));
        Assert.Throws<ProtocolException>(() => MultipartForm.Parse(cut, Boundary));
        var garbage = Encoding.ASCII.GetBytes($"--{Boundary}junk on the boundary line\r\n\r\nx\r\n--{Boundary}--");
        Assert.Throws<ProtocolException>(() => MultipartForm.Parse(garbage, Boundary));
        var tooLarge = await Assert.ThrowsAsync<GatewayException>(() =>
            MultipartForm.ReadUntilFieldAsync(new MemoryStream(Form), Boundary, "quality", 64, CancellationToken.None));
        Assert.Equal(413, tooLarge.Status);
    }

    [Fact]
    public void Splice_replaces_values_and_keeps_every_other_byte()
    {
        var parts = MultipartForm.Parse(Form, Boundary);
        var spliced = MultipartForm.Splice(Form, [(parts[2].ValueStart, parts[2].ValueEnd, "un renard"), (parts[1].ValueStart, parts[1].ValueEnd, "gpt-image-2")]);
        try
        {
            var expected = Encoding.Latin1.GetString(Form).Replace("gpt-image-1", "gpt-image-2").Replace("a fox", "un renard");
            Assert.Equal(expected, Encoding.Latin1.GetString(spliced.Span));
            Assert.Equal(["image[]", "model", "prompt"], MultipartForm.Parse(spliced.Span, Boundary).Select(p => p.Name));
        }
        finally
        {
            spliced.Release();
        }
    }

    [Theory]
    [InlineData(ApiProtocol.OpenAIChat, "https://api.openai.com/v1", false, "https://api.openai.com/v1/images/generations")]
    [InlineData(ApiProtocol.OpenAIResponses, "https://api.example.com/v1/", false, "https://api.example.com/v1/images/generations")]
    [InlineData(ApiProtocol.OpenAIChat, "https://x.openai.azure.com/openai/deployments/img/chat/completions?api-version=2025-04-01-preview", true,
        "https://x.openai.azure.com/openai/deployments/img/images/generations?api-version=2025-04-01-preview")]
    [InlineData(ApiProtocol.OpenAIResponses, "https://gw.example.com/openai/v1/responses", true, "https://gw.example.com/openai/v1/images/generations")]
    [InlineData(ApiProtocol.OpenAIChat, "https://gw.example.com/custom/generate", true, null)]
    [InlineData(ApiProtocol.Anthropic, "https://api.anthropic.com", false, null)]
    public void The_images_address_sits_beside_the_openai_endpoint(ApiProtocol protocol, string baseUrl, bool fullUrl, string? expected) =>
        Assert.Equal(expected, UpstreamUrls.Images(new ProviderEndpoint { Protocol = protocol, BaseUrl = baseUrl, FullUrl = fullUrl }, "images/generations"));

    [Fact]
    public void Usage_is_read_past_megabytes_of_image_without_parsing_them()
    {
        var b64 = new string('A', 3_000_000);
        var body = Encoding.UTF8.GetBytes("""{"created":1,"data":[{"b64_json":""" + "\"" + b64 + "\""
            + ""","revised_prompt":"x"}],"meta":{"a":[1,{"b":2}]},"usage":{"input_tokens":50,"output_tokens":4160,"input_tokens_details":{"text_tokens":10,"image_tokens":40}}}""");
        var usage = GatewayPipeline.ImageUsageOf(body);
        Assert.Equal(50, usage!["input_tokens"]!.GetValue<int>());
        Assert.Equal(40, usage["input_tokens_details"]!["image_tokens"]!.GetValue<int>());
        Assert.Null(GatewayPipeline.ImageUsageOf("""{"data":[]}"""u8));
        Assert.Null(GatewayPipeline.ImageUsageOf("<html>busy</html>"u8));
        Assert.Null(GatewayPipeline.ImageUsageOf("""{"usage":"""u8));
    }
}
