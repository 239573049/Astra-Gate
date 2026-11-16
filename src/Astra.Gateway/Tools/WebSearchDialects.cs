using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Tools;

internal static class JsonArrayExtensions
{
    // JsonArray.Add<T>(T) is the overload a JsonObject argument binds to, and it is not AOT-safe: go through the JsonNode one.
    public static void Push(this JsonArray array, JsonNode? node) => array.Add(node);
}

/// <summary>A call of the stand-in function tool the model made.</summary>
public sealed record SearchCall(string Id, string Query);

/// <summary>One search Astra ran for the model: what it asked, what came back (or why nothing did).</summary>
public sealed record SearchRun(string Id, string Query, IReadOnlyList<FoundPage> Pages, string? Error);

/// <summary>One round of the loop: what the model wrote besides its search calls, and the searches it asked for.</summary>
public sealed record SearchRound(JsonArray Kept, List<SearchRun> Runs);

/// <summary>
/// How one wire protocol offers a web-search server tool, how it is replaced by a function tool, how a call of that tool is read and
/// answered, and how the final reply says "the server searched" the way the client expects (server-tool blocks, <c>web_search_call</c>
/// items, citations, grounding metadata). The loop in <see cref="WebSearchBridge"/> is the same for every protocol.
/// The idea — search for a model that cannot, by a stand-in tool the gateway answers itself — is magpie's (MIT).
/// </summary>
public abstract class SearchDialect
{
    public const string ToolName = "web_search";

    public const string Description = "Search the web for current information. Returns pages with their title, address and an excerpt. Use it for anything recent or that you are unsure of.";

    /// <summary>The path an internal call is made at.</summary>
    public abstract string Path { get; }

    /// <summary>Whether the request offers a web-search server tool this dialect can stand in for.</summary>
    public abstract bool Detect(JsonObject body);

    /// <summary>Replaces the server tool with the function tool, takes the search marks out of the history and turns streaming off.</summary>
    public abstract void Rewrite(JsonObject body);

    /// <summary>The calls of the stand-in tool in a (non-streamed) reply.</summary>
    public abstract List<SearchCall> Calls(JsonObject reply);

    /// <summary>Whether the reply also calls tools of the client's own.</summary>
    public abstract bool HasOtherCalls(JsonObject reply);

    /// <summary>What the model wrote in this reply besides its search calls.</summary>
    public abstract JsonArray Kept(JsonObject reply);

    /// <summary>Adds the model's turn and the search results to the request, for the next round.</summary>
    public abstract void Continue(JsonObject body, JsonObject reply, List<SearchRun> runs);

    /// <summary>Says in the final reply that searches ran. <paramref name="stripCalls"/>: take the stand-in calls out (a reply that also calls the client's tools).</summary>
    public abstract void Decorate(JsonObject reply, List<SearchRound> rounds, bool stripCalls);

    /// <summary>The reply as the events a streaming client expects (each string is one write).</summary>
    public abstract IEnumerable<string> Stream(JsonObject reply, JsonObject request, bool altSse);

    public static SearchDialect For(Astra.Core.ApiProtocol protocol) => protocol switch
    {
        Astra.Core.ApiProtocol.Anthropic => new AnthropicSearchDialect(),
        Astra.Core.ApiProtocol.OpenAIResponses => new ResponsesSearchDialect(),
        Astra.Core.ApiProtocol.Gemini => new GeminiSearchDialect(),
        _ => new ChatSearchDialect(),
    };

    // ---------------------------------------------------------------- shared helpers

    /// <summary>The search results as the text the model reads.</summary>
    public static string ResultText(SearchRun run)
    {
        if (run.Error is not null) return $"The search failed: {run.Error}";
        if (run.Pages.Count == 0) return $"No results for \"{run.Query}\".";
        var sb = new StringBuilder();
        sb.Append("Search results for \"").Append(run.Query).Append("\":\n");
        var n = 1;
        foreach (var p in run.Pages)
        {
            sb.Append('\n').Append('[').Append(n++).Append("] ").Append(p.Title).Append('\n').Append("URL: ").Append(p.Url).Append('\n');
            if (p.Age.Length > 0) sb.Append("Date: ").Append(p.Age).Append('\n');
            if (p.Text.Length > 0) sb.Append(p.Text).Append('\n');
        }
        return sb.ToString();
    }

