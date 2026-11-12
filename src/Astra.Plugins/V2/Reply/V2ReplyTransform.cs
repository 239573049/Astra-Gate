using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Plugins.V2.Runtime;

namespace Astra.Plugins.V2.Reply;

/// <summary>
/// A v2 provider's reply, reshaped (design/plugin-v2 08 §2.2): the host cuts the upstream reply into frames
/// (<see cref="FrameReader"/>), the plugin's <c>transformResponse.frame</c> turns each into frames of the provider's
/// <c>reply.output</c> protocol, and this writes them back as the SSE stream (or the single JSON body) the gateway decodes.
/// Nothing is buffered beyond one frame in a streaming reply; a plugin's failure fails the request (never a truncated stream).
/// </summary>
public sealed class V2ReplyTransform
{
    /// <summary>How long one start / frame / end call may take.</summary>
    public static TimeSpan CallLimit { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The largest single upstream frame (and the largest output frame the plugin may return).</summary>
    public static int MaxFrameBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>The most bytes one upstream reply may carry, and one transformed reply may produce.</summary>
    public static long MaxReplyBytes { get; set; } = 256L * 1024 * 1024;

    private const int ErrorBodyCap = 64 * 1024;

    private static readonly HashSet<string> NoCopy = new(StringComparer.OrdinalIgnoreCase)
    {
        "transfer-encoding", "connection", "keep-alive", "content-length", "content-encoding", "content-type", "content-md5",
    };

    /// <summary>
    /// Wraps <paramref name="upstream"/>. <paramref name="request"/> is the plugin's view of the request (protocol, model, stream).
    /// A non-2xx reply is handed to <c>start</c> (with a body excerpt) and, unless it answers with an error, goes through untouched.
    /// </summary>
    /// <param name="state">What the plugin's <c>prepare</c> returned as <c>state</c>: the reply's <c>ctx.state</c> starts as a copy of it.</param>
    /// <exception cref="HttpRequestException">The plugin failed, timed out, or the upstream bytes are not the declared framing.</exception>
    public static async Task<HttpResponseMessage> ApplyAsync(PluginV2Host host, string pluginId, string providerId, ProviderReply reply,
        JsonObject request, JsonObject? account, HttpResponseMessage upstream, CancellationToken ct, JsonObject? state = null)
    {
        var rid = Guid.NewGuid().ToString("N");
        var wantStream = request["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var sb) && sb;
        var self = new V2ReplyTransform(host, pluginId, providerId, reply, request, account, rid, wantStream);
        try
        {
            var ok = upstream.IsSuccessStatusCode;
            var res = new JsonObject { ["status"] = (int)upstream.StatusCode, ["headers"] = HeadersOf(upstream), ["stream"] = reply.Framing != ReplyFramings.Json };
            byte[]? errorBody = null;
            if (!ok)
            {
                errorBody = await ReadCappedAsync(upstream, ct);
                res["body"] = Encoding.UTF8.GetString(errorBody);
            }
            var startOp = new JsonObject { ["response"] = res };
            if (state is not null) startOp["state"] = state.DeepClone();
            var started = await self.CallAsync("start", startOp, ct) as JsonObject;
            if (started?["error"] is JsonObject err)
            {
                upstream.Dispose();
                await self.DisposeStateAsync();
                return ErrorResponse(err, upstream.RequestMessage);
            }
            if (!ok)
            {
                // nothing to translate in a refusal: it goes on as it came (failover classifies its status), unless start changed it
                await self.DisposeStateAsync();
                var passthrough = new HttpResponseMessage(StatusOf(started) ?? upstream.StatusCode) { RequestMessage = upstream.RequestMessage, Content = new ByteArrayContent(errorBody!) };
                foreach (var h in upstream.Headers) passthrough.Headers.TryAddWithoutValidation(h.Key, h.Value);
                foreach (var h in upstream.Content.Headers.Where(h => !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
                    passthrough.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                upstream.Dispose();
                return passthrough;
            }

            var stream = new ReplyStream(self, upstream, FrameReader.For(reply.Framing, await upstream.Content.ReadAsStreamAsync(ct), MaxFrameBytes, MaxReplyBytes));
            var response = new HttpResponseMessage(StatusOf(started) ?? upstream.StatusCode) { RequestMessage = upstream.RequestMessage, Content = new StreamContent(stream) };
            foreach (var h in upstream.Headers.Where(h => !NoCopy.Contains(h.Key))) response.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (started?["headers"] is JsonObject set)
                foreach (var (k, v) in set)
                    if (!NoCopy.Contains(k) && !k.Equals("host", StringComparison.OrdinalIgnoreCase))
                    {
                        response.Headers.Remove(k);
                        if (v is JsonValue hv && hv.TryGetValue<string>(out var text)) response.Headers.TryAddWithoutValidation(k, text);
                    }
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(wantStream ? "text/event-stream" : "application/json") { CharSet = "utf-8" };
            try
            {
                await stream.PrimeAsync(ct);
            }
            catch (ReplyFailure f)
            {
                response.Dispose();
                return ErrorResponse(f.Error, upstream.RequestMessage);
            }
            catch
            {
                response.Dispose();
                throw;
            }
            return response;
        }
        catch (Exception e) when (e is not HttpRequestException && e is not OperationCanceledException)
        {
            upstream.Dispose();
            await self.DisposeStateAsync();
            throw self.Wrap("start", e);
        }
    }

    private readonly PluginV2Host _host;
    private readonly string _plugin, _provider, _rid;
    private readonly ProviderReply _reply;
    private readonly JsonObject _request;
    private readonly JsonObject? _account;
    private readonly bool _stream;

    private V2ReplyTransform(PluginV2Host host, string plugin, string provider, ProviderReply reply, JsonObject request, JsonObject? account, string rid, bool stream)
    {
        (_host, _plugin, _provider, _reply, _request, _account, _rid, _stream) = (host, plugin, provider, reply, request, account, rid, stream);
    }

    private async Task<JsonNode?> CallAsync(string phase, JsonObject extra, CancellationToken ct)
    {
        var op = new JsonObject
        {
            ["op"] = "provider.response." + phase, ["provider"] = _provider, ["rid"] = _rid,
            ["request"] = new JsonObject
            {
                ["protocol"] = _request["protocol"]?.DeepClone(), ["output"] = _reply.Output,
                ["model"] = _request["model"]?.DeepClone(), ["stream"] = _stream,
            },
            ["account"] = _account?.DeepClone(),
        };
        foreach (var (k, v) in extra) op[k] = v?.DeepClone();
        try
        {
            return await _host.ProviderReplyCallAsync(_plugin, _provider, op, CallLimit, ct);
        }
        catch (PluginCallException e)
        {
            throw Wrap(phase, e);
        }
    }

    private HttpRequestException Wrap(string phase, Exception e) =>
        e is HttpRequestException h ? h
        : e is ReplyFramingException ? new HttpRequestException($"{_plugin}: bad {_reply.Framing} reply: {e.Message}", e)
        : new HttpRequestException($"{_plugin}: transformResponse.{phase} failed: {e.Message}", e);

    private async Task DisposeStateAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(CallLimit);
            await _host.ProviderReplyCallAsync(_plugin, _provider, new JsonObject { ["op"] = "provider.response.dispose", ["provider"] = _provider, ["rid"] = _rid }, CallLimit, cts.Token);
        }
        catch (Exception e) when (e is PluginCallException or OperationCanceledException)
        {
        }
    }

    private static HttpStatusCode? StatusOf(JsonObject? started) =>
        started?["status"] is JsonValue v && v.TryGetValue<int>(out var s) && s is >= 100 and <= 599 ? (HttpStatusCode)s : null;

    private static JsonObject HeadersOf(HttpResponseMessage r)
    {
        var o = new JsonObject();
        foreach (var h in r.Headers.Concat(r.Content.Headers)) o[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
        return o;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage r, CancellationToken ct)
    {
        await using var s = await r.Content.ReadAsStreamAsync(ct);
        var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while (ms.Length < ErrorBodyCap && (n = await s.ReadAsync(buf, ct)) > 0) ms.Write(buf, 0, (int)Math.Min(n, ErrorBodyCap - ms.Length));
        return ms.ToArray();
    }

    /// <summary>A reply turned into a refusal: an OpenAI-shaped error body under the plugin's status (default 502).</summary>
    private static HttpResponseMessage ErrorResponse(JsonObject error, HttpRequestMessage? request)
    {
        var status = error["status"] is JsonValue v && v.TryGetValue<int>(out var s) && s is >= 400 and <= 599 ? s : 502;
        var message = error["message"] is JsonValue m && m.TryGetValue<string>(out var t) && t.Length > 0 ? t : "the provider's plugin refused the reply";
        var body = new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = status == 429 ? "rate_limit_error" : "api_error" } };
        var response = new HttpResponseMessage((HttpStatusCode)status) { RequestMessage = request, Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body.ToJsonString())) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return response;
    }

