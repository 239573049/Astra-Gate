using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Astra.Gateway.Http;

/// <summary>
/// An Ollama-compatible face of the gateway, for a client that takes a custom endpoint only as an Ollama server (VS Code's and
/// Visual Studio's Copilot "Ollama" provider, Zed's Ollama provider, <c>ollama run</c>): <c>/api/version</c>, <c>/api/tags</c>,
/// <c>/api/show</c>, <c>/api/chat</c> and <c>/api/generate</c>. The shapes and the way chat is served follow magpie
/// (<c>internal/gateway/ollama*.go</c>, MIT).
/// <para>
/// <b>Where it lives.</b> Astra's own admin API owns <c>/api</c> on the same port, so the Ollama routes sit under the prefix
/// <c>/ollama</c>: the user gives the client <c>http://127.0.0.1:&lt;port&gt;/ollama</c> as the Ollama URL and the client appends
/// <c>/api/tags</c> and the rest. <c>/ollama/v1/chat/completions</c> and <c>/ollama/v1/models</c> answer too (the clients chat
/// there). They are gateway paths like <c>/v1</c>: not under <c>/api</c>, so <c>SecurityMiddleware</c>'s admin rules (the
/// <c>X-Astra-Admin</c> header, the session cookie) neither apply to them nor are weakened for the admin API — and the gateway's
/// own rules do: the Host allowlist, and a token from every caller that is not this machine.
/// </para>
/// <para>
/// Chat is the <c>/v1/chat/completions</c> request it stands for, served by the same pipeline (<see cref="InternalCall"/>: token,
/// caps, routing, translation to the provider's protocol, failover, request log and bill) and answered Ollama's way: NDJSON
/// lines, the last one <c>done: true</c> with the token counts, or one JSON object for <c>"stream": false</c>. Most Ollama
/// clients send no key, so this works for them on this machine with the gateway-key requirement off, or for a caller whose
/// client can send a token (<c>Authorization: Bearer</c>).
/// </para>
/// </summary>
public static class OllamaEndpoints
{
    /// <summary>The Ollama release answered as <c>/api/version</c>: one past every client's minimum (VS Code's Copilot asks 0.6.4).</summary>
    public const string Version = "0.12.6";

    /// <summary>The route prefix the Ollama face is served under.</summary>
    public const string Prefix = "/ollama";

    private const string Architecture = "astra";
    private const int DefaultContext = 32768;
    private const string Modified = "2025-01-01T00:00:00Z";