    protected static JsonObject QuerySchema(string type = "object", string stringType = "string") => new()
    {
        ["type"] = type,
        ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = stringType, ["description"] = "The search query" } },
        ["required"] = new JsonArray("query"),
    };

    protected static string QueryOf(JsonNode? args)
    {
        if (args is JsonValue v && v.TryGetValue<string>(out var text))
        {
            try { args = JsonNode.Parse(text); }
            catch (JsonException) { return text; }
        }
        return (args?["query"] ?? args?["q"])?.GetValue<string>() ?? "";
    }

    protected static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..24];

    protected static JsonArray Clone(JsonArray a) => (JsonArray)a.DeepClone();

    /// <summary>One SSE frame.</summary>
    protected static string Sse(string? eventName, JsonNode data) =>
        (eventName is null ? "" : $"event: {eventName}\n") + "data: " + data.ToJsonString(GatewayJson.Options) + "\n\n";

    protected static long Seconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

// ======================================================================= Anthropic Messages

public sealed class AnthropicSearchDialect : SearchDialect
{
    public override string Path => "/v1/messages";

    private static bool IsSearchTool(JsonNode? t) => t is JsonObject o && o["type"] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("web_search", StringComparison.Ordinal);

    public override bool Detect(JsonObject body) => body["tools"] is JsonArray tools && tools.Any(IsSearchTool);

    public override void Rewrite(JsonObject body)
    {
        var tools = (JsonArray)body["tools"]!;
        for (var i = 0; i < tools.Count; i++)
            if (IsSearchTool(tools[i]))
                tools[i] = new JsonObject { ["name"] = ToolName, ["description"] = Description, ["input_schema"] = QuerySchema() };
        // A search from an earlier turn was Astra's: the upstream never saw it as a server tool, so it must not find its blocks.
        if (body["messages"] is JsonArray messages)
        {
            for (var i = messages.Count - 1; i >= 0; i--)
            {
                if (messages[i] is not JsonObject m || m["content"] is not JsonArray content) continue;
                for (var j = content.Count - 1; j >= 0; j--)
                    if (content[j]?["type"]?.GetValue<string>() is "server_tool_use" or "web_search_tool_result") content.RemoveAt(j);
                if (content.Count == 0) messages.RemoveAt(i);
            }
        }
        if (body["tool_choice"] is JsonObject choice && choice["type"]?.GetValue<string>() == "tool") choice["name"] = ToolName;
        body["stream"] = false;
    }