    /// <summary>A frame/end result that turns the reply into a failure: <c>{ "$error": { status, message } }</c>.</summary>
    private sealed class ReplyFailure(JsonObject error) : Exception("the plugin refused the reply")
    {
        public JsonObject Error { get; } = error;
    }

    private sealed class ReplyStream(V2ReplyTransform owner, HttpResponseMessage upstream, FrameReader reader) : Stream
    {
        private byte[] _cur = [];
        private int _pos;
        private bool _ended, _started, _sawDone, _disposed, _stop;
        private long _out;
        private readonly List<JsonNode> _body = [];

        public async Task PrimeAsync(CancellationToken ct) => await FillAsync(ct);

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            while (_pos >= _cur.Length)
            {
                if (_ended) return false;
                try
                {
                    var next = await reader.NextAsync(ct);
                    if (next is not null) Handle(await owner.CallAsync("frame", new JsonObject { ["frame"] = next }, ct));
                    if (next is null || _stop)
                    {
                        // the upstream ended, or the plugin said the reply is complete ({ $end: true }) while the upstream stays
                        // open (a bidirectional stream waiting on the client): stop reading and close it
                        if (_stop) upstream.Dispose();
                        Handle(await owner.CallAsync("end", [], ct));
                        _ended = true;
                        if (!owner._stream) _cur = Body();
                        else if (owner._reply.Output == "chat" && !_sawDone)
                            _cur = [.. _cur.AsSpan(_pos), .. "data: [DONE]\n\n"u8];
                        _pos = 0;
                    }
                }
                catch (ReplyFailure f) when (_started)
                {
                    throw new HttpRequestException($"{owner._plugin}: {f.Error["message"]?.ToString() ?? "transformResponse failed"} (after the reply began)");
                }
                catch (ReplyFailure)
                {
                    throw;
                }
                catch (Exception e) when (e is not HttpRequestException and not OperationCanceledException)
                {
                    throw owner.Wrap("frame", e);
                }
            }
            return true;
        }

