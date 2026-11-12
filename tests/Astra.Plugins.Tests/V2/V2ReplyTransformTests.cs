using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Plugins.V2;
using Astra.Plugins.V2.Reply;

namespace Astra.Plugins.Tests.V2;

/// <summary>A provider's reply framed by the host and translated by its plugin (design/plugin-v2 08 §2.2).</summary>
public class V2ReplyTransformTests
{
    // ---- framing readers

    /// <summary>A stream that hands out <paramref name="step"/> bytes at a time, to find what chunk boundaries break.</summary>
    private sealed class Trickle(byte[] data, int step) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, step), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<List<JsonObject>> ReadAllAsync(string framing, byte[] bytes, int step = 3, int maxFrame = 1 << 20)
    {
        var reader = FrameReader.For(framing, new Trickle(bytes, step), maxFrame, 1 << 24);
        var all = new List<JsonObject>();
        while (await reader.NextAsync(default) is { } f) all.Add(f);
        return all;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Sse_follows_the_spec_across_chunk_boundaries(int step)
    {
        var text = "﻿: heartbeat\r\nevent: delta\r\ndata: {\"a\":1}\r\nid: 7\r\n\r\n"
                   + "data:no space\ndata: second line\n\n"
                   + "data: [DONE]\r\r"
                   + "\n\n"            // a bare blank line dispatches nothing
                   + "event: lone\n\n" // no data: not dispatched
                   + "data: tail without a blank line";
        var frames = await ReadAllAsync("sse", Encoding.UTF8.GetBytes(text), step);
        Assert.Equal(4, frames.Count);
        Assert.Equal("delta", frames[0]["event"]!.GetValue<string>());
        Assert.Equal("7", frames[0]["id"]!.GetValue<string>());
        Assert.Equal(1, frames[0]["json"]!["a"]!.GetValue<int>());
        Assert.Equal("no space\nsecond line", frames[1]["data"]!.GetValue<string>());
        Assert.Null(frames[1]["json"]);
        Assert.Equal("[DONE]", frames[2]["data"]!.GetValue<string>());
        Assert.Equal("tail without a blank line", frames[3]["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task Sse_refuses_an_event_over_the_frame_cap()
    {
        var e = await Assert.ThrowsAsync<ReplyFramingException>(() => ReadAllAsync("sse", Encoding.UTF8.GetBytes("data: " + new string('x', 200) + "\n\n"), maxFrame: 100));
        Assert.Contains("100", e.Message);
    }

    [Fact]
    public async Task Ndjson_lines_split_across_chunks_and_text_lines_survive()
    {
        var frames = await ReadAllAsync("ndjson", Encoding.UTF8.GetBytes("{\"a\":1}\n\n{\"b\":\"é\"}\r\nnot json\n[1,2]"), step: 2);
        Assert.Equal(4, frames.Count);
        Assert.Equal(1, frames[0]["json"]!["a"]!.GetValue<int>());
        Assert.Equal("é", frames[1]["json"]!["b"]!.GetValue<string>());
        Assert.Equal("not json", frames[2]["text"]!.GetValue<string>());
        Assert.Equal(2, frames[3]["json"]!.AsArray().Count);
    }

    [Fact]
    public async Task Json_framing_is_the_whole_body_once()
    {
        var frames = await ReadAllAsync("json", Encoding.UTF8.GetBytes("{\"response\":{\"x\":1}}"), step: 5);
        Assert.Equal(1, Assert.Single(frames)["json"]!["response"]!["x"]!.GetValue<int>());
        Assert.Equal("plain", Assert.Single(await ReadAllAsync("json", "plain"u8.ToArray()))["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Event_stream_messages_carry_headers_and_json_text_or_bytes()
    {
        var body = new MemoryStream();
        body.Write(EventMessageWith("assistantResponseEvent", "{\"content\":\"hi\"}"u8.ToArray()));
        body.Write(EventMessageWith("note", "plain words"u8.ToArray()));
        body.Write(EventMessageWith("blob", [0xFF, 0xFE, 0x00]));
        var frames = await ReadAllAsync("aws-eventstream", body.ToArray(), step: 5);
        Assert.Equal(3, frames.Count);
        Assert.Equal("assistantResponseEvent", frames[0]["headers"]![":event-type"]!.GetValue<string>());
        Assert.Equal(42, frames[0]["headers"]!["n"]!.GetValue<int>());
        Assert.Equal("hi", frames[0]["json"]!["content"]!.GetValue<string>());
        Assert.Equal("plain words", frames[1]["text"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String([0xFF, 0xFE, 0x00]), frames[2]["bytes"]!.GetValue<string>());
    }

    private static byte[] EventMessageWith(string eventType, byte[] payload)
    {
        var headers = new MemoryStream();
        void Str(string k, string v)
        {
            headers.WriteByte((byte)k.Length);
            headers.Write(Encoding.UTF8.GetBytes(k));
            headers.WriteByte(7);
            var len = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(len, (ushort)v.Length);
            headers.Write(len);
            headers.Write(Encoding.UTF8.GetBytes(v));
        }
        Str(":event-type", eventType);
        headers.WriteByte(1);
        headers.Write("n"u8);
        headers.WriteByte(4);
        headers.Write([0, 0, 0, 42]);
        var total = 12 + (int)headers.Length + payload.Length + 4;
        var msg = new byte[total];
        BinaryPrimitives.WriteUInt32BigEndian(msg, (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), (uint)headers.Length);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(8), EventStreamFrameReader.Crc32(msg.AsSpan(0, 8)));
        headers.ToArray().CopyTo(msg, 12);
        payload.CopyTo(msg, 12 + (int)headers.Length);
        BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(total - 4), EventStreamFrameReader.Crc32(msg.AsSpan(0, total - 4)));
        return msg;
    }

    [Fact]
    public async Task Event_stream_crc_failures_and_cut_messages_are_errors()
    {
        var good = EventMessageWith("e", "{}"u8.ToArray());
        var badMessage = (byte[])good.Clone();
        badMessage[^6] ^= 0xFF; // a payload byte: the message CRC no longer matches
        Assert.Contains("message CRC", (await Assert.ThrowsAsync<ReplyFramingException>(() => ReadAllAsync("aws-eventstream", badMessage))).Message);
        var badPrelude = (byte[])good.Clone();
        badPrelude[5] ^= 0x01;
        Assert.Contains("prelude CRC", (await Assert.ThrowsAsync<ReplyFramingException>(() => ReadAllAsync("aws-eventstream", badPrelude))).Message);
        Assert.Contains("inside a message", (await Assert.ThrowsAsync<ReplyFramingException>(() => ReadAllAsync("aws-eventstream", good[..^3]))).Message);
        Assert.Equal(0xCBF43926u, EventStreamFrameReader.Crc32("123456789"u8)); // the CRC-32 check value
    }

    private static byte[] ConnectMessage(byte flags, byte[] payload)
    {
        var m = new byte[5 + payload.Length];
        m[0] = flags;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(1), (uint)payload.Length);
        payload.CopyTo(m, 5);
        return m;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(4096)]
    public async Task Connect_messages_are_base64_frames_and_the_end_message_is_its_json_trailer(int step)
    {
        var body = new MemoryStream();
        body.Write(ConnectMessage(0, [0x0A, 0x02, 0x68, 0x69]));
        body.Write(ConnectMessage(0, []));
        body.Write(ConnectMessage(2, "{\"error\":{\"code\":\"resource_exhausted\",\"message\":\"quota\"}}"u8.ToArray()));
        body.Write(ConnectMessage(0, [0xFF])); // after the end message: never read
        var frames = await ReadAllAsync("connect", body.ToArray(), step);
        Assert.Equal(3, frames.Count);
        Assert.Equal(0, frames[0]["flags"]!.GetValue<int>());
        Assert.Equal(Convert.ToBase64String([0x0A, 0x02, 0x68, 0x69]), frames[0]["bytes"]!.GetValue<string>());
        Assert.Equal("", frames[1]["bytes"]!.GetValue<string>());
        Assert.True(frames[2]["end"]!.GetValue<bool>());
        Assert.Equal("resource_exhausted", frames[2]["json"]!["error"]!["code"]!.GetValue<string>());
        Assert.Null(frames[2]["bytes"]);
    }

    [Fact]
    public async Task Connect_refuses_compression_cut_messages_and_oversized_ones()
    {
        Assert.Contains("compressed", (await Assert.ThrowsAsync<ReplyFramingException>(() => ReadAllAsync("connect", ConnectMessage(1, [1])))).Message);
        Assert.Contains("inside a message", (await Assert.ThrowsAsync<ReplyFramingException>(() => ReadAllAsync("connect", ConnectMessage(0, [1, 2, 3])[..^1]))).Message);
        Assert.Contains("header", (await Assert.ThrowsAsync<ReplyFramingException>(() => ReadAllAsync("connect", [0, 0, 0]))).Message);
        Assert.Contains("larger", (await Assert.ThrowsAsync<ReplyFramingException>(() => ReadAllAsync("connect", ConnectMessage(0, new byte[200]), maxFrame: 100))).Message);
    }

    // ---- the transform through V2ProviderTransport

    private const string Qoder = """
        import { definePlugin, providers } from 'astra';
        export default definePlugin({
          activate() {
            providers.register('qoder', {
              async prepare(req, account) {
                return { url: 'https://api.qoder.example/chat?p=' + req.provider, headers: { authorization: 'Bearer test' } };
              },
              transformResponse: {
                start(res, ctx) {
                  ctx.state.text = '';
                  ctx.state.id = 'chatcmpl-1';
                  if (res.status === 402) return { error: { status: 429, message: 'quota used up' } };
                },
                frame(f, ctx) {
                  // Qoder wraps each chunk: { statusCodeValue, body: "<json>" }
                  const env = f.json;
                  if (!env) return [];
                  if (env.statusCodeValue !== 200) return { $error: { status: 502, message: 'qoder said ' + env.statusCodeValue } };
                  const inner = JSON.parse(env.body);
                  const delta = inner.choices[0].delta.content || '';
                  if (!ctx.request.stream) { ctx.state.text += delta; return []; }
                  return { id: ctx.state.id, object: 'chat.completion.chunk', model: ctx.request.model, choices: [{ index: 0, delta: { content: delta }, finish_reason: null }] };
                },
                end(ctx) {
                  if (ctx.request.stream) return { id: ctx.state.id, object: 'chat.completion.chunk', model: ctx.request.model, choices: [{ index: 0, delta: {}, finish_reason: 'stop' }] };
                  return { id: ctx.state.id, object: 'chat.completion', model: ctx.request.model, choices: [{ index: 0, message: { role: 'assistant', content: ctx.state.text }, finish_reason: 'stop' }] };
                },
              },
            });
          },
        });
        """;

    private static async Task<(V2TestHost Host, V2ProviderTransport Transport)> StartAsync(string main, JsonObject? reply = null)
    {
        var t = new V2TestHost();
        await t.AddAsync("acme.qoder", new JsonObject
        {
            ["main"] = "main.js", ["activation"] = new JsonArray("onStartup"),
            ["permissions"] = new JsonArray("providers", "net:https://api.qoder.example"),
            ["contributes"] = new JsonObject
            {
                ["providers"] = new JsonArray(new JsonObject
                {
                    ["id"] = "qoder", ["name"] = "Qoder", ["protocols"] = new JsonArray("chat"),
                    ["reply"] = reply ?? new JsonObject { ["framing"] = "sse", ["output"] = "chat" },
                    ["models"] = new JsonArray(new JsonObject { ["id"] = "q-1", ["protocol"] = "chat" }),
                }),
            },
        }, [("main.js", main)]);
        return (t, new V2ProviderTransport(t.Host));
    }

    private static string Sse(params string[] bodies) =>
        string.Concat(bodies.Select(b => "data: " + new JsonObject { ["statusCodeValue"] = 200, ["body"] = b }.ToJsonString() + "\n\n"));

    private static HttpRequestMessage ChatRequest(bool stream)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "plugin://v2.acme.qoder.qoder/v1/chat/completions")
        {
            Content = new StringContent($"{{\"model\":\"q-1\",\"stream\":{(stream ? "true" : "false")},\"messages\":[]}}", Encoding.UTF8, "application/json"),
        };
        r.Options.Set(Astra.Plugins.Host.PluginFetchHandler.AccountOption, "k1");
        return r;
    }

    private static void Reply(V2TestHost t, HttpStatusCode status, string body, string type = "text/event-stream") =>
        t.Services.Handler = _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static readonly string Chunks = Sse("{\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}", "{\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}");

    [Fact]
    public async Task A_qoder_like_envelope_becomes_openai_chat_chunks()
    {
        var (t, transport) = await StartAsync(Qoder);
        await using var _ = t;
        Reply(t, HttpStatusCode.OK, Chunks);
        using var response = await transport.SendAsync(ChatRequest(stream: true), default);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Null(response.Content.Headers.ContentLength);
        var text = await response.Content.ReadAsStringAsync();
        var events = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, events.Length);
        Assert.Equal("Hel", JsonNode.Parse(events[0]["data: ".Length..])!["choices"]![0]!["delta"]!["content"]!.GetValue<string>());
        Assert.Equal("q-1", JsonNode.Parse(events[0]["data: ".Length..])!["model"]!.GetValue<string>());
        Assert.Equal("stop", JsonNode.Parse(events[2]["data: ".Length..])!["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.Equal("data: [DONE]", events[3]);
    }

    [Fact]
    public async Task A_non_streaming_request_gets_one_body_the_plugin_aggregated()
    {
        var (t, transport) = await StartAsync(Qoder);
        await using var _ = t;
        Reply(t, HttpStatusCode.OK, Chunks);
        using var response = await transport.SendAsync(ChatRequest(stream: false), default);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("Hello", body["choices"]![0]!["message"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Start_can_turn_a_refusal_into_a_status_and_a_failure_in_the_first_frame_does_too()
    {
        var (t, transport) = await StartAsync(Qoder);
        await using var _ = t;
        Reply(t, HttpStatusCode.PaymentRequired, "{\"msg\":\"no credit\"}", "application/json");
        using var quota = await transport.SendAsync(ChatRequest(stream: true), default);
        Assert.Equal(HttpStatusCode.TooManyRequests, quota.StatusCode);
        Assert.Equal("quota used up", JsonNode.Parse(await quota.Content.ReadAsStringAsync())!["error"]!["message"]!.GetValue<string>());

        // another refusal start leaves alone passes through as it came
        Reply(t, HttpStatusCode.ServiceUnavailable, "busy", "text/plain");
        using var busy = await transport.SendAsync(ChatRequest(stream: true), default);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
        Assert.Equal("busy", await busy.Content.ReadAsStringAsync());

        // a 200 whose first frame is the vendor's own error: nothing was sent yet, so it is an honest status
        t.Services.Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: " + new JsonObject { ["statusCodeValue"] = 500, ["body"] = "{}" }.ToJsonString() + "\n\n", Encoding.UTF8, "text/event-stream"),
        };
        using var failed = await transport.SendAsync(ChatRequest(stream: true), default);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Contains("qoder said 500", await failed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_plugin_that_throws_fails_the_request_naming_the_plugin()
    {
        var (t, transport) = await StartAsync(Qoder.Replace("const inner = JSON.parse(env.body);", "const inner = JSON.parse(env.body); if (inner.boom) throw new Error('kaboom');"));
        await using var _ = t;
        // the first frame is fine, the second throws: the failure surfaces in the stream, never as a clean end
        Reply(t, HttpStatusCode.OK, Sse("{\"choices\":[{\"delta\":{\"content\":\"a\"}}]}", "{\"boom\":1}"));
        using var response = await transport.SendAsync(ChatRequest(stream: true), default);
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => response.Content.ReadAsStringAsync());
        Assert.Contains("acme.qoder", e.Message);
        Assert.Contains("kaboom", e.Message);

        // thrown before anything was sent: SendAsync itself fails (so the gateway fails over)
        Reply(t, HttpStatusCode.OK, Sse("{\"boom\":1}"));
        var first = await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(ChatRequest(stream: true), default));
        Assert.Contains("acme.qoder", first.Message);
    }

    [Fact]
    public async Task A_frame_call_over_its_time_limit_fails_the_request()
    {
        var old = V2ReplyTransform.CallLimit;
        V2ReplyTransform.CallLimit = TimeSpan.FromMilliseconds(300);
        try
        {
            var (t, transport) = await StartAsync(Qoder.Replace("import { definePlugin, providers }", "import { definePlugin, providers, timers }")
                .Replace("frame(f, ctx) {", "async frame(f, ctx) { await timers.sleep(5000);"));
            await using var _ = t;
            Reply(t, HttpStatusCode.OK, Chunks);
            var e = await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(ChatRequest(stream: true), default));
            Assert.Contains("acme.qoder", e.Message);
        }
        finally
        {
            V2ReplyTransform.CallLimit = old;
        }
    }

    [Fact]
    public async Task Anthropic_output_is_written_as_event_data_pairs_and_json_framing_unwraps_an_envelope()
    {
        const string main = """
            import { definePlugin, providers } from 'astra';
            export default definePlugin({
              activate() {
                providers.register('qoder', {
                  prepare: async () => ({ url: 'https://api.qoder.example/x' }),
                  transformResponse: { frame: (f) => f.json.response },
                });
              },
            });
            """;
        var (t, transport) = await StartAsync(main, new JsonObject { ["framing"] = "json", ["output"] = "chat" });
        await using var _ = t;
        // wrapped Code Assist style, non-streaming
        var req = new HttpRequestMessage(HttpMethod.Post, "plugin://v2.acme.qoder.qoder/v1/chat/completions") { Content = new StringContent("{\"model\":\"q-1\"}", Encoding.UTF8, "application/json") };
        Reply(t, HttpStatusCode.OK, "{\"response\":{\"id\":\"x\",\"object\":\"chat.completion\"}}", "application/json");
        using var response = await transport.SendAsync(req, default);
        Assert.Equal("x", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<string>());
    }

    /// <summary>A reply that sends <paramref name="first"/> and then nothing, never ending by itself — a bidirectional stream waiting on the client.</summary>
    private sealed class Hanging(byte[] first) : Stream
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _pos;
        public bool Disposed => _closed.Task.IsCompleted;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos < first.Length)
            {
                var n = Math.Min(buffer.Length, first.Length - _pos);
                first.AsMemory(_pos, n).CopyTo(buffer);
                _pos += n;
                return n;
            }
            await _closed.Task.WaitAsync(ct);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private const string Bidi = """
        import { definePlugin, providers, http, encoding } from 'astra';
        export default definePlugin({
          activate() {
            providers.register('qoder', {
              async prepare(req) {
                // the first client message goes up on its own call, as Cursor's HTTP/1.1 BidiAppend does
                await http.fetch('https://api.qoder.example/append', { method: 'POST', headers: { 'content-type': 'application/proto' }, body: new Uint8Array([1, 2, 3]) });
                return { url: 'https://api.qoder.example/run', headers: { 'content-type': 'application/connect+proto' }, body: new Uint8Array([0, 0, 0, 0, 2, 0x0a, 0x00]), state: { id: 'run-1', model: req.model } };
              },
              transformResponse: {
                frame(f, ctx) {
                  if (f.end) return [];
                  const text = new TextDecoder().decode(encoding.base64.decode(f.bytes));
                  // the answer is complete although the upstream stays open: end the reply here
                  return [{ id: ctx.state.id, object: 'chat.completion.chunk', model: ctx.state.model, choices: [{ index: 0, delta: { content: text }, finish_reason: null }] }, { $end: true }];
                },
                end(ctx) {
                  return { id: ctx.state.id, object: 'chat.completion.chunk', choices: [{ index: 0, delta: {}, finish_reason: 'stop' }] };
                },
                async dispose(ctx) {
                  await http.fetch('https://api.qoder.example/disposed?id=' + ctx.state.id, { method: 'POST' });
                },
              },
            });
          },
        });
        """;

    [Fact]
    public async Task A_bidi_provider_sends_bytes_hands_its_state_over_ends_the_reply_early_and_is_told_when_it_is_over()
    {
        var (t, transport) = await StartAsync(Bidi, new JsonObject { ["framing"] = "connect", ["output"] = "chat" });
        await using var _ = t;
        var seen = new System.Collections.Concurrent.ConcurrentQueue<(string Path, string? Type, byte[] Body)>();
        var upstream = new Hanging(ConnectMessage(0, "Hi"u8.ToArray()));
        t.Services.Handler = r =>
        {
            seen.Enqueue((r.RequestUri!.PathAndQuery, r.Content?.Headers.ContentType?.MediaType, r.Content?.ReadAsByteArrayAsync().GetAwaiter().GetResult() ?? []));
            if (r.RequestUri.AbsolutePath != "/run") return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            var res = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(upstream) };
            res.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/connect+proto");
            return res;
        };
        var response = await transport.SendAsync(ChatRequest(stream: true), default);
        var text = await response.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var events = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, events.Length);
        Assert.Equal("Hi", JsonNode.Parse(events[0]["data: ".Length..])!["choices"]![0]!["delta"]!["content"]!.GetValue<string>());
        Assert.Equal("q-1", JsonNode.Parse(events[0]["data: ".Length..])!["model"]!.GetValue<string>());
        Assert.Equal("stop", JsonNode.Parse(events[1]["data: ".Length..])!["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.Equal("data: [DONE]", events[2]);
        Assert.True(upstream.Disposed, "the upstream stays open after $end");

        var list = seen.ToList();
        Assert.Equal("/append", list[0].Path);
        Assert.Equal([1, 2, 3], list[0].Body);
        Assert.Equal("/run", list[1].Path);
        Assert.Equal("application/connect+proto", list[1].Type);
        Assert.Equal([0, 0, 0, 0, 2, 0x0a, 0x00], list[1].Body);

        response.Dispose();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!seen.Any(s => s.Path == "/disposed?id=run-1") && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Contains(seen, s => s.Path == "/disposed?id=run-1");
    }

    [Fact]
    public async Task A_content_type_from_prepare_replaces_the_default_instead_of_adding_to_it()
    {
        const string main = """
            import { definePlugin, providers } from 'astra';
            export default definePlugin({
              activate() {
                providers.register('qoder', { prepare: async () => ({ url: 'https://api.qoder.example/x', headers: { 'content-type': 'application/vnd.qoder+json' }, body: { a: 1 } }), transformResponse: { frame: () => [] } });
              },
            });
            """;
        var (t, transport) = await StartAsync(main, reply: null);
        await using var _ = t;
        string? type = null;
        t.Services.Handler = r =>
        {
            type = string.Join(";", r.Content!.Headers.GetValues("Content-Type"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: {}\n\n", Encoding.UTF8, "text/event-stream") };
        };
        using var response = await transport.SendAsync(ChatRequest(stream: true), default);
        Assert.Equal("application/vnd.qoder+json", type);
    }

    [Fact]
    public void The_manifest_checks_the_reply_block()
    {
        static List<string> Errors(JsonObject provider)
        {
            var dir = Path.Combine(Path.GetTempPath(), "astra-reply-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "astra.plugin.json"), new JsonObject
                {
                    ["manifestVersion"] = 2, ["id"] = "a.b", ["version"] = "1.0.0", ["name"] = "n", ["main"] = "main.js", ["permissions"] = new JsonArray("providers"),
                    ["contributes"] = new JsonObject { ["providers"] = new JsonArray(provider) },
                }.ToJsonString());
                File.WriteAllText(Path.Combine(dir, "main.js"), "");
                try
                {
                    PluginV2Manifest.Read(dir);
                    return [];
                }
                catch (PluginManifestException e)
                {
                    return [e.Message];
                }
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
        static JsonObject P(JsonNode? reply) => new() { ["id"] = "p", ["protocols"] = new JsonArray("chat", "anthropic"), ["reply"] = reply };
        Assert.Empty(Errors(P(new JsonObject { ["framing"] = "sse", ["output"] = "anthropic" })));
        Assert.Empty(Errors(P(new JsonObject { ["framing"] = "ndjson" })));
        Assert.Empty(Errors(P(new JsonObject { ["framing"] = "connect", ["output"] = "chat" })));
        Assert.Contains("framing", Assert.Single(Errors(P(new JsonObject { ["framing"] = "xml" }))));
        Assert.Contains("framing", Assert.Single(Errors(P(new JsonObject()))));
        Assert.Contains("output", Assert.Single(Errors(P(new JsonObject { ["framing"] = "sse", ["output"] = "gemini" }))));
        Assert.Contains("must be an object", Assert.Single(Errors(P("sse"))));
    }
}