    public override List<SearchCall> Calls(JsonObject reply) =>
        (reply["content"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(b => b["type"]?.GetValue<string>() == "tool_use" && b["name"]?.GetValue<string>() == ToolName)
            .Select(b => new SearchCall(b["id"]?.GetValue<string>() ?? NewId("toolu_"), QueryOf(b["input"]))).ToList();

    public override bool HasOtherCalls(JsonObject reply) =>
        (reply["content"] as JsonArray ?? []).OfType<JsonObject>().Any(b => b["type"]?.GetValue<string>() == "tool_use" && b["name"]?.GetValue<string>() != ToolName);

    public override JsonArray Kept(JsonObject reply)
    {
        var kept = new JsonArray();
        foreach (var b in reply["content"] as JsonArray ?? [])
            if (!(b?["type"]?.GetValue<string>() == "tool_use" && b["name"]?.GetValue<string>() == ToolName)) kept.Push(b!.DeepClone());
        return kept;
    }

    public override void Continue(JsonObject body, JsonObject reply, List<SearchRun> runs)
    {
        var messages = (JsonArray)body["messages"]!;
        messages.Push(new JsonObject { ["role"] = "assistant", ["content"] = Clone((JsonArray)reply["content"]!) });
        var results = new JsonArray();
        foreach (var r in runs)
            results.Push(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = r.Id, ["content"] = ResultText(r), ["is_error"] = r.Error is not null ? true : null });
        messages.Push(new JsonObject { ["role"] = "user", ["content"] = results });
    }

    public override void Decorate(JsonObject reply, List<SearchRound> rounds, bool stripCalls)
    {
        var content = new JsonArray();
        foreach (var round in rounds)
        {
            foreach (var b in round.Kept) content.Push(b!.DeepClone());
            foreach (var run in round.Runs)
            {
                var id = NewId("srvtoolu_");
                content.Push(new JsonObject { ["type"] = "server_tool_use", ["id"] = id, ["name"] = "web_search", ["input"] = new JsonObject { ["query"] = run.Query } });
                var inner = new JsonArray();
                JsonNode result = inner;
                if (run.Error is not null) result = new JsonObject { ["type"] = "web_search_tool_result_error", ["error_code"] = "unavailable" };
                else
                    foreach (var p in run.Pages)
                        inner.Push(new JsonObject { ["type"] = "web_search_result", ["url"] = p.Url, ["title"] = p.Title, ["encrypted_content"] = "", ["page_age"] = p.Age.Length > 0 ? p.Age : null });
                content.Push(new JsonObject { ["type"] = "web_search_tool_result", ["tool_use_id"] = id, ["content"] = result });
            }
        }
        foreach (var b in (reply["content"] as JsonArray ?? []).ToList())
            if (!(stripCalls && b?["type"]?.GetValue<string>() == "tool_use" && b["name"]?.GetValue<string>() == ToolName)) content.Push(b!.DeepClone());
        reply["content"] = content;
        var searches = rounds.Sum(r => r.Runs.Count);
        if (reply["usage"] is JsonObject usage) usage["server_tool_use"] = new JsonObject { ["web_search_requests"] = searches };
    }

    public override IEnumerable<string> Stream(JsonObject reply, JsonObject request, bool altSse)
    {
        var usage = reply["usage"] as JsonObject;
        var start = new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = reply["id"]?.DeepClone(), ["type"] = "message", ["role"] = "assistant", ["model"] = reply["model"]?.DeepClone(),
                ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = usage?["input_tokens"]?.DeepClone() ?? 0, ["output_tokens"] = 0 },
            },
        };
        yield return Sse("message_start", start);
        var index = 0;
        foreach (var block in reply["content"] as JsonArray ?? [])
        {
            if (block is not JsonObject b) continue;
            var type = b["type"]?.GetValue<string>();
            var shell = (JsonObject)b.DeepClone();
            JsonNode? delta = null;
            switch (type)
            {
                case "text":
                    shell["text"] = "";
                    delta = new JsonObject { ["type"] = "text_delta", ["text"] = b["text"]?.DeepClone() ?? "" };
                    break;
                case "thinking":
                    shell["thinking"] = "";
                    delta = new JsonObject { ["type"] = "thinking_delta", ["thinking"] = b["thinking"]?.DeepClone() ?? "" };
                    break;
                case "tool_use" or "server_tool_use":
                    shell["input"] = new JsonObject();
                    delta = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = (b["input"] ?? new JsonObject()).ToJsonString() };
                    break;
            }
            yield return Sse("content_block_start", new JsonObject { ["type"] = "content_block_start", ["index"] = index, ["content_block"] = shell });
            if (delta is not null) yield return Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = delta });
            if (type == "thinking" && b["signature"] is { } sig)
                yield return Sse("content_block_delta", new JsonObject { ["type"] = "content_block_delta", ["index"] = index, ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = sig.DeepClone() } });
            yield return Sse("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = index });
            index++;
        }
        var finalUsage = usage is null ? new JsonObject() : (JsonObject)usage.DeepClone();
        yield return Sse("message_delta", new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = reply["stop_reason"]?.DeepClone(), ["stop_sequence"] = reply["stop_sequence"]?.DeepClone() },
            ["usage"] = finalUsage,
        });
        yield return Sse("message_stop", new JsonObject { ["type"] = "message_stop" });
    }
}

// ======================================================================= OpenAI Responses

public sealed class ResponsesSearchDialect : SearchDialect
{
    public override string Path => "/v1/responses";

    private static bool IsSearchTool(JsonNode? t) => t is JsonObject o && o["type"] is JsonValue v && v.TryGetValue<string>(out var s) && s.StartsWith("web_search", StringComparison.Ordinal);

    // A request continuing a stored response cannot be continued by appending items: it is left to the upstream.
    public override bool Detect(JsonObject body) => body["tools"] is JsonArray tools && tools.Any(IsSearchTool) && body["previous_response_id"] is null;

