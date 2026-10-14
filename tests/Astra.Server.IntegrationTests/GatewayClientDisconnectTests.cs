using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Requests;
using Astra.Gateway.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// A client that disconnects while the upstream still holds its stream open: after the whole answer (the ChatGPT
/// backend keeps the connection open past <c>response.completed</c>, and agents leave on their <c>[DONE]</c>) the request
/// succeeded; before it, the client cancelled.
/// </summary>
public class GatewayClientDisconnectTests
{
    private const string Head =
        "event: response.created\n" +
        "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"in_progress\",\"model\":\"gpt-5\",\"output\":[]}}\n\n" +
        "event: response.output_item.added\n" +
        "data: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_1\",\"status\":\"in_progress\",\"role\":\"assistant\",\"content\":[]}}\n\n" +
        "event: response.content_part.added\n" +
        "data: {\"type\":\"response.content_part.added\",\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"\"}}\n\n" +
        "event: response.output_text.delta\n" +
        "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"delta\":\"OK\"}\n\n";

    private const string Tail =
        "event: response.content_part.done\n" +
        "data: {\"type\":\"response.content_part.done\",\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"part\":{\"type\":\"output_text\",\"text\":\"OK\"}}\n\n" +
        "event: response.output_item.done\n" +
        "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"type\":\"message\",\"id\":\"msg_1\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}}\n\n" +
        "event: response.completed\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"completed\",\"model\":\"gpt-5\",\"output\":[{\"type\":\"message\",\"id\":\"msg_1\",\"status\":\"completed\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}],\"usage\":{\"input_tokens\":12,\"output_tokens\":3,\"total_tokens\":15}}}\n\n";

    [Theory]
    // Translated (the ChatGPT subscription behind a Chat client): the client leaves on [DONE].
    [InlineData("/v1/chat/completions", """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}],"stream":true}""", "data: [DONE]")]
    // Pass-through (Codex): the client leaves on response.completed.
    [InlineData("/v1/responses", """{"model":"gpt-5","input":"hi","stream":true}""", "\"type\":\"response.completed\"")]
    public async Task Leaving_After_The_Whole_Answer_Is_A_Success(string path, string body, string lastLine)
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ => Lingering(Head + Tail));

        var id = await ReadUntilAndLeaveAsync(gw, path, body, lastLine);

        var record = await RecordAsync(gw, id);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.Equal("stop", record.FinishReason);
        Assert.Equal(3, record.TotalOutputTokens);
    }

    [Fact]
    public async Task Leaving_After_An_Upstream_Error_Event_Records_The_Error()
    {
        // What the ChatGPT backend sends when it is overloaded: the error nested in "error", then response.failed.
        const string overloaded =
            "event: response.created\n" +
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"in_progress\",\"model\":\"gpt-5\",\"output\":[]}}\n\n" +
            "event: error\n" +
            "data: {\"type\":\"error\",\"sequence_number\":2,\"error\":{\"message\":\"Our servers are currently overloaded. Please try again later.\",\"type\":\"service_unavailable_error\",\"param\":null,\"code\":\"server_is_overloaded\"}}\n\n" +
            "event: response.failed\n" +
            "data: {\"type\":\"response.failed\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"failed\",\"model\":\"gpt-5\",\"output\":[]}}\n\n";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ => Lingering(overloaded));

        string? errorLine = null;
        var id = await ReadUntilAndLeaveAsync(gw, "/v1/chat/completions",
            """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}],"stream":true}""", "data: [DONE]",
            line => errorLine ??= line.Contains("\"error\"", StringComparison.Ordinal) ? line : null);

        Assert.Contains("Our servers are currently overloaded.", errorLine);
        var record = await RecordAsync(gw, id);
        Assert.Equal(RequestStatus.UpstreamError, record.Status);
        Assert.Equal("server_is_overloaded", record.ErrorType);
        Assert.Equal("Our servers are currently overloaded. Please try again later.", record.ErrorMessage);
    }

    [Fact]
    public async Task Leaving_Mid_Answer_Is_A_Cancellation()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ => Lingering(Head));

        var id = await ReadUntilAndLeaveAsync(gw, "/v1/chat/completions",
            """{"model":"gpt-5","messages":[{"role":"user","content":"hi"}],"stream":true}""", "\"content\":\"OK\"");

        var record = await RecordAsync(gw, id);
        Assert.Equal(RequestStatus.ClientCancelled, record.Status);
        Assert.Null(record.FinishReason);
    }

    /// <summary>Streams the response until a line containing <paramref name="lastLine"/>, then drops the connection.</summary>
    private static async Task<string> ReadUntilAndLeaveAsync(GatewayFixture gw, string path, string body, string lastLine, Action<string>? seen = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        var response = await gw.Host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = response.Headers.GetValues("x-astra-request-id").Single();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await reader.ReadLineAsync(timeout.Token) is { } line && !line.Contains(lastLine, StringComparison.Ordinal))
            seen?.Invoke(line);
        response.Dispose();
        return id;
    }

    /// <summary>The request's row, once the pipeline has noticed the client left and written it.</summary>
    private static async Task<RequestRecord> RecordAsync(GatewayFixture gw, string id)
    {
        var writer = gw.Host.App.Services.GetRequiredService<UsageWriter>();
        for (var i = 0; i < 250; i++)
        {
            await writer.FlushAsync();
            if (await gw.Host.Db.Requests.GetAsync(id) is { } record) return record;
            await Task.Delay(20);
        }
        throw new TimeoutException($"request {id} was never recorded");
    }

    private static HttpResponseMessage Lingering(string sse)
    {
        var content = new StreamContent(new LingeringStream(sse));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Sends <c>sse</c>, then keeps the connection open without another byte until the read is cancelled.</summary>
    private sealed class LingeringStream(string sse) : Stream
    {
        private ReadOnlyMemory<byte> _pending = Encoding.UTF8.GetBytes(sse);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.IsEmpty) await Task.Delay(Timeout.Infinite, cancellationToken);
            var count = Math.Min(buffer.Length, _pending.Length);
            _pending[..count].CopyTo(buffer);
            _pending = _pending[count..];
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
