using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Plugins.Host;

namespace Astra.Plugins.Tests;

/// <summary>The host protocol (magpie's host.go) against a scripted in-memory host.</summary>
public class PluginHostClientTests
{
    /// <summary>A host on two in-memory pipes: reads what the client sends, writes what the test says.</summary>
    private sealed class FakeHost : IAsyncDisposable
    {
        private readonly Pipe _toHost = new();
        private readonly Pipe _fromHost = new();
        private readonly StreamReader _reader;
        private readonly Stream _writer;

        public FakeHost()
        {
            _reader = new StreamReader(_toHost.Reader.AsStream(), Encoding.UTF8);
            _writer = _fromHost.Writer.AsStream();
            Client = new PluginHostClient(_toHost.Writer.AsStream(), _fromHost.Reader.AsStream(), e => Events.Add(e));
        }

        public PluginHostClient Client { get; }
        public List<JsonObject> Events { get; } = [];

        public async Task<JsonObject> ReadAsync()
        {
            var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            return JsonNode.Parse(line!)!.AsObject();
        }

        public async Task WriteAsync(JsonObject message)
        {
            await _writer.WriteAsync(Encoding.UTF8.GetBytes(message.ToJsonString() + "\n"));
            await _writer.FlushAsync();
        }

        public Task Chunk(long id, string text) =>
            WriteAsync(new JsonObject { ["id"] = id, ["event"] = "chunk", ["data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) });

        public async Task DieAsync() => await _fromHost.Writer.CompleteAsync();

        public async ValueTask DisposeAsync() => await Client.DisposeAsync();
    }

    private static HostFetchRequest Fetch() =>
        new("zcode", "zcode#ab12cd", "glm-5", "@ai-sdk/anthropic", "https://api.z.ai/v1/messages", "POST",
            new Dictionary<string, string> { ["content-type"] = "application/json" }, Encoding.UTF8.GetBytes("""{"model":"glm-5"}"""), "s1", null);

    [Fact]
    public void A_Wire_Line_Is_One_Json_Object_With_The_Body_As_Base64_In_Its_Params()
    {
        var body = Encoding.UTF8.GetBytes("{\"model\":\"m\",\"input\":\"汉字 é\"}");
        var message = new JsonObject
        {
            ["id"] = 7,
            ["method"] = "fetch",
            ["params"] = new JsonObject { ["url"] = "https://x/v1", ["headers"] = new JsonObject { ["a"] = "b" }, ["account"] = null },
        };
        var line = PluginHostClient.Line(message, body).ToArray();

        Assert.Equal((byte)'\n', line[^1]);
        Assert.DoesNotContain((byte)'\n', line[..^1]);
        var parsed = JsonNode.Parse(line.AsSpan(0, line.Length - 1))!.AsObject();
        Assert.Equal(7, parsed["id"]!.GetValue<int>());
        Assert.Equal("fetch", parsed["method"]!.GetValue<string>());
        var p = parsed["params"]!.AsObject();
        Assert.Equal("b", p["headers"]!["a"]!.GetValue<string>());
        Assert.True(p.ContainsKey("account"));
        Assert.Equal(body, Convert.FromBase64String(p["body"]!.GetValue<string>()));
        // Without a body the message is written as it is.
        Assert.Equal(message.ToJsonString() + "\n", Encoding.UTF8.GetString(PluginHostClient.Line(message).Span));
    }

    [Fact]
    public async Task Rpc_results_and_errors()
    {
        await using var host = new FakeHost();
        var call = host.Client.CallAsync("providers", new JsonObject { ["proxies"] = new JsonObject() });
        var sent = await host.ReadAsync();
        Assert.Equal("providers", sent["method"]!.GetValue<string>());
        await host.WriteAsync(new JsonObject { ["id"] = sent["id"]!.DeepClone(), ["result"] = new JsonArray(new JsonObject { ["id"] = "zcode" }) });
        Assert.Equal("""[{"id":"zcode"}]""", (await call)!.ToJsonString());

        var failing = host.Client.CallAsync("load", null);
        var sent2 = await host.ReadAsync();
        await host.WriteAsync(new JsonObject { ["id"] = sent2["id"]!.DeepClone(), ["error"] = new JsonObject { ["message"] = "no provider zcode" } });
        Assert.Equal("no provider zcode", (await Assert.ThrowsAsync<PluginHostException>(() => failing)).Message);
        Assert.Equal(0, host.Client.InFlight);
    }

    [Fact]
    public async Task A_fetch_streams_its_body_and_gives_credit_back()
    {
        await using var host = new FakeHost();
        var fetch = host.Client.FetchAsync(Fetch());
        var sent = await host.ReadAsync();
        var p = sent["params"]!;
        Assert.Equal("fetch", sent["method"]!.GetValue<string>());
        Assert.Equal("zcode#ab12cd", p["account"]!.GetValue<string>());
        Assert.Equal("""{"model":"glm-5"}""", Encoding.UTF8.GetString(Convert.FromBase64String(p["body"]!.GetValue<string>())));
        Assert.Equal(512 << 10, p["window"]!.GetValue<int>());
        Assert.Equal("s1", p["session"]!.GetValue<string>());
        var id = sent["id"]!.GetValue<long>();

        await host.WriteAsync(new JsonObject
        {
            ["id"] = id, ["event"] = "head", ["status"] = 200,
            ["headers"] = new JsonObject { ["content-type"] = "text/event-stream", ["content-encoding"] = "gzip", ["x-request-id"] = "r1" },
        });
        var response = await fetch;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.False(response.Content.Headers.Contains("content-encoding"));
        Assert.Equal("r1", response.Headers.GetValues("x-request-id").Single());

        await host.Chunk(id, "data: one\n\n");
        var body = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[64];
        Assert.Equal(11, await body.ReadAsync(buffer));

        // the bytes read, and the frame's overhead once it is done, go back to the host as credit
        var credited = 0;
        for (var i = 0; i < 2; i++)
        {
            var c = await host.ReadAsync();
            Assert.Equal("credit", c["method"]!.GetValue<string>());
            Assert.Equal(id, c["params"]!["id"]!.GetValue<long>());
            credited += c["params"]!["n"]!.GetValue<int>();
        }
        Assert.Equal(11 + 2048, credited);

        await host.Chunk(id, "data: two\n\n");
        await host.WriteAsync(new JsonObject { ["id"] = id, ["result"] = null });
        Assert.Equal("data: two\n\n", await new StreamReader(body).ReadToEndAsync());
        Assert.Equal(0, host.Client.InFlight);
    }

    [Fact]
    public async Task An_error_before_the_head_fails_the_fetch()
    {
        await using var host = new FakeHost();
        var fetch = host.Client.FetchAsync(Fetch());
        var id = (await host.ReadAsync())["id"]!.GetValue<long>();
        await host.WriteAsync(new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["message"] = "sign in to ZCode again" } });

        Assert.Equal("sign in to ZCode again", (await Assert.ThrowsAsync<PluginHostException>(() => fetch)).Message);
        Assert.Equal(0, host.Client.InFlight);
    }