    public override void Rewrite(JsonObject body)
    {
        var tools = (JsonArray)body["tools"]!;
        for (var i = 0; i < tools.Count; i++)
            if (IsSearchTool(tools[i]))
                tools[i] = new JsonObject { ["type"] = "function", ["name"] = ToolName, ["description"] = Description, ["parameters"] = QuerySchema(), ["strict"] = false };
        if (body["tool_choice"] is JsonObject choice && choice["type"]?.GetValue<string>() is { } t && t.StartsWith("web_search", StringComparison.Ordinal))
            body["tool_choice"] = new JsonObject { ["type"] = "function", ["name"] = ToolName };
        if (body["include"] is JsonArray include)
            for (var i = include.Count - 1; i >= 0; i--)
                if (include[i]?.GetValue<string>().StartsWith("web_search_call", StringComparison.Ordinal) == true) include.RemoveAt(i);
        if (body["input"] is JsonValue text && text.TryGetValue<string>(out var s))
            body["input"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = s });
        if (body["input"] is JsonArray input)
            for (var i = input.Count - 1; i >= 0; i--)
                if (input[i]?["type"]?.GetValue<string>() == "web_search_call") input.RemoveAt(i);
        body["stream"] = false;
    }

    private static IEnumerable<JsonObject> Output(JsonObject reply) => (reply["output"] as JsonArray ?? []).OfType<JsonObject>();

    private static bool IsOurCall(JsonObject item) => item["type"]?.GetValue<string>() == "function_call" && item["name"]?.GetValue<string>() == ToolName;

    public override List<SearchCall> Calls(JsonObject reply) =>
        Output(reply).Where(IsOurCall).Select(i => new SearchCall(i["call_id"]?.GetValue<string>() ?? NewId("call_"), QueryOf(i["arguments"]))).ToList();

    public override bool HasOtherCalls(JsonObject reply) =>
        Output(reply).Any(i => i["type"]?.GetValue<string>() == "function_call" && !IsOurCall(i));

    public override JsonArray Kept(JsonObject reply)
    {
        var kept = new JsonArray();
        foreach (var i in Output(reply)) if (!IsOurCall(i)) kept.Push(i.DeepClone());
        return kept;
    }

    public override void Continue(JsonObject body, JsonObject reply, List<SearchRun> runs)
    {
        var input = (JsonArray)body["input"]!;
        foreach (var item in Output(reply)) input.Push(item.DeepClone());
        foreach (var r in runs)
            input.Push(new JsonObject { ["type"] = "function_call_output", ["call_id"] = r.Id, ["output"] = ResultText(r) });
    }

    public override void Decorate(JsonObject reply, List<SearchRound> rounds, bool stripCalls)
    {
        var output = new JsonArray();
        foreach (var round in rounds)
        {
            foreach (var item in round.Kept) output.Push(item!.DeepClone());
            foreach (var run in round.Runs)
            {
                var sources = new JsonArray();
                foreach (var p in run.Pages) sources.Push(new JsonObject { ["type"] = "url", ["url"] = p.Url });
                output.Push(new JsonObject
                {
                    ["id"] = NewId("ws_"), ["type"] = "web_search_call", ["status"] = run.Error is null ? "completed" : "failed",
                    ["action"] = new JsonObject { ["type"] = "search", ["query"] = run.Query, ["sources"] = sources },
                });
            }
        }
        var citations = new JsonArray();
        foreach (var run in rounds.SelectMany(r => r.Runs))
            foreach (var p in run.Pages.Take(5)) citations.Push(new JsonObject { ["type"] = "url_citation", ["url"] = p.Url, ["title"] = p.Title, ["start_index"] = 0, ["end_index"] = 0 });
        foreach (var item in Output(reply).ToList())
        {
            if (stripCalls && IsOurCall(item)) continue;
            var copy = (JsonObject)item.DeepClone();
            // Where the protocol has citations, the pages the answer drew on: attached to the last text part of the final message.
            if (copy["type"]?.GetValue<string>() == "message" && copy["content"] is JsonArray parts && citations.Count > 0)
                foreach (var part in parts.OfType<JsonObject>().Where(p => p["type"]?.GetValue<string>() == "output_text"))
                {
                    var annotations = part["annotations"] as JsonArray ?? new JsonArray();
                    foreach (var c in citations) annotations.Push(c!.DeepClone());
                    part["annotations"] = annotations;
                }
            output.Push(copy);
        }
        reply["output"] = output;
    }

    public override IEnumerable<string> Stream(JsonObject reply, JsonObject request, bool altSse)
    {
        var seq = 0;
        JsonObject Event(string type, Action<JsonObject>? fill = null)
        {
            var e = new JsonObject { ["type"] = type, ["sequence_number"] = seq++ };
            fill?.Invoke(e);
            return e;
        }
        var shell = (JsonObject)reply.DeepClone();
        shell["output"] = new JsonArray();
        shell["status"] = "in_progress";
        shell.Remove("usage");
        yield return Sse("response.created", Event("response.created", e => e["response"] = shell.DeepClone()));
        yield return Sse("response.in_progress", Event("response.in_progress", e => e["response"] = shell.DeepClone()));
        var index = 0;
        foreach (var item in reply["output"] as JsonArray ?? [])
        {
            if (item is not JsonObject it) continue;
            var type = it["type"]?.GetValue<string>();
            var itemId = it["id"]?.GetValue<string>() ?? "";
            var added = (JsonObject)it.DeepClone();
            if (type == "message") { added["content"] = new JsonArray(); added["status"] = "in_progress"; }
            if (type == "function_call") { added["arguments"] = ""; added["status"] = "in_progress"; }
            var i = index;
            yield return Sse("response.output_item.added", Event("response.output_item.added", e => { e["output_index"] = i; e["item"] = added; }));
            if (type == "message")
            {
                var c = 0;
                foreach (var part in it["content"] as JsonArray ?? [])
                {
                    if (part is not JsonObject p) continue;
                    var ci = c++;
                    var text = p["text"]?.GetValue<string>() ?? "";
                    var partShell = (JsonObject)p.DeepClone();
                    partShell["text"] = "";
                    partShell["annotations"] = new JsonArray();
                    yield return Sse("response.content_part.added", Event("response.content_part.added", e => { e["item_id"] = itemId; e["output_index"] = i; e["content_index"] = ci; e["part"] = partShell; }));
                    yield return Sse("response.output_text.delta", Event("response.output_text.delta", e => { e["item_id"] = itemId; e["output_index"] = i; e["content_index"] = ci; e["delta"] = text; }));
                    yield return Sse("response.output_text.done", Event("response.output_text.done", e => { e["item_id"] = itemId; e["output_index"] = i; e["content_index"] = ci; e["text"] = text; }));
                    yield return Sse("response.content_part.done", Event("response.content_part.done", e => { e["item_id"] = itemId; e["output_index"] = i; e["content_index"] = ci; e["part"] = p.DeepClone(); }));
                }
            }
            else if (type == "function_call")
            {
                var args = it["arguments"]?.GetValue<string>() ?? "";
                yield return Sse("response.function_call_arguments.delta", Event("response.function_call_arguments.delta", e => { e["item_id"] = itemId; e["output_index"] = i; e["delta"] = args; }));
                yield return Sse("response.function_call_arguments.done", Event("response.function_call_arguments.done", e => { e["item_id"] = itemId; e["output_index"] = i; e["arguments"] = args; }));
            }
            yield return Sse("response.output_item.done", Event("response.output_item.done", e => { e["output_index"] = i; e["item"] = it.DeepClone(); }));
            index++;
        }
        var done = (JsonObject)reply.DeepClone();
        done["status"] = "completed";
        yield return Sse("response.completed", Event("response.completed", e => e["response"] = done));
    }
}

