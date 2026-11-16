using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Astra.Core;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Astra.Gateway.Http;

/// <summary>
/// The Responses API over a WebSocket, as Codex speaks it to its provider (<c>GET /v1/responses</c> with an upgrade): one socket
/// per session, each turn a text frame — the HTTP body with <c>"type":"response.create"</c> added — and the reply one text frame
/// per event the HTTP route streams. On a socket it has had a reply on, Codex names that reply (<c>previous_response_id</c>)
/// and sends only what came after it. The design is magpie's (MIT; <c>internal/gateway/responses_ws.go</c>):
/// <list type="bullet">
/// <item>Each <c>response.create</c> is served as the <c>POST /v1/responses</c> it stands for (<see cref="InternalCall"/>: the
/// upgrade request's headers, the frame as the body, always streamed), through the whole <see cref="GatewayPipeline"/> — token,
/// caps, routing, translation to other protocols, failover, request log and bill judge it as an HTTP turn. A token turned
/// off ends the next turn, not the socket.</item>
/// <item>The conversation a turn continues is the socket's own: each reply's input and output items, kept for the socket's life
/// (the last two replies), because the pipeline is stateless and the upstream may be a provider that never saw the earlier turn.
/// A <c>previous_response_id</c> the socket does not hold is answered <c>previous_response_not_found</c>, on which Codex opens a
/// new socket and sends the conversation whole.</item>
/// <item><c>generate: false</c> (Codex's warmup) is answered locally with an empty completed response; <c>response.interrupt</c>
/// cancels the turn in flight.</item>
/// </list>
/// Codex's backend-only frames (turn state, rate-limit windows) are not produced: they describe a ChatGPT account, not Astra's.
/// </summary>
public static class ResponsesSocket
{
    /// <summary>How many replies a socket keeps: Codex names only the last one (and a warmup's before its first turn).</summary>
    private const int KeepReplies = 2;

