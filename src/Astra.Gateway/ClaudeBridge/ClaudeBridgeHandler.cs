using System.IO.Pipelines;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Astra.Gateway.ClaudeBridge;

/// <summary>
/// The transport of a Claude bridge account (like the plugin providers' <c>PluginFetchHandler</c>): the gateway sends its Messages request to
/// the Claude subscription's address as to any upstream, and this answers it through the local <c>claude</c> program instead
/// (<see cref="ClaudeBridgeService"/>). The answer is an Anthropic reply — a stream of Messages events, or the whole message when the request
/// did not stream — so every caller protocol, usage accounting and the request log work as they do for the API. A failure is an error
/// response with the status an upstream would have used (429 for a limit, with the reset in <c>anthropic-ratelimit-unified-reset</c>; 401 for a
/// sign-in that is gone), which the gateway's account failover already knows.
/// </summary>
public sealed class ClaudeBridgeHandler(ClaudeBridgeService service) : HttpMessageHandler
{
    /// <summary>The account a request is made for.</summary>
    public static readonly HttpRequestOptionsKey<ClaudeTarget> TargetOption = new("astra.claude_bridge.target");

    /// <summary>The header that marks an answer as the bridge's (the request log and traces read it; <c>claude</c> by hand).</summary>
    public const string BridgeHeader = "x-astra-claude-bridge";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (!path.EndsWith("/messages", StringComparison.Ordinal))
            return Error(new ClaudeBridgeException(404, "not_found_error", "claude 程序只回答 Messages 请求。"), request);
        JsonObject body;
        try
        {
            body = JsonNode.Parse(request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct)) as JsonObject ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return Error(new ClaudeBridgeException(400, "invalid_request_error", "请求体不是 JSON。"), request);
        }
        var target = request.Options.TryGetValue(TargetOption, out var t) ? t : new ClaudeTarget("", null, null);
        var claude = ClaudeRequest.Parse(body);
        var events = service.AnswerAsync(claude, target, ct).GetAsyncEnumerator(ct);
        JsonObject? first;
        try
        {
            first = await events.MoveNextAsync() ? events.Current : null;
        }
        catch (ClaudeBridgeException e)
        {
            await events.DisposeAsync();
            return Error(e, request);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // the program went away between the turn's start and its first word
            await events.DisposeAsync();
            return Error(new ClaudeBridgeException(502, "api_error", "claude 进程异常结束：" + e.Message), request);
        }
        catch
        {
            await events.DisposeAsync();
            throw;
        }
        if (first is null)
        {
            await events.DisposeAsync();
            return Error(new ClaudeBridgeException(502, "api_error", "claude 没有给出回复。"), request);
        }
        return claude.Stream ? Stream(request, events, first, ct) : await WholeAsync(request, events, first);
    }

    private static HttpResponseMessage Stream(HttpRequestMessage request, IAsyncEnumerator<JsonObject> events, JsonObject first, CancellationToken ct)
    {
        var pipe = new Pipe();
        _ = PumpAsync(pipe.Writer, events, first, ct);
        var content = new StreamContent(pipe.Reader.AsStream());
        content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request };
        response.Headers.TryAddWithoutValidation(BridgeHeader, "1");
        return response;
    }

    private static async Task PumpAsync(PipeWriter writer, IAsyncEnumerator<JsonObject> events, JsonObject first, CancellationToken ct)
    {
        try
        {
            var ev = first;
            while (true)
            {
                var data = ev.ToJsonString();
                var type = ev["type"]?.GetValue<string>() ?? "message";
                if (!await WriteAsync(writer, $"event: {type}\ndata: {data}\n\n", ct)) return;
                if (!await events.MoveNextAsync()) break;
                ev = events.Current;
            }
            await writer.CompleteAsync();
        }
        catch (ClaudeBridgeException e)
        {
            // the reply had begun: the protocol's own way of ending it badly
            var error = new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = e.ErrorType, ["message"] = e.Message } };
            await WriteAsync(writer, $"event: error\ndata: {error.ToJsonString()}\n\n", CancellationToken.None);
            await writer.CompleteAsync();
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            await writer.CompleteAsync(e);
        }
        finally
        {
            await events.DisposeAsync();
        }
    }

    /// <summary>False once the reader is gone (the client left): the reply is let go.</summary>
    private static async Task<bool> WriteAsync(PipeWriter writer, string text, CancellationToken ct)
    {
        var result = await writer.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        return !result.IsCompleted && !result.IsCanceled;
    }

    /// <summary>A request that did not stream: the whole message, put together from the events.</summary>
    private static async Task<HttpResponseMessage> WholeAsync(HttpRequestMessage request, IAsyncEnumerator<JsonObject> events, JsonObject first)
    {
        var message = new ClaudeMessageBuilder();
        try
        {
            message.Add(first);
            while (await events.MoveNextAsync()) message.Add(events.Current);
        }
        catch (ClaudeBridgeException e)
        {
            return Error(e, request);
        }
        finally
        {
            await events.DisposeAsync();
        }
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(message.Build().ToJsonString(), Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
        response.Headers.TryAddWithoutValidation(BridgeHeader, "1");
        return response;
    }

    private static HttpResponseMessage Error(ClaudeBridgeException e, HttpRequestMessage request)
    {
        var body = new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = e.ErrorType, ["message"] = e.Message } };
        var response = new HttpResponseMessage((HttpStatusCode)e.Status)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            RequestMessage = request,
        };
        response.Headers.TryAddWithoutValidation(BridgeHeader, "1");
        if (e.ResetsAt is { } reset) response.Headers.TryAddWithoutValidation("anthropic-ratelimit-unified-reset", reset.ToUnixTimeSeconds().ToString());
        return response;
    }
}