// ======================================================================= OpenAI Chat

public sealed class ChatSearchDialect : SearchDialect
{
    public override string Path => "/v1/chat/completions";

    public override bool Detect(JsonObject body) => body["web_search_options"] is not null;

    public override void Rewrite(JsonObject body)
    {
        body.Remove("web_search_options");
        var tools = body["tools"] as JsonArray ?? new JsonArray();
        tools.Push(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = ToolName, ["description"] = Description, ["parameters"] = QuerySchema() } });
        body["tools"] = tools;
        body["stream"] = false;
        body.Remove("stream_options");
    }

    private static JsonObject? Message(JsonObject reply) => reply["choices"]?[0]?["message"] as JsonObject;

    private static IEnumerable<JsonObject> ToolCalls(JsonObject reply) => (Message(reply)?["tool_calls"] as JsonArray ?? []).OfType<JsonObject>();

    private static bool IsOurCall(JsonObject c) => c["function"]?["name"]?.GetValue<string>() == ToolName;

    public override List<SearchCall> Calls(JsonObject reply) =>
        ToolCalls(reply).Where(IsOurCall).Select(c => new SearchCall(c["id"]?.GetValue<string>() ?? NewId("call_"), QueryOf(c["function"]?["arguments"]))).ToList();

    public override bool HasOtherCalls(JsonObject reply) => ToolCalls(reply).Any(c => !IsOurCall(c));

    public override JsonArray Kept(JsonObject reply) => new();

    public override void Continue(JsonObject body, JsonObject reply, List<SearchRun> runs)
    {
        var messages = (JsonArray)body["messages"]!;
        messages.Push(Message(reply)!.DeepClone());
        foreach (var r in runs) messages.Push(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.Id, ["content"] = ResultText(r) });
    }

    public override void Decorate(JsonObject reply, List<SearchRound> rounds, bool stripCalls)
    {
        if (Message(reply) is not { } message) return;
        if (stripCalls && message["tool_calls"] is JsonArray calls)
        {
            for (var i = calls.Count - 1; i >= 0; i--) if (calls[i] is JsonObject c && IsOurCall(c)) calls.RemoveAt(i);
            if (calls.Count == 0) message.Remove("tool_calls");
        }
        var annotations = message["annotations"] as JsonArray ?? new JsonArray();
        var length = message["content"] is JsonValue v && v.TryGetValue<string>(out var text) ? text.Length : 0;
        foreach (var p in rounds.SelectMany(r => r.Runs).SelectMany(r => r.Pages).Take(8))
            annotations.Push(new JsonObject { ["type"] = "url_citation", ["url_citation"] = new JsonObject { ["url"] = p.Url, ["title"] = p.Title, ["start_index"] = 0, ["end_index"] = length } });
        if (annotations.Count > 0) message["annotations"] = annotations;
    }

    public override IEnumerable<string> Stream(JsonObject reply, JsonObject request, bool altSse)
    {
        var id = reply["id"]?.GetValue<string>() ?? NewId("chatcmpl-");
        var model = reply["model"]?.DeepClone();
        var created = reply["created"]?.DeepClone() ?? Seconds();
        JsonObject Chunk(JsonObject delta, string? finish) => new()
        {
            ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = created.DeepClone(), ["model"] = model?.DeepClone(),
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
        };
        var message = Message(reply) ?? new JsonObject();
        yield return Sse(null, Chunk(new JsonObject { ["role"] = "assistant", ["content"] = "" }, null));
        if (message["reasoning_content"] is JsonValue rc && rc.TryGetValue<string>(out var reasoning) && reasoning.Length > 0)
            yield return Sse(null, Chunk(new JsonObject { ["reasoning_content"] = reasoning }, null));
        if (message["content"] is JsonValue c && c.TryGetValue<string>(out var content) && content.Length > 0)
            yield return Sse(null, Chunk(new JsonObject { ["content"] = content }, null));
        var n = 0;
        foreach (var call in ToolCalls(reply))
        {
            var d = new JsonObject
            {
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["index"] = n++, ["id"] = call["id"]?.DeepClone(), ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = call["function"]?["name"]?.DeepClone(), ["arguments"] = call["function"]?["arguments"]?.DeepClone() ?? "{}" },
                }),
            };
            yield return Sse(null, Chunk(d, null));
        }
        if (message["annotations"] is JsonArray ann && ann.Count > 0)
            yield return Sse(null, Chunk(new JsonObject { ["annotations"] = ann.DeepClone() }, null));
        var last = Chunk(new JsonObject(), reply["choices"]?[0]?["finish_reason"]?.GetValue<string>() ?? "stop");
        yield return Sse(null, last);
        if (request["stream_options"]?["include_usage"]?.GetValue<bool>() == true && reply["usage"] is { } usage)
        {
            var u = Chunk(new JsonObject(), null);
            u["choices"] = new JsonArray();
            u["usage"] = usage.DeepClone();
            yield return Sse(null, u);
        }
        yield return "data: [DONE]\n\n";
    }
}