    /// <summary>Serves <c>GET /v1/responses</c>: the upgrade, or a 426 telling a plain GET what this route is for.</summary>
    public static async Task ServeAsync(HttpContext ctx, GatewayPipeline pipeline, IGatewayRouter router, GatewayAuxiliary errors,
        IGatewaySettings settings, ILogger logger)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.Headers["Upgrade"] = "websocket";
            await errors.WriteErrorAsync(ctx, ApiProtocol.OpenAIResponses, StatusCodes.Status426UpgradeRequired, "invalid_request_error",
                "GET /v1/responses 只用于 WebSocket 升级（Codex 的 Responses WebSocket 模式）；HTTP 请求请使用 POST。");
            return;
        }
        // A browser's page from another site can open a WebSocket to localhost (CORS does not apply to it) and read the answer:
        // only a caller that sends no Origin (an agent, a CLI) or the server's own origin gets in.
        if (ctx.Request.Headers.Origin.ToString() is { Length: > 0 } origin
            && !(Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                 && string.Equals(uri.Authority, ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
        {
            await errors.WriteErrorAsync(ctx, ApiProtocol.OpenAIResponses, StatusCodes.Status403Forbidden, "permission_error",
                "不接受来自其他网站的 WebSocket 连接。");
            return;
        }
        // The caller is judged before the upgrade (a network or tunnel caller without a token never gets a socket) and
        // again on every turn.
        try
        {
            await router.ResolveAsync(ctx.Request, ctx.RequestAborted);
        }
        catch (GatewayException e)
        {
            await errors.WriteErrorAsync(ctx, ApiProtocol.OpenAIResponses, e.Status, e.Type, e.Message);
            return;
        }
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var session = new Session(ctx, pipeline, socket, Math.Clamp(settings.Current.MaxRequestBodyMb, 1, 1024) * 1024L * 1024, logger);
        await session.RunAsync();
    }

    /// <summary>One reply the socket holds: the conversation up to and with it.</summary>
    private sealed record Held(string Id, List<JsonNode> Items);

    /// <summary>What a turn in flight has said so far.</summary>
    private sealed class TurnRun
    {
        public readonly List<string> Ids = [];
        public readonly List<JsonNode> Items = [];
        public string? Id;
        public bool Ended, Completed, Cut;
        public bool HasId(string id) => Ids.Contains(id);
    }

    private sealed class Session(HttpContext origin, GatewayPipeline pipeline, WebSocket socket, long maxFrameBytes, ILogger logger)
    {
        private readonly CancellationToken _live = origin.RequestAborted;
        private readonly SemaphoreSlim _writes = new(1, 1);
        private readonly Channel<string?> _inbox = Channel.CreateUnbounded<string?>();
        private readonly Queue<string?> _queued = new();
        private readonly List<Held> _held = [];
        private Task<string?>? _next;
        private int _frames, _continued;

        public async Task RunAsync()
        {
            var reader = Task.Run(ReadLoopAsync);
            try
            {
                while (true)
                {
                    var message = _queued.Count > 0 ? _queued.Dequeue() : await NextAsync();
                    if (message is null) return;
                    if (await HandleAsync(message) is false) return;
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // The client went away.
            }
            finally
            {
                if (_frames > 0) logger.LogInformation("Responses socket ended: {Turns} turns, {Continued} continued", _frames, _continued);
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await CloseAsync(WebSocketCloseStatus.NormalClosure, "bye");
                await Task.WhenAny(reader, Task.Delay(TimeSpan.FromSeconds(1)));
            }
        }

        // The pending read of the next client frame is shared by the loop and a turn waiting for an interrupt.
        private Task<string?> Next() => _next ??= ReadInboxAsync();

        private async Task<string?> NextAsync()
        {
            var read = Next();
            var message = await read;
            _next = null;
            return message;
        }

        private async Task<string?> ReadInboxAsync()
        {
            try
            {
                return await _inbox.Reader.ReadAsync(_live);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        private async Task ReadLoopAsync()
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    ValueWebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer.AsMemory(), _live);
                        if (result.MessageType == WebSocketMessageType.Close) return;
                        if (result.MessageType != WebSocketMessageType.Text)
                        {
                            await CloseAsync(WebSocketCloseStatus.InvalidMessageType, "text frames only");
                            return;
                        }
                        message.Write(buffer.AsSpan(0, result.Count));
                        if (message.Length > maxFrameBytes)
                        {
                            await CloseAsync(WebSocketCloseStatus.MessageTooBig, "frame too large");
                            return;
                        }
                    }
                    while (!result.EndOfMessage);
                    await _inbox.Writer.WriteAsync(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length), _live);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // closed by the client or the server
            }
            finally
            {
                _inbox.Writer.TryComplete();
            }
        }

        /// <summary>Handles one client frame; false ends the socket.</summary>
        private async Task<bool> HandleAsync(string frame)
        {
            JsonObject? q;
            try
            {
                q = JsonNode.Parse(frame) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                q = null;
            }
            if (q is null)
            {
                await WriteErrorAsync(_live, 400, "invalid_request_error", "invalid_json", "response.create is not a JSON object");
                return true;
            }
            var kind = q["type"]?.GetValue<string>();
            switch (kind)
            {
                case "response.create":
                    break;
                case "response.interrupt":
                    // nothing in flight: it ended before the ask came
                    await WriteAsync(_live, Interrupt("response.interrupt.failed", q["response_id"]?.GetValue<string>() ?? ""));
                    return true;
                default:
                    await WriteErrorAsync(_live, 400, "invalid_request_error", "unsupported_message",
                        $"Astra serves response.create on this socket, not \"{kind}\"", "type");
                    return true;
            }

            using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(_live);
            var run = new TurnRun();
            var turn = Task.Run(() => TurnAsync(q, frame.Length, run, turnCts.Token));
            while (!turn.IsCompleted)
            {
                var read = Next();
                await Task.WhenAny(turn, read);
                if (!read.IsCompleted) continue;
                var message = await read;
                _next = null;
                if (message is null)
                {
                    // the client went: what is in flight ends with it
                    turnCts.Cancel();
                    await Quiet(turn);
                    return false;
                }
                if (JsonNode.Parse(message) is JsonObject m && m["type"]?.GetValue<string>() == "response.interrupt")
                {
                    var id = m["response_id"]?.GetValue<string>() ?? "";
                    bool mine;
                    lock (run) mine = run.HasId(id);
                    if (!mine)
                    {
                        await WriteAsync(_live, Interrupt("response.interrupt.failed", id));
                        continue;
                    }
                    turnCts.Cancel();
                    await Quiet(turn);
                    await WriteAsync(_live, Interrupt("response.interrupt.accepted", id));
                    // the reply ends as interrupted: Codex keeps none of it and waits for its end
                    await WriteAsync(_live, new JsonObject
                    {
                        ["type"] = "response.incomplete",
                        ["response"] = new JsonObject
                        {
                            ["id"] = id, ["object"] = "response", ["status"] = "incomplete",
                            ["incomplete_details"] = new JsonObject { ["reason"] = "interrupted" }, ["output"] = new JsonArray(),
                        },
                    }.ToJsonString(GatewayJson.Options));
                    break;
                }
                _queued.Enqueue(message);
            }
            await Quiet(turn);
            if (run.Cut)
            {
                // the reply ended before its end: as over HTTP, Codex asks again, on a new socket, with the conversation whole
                await CloseAsync(WebSocketCloseStatus.InternalServerError, "the reply ended before response.completed");
                return false;
            }
            return true;
        }

        private static async Task Quiet(Task task)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
                // interrupted
            }
        }

        private static string Interrupt(string type, string id) =>
            new JsonObject { ["type"] = type, ["response_id"] = id }.ToJsonString(GatewayJson.Options);

        /// <summary>Serves one response.create as the POST it stands for.</summary>
        private async Task TurnAsync(JsonObject q, int frameBytes, TurnRun run, CancellationToken ct)
        {
            _frames++;
            var fresh = FreshInput(q["input"]);
            if (fresh is null)
            {
                await WriteErrorAsync(ct, 400, "invalid_request_error", "invalid_value", "input is neither text nor a list of items", "input");
                return;
            }
            List<JsonNode> prior = [];
            var input = fresh;
            if (q["previous_response_id"] is JsonValue previous && previous.TryGetValue<string>(out var previousId))
            {
                var held = _held.LastOrDefault(h => h.Id == previousId);
                if (held is null)
                {
                    // Codex opens a new socket and sends the conversation whole
                    await WriteErrorAsync(ct, 400, "invalid_request_error", "previous_response_not_found",
                        $"Previous response with id '{previousId}' not found.", "previous_response_id");
                    return;
                }
                prior = held.Items;
                input = [.. prior.Select(i => i.DeepClone()), .. fresh.Select(i => i.DeepClone())];
                _continued++;
            }

            // a warmup (generate false) asks for no reply: Codex sends it with the conversation ahead of its first turn
            if (q["generate"] is JsonValue generate && generate.TryGetValue<bool>(out var wanted) && !wanted)
            {
                var id = "resp_" + Ulid.NewUlid();
                lock (run) run.Ids.Add(id);
                var response = new JsonObject
                {
                    ["id"] = id, ["object"] = "response", ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["status"] = "in_progress", ["output"] = new JsonArray(),
                };
                if (q["model"] is { } model) response["model"] = model.DeepClone();
                await WriteAsync(ct, new JsonObject { ["type"] = "response.created", ["response"] = response.DeepClone() }.ToJsonString(GatewayJson.Options));
                response["status"] = "completed";
                response["usage"] = new JsonObject { ["input_tokens"] = 0, ["output_tokens"] = 0, ["total_tokens"] = 0 };
                if (await WriteAsync(ct, new JsonObject { ["type"] = "response.completed", ["response"] = response }.ToJsonString(GatewayJson.Options)))
                    Keep(id, prior, fresh, []);
                return;
            }

            // the body the HTTP route would have had; WebSocket mode is always streamed
            q.Remove("type");
            q.Remove("previous_response_id");
            q.Remove("generate");
            q["stream"] = true;
            q["input"] = new JsonArray(input.ToArray());
            var body = Encoding.UTF8.GetBytes(q.ToJsonString(GatewayJson.Options));

            var call = InternalCall.Create(origin, "/v1/responses", body, ct, data => OnEventAsync(run, data, ct), headers =>
            {
                // the socket's own beta (responses_websockets=…) is not the turn's
                if (headers.TryGetValue("OpenAI-Beta", out var beta))
                {
                    var kept = beta.Where(v => v is not null && !v.Contains("responses_websockets", StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (kept.Length > 0) headers["OpenAI-Beta"] = kept;
                    else headers.Remove("OpenAI-Beta");
                }
            });
            await pipeline.HandleAsync(call.Context, ApiProtocol.OpenAIResponses);
            await call.FinishAsync();
            if (ct.IsCancellationRequested) return;
            if (!call.Streamed)
            {
                // an error reply, as Codex reads one on a socket: the status and the error object an HTTP reply would have carried
                run.Ended = true;
                JsonNode? error = null;
                try
                {
                    error = JsonNode.Parse(call.BodyText)?["error"]?.DeepClone();
                }
                catch (System.Text.Json.JsonException)
                {
                    // not JSON
                }
                var status = call.Status is >= 400 ? call.Status : 502;
                error ??= new JsonObject { ["type"] = "server_error", ["message"] = call.BodyText is { Length: > 0 } text ? text : "the reply was not a stream" };
                var frame = new JsonObject { ["type"] = "error", ["status"] = status, ["error"] = error };
                if (call.Headers.RetryAfter.ToString() is { Length: > 0 } retry)
                    frame["headers"] = new JsonObject { ["retry-after"] = retry };
                await WriteAsync(ct, frame.ToJsonString(GatewayJson.Options));
                return;
            }
            if (!run.Ended)
            {
                run.Cut = true;
                return;
            }
            if (run.Id is { } replyId && run.Completed)
                Keep(replyId, prior, fresh, run.Items);
        }

        /// <summary>One event of the reply's stream, sent on as one frame; what the turn needs is read from it on the way.</summary>
        private async ValueTask OnEventAsync(TurnRun run, string data, CancellationToken ct)
        {
            data = data.Trim();
            if (data.Length == 0 || data == "[DONE]") return;
            JsonObject? e;
            try
            {
                e = JsonNode.Parse(data) as JsonObject;
            }
            catch (System.Text.Json.JsonException)
            {
                return; // not JSON: Codex would skip it too
            }
            if (e is null) return;
            switch (e["type"]?.GetValue<string>())
            {
                case "response.created" or "response.in_progress":
                    Note(run, e["response"]?["id"]?.GetValue<string>());
                    break;
                case "response.output_item.done":
                    // in the order they came, as Codex adds them to its side
                    if (e["item"] is { } item) lock (run) run.Items.Add(item.DeepClone());
                    break;
                case "response.completed":
                    Note(run, e["response"]?["id"]?.GetValue<string>());
                    run.Ended = run.Completed = true;
                    break;
                case "response.incomplete" or "response.failed" or "response.cancelled" or "error":
                    run.Ended = true;
                    break;
            }
            await WriteAsync(ct, data);
        }

        private static void Note(TurnRun run, string? id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (run)
            {
                run.Id = id;
                if (!run.Ids.Contains(id)) run.Ids.Add(id);
            }
        }

        private static List<JsonNode>? FreshInput(JsonNode? input)
        {
            switch (input)
            {
                case JsonValue v when v.TryGetValue<string>(out var text):
                    return
                    [
                        new JsonObject
                        {
                            ["type"] = "message", ["role"] = "user",
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text }),
                        },
                    ];
                case JsonArray list:
                    return list.Where(i => i is not null).Select(i => i!.DeepClone()).ToList();
                default:
                    return null;
            }
        }

        /// <summary>Holds the reply <paramref name="id"/> ends: the conversation before this turn, the turn's own input and the reply's items.</summary>
        private void Keep(string id, List<JsonNode> prior, List<JsonNode> fresh, List<JsonNode> reply)
        {
            var items = new List<JsonNode>(prior.Count + fresh.Count + reply.Count);
            items.AddRange(prior);
            items.AddRange(fresh);
            items.AddRange(reply);
            _held.Add(new Held(id, items));
            if (_held.Count > KeepReplies) _held.RemoveRange(0, _held.Count - KeepReplies);
        }

        /// <summary>Sends a frame unless the turn's token has ended (interrupted, or the client gone); false when it was not sent.</summary>
        private async Task<bool> WriteAsync(CancellationToken turn, string frame)
        {
            if (turn.IsCancellationRequested) return false;
            await _writes.WaitAsync(_live);
            try
            {
                // The write itself is under the socket's own token: one whose token ends mid-write would close the socket,
                // which an interrupt must not.
                await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, _live);
                return true;
            }
            finally
            {
                _writes.Release();
            }
        }

        /// <summary>An error frame as Codex reads one: the HTTP status and the error object an HTTP reply would have carried.</summary>
        private Task<bool> WriteErrorAsync(CancellationToken turn, int status, string type, string code, string message, string? param = null)
        {
            var error = new JsonObject { ["type"] = type, ["code"] = code, ["message"] = message };
            if (param is not null) error["param"] = param;
            return WriteAsync(turn, new JsonObject { ["type"] = "error", ["status"] = status, ["error"] = error }.ToJsonString(GatewayJson.Options));
        }

        private async Task CloseAsync(WebSocketCloseStatus status, string reason)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_live);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(status, reason, timeout.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
            {
                // already gone
            }
        }
    }
}