    [Fact]
    public async Task Frames_past_the_window_give_the_call_up()
    {
        await using var host = new FakeHost();
        var fetch = host.Client.FetchAsync(Fetch());
        var id = (await host.ReadAsync())["id"]!.GetValue<long>();
        await host.WriteAsync(new JsonObject { ["id"] = id, ["event"] = "head", ["status"] = 200, ["headers"] = new JsonObject() });
        var response = await fetch;
        var big = new string('x', 60 << 10);
        for (var i = 0; i < 10; i++) await host.Chunk(id, big); // 600 KiB with nothing read: past the 512 KiB window

        var abort = await host.ReadAsync();
        Assert.Equal("abort", abort["method"]!.GetValue<string>());
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => response.Content.ReadAsStringAsync());
        Assert.Contains("more of a reply than the host credited", e.ToString());
    }

    [Fact]
    public async Task Disposing_a_body_before_its_end_aborts_the_fetch()
    {
        await using var host = new FakeHost();
        var fetch = host.Client.FetchAsync(Fetch());
        var id = (await host.ReadAsync())["id"]!.GetValue<long>();
        await host.WriteAsync(new JsonObject { ["id"] = id, ["event"] = "head", ["status"] = 200, ["headers"] = new JsonObject() });
        var response = await fetch;
        await host.Chunk(id, "partial");

        response.Dispose();

        var abort = await host.ReadAsync();
        Assert.Equal("abort", abort["method"]!.GetValue<string>());
        Assert.Equal(id, abort["params"]!["id"]!.GetValue<long>());
        Assert.Equal(0, host.Client.InFlight);
    }

    [Fact]
    public async Task Cancelling_before_the_head_aborts()
    {
        await using var host = new FakeHost();
        using var cts = new CancellationTokenSource();
        var fetch = host.Client.FetchAsync(Fetch(), cts.Token);
        var id = (await host.ReadAsync())["id"]!.GetValue<long>();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
        var abort = await host.ReadAsync();
        Assert.Equal("abort", abort["method"]!.GetValue<string>());
        Assert.Equal(id, abort["params"]!["id"]!.GetValue<long>());
    }

    [Fact]
    public async Task Events_and_a_host_that_goes_away()
    {
        await using var host = new FakeHost();
        await host.WriteAsync(new JsonObject { ["event"] = "auth", ["provider"] = "zcode" });
        var call = host.Client.CallAsync("usage", null);
        await host.ReadAsync();

        await host.DieAsync();

        Assert.StartsWith("the plugin host is gone", (await Assert.ThrowsAsync<PluginHostException>(() => call)).Message);
        Assert.False(host.Client.Alive);
        Assert.Equal("auth", Assert.Single(host.Events)["event"]!.GetValue<string>());
        await Assert.ThrowsAsync<PluginHostException>(() => host.Client.CallAsync("providers", null));
    }
}