        private static bool IsEnd(JsonNode? f) => f is JsonObject o && o["$end"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

        private void Handle(JsonNode? result)
        {
            var frames = (result as JsonObject)?["frames"] as JsonArray ?? [];
            if (frames.OfType<JsonObject>().FirstOrDefault(f => f["$error"] is JsonObject) is { } fail)
                throw new ReplyFailure((JsonObject)fail["$error"]!);
            if (frames.Any(IsEnd)) _stop = true;
            var output = frames.Where(f => !IsEnd(f)).ToList();
            if (!owner._stream)
            {
                foreach (var f in output) _body.Add(f!.DeepClone());
                return;
            }
            if (output.Count == 0) return;
            var ms = new MemoryStream();
            ms.Write(_cur.AsSpan(_pos)); // what a frame call before this one (the same fill) produced and nobody read yet
            foreach (var f in output) WriteSse(ms, f);
            _cur = ms.ToArray();
            _pos = 0;
            _out += _cur.Length;
            if (_out > MaxReplyBytes) throw new ReplyFramingException($"the transformed reply is larger than {MaxReplyBytes} bytes");
            if (_cur.Length > 0) _started = true;
        }

        private byte[] Body()
        {
            if (_body.Count != 1 || _body[0] is not JsonObject)
                throw new HttpRequestException($"{owner._plugin}: a non-streaming reply must produce exactly one body object, got {_body.Count}");
            var bytes = Encoding.UTF8.GetBytes(_body[0].ToJsonString());
            _started = true;
            return bytes;
        }

        private void WriteSse(MemoryStream ms, JsonNode? f)
        {
            string? evt = null;
            string data;
            if (f is JsonValue sv && sv.TryGetValue<string>(out var s))
            {
                data = s;
                if (s == "[DONE]") _sawDone = true;
            }
            else if (f is JsonObject o && o["event"] is JsonValue ev && ev.TryGetValue<string>(out var es) && o.ContainsKey("data"))
            {
                evt = es;
                data = o["data"] is JsonValue dv && dv.TryGetValue<string>(out var ds) ? ds : o["data"]?.ToJsonString() ?? "";
            }
            else if (f is JsonObject or JsonArray)
                data = f.ToJsonString();
            else
                throw new ReplyFramingException("a frame must be an object, an { event, data } pair or the string \"[DONE]\"");
            if (data.Length > MaxFrameBytes) throw new ReplyFramingException($"an output frame is larger than {MaxFrameBytes} bytes");
            var sb = new StringBuilder();
            if (evt is not null) sb.Append("event: ").Append(evt.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
            foreach (var line in data.Split('\n')) sb.Append("data: ").Append(line.TrimEnd('\r')).Append('\n');
            sb.Append('\n');
            ms.Write(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= _cur.Length && !await FillAsync(ct)) return 0;
            var n = Math.Min(buffer.Length, _cur.Length - _pos);
            _cur.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                upstream.Dispose();
                _ = owner.DisposeStateAsync();
            }
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
}