    public static IEndpointRouteBuilder MapOllamaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Prefix, () => Results.Text("Ollama is running")).ExcludeFromDescription();
        app.MapGet(Prefix + "/", () => Results.Text("Ollama is running")).ExcludeFromDescription();
        app.MapGet(Prefix + "/api/version", (HttpContext c) => WriteJsonAsync(c, 200, new JsonObject { ["version"] = Version })).ExcludeFromDescription();
        app.MapGet(Prefix + "/api/tags", (HttpContext c, GatewayAuxiliary a) => TagsAsync(c, a)).ExcludeFromDescription();
        app.MapPost(Prefix + "/api/show", (HttpContext c, GatewayAuxiliary a) => ShowAsync(c, a)).ExcludeFromDescription();
        app.MapPost(Prefix + "/api/chat", (HttpContext c, GatewayPipeline p, GatewayAuxiliary a) => ChatAsync(c, p, a, generate: false)).ExcludeFromDescription();
        app.MapPost(Prefix + "/api/generate", (HttpContext c, GatewayPipeline p, GatewayAuxiliary a) => ChatAsync(c, p, a, generate: true)).ExcludeFromDescription();
        // The OpenAI-compatible routes these clients chat and list on, beside the Ollama URL.
        app.MapPost(Prefix + "/v1/chat/completions", (HttpContext c, GatewayPipeline p) => p.HandleAsync(c, ApiProtocol.OpenAIChat)).ExcludeFromDescription();
        app.MapGet(Prefix + "/v1/models", (HttpContext c, GatewayAuxiliary a) => a.ListModelsAsync(c, ApiProtocol.OpenAIChat)).ExcludeFromDescription();
        return app;
    }

    // ------------------------------------------------------------------ models

    private static async Task TagsAsync(HttpContext ctx, GatewayAuxiliary auxiliary)
    {
        var list = await ModelsOrErrorAsync(ctx, auxiliary);
        if (list is null) return;
        var models = new JsonArray();
        foreach (var m in list)
        {
            models.Add((JsonNode)new JsonObject
            {
                ["name"] = m.Id, ["model"] = m.Id, ["modified_at"] = Modified, ["size"] = 0,
                ["digest"] = Digest(m.Id), ["details"] = Details(m), ["capabilities"] = Capabilities(m),
            });
        }
        await WriteJsonAsync(ctx, 200, new JsonObject { ["models"] = models });
    }

    /// <summary>One model as Ollama's <c>/api/show</c> tells it: the capabilities a client reads, and its window as <c>model_info</c>.</summary>
    private static async Task ShowAsync(HttpContext ctx, GatewayAuxiliary auxiliary)
    {
        var ct = ctx.RequestAborted;
        var list = await ModelsOrErrorAsync(ctx, auxiliary);
        if (list is null) return;
        JsonObject? body;
        try
        {
            body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct) as JsonObject;
        }
        catch (System.Text.Json.JsonException e)
        {
            await ErrorAsync(ctx, 400, "invalid request: " + e.Message);
            return;
        }
        var id = (Str(body, "model") ?? Str(body, "name"))?.Trim();
        if (string.IsNullOrEmpty(id))
        {
            await ErrorAsync(ctx, 400, "model is required");
            return;
        }
        id = Resolve(list, id);
        if (list.FirstOrDefault(m => m.Id == id) is not { } model)
        {
            await ErrorAsync(ctx, 404, $"model '{id}' not found");
            return;
        }
        // Every key is sent: the ollama CLI before 0.7 reads parameter_count, context_length and embedding_length without
        // checking them and panics on one missing.
        var window = model.Effective.ContextWindow is > 0 ? model.Effective.ContextWindow.Value : DefaultContext;
        await WriteJsonAsync(ctx, 200, new JsonObject
        {
            ["modelfile"] = "", ["parameters"] = "", ["template"] = "",
            ["details"] = Details(model),
            ["model_info"] = new JsonObject
            {
                ["general.architecture"] = Architecture,
                ["general.basename"] = model.Effective.DisplayName is { Length: > 0 } name ? name : model.Id,
                ["general.parameter_count"] = 0,
                [Architecture + ".context_length"] = window,
                [Architecture + ".embedding_length"] = 0,
            },
            ["capabilities"] = Capabilities(model),
            ["modified_at"] = Modified,
        });
    }

    private static async Task<List<RoutableModel>?> ModelsOrErrorAsync(HttpContext ctx, GatewayAuxiliary auxiliary)
    {
        try
        {
            return await auxiliary.ListRoutableAsync(ctx);
        }
        catch (GatewayException e)
        {
            await ErrorAsync(ctx, e.Status, e.Message);
            return null;
        }
    }

    /// <summary>The model id a request names: the ollama CLI names a model with no tag as <c>name:latest</c>, which is the bare id unless a model really is called that.</summary>
    private static string Resolve(List<RoutableModel> list, string id)
    {
        if (list.Any(m => m.Id == id)) return id;
        if (id.EndsWith(":latest", StringComparison.Ordinal) && list.Any(m => m.Id == id[..^":latest".Length])) return id[..^":latest".Length];
        return id;
    }

    /// <summary>What the model does, in Ollama's words: it completes and calls tools, sees when it takes images, thinks when it reasons.</summary>
    private static JsonArray Capabilities(RoutableModel model)
    {
        var caps = new JsonArray("completion", "tools");
        if (model.Effective.Capabilities.Vision == true) caps.Add((JsonNode)"vision");
        if (model.Effective.Capabilities.Reasoning == true) caps.Add((JsonNode)"thinking");
        return caps;
    }

    private static JsonObject Details(RoutableModel model)
    {
        var details = new JsonObject
        {
            ["parent_model"] = "", ["format"] = "", ["family"] = Architecture, ["families"] = new JsonArray(Architecture),
            ["parameter_size"] = "", ["quantization_level"] = "",
        };
        if (model.Effective.ContextWindow is > 0) details["context_length"] = model.Effective.ContextWindow;
        return details;
    }

    /// <summary>Stands in for a model file's digest: the same id always gives the same one.</summary>
    private static string Digest(string id) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    // ------------------------------------------------------------------ chat

    private static async Task ChatAsync(HttpContext ctx, GatewayPipeline pipeline, GatewayAuxiliary auxiliary, bool generate)
    {
        var ct = ctx.RequestAborted;
        JsonObject? req;
        try
        {
            req = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct) as JsonObject;
        }
        catch (System.Text.Json.JsonException e)
        {
            await ErrorAsync(ctx, 400, "invalid request: " + e.Message);
            return;
        }
        var model = Str(req, "model")?.Trim();
        if (req is null || string.IsNullOrEmpty(model))
        {
            await ErrorAsync(ctx, 400, "model is required");
            return;
        }
        List<RoutableModel> known;
        try
        {
            known = await auxiliary.ListRoutableAsync(ctx);
        }
        catch (GatewayException e)
        {
            await ErrorAsync(ctx, e.Status, e.Message);
            return;
        }
        model = Resolve(known, model);
        var stream = req["stream"] is not JsonValue s || !s.TryGetValue<bool>(out var wanted) || wanted;

        var messages = req["messages"] as JsonArray;
        if (generate ? string.IsNullOrEmpty(Str(req, "prompt")) && req["images"] is not JsonArray { Count: > 0 } : messages is not { Count: > 0 })
        {
            // What Ollama answers a request with nothing to say: the model loaded, which a client asks to warm it up.
            if (known.All(m => m.Id != model))
            {
                await ErrorAsync(ctx, 404, $"model '{model}' not found");
                return;
            }
            var load = new JsonObject { ["model"] = model, ["created_at"] = Now(), ["done"] = true, ["done_reason"] = "load" };
            if (generate) load["response"] = "";
            else load["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "" };
            if (stream) ctx.Response.ContentType = "application/x-ndjson";
            await WriteJsonAsync(ctx, 200, load, stream ? "application/x-ndjson" : null, newline: stream);
            return;
        }

        JsonObject chat;
        try
        {
            chat = ChatBody(req, model, generate, stream);
        }
        catch (ProtocolException e)
        {
            await ErrorAsync(ctx, 400, e.Message);
            return;
        }

        InternalCall call = null!;
        var reply = new Reply(model, generate, stream, req["think"] is JsonValue think && think.TryGetValue<bool>(out var thinks) && !thinks);
        var started = false;
        async ValueTask Line(JsonObject line)
        {
            if (!started)
            {
                started = true;
                Stamp();
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/x-ndjson";
            }
            await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(line.ToJsonString(GatewayJson.Options) + "\n"), ct);
            await ctx.Response.Body.FlushAsync(ct);
        }

        // The request id the pipeline gave the chat, so a client (and the request log) can tell which row it was.
        void Stamp()
        {
            if (!ctx.Response.HasStarted && call.Headers["x-astra-request-id"].ToString() is { Length: > 0 } id) ctx.Response.Headers["x-astra-request-id"] = id;
        }

        call = InternalCall.Create(ctx, "/v1/chat/completions", Encoding.UTF8.GetBytes(chat.ToJsonString(GatewayJson.Options)), ct, async data =>
        {
            data = data.Trim();
            if (data.Length == 0 || data == "[DONE]") return;
            JsonNode? chunk;
            try
            {
                chunk = JsonNode.Parse(data);
            }
            catch (System.Text.Json.JsonException)
            {
                return;
            }
            if (chunk is null) return;
            if (chunk["error"] is { } error)
            {
                reply.Failed = true;
                await Line(new JsonObject { ["error"] = error["message"]?.GetValue<string>() ?? error.ToJsonString(GatewayJson.Options) });
                return;
            }
            if (reply.Take(chunk) is { } part && stream) await Line(part);
        });
        await pipeline.HandleAsync(call.Context, ApiProtocol.OpenAIChat);
        await call.FinishAsync();

        Stamp();
        if (call.Status >= 300)
        {
            // The gateway's refusal or the provider's error, in Ollama's words; a Retry-After goes with it.
            var body = call.BodyText;
            string message = body;
            try
            {
                var parsed = JsonNode.Parse(body);
                message = parsed?["error"]?["message"]?.GetValue<string>() ?? parsed?["error"]?.ToString() ?? body;
            }
            catch (System.Text.Json.JsonException)
            {
                // plain text
            }
            if (string.IsNullOrWhiteSpace(message)) message = $"HTTP {call.Status}";
            if (call.Headers.RetryAfter.ToString() is { Length: > 0 } retry && !ctx.Response.HasStarted) ctx.Response.Headers.RetryAfter = retry;
            if (started) await Line(new JsonObject { ["error"] = message });
            else await ErrorAsync(ctx, call.Status, message);
            return;
        }
        if (!call.Streamed)
        {
            // one completion, as asked with stream false
            try
            {
                if (JsonNode.Parse(call.BodyText) is { } whole) reply.Take(whole);
            }
            catch (System.Text.Json.JsonException)
            {
                await ErrorAsync(ctx, 502, "the reply was not JSON");
                return;
            }
        }
        if (reply.Failed) return;
        var calls = reply.ToolCalls();
        if (stream)
        {
            if (calls.Count > 0) await Line(reply.Part("", "", calls, done: false));
            await Line(reply.Part("", "", null, done: true));
            return;
        }
        await WriteJsonAsync(ctx, 200, reply.Part(reply.Content.ToString(), reply.Thinking.ToString(), calls, done: true));
    }

    /// <summary>An Ollama chat or generate request as the Chat Completions request it stands for.</summary>
    /// <exception cref="ProtocolException">A message or option Chat Completions cannot say.</exception>
    private static JsonObject ChatBody(JsonObject req, string model, bool generate, bool stream)
    {
        var messages = new JsonArray();
        if (generate)
        {
            if (Str(req, "system") is { Length: > 0 } system) messages.Add((JsonNode)new JsonObject { ["role"] = "system", ["content"] = system });
            messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = Content(Str(req, "prompt") ?? "", req["images"] as JsonArray) });
        }
        else messages = Messages(req["messages"] as JsonArray ?? []);

        var body = new JsonObject { ["model"] = model, ["messages"] = messages, ["stream"] = stream };
        if (stream) body["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (!generate && req["tools"] is JsonArray { Count: > 0 } tools) body["tools"] = tools.DeepClone(); // Ollama's tools are Chat Completions' own shape
        switch (req["format"])
        {
            case null:
                break;
            case JsonValue f when f.TryGetValue<string>(out var name):
                if (name == "json") body["response_format"] = new JsonObject { ["type"] = "json_object" };
                else if (name.Length > 0) throw new ProtocolException($"invalid format: {name}");
                break;
            case JsonObject schema:
                body["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = "response", ["schema"] = schema.DeepClone() },
                };
                break;
            default:
                throw new ProtocolException("invalid format");
        }
        // The options a Chat Completions API takes; the rest (num_ctx, top_k, repeat_penalty, …) are the local runner's.
        if (req["options"] is JsonObject options)
        {
            foreach (var key in (string[])["temperature", "top_p", "seed", "presence_penalty", "frequency_penalty", "stop"])
                if (options[key] is { } value) body[key] = value.DeepClone();
            // -1 is no limit and -2 fill the window, which a Chat API says by leaving max_tokens out
            if (options["num_predict"] is JsonValue predict && predict.TryGetValue<double>(out var n) && n > 0) body["max_tokens"] = (int)n;
        }
        switch (req["think"])
        {
            case JsonValue t when t.TryGetValue<string>(out var effort):
                if (effort.Length > 0) body["reasoning_effort"] = effort;
                break;
            case null or JsonValue:
                break;
            default:
                throw new ProtocolException("invalid think value");
        }
        return body;
    }

    /// <summary>
    /// Ollama's messages in Chat Completions' shape. A tool call gets an id when it has none (Ollama's have none before 0.12.x),
    /// and a tool's result is matched to its call by the id it names, else by the tool's name, else in order.
    /// </summary>
    private static JsonArray Messages(JsonArray input)
    {
        var open = new List<(string Id, string Name)>();
        var output = new JsonArray();
        for (var i = 0; i < input.Count; i++)
        {
            var m = input[i] as JsonObject ?? throw new ProtocolException($"messages[{i}]: not an object");
            var role = (Str(m, "role") ?? "").ToLowerInvariant();
            var text = Str(m, "content") ?? "";
            switch (role)
            {
                case "system" or "developer":
                    output.Add((JsonNode)new JsonObject { ["role"] = role, ["content"] = text });
                    break;
                case "user":
                    output.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = Content(text, m["images"] as JsonArray) });
                    break;
                case "assistant":
                {
                    var message = new JsonObject { ["role"] = "assistant", ["content"] = text };
                    if (Str(m, "thinking") is { Length: > 0 } thinking) message["reasoning_content"] = thinking;
                    if (m["tool_calls"] is JsonArray { Count: > 0 } calls)
                    {
                        var converted = new JsonArray();
                        open.Clear();
                        for (var j = 0; j < calls.Count; j++)
                        {
                            var call = calls[j] as JsonObject;
                            var function = call?["function"] as JsonObject;
                            var id = Str(call, "id") is { Length: > 0 } given ? given : $"call_{i}_{j}";
                            var name = Str(function, "name") ?? "";
                            var arguments = function?["arguments"] switch
                            {
                                null => "{}",
                                JsonValue v when v.TryGetValue<string>(out var raw) => raw, // already a JSON string, as a Chat client sends them
                                var node => node.ToJsonString(GatewayJson.Options),
                            };
                            converted.Add((JsonNode)new JsonObject
                            {
                                ["id"] = id, ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments },
                            });
                            open.Add((id, name));
                        }
                        message["tool_calls"] = converted;
                        if (text.Length == 0) message["content"] = null;
                    }
                    output.Add((JsonNode)message);
                    break;
                }
                case "tool":
                {
                    var id = Str(m, "tool_call_id") ?? "";
                    var toolName = Str(m, "tool_name") ?? "";
                    var pick = open.FindIndex(c => id.Length > 0 ? c.Id == id : toolName.Length > 0 && c.Name == toolName);
                    if (pick < 0 && id.Length == 0 && open.Count > 0) pick = 0;
                    if (pick >= 0)
                    {
                        id = open[pick].Id;
                        open.RemoveAt(pick);
                    }
                    if (id.Length == 0) throw new ProtocolException($"messages[{i}]: a tool result with no tool call before it");
                    output.Add((JsonNode)new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = text });
                    // a Chat tool message carries text alone: its images go in a user message after it
                    if (m["images"] is JsonArray { Count: > 0 } pictures)
                        output.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = Content("", pictures) });
                    break;
                }
                default:
                    throw new ProtocolException($"messages[{i}]: unknown role \"{Str(m, "role")}\"");
            }
        }
        return output;
    }

    /// <summary>A message's text with its images, which Ollama sends as bare base64, as Chat Completions content parts with data URLs.</summary>
    private static JsonNode Content(string text, JsonArray? images)
    {
        if (images is not { Count: > 0 }) return text;
        var parts = new JsonArray();
        if (text.Length > 0) parts.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var image in images)
        {
            var b64 = image?.GetValue<string>() ?? "";
            var url = b64.StartsWith("data:", StringComparison.Ordinal) ? b64 : $"data:{ImageType(b64)};base64,{b64}";
            parts.Add((JsonNode)new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
        }
        return parts;
    }

    /// <summary>The media type of a base64 image, from its first bytes.</summary>
    private static string ImageType(string b64) =>
        b64.StartsWith("/9j/", StringComparison.Ordinal) ? "image/jpeg"
        : b64.StartsWith("R0lGOD", StringComparison.Ordinal) ? "image/gif"
        : b64.StartsWith("UklGR", StringComparison.Ordinal) ? "image/webp"
        : "image/png";

    /// <summary>Takes what the Chat Completions handler says — stream chunks or one completion — and says it the Ollama way.</summary>
    private sealed class Reply(string model, bool generate, bool stream, bool hideThinking)
    {
        private readonly DateTime _start = DateTime.UtcNow;
        private readonly List<(string Id, string Name, StringBuilder Arguments)> _calls = [];
        private DateTime? _firstToken;
        private string _finish = "";
        private long _promptTokens, _outputTokens;

        public StringBuilder Content { get; } = new();
        public StringBuilder Thinking { get; } = new();
        public bool Failed { get; set; }

        /// <summary>Reads a chunk (or a whole completion: its message where a chunk has its delta); the line to write when streaming, if it adds text.</summary>
        public JsonObject? Take(JsonNode chunk)
        {
            if (chunk["usage"] is JsonObject usage)
            {
                _promptTokens = Long(usage["prompt_tokens"]);
                _outputTokens = Long(usage["completion_tokens"]);
            }
            if (chunk["choices"] is not JsonArray { Count: > 0 } choices || choices[0] is not JsonObject choice) return null;
            var d = choice["delta"] as JsonObject ?? choice["message"] as JsonObject;
            var text = Str(d, "content") ?? "";
            var think = Str(d, "reasoning_content") is { Length: > 0 } rc ? rc : Str(d, "reasoning") ?? "";
            if (hideThinking) think = "";
            if (d?["tool_calls"] is JsonArray calls)
            {
                foreach (var tc in calls)
                {
                    var index = tc?["index"] is { } at ? (int)Long(at) : _calls.Count;
                    while (_calls.Count <= index) _calls.Add(("", "", new StringBuilder()));
                    var entry = _calls[index];
                    if (Str(tc as JsonObject, "id") is { Length: > 0 } id) entry.Id = id;
                    if (Str(tc?["function"] as JsonObject, "name") is { Length: > 0 } name) entry.Name = name;
                    entry.Arguments.Append(Str(tc?["function"] as JsonObject, "arguments") ?? "");
                    _calls[index] = entry;
                }
            }
            if (Str(choice, "finish_reason") is { Length: > 0 } finish) _finish = finish;
            if (text.Length == 0 && think.Length == 0) return null;
            _firstToken ??= DateTime.UtcNow;
            Content.Append(text);
            Thinking.Append(think);
            return stream ? Part(text, think, null, done: false) : null;
        }

        /// <summary>The calls the reply made, their arguments as objects.</summary>
        public JsonArray ToolCalls()
        {
            var output = new JsonArray();
            for (var i = 0; i < _calls.Count; i++)
            {
                var (id, name, arguments) = _calls[i];
                if (name.Length == 0) continue;
                var text = arguments.ToString().Trim();
                JsonNode args;
                if (text.Length == 0) args = new JsonObject();
                else
                {
                    try
                    {
                        args = JsonNode.Parse(text) ?? new JsonObject();
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        args = text; // what a model wrote that is not JSON is kept, as a string
                    }
                }
                output.Add((JsonNode)new JsonObject
                {
                    ["id"] = id.Length > 0 ? id : $"call_{i}",
                    ["function"] = new JsonObject { ["index"] = i, ["name"] = name, ["arguments"] = args },
                });
            }
            return output;
        }

        /// <summary>One line of Ollama's reply.</summary>
        public JsonObject Part(string text, string think, JsonArray? calls, bool done)
        {
            var line = new JsonObject { ["model"] = model, ["created_at"] = Now(), ["done"] = done };
            if (generate)
            {
                line["response"] = text;
                if (think.Length > 0) line["thinking"] = think;
            }
            else
            {
                var message = new JsonObject { ["role"] = "assistant", ["content"] = text };
                if (think.Length > 0) message["thinking"] = think;
                if (calls is { Count: > 0 }) message["tool_calls"] = calls;
                line["message"] = message;
            }
            if (done)
            {
                var end = DateTime.UtcNow;
                var first = _firstToken ?? end;
                line["done_reason"] = _finish == "length" ? "length" : "stop";
                line["total_duration"] = Nanos(end - _start);
                line["load_duration"] = 0;
                line["prompt_eval_count"] = _promptTokens;
                line["prompt_eval_duration"] = Nanos(first - _start);
                line["eval_count"] = _outputTokens;
                line["eval_duration"] = Nanos(end - first);
            }
            return line;
        }

        private static long Nanos(TimeSpan span) => span.Ticks * 100;

        private static long Long(JsonNode? node) =>
            node is JsonValue v && v.TryGetValue<long>(out var n) ? n : node is JsonValue d && d.TryGetValue<double>(out var f) ? (long)f : 0;
    }

    // ------------------------------------------------------------------ helpers

    private static string? Str(JsonObject? o, string key) => o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static string Now() => DateTime.UtcNow.ToString("O");

    private static Task ErrorAsync(HttpContext ctx, int status, string message) => WriteJsonAsync(ctx, status, new JsonObject { ["error"] = message });

    private static async Task WriteJsonAsync(HttpContext ctx, int status, JsonObject body, string? contentType = null, bool newline = false)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType ?? "application/json";
        await ctx.Response.WriteAsync(body.ToJsonString(GatewayJson.Options) + (newline ? "\n" : ""), ctx.RequestAborted);
    }
}
