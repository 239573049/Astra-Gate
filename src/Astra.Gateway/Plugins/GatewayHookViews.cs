using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;
using Astra.Plugins.Middleware;

namespace Astra.Gateway.Plugins;

/// <summary>
/// The protocol-free view v2 hook sets with <c>view: "ir"</c> get (design/plugin-v2 02 §3), built on the gateway's codecs.
/// A request reads as <c>{ model, stream, system: [parts], messages: [{ role, content: [parts] }], tools, params }</c>:
/// text, tool calls and tool results are plain fields a plugin edits; images, files and reasoning come as
/// <c>{ type, ref }</c> placeholders that keep the original when the plugin leaves them in. A request the hook didn't
/// change keeps its own bytes; a changed one is re-encoded in its protocol (the codec's own mapping, so a field that
/// protocol can't carry is lost — Ir.cs lists those). A reply reads as <c>{ content: [{ type: "text", text }] }</c> and
/// its texts are written back in place, so nothing else in it changes.
/// </summary>
public sealed class GatewayHookViews(CodecRegistry codecs) : IHookViews
{
    public byte[]? RequestView(byte[] body, string protocol)
    {
        if (Decode(body, protocol) is not { } ir) return null;
        var view = new JsonObject
        {
            ["model"] = ir.Model,
            ["stream"] = ir.Stream,
            ["system"] = Parts(ir.System, "s"),
            ["messages"] = new JsonArray([.. ir.Messages.Select((m, i) => (JsonNode?)new JsonObject
            {
                ["role"] = m.Role == MessageRole.User ? "user" : "assistant",
                ["content"] = Parts(m.Parts, "m" + i + "."),
            })]),
            ["tools"] = new JsonArray([.. ir.Tools.Select(t => (JsonNode?)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema?.DeepClone(),
            })]),
            ["params"] = new JsonObject
            {
                ["temperature"] = ir.Temperature,
                ["topP"] = ir.TopP,
                ["topK"] = ir.TopK,
                ["maxOutputTokens"] = ir.MaxOutputTokens,
                ["stop"] = ir.Stop is null ? null : new JsonArray([.. ir.Stop.Select(s => (JsonNode?)s)]),
            },
        };
        return Encoding.UTF8.GetBytes(view.ToJsonString());
    }

    public byte[]? ApplyRequestView(byte[] body, string protocol, byte[] view, byte[] changed)
    {
        JsonObject next;
        try
        {
            if (JsonNode.DeepEquals(JsonNode.Parse(view), JsonNode.Parse(changed))) return null;
            next = JsonNode.Parse(changed) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException)
        {
            return null;
        }
        if (Decode(body, protocol) is not { } ir || Codec(protocol) is not { } codec) return null;
        var refs = new Dictionary<string, ContentPart>();
        Index(ir.System, "s", refs);
        for (var i = 0; i < ir.Messages.Count; i++) Index(ir.Messages[i].Parts, "m" + i + ".", refs);

        if (next["model"] is JsonValue mv && mv.TryGetValue<string>(out var model) && model.Length > 0) ir.Model = model;
        if (next["system"] is JsonArray system) ir.System = FromParts(system, refs);
        if (next["messages"] is JsonArray messages)
            ir.Messages = [.. messages.OfType<JsonObject>().Select(m => new UnifiedMessage(
                m["role"]?.GetValue<string>() == "assistant" ? MessageRole.Assistant : MessageRole.User,
                FromParts(m["content"] as JsonArray ?? [], refs)))];
        if (next["tools"] is JsonArray tools)
        {
            var before = ir.Tools.ToDictionary(t => t.Name);
            ir.Tools = [.. tools.OfType<JsonObject>().Select(t =>
            {
                var name = t["name"]?.GetValue<string>() ?? "";
                var description = t["description"]?.GetValue<string>();
                var schema = t["inputSchema"]?.DeepClone();
                if (before.TryGetValue(name, out var old) && old.Description == description && JsonNode.DeepEquals(old.InputSchema, schema)) return old;
                return new UnifiedTool
                {
                    Name = name, Description = description, InputSchema = schema,
                    Strict = old?.Strict, BuiltinType = old?.BuiltinType, BuiltinOrigin = old?.BuiltinOrigin, BuiltinRaw = old?.BuiltinRaw,
                };
            })];
        }
        if (next["params"] is JsonObject p)
        {
            ir.Temperature = Num(p["temperature"]);
            ir.TopP = Num(p["topP"]);
            ir.TopK = Num(p["topK"]) is { } k ? (int)k : null;
            ir.MaxOutputTokens = Num(p["maxOutputTokens"]) is { } mx ? (int)mx : null;
            ir.Stop = p["stop"] is JsonArray stop ? [.. stop.Select(s => s?.GetValue<string>()).OfType<string>()] : null;
        }
        var encoded = codec.EncodeRequest(ir, new RequestEncodeContext { UpstreamModel = ir.Model, AutoCacheControl = false });
        return Encoding.UTF8.GetBytes(encoded.ToJsonString());
    }

    public byte[]? ResponseView(byte[] body, string protocol)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
        if (root is not JsonObject o) return null;
        var texts = Texts(protocol, o);
        var view = new JsonObject { ["content"] = new JsonArray([.. texts.Select(t => (JsonNode?)new JsonObject { ["type"] = "text", ["text"] = t.Get() })]) };
        return Encoding.UTF8.GetBytes(view.ToJsonString());
    }

    public byte[]? ApplyResponseView(byte[] body, string protocol, byte[] view, byte[] changed)
    {
        try
        {
            if (JsonNode.Parse(body) is not JsonObject o || JsonNode.Parse(changed)?["content"] is not JsonArray content) return null;
            var texts = Texts(protocol, o);
            var any = false;
            for (var i = 0; i < Math.Min(texts.Count, content.Count); i++)
            {
                var text = content[i]?["text"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : null;
                if (text is null || text == texts[i].Get()) continue;
                texts[i].Set(text);
                any = true;
            }
            return any ? Encoding.UTF8.GetBytes(o.ToJsonString()) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- request

    private IProtocolCodec? Codec(string protocol) => protocol switch
    {
        "chat" => codecs.Get(ApiProtocol.OpenAIChat),
        "responses" => codecs.Get(ApiProtocol.OpenAIResponses),
        "anthropic" => codecs.Get(ApiProtocol.Anthropic),
        "gemini" => codecs.Get(ApiProtocol.Gemini),
        _ => null,
    };

    private UnifiedRequest? Decode(byte[] body, string protocol)
    {
        if (Codec(protocol) is not { } codec) return null;
        try
        {
            return JsonNode.Parse(body) is JsonObject o ? codec.DecodeRequest(o, new RequestDecodeContext()) : null;
        }
        catch (Exception e) when (e is JsonException or ProtocolException or InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonArray Parts(IReadOnlyList<ContentPart> parts, string prefix)
    {
        var a = new JsonArray();
        for (var i = 0; i < parts.Count; i++)
        {
            JsonObject node = parts[i] switch
            {
                TextPart t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
                ToolCallPart c => new JsonObject { ["type"] = "tool_call", ["id"] = c.Id, ["name"] = c.Name, ["arguments"] = Args(c.ArgumentsJson) },
                ToolResultPart r => new JsonObject
                {
                    ["type"] = "tool_result", ["callId"] = r.CallId, ["name"] = r.Name, ["isError"] = r.IsError,
                    ["content"] = Parts(r.Content, prefix + i + "r"),
                },
                ImagePart => new JsonObject { ["type"] = "image", ["ref"] = prefix + i },
                FilePart => new JsonObject { ["type"] = "file", ["ref"] = prefix + i },
                ReasoningPart rp => new JsonObject { ["type"] = "reasoning", ["text"] = rp.Text, ["ref"] = prefix + i },
                _ => new JsonObject { ["type"] = "other", ["ref"] = prefix + i },
            };
            a.Add((JsonNode)node);
        }
        return a;
    }

    private static JsonNode? Args(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void Index(IReadOnlyList<ContentPart> parts, string prefix, Dictionary<string, ContentPart> refs)
    {
        for (var i = 0; i < parts.Count; i++)
        {
            refs[prefix + i] = parts[i];
            if (parts[i] is ToolResultPart r) Index(r.Content, prefix + i + "r", refs);
        }
    }

    private static List<ContentPart> FromParts(JsonArray parts, Dictionary<string, ContentPart> refs)
    {
        var list = new List<ContentPart>();
        foreach (var p in parts.OfType<JsonObject>())
        {
            var type = p["type"]?.GetValue<string>();
            var reference = p["ref"]?.GetValue<string>();
            switch (type)
            {
                case "text":
                    list.Add(new TextPart(p["text"]?.GetValue<string>() ?? ""));
                    break;
                case "tool_call":
                    list.Add(new ToolCallPart(p["id"]?.GetValue<string>() ?? "", p["name"]?.GetValue<string>() ?? "",
                        p["arguments"] is JsonValue av && av.TryGetValue<string>(out var s) ? s : p["arguments"]?.ToJsonString() ?? "{}"));
                    break;
                case "tool_result":
                    list.Add(new ToolResultPart(p["callId"]?.GetValue<string>() ?? "", p["name"]?.GetValue<string>(),
                        FromParts(p["content"] as JsonArray ?? [], refs), p["isError"] is JsonValue ev && ev.TryGetValue<bool>(out var e) && e));
                    break;
                case "reasoning" when reference is not null && refs.TryGetValue(reference, out var original) && original is ReasoningPart rp:
                    var text = p["text"]?.GetValue<string>();
                    list.Add(text == rp.Text ? rp : rp with { Text = text, Signature = null, EncryptedContent = null });
                    break;
                default:
                    if (reference is not null && refs.TryGetValue(reference, out var kept)) list.Add(kept);
                    break;
            }
        }
        return list;
    }

    private static double? Num(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    // ---- reply

    private sealed record Slot(Func<string> Get, Action<string> Set);

    private static List<Slot> Texts(string protocol, JsonObject o)
    {
        var slots = new List<Slot>();
        switch (protocol)
        {
            case "chat":
                foreach (var c in (o["choices"] as JsonArray ?? []).OfType<JsonObject>())
                    if (c["message"] is JsonObject m && m["content"] is JsonValue cv && cv.TryGetValue<string>(out _))
                        slots.Add(new Slot(() => m["content"]!.GetValue<string>(), t => m["content"] = t));
                break;
            case "anthropic":
                foreach (var b in (o["content"] as JsonArray ?? []).OfType<JsonObject>())
                    if (b["type"]?.GetValue<string>() == "text")
                        slots.Add(new Slot(() => b["text"]?.GetValue<string>() ?? "", t => b["text"] = t));
                break;
            case "responses":
                foreach (var item in (o["output"] as JsonArray ?? []).OfType<JsonObject>())
                foreach (var c in (item["content"] as JsonArray ?? []).OfType<JsonObject>())
                    if (c["type"]?.GetValue<string>() == "output_text")
                        slots.Add(new Slot(() => c["text"]?.GetValue<string>() ?? "", t => c["text"] = t));
                break;
            case "gemini":
                foreach (var cand in (o["candidates"] as JsonArray ?? []).OfType<JsonObject>())
                foreach (var part in (cand["content"]?["parts"] as JsonArray ?? []).OfType<JsonObject>())
                    if (part["text"] is JsonValue && !(part["thought"] is JsonValue th && th.GetValueKind() == JsonValueKind.True))
                        slots.Add(new Slot(() => part["text"]!.GetValue<string>(), t => part["text"] = t));
                break;
        }
        return slots;
    }
}
