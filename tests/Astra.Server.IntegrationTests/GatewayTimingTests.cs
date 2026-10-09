using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Requests;
using Astra.Data.Repositories;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

public class GatewayTimingTests
{
    [Theory]
    [InlineData("stream-custom-tool.sse", false)]
    [InlineData("stream-custom-tool.sse", true)]
    [InlineData("stream-encrypted-reasoning.sse", false)]
    [InlineData("stream-encrypted-reasoning.sse", true)]
    public async Task Native_Responses_Output_Records_Ttft_And_Tps(string fixture, bool connectionTest)
    {
        var frames = SseParser.ParseAll(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "responses", fixture)));
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ =>
        {
            var content = new StreamContent(new DelayedSseStream(frames));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        RequestRecord record;
        if (connectionTest)
        {
            var result = await gw.Host.TestAsync(gw.Provider.Id, new { modelId = "gpt-5", stream = true });
            Assert.True(result.Done!["ok"]!.GetValue<bool>());
            Assert.NotNull(result.Done["ttftMs"]);
            await gw.Host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
            record = Assert.Single((await gw.Host.Db.Requests.QueryAsync(new RequestQuery { ProviderId = gw.Provider.Id })).Items);
        }
        else
        {
            var response = await gw.PostAsync("/v1/responses", """{"model":"gpt-5","input":"hi","stream":true}""");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(frames, SseParser.ParseAll(await response.Content.ReadAsStringAsync()));
            record = await gw.RecordOfAsync(response);
        }

        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.NotNull(record.TtftMs);
        Assert.True(record.TtftMs > 0);
        Assert.NotNull(record.GenerationMs);
        Assert.True(record.GenerationMs > 0);
        Assert.Equal(record.TotalMs - record.TtftMs, record.GenerationMs);
        Assert.Equal(912, record.TotalOutputTokens);
        Assert.Equal(Math.Round(912 / (record.GenerationMs.Value / 1000.0), 2), record.OutputTps);
        var detail = await gw.Host.GetJsonAsync($"/api/requests/{record.Id}");
        Assert.Equal(record.TtftMs, detail["ttftMs"]!.GetValue<long>());
        Assert.Equal(record.OutputTps, detail["outputTps"]!.GetValue<double>());
    }

    [Fact]
    public async Task Non_Stream_Output_Records_No_Ttft_Or_Tps()
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses", "response-completed.json"));
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ => FakeUpstream.Json(body));

        var response = await gw.PostAsync("/v1/responses", """{"model":"gpt-5","input":"hi","stream":false}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var record = await gw.RecordOfAsync(response);

        // The usage still lands ...
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.Equal(40, record.TotalOutputTokens);
        // ... but one complete body has no first-token moment, so TTFT, generation time and output speed
        // are never guessed from the decode time.
        Assert.Null(record.TtftMs);
        Assert.Null(record.GenerationMs);
        Assert.Null(record.OutputTps);
        var detail = await gw.Host.GetJsonAsync($"/api/requests/{record.Id}");
        Assert.Null(detail["ttftMs"]);
        Assert.Null(detail["outputTps"]);
    }

    [Fact]
    public async Task Non_Stream_Connection_Test_Records_No_Ttft_Or_Tps()
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses", "response-completed.json"));
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ => FakeUpstream.Json(body));

        var result = await gw.Host.TestAsync(gw.Provider.Id, new { modelId = "gpt-5", stream = false });
        Assert.True(result.Done!["ok"]!.GetValue<bool>());
        Assert.Null(result.Done["ttftMs"]);
        await gw.Host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var record = Assert.Single((await gw.Host.Db.Requests.QueryAsync(new RequestQuery { ProviderId = gw.Provider.Id })).Items);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.Equal(40, record.TotalOutputTokens);
        Assert.Null(record.TtftMs);
        Assert.Null(record.GenerationMs);
        Assert.Null(record.OutputTps);
    }

    // Each event arrives on a separate read, so generation time cannot collapse to zero in a fast fake response.
    private sealed class DelayedSseStream(IEnumerable<SseEvent> frames) : Stream
    {
        private readonly Queue<byte[]> _chunks = new(frames.Select(f => Encoding.UTF8.GetBytes(SseWriter.Format(f))));
        private ReadOnlyMemory<byte> _pending;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pending.IsEmpty)
            {
                if (!_chunks.TryDequeue(out var chunk)) return 0;
                await Task.Delay(20, cancellationToken);
                _pending = chunk;
            }
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
