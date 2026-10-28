using System.Text.Json.Nodes;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Tests.Api;

public class SseParserTests
{
    [Fact]
    public void Joins_data_lines_ignores_comments_and_reads_event()
    {
        var p = new SseParser();
        var m = Assert.Single(p.Feed(": hi\nevent: delta\ndata: a\ndata:b\n\n"));
        Assert.Equal(new SseMessage("delta", "a\nb"), m);
    }

    [Fact]
    public void Frames_split_across_chunks_and_crlf()
    {
        var p = new SseParser();
        Assert.Empty(p.Feed("data: {\"a\""));
        Assert.Empty(p.Feed(":1}\r\n"));
        var frames = p.Feed("\r\ndata: two\r\r");
        Assert.Equal(["{\"a\":1}", "two"], frames.Select(f => f.Data));
        Assert.Equal("", frames[0].Event);
    }

    [Fact]
    public void Frames_without_data_are_dropped_and_many_frames_in_one_chunk_come_out_in_order()
    {
        var p = new SseParser();
        var all = p.Feed("event: ping\n\ndata: 1\n\ndata: 2\n\n");
        Assert.Equal(["1", "2"], all.Select(x => x.Data));
    }
}

public class SseStreamTests
{
    private sealed record Row(string Id, int N);

    [Fact]
    public async Task Get_stream_yields_frames_and_reports_open()
    {
        using var server = new FakeServer(c => FakeServer.Sse(c, "data: {\"id\":\"a\",\"n\":1}\n\n", "event: x\ndata: {\"id\":\"b\",\"n\":2}", "\n\n"));
        using var api = new ApiClient(server.Url);
        var opened = 0;
        var frames = new List<SseMessage>();
        await foreach (var f in api.StreamAsync("/api/requests/live", onOpen: () => opened++)) frames.Add(f);
        Assert.Equal(1, opened);
        Assert.Equal(["", "x"], frames.Select(f => f.Event));
        Assert.Contains("text/event-stream", server.Requests.Single().Accept);
        Assert.Null(server.Requests.Single().Admin);
    }

    [Fact]
    public async Task Typed_stream_decodes_and_skips_unreadable_frames()
    {
        using var server = new FakeServer(c => FakeServer.Sse(c, "data: not json\n\n", "data: {\"id\":\"a\",\"n\":1}\n\n"));
        using var api = new ApiClient(server.Url);
        var rows = new List<JsonObject>();
        await foreach (var r in api.StreamAsync<JsonObject>("/api/s")) rows.Add(r);
        Assert.Equal("a", (string?)Assert.Single(rows)["id"]);
    }

    [Fact]
    public async Task Post_stream_sends_body_and_admin_header()
    {
        using var server = new FakeServer(c => FakeServer.Sse(c, "data: {\"done\":true}\n\n"));
        using var api = new ApiClient(server.Url);
        var got = new List<SseMessage>();
        await foreach (var f in api.StreamPostAsync("/api/providers/p/test", new JsonObject { ["model"] = "m" })) got.Add(f);
        Assert.Single(got);
        var seen = server.Requests.Single();
        Assert.Equal("1", seen.Admin);
        Assert.Equal("""{"model":"m"}""", seen.Body);
    }

    [Fact]
    public async Task Refusal_before_the_stream_is_an_ApiException()
    {
        using var server = new FakeServer(c => FakeServer.Json(c, 400, """{"error":"no model"}"""));
        using var api = new ApiClient(server.Url);
        var e = await Assert.ThrowsAsync<ApiException>(async () => { await foreach (var _ in api.StreamPostAsync("/api/p", new JsonObject())) { } });
        Assert.Equal(400, e.Status);
        Assert.Equal("no model", e.Message);
    }

    [Fact]
    public async Task Cancellation_ends_a_silent_stream()
    {
        using var server = new FakeServer(async c => { await FakeServer.Sse(c, ": open\n\n"); await Task.Delay(5000); });
        using var api = new ApiClient(server.Url);
        using var cts = new CancellationTokenSource();
        var opened = new TaskCompletionSource();
        var task = Task.Run(async () =>
        {
            await foreach (var _ in api.StreamAsync("/api/s", cts.Token, () => opened.TrySetResult())) { }
        });
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Reconnecting_stream_survives_a_drop_and_a_refusal()
    {
        var hits = 0;
        using var server = new FakeServer(async c =>
        {
            switch (Interlocked.Increment(ref hits))
            {
                case 1: await FakeServer.Sse(c, "data: {\"id\":\"1\"}\n\n"); break;          // clean close
                case 2: await FakeServer.Json(c, 503, """{"error":"starting"}"""); break;  // refused
                default: await FakeServer.Sse(c, "data: {\"id\":\"2\"}\n\n"); await Task.Delay(3000); break;
            }
        });
        using var api = new ApiClient(server.Url);
        var opens = 0;
        var drops = new List<Exception?>();
        var options = new SseReconnectOptions
        {
            InitialDelay = TimeSpan.FromMilliseconds(20),
            MaxDelay = TimeSpan.FromMilliseconds(80),
            OnOpen = () => opens++,
            OnDrop = e => drops.Add(e),
        };
        using var cts = new CancellationTokenSource();
        var ids = new List<string>();
        try
        {
            await foreach (var row in api.StreamReconnectingAsync<JsonObject>("/api/requests/live", options, cts.Token))
            {
                ids.Add((string)row["id"]!);
                if (ids.Count == 2) cts.Cancel();
            }
        }
        catch (OperationCanceledException) { }
        Assert.Equal(["1", "2"], ids);
        Assert.Equal(2, opens);
        Assert.Equal(2, drops.Count);
        Assert.Null(drops[0]);
        Assert.Equal(503, Assert.IsType<ApiException>(drops[1]).Status);
    }

    [Fact]
    public async Task Gate_holds_the_connection_until_released()
    {
        using var server = new FakeServer(c => FakeServer.Sse(c, "data: {\"id\":\"1\"}\n\n"));
        using var api = new ApiClient(server.Url);
        var gate = new TaskCompletionSource();
        var options = new SseReconnectOptions { Gate = ct => gate.Task.WaitAsync(ct), InitialDelay = TimeSpan.FromSeconds(5) };
        using var cts = new CancellationTokenSource();
        var first = Task.Run(async () =>
        {
            await foreach (var r in api.StreamReconnectingAsync<JsonObject>("/api/s", options, cts.Token)) return r;
            return null;
        });
        await Task.Delay(150);
        Assert.Empty(server.Requests);
        gate.SetResult();
        Assert.Equal("1", (string?)(await first.WaitAsync(TimeSpan.FromSeconds(5)))!["id"]);
        cts.Cancel();
    }
}