/// <summary>Puts Anthropic stream events together into the Messages response a request that did not stream gets.</summary>
internal sealed class ClaudeMessageBuilder
{
    private JsonObject _message = [];
    private readonly Dictionary<int, StringBuilder> _json = [];

    public void Add(JsonObject ev)
    {
        switch (ev["type"]?.GetValue<string>())
        {
            case "message_start":
                _message = ev["message"] is JsonObject m ? (JsonObject)m.DeepClone() : [];
                _message["content"] = new JsonArray();
                break;
            case "content_block_start" when ev["content_block"] is JsonObject block:
                if (_message["content"] is JsonArray content) content.Add(block.DeepClone());
                break;
            case "content_block_delta" when ev["delta"] is JsonObject delta && ev["index"]?.GetValue<int>() is { } index
                                              && _message["content"] is JsonArray blocks && index < blocks.Count && blocks[index] is JsonObject target:
                switch (delta["type"]?.GetValue<string>())
                {
                    case "text_delta": target["text"] = (target["text"]?.GetValue<string>() ?? "") + delta["text"]?.GetValue<string>(); break;
                    case "thinking_delta": target["thinking"] = (target["thinking"]?.GetValue<string>() ?? "") + delta["thinking"]?.GetValue<string>(); break;
                    case "signature_delta": target["signature"] = delta["signature"]?.GetValue<string>(); break;
                    case "input_json_delta":
                        if (!_json.TryGetValue(index, out var sb)) _json[index] = sb = new StringBuilder();
                        sb.Append(delta["partial_json"]?.GetValue<string>());
                        break;
                }
                break;
            case "content_block_stop" when ev["index"]?.GetValue<int>() is { } stopped && _json.TryGetValue(stopped, out var partial)
                                           && _message["content"] is JsonArray done && stopped < done.Count && done[stopped] is JsonObject tool:
                try
                {
                    tool["input"] = partial.Length == 0 ? new JsonObject() : JsonNode.Parse(partial.ToString());
                }
                catch (System.Text.Json.JsonException)
                {
                    tool["input"] = new JsonObject();
                }
                _json.Remove(stopped);
                break;
            case "message_delta" when ev["delta"] is JsonObject d:
                _message["stop_reason"] = d["stop_reason"]?.DeepClone();
                _message["stop_sequence"] = d["stop_sequence"]?.DeepClone();
                if (ev["usage"] is JsonObject u)
                {
                    var usage = _message["usage"] as JsonObject ?? [];
                    foreach (var (k, v) in u) usage[k] = v?.DeepClone();
                    _message["usage"] = usage;
                }
                break;
        }
    }

    public JsonObject Build() => _message;
}