// ======================================================================= Gemini

public sealed class GeminiSearchDialect : SearchDialect
{
    // The path is the generateContent one; the model is in the URL, which the bridge passes by itself.
    public override string Path => "/v1beta/models/x:generateContent";

    private static readonly string[] SearchKeys = ["google_search", "googleSearch", "google_search_retrieval", "googleSearchRetrieval"];

    private static bool IsSearchTool(JsonNode? t) => t is JsonObject o && SearchKeys.Any(o.ContainsKey);

    public override bool Detect(JsonObject body) => body["tools"] is JsonArray tools && tools.Any(IsSearchTool);

    public override void Rewrite(JsonObject body)
    {
        var tools = (JsonArray)body["tools"]!;
        var declaration = new JsonObject
        {
            ["name"] = ToolName, ["description"] = Description,
            ["parameters"] = QuerySchema("OBJECT", "STRING"),
        };
        var at = tools.Select((t, i) => (t, i)).First(x => IsSearchTool(x.t)).i;
        for (var i = tools.Count - 1; i >= 0; i--) if (IsSearchTool(tools[i])) tools.RemoveAt(i);
        // Gemini takes the declarations of all function tools in one entry, or one per entry: a new entry is always valid.
        tools.Insert(Math.Min(at, tools.Count), new JsonObject { ["functionDeclarations"] = new JsonArray(declaration) });
    }

    private static JsonObject? Content(JsonObject reply) => reply["candidates"]?[0]?["content"] as JsonObject;

    private static IEnumerable<JsonObject> Parts(JsonObject reply) => (Content(reply)?["parts"] as JsonArray ?? []).OfType<JsonObject>();

    private static JsonObject? Call(JsonObject part) => (part["functionCall"] ?? part["function_call"]) as JsonObject;

    private static bool IsOurCall(JsonObject part) => Call(part)?["name"]?.GetValue<string>() == ToolName;

    public override List<SearchCall> Calls(JsonObject reply) =>
        Parts(reply).Where(IsOurCall).Select(p => new SearchCall(Call(p)!["id"]?.GetValue<string>() ?? "", QueryOf(Call(p)!["args"]))).ToList();

    public override bool HasOtherCalls(JsonObject reply) => Parts(reply).Any(p => Call(p) is not null && !IsOurCall(p));

    public override JsonArray Kept(JsonObject reply) => new();

    public override void Continue(JsonObject body, JsonObject reply, List<SearchRun> runs)
    {
        var contents = (JsonArray)body["contents"]!;
        contents.Push(Content(reply)!.DeepClone());
        var parts = new JsonArray();
        foreach (var r in runs)
        {
            var response = new JsonObject { ["name"] = ToolName, ["response"] = new JsonObject { ["content"] = ResultText(r) } };
            if (r.Id.Length > 0) response["id"] = r.Id;
            parts.Push(new JsonObject { ["functionResponse"] = response });
        }
        contents.Push(new JsonObject { ["role"] = "user", ["parts"] = parts });
    }

    public override void Decorate(JsonObject reply, List<SearchRound> rounds, bool stripCalls)
    {
        if (reply["candidates"]?[0] is not JsonObject candidate) return;
        if (stripCalls && Content(reply)?["parts"] is JsonArray parts)
            for (var i = parts.Count - 1; i >= 0; i--) if (parts[i] is JsonObject p && IsOurCall(p)) parts.RemoveAt(i);
        var chunks = new JsonArray();
        foreach (var p in rounds.SelectMany(r => r.Runs).SelectMany(r => r.Pages))
            chunks.Push(new JsonObject { ["web"] = new JsonObject { ["uri"] = p.Url, ["title"] = p.Title } });
        candidate["groundingMetadata"] = new JsonObject
        {
            ["webSearchQueries"] = new JsonArray(rounds.SelectMany(r => r.Runs).Select(r => (JsonNode)JsonValue.Create(r.Query)!).ToArray()),
            ["groundingChunks"] = chunks,
        };
    }

    public override IEnumerable<string> Stream(JsonObject reply, JsonObject request, bool altSse)
    {
        // Gemini's stream is a series of whole responses: one is enough.
        yield return altSse ? Sse(null, reply) : new JsonArray(reply.DeepClone()).ToJsonString(GatewayJson.Options);
    }
}
