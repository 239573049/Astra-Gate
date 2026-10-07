using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Protocol.Chat;

/// <summary>
/// OpenAI Chat Completions wire protocol (POST /v1/chat/completions). Stateless singleton; per-response
/// state lives in <see cref="ChatResponseDecoder"/> / <see cref="ChatResponseEncoder"/>.
/// </summary>
public sealed class ChatCodec : IProtocolCodec
{
    public ApiProtocol Protocol => ApiProtocol.OpenAIChat;

    // ---------------------------------------------------------------- request decode

    /// <summary>Top-level fields this codec maps into the IR; everything else is preserved verbatim in Extensions.</summary>
    private static readonly HashSet<string> KnownRequestFields =
    [
        "model", "messages", "tools", "tool_choice", "parallel_tool_calls", "temperature", "top_p", "stop",
        "max_completion_tokens", "max_tokens", "reasoning_effort", "response_format", "stream", "user",
        "metadata", "service_tier",
    ];

    public UnifiedRequest DecodeRequest(JsonObject body, RequestDecodeContext ctx)
    {
        var model = body["model"] is JsonValue m && m.TryGetValue<string>(out var name) && name.Length > 0
            ? name
            : throw new ProtocolException("请求缺少 model。");
        if (body["messages"] is not JsonArray rawMessages)
            throw new ProtocolException("请求缺少 messages。");

        var request = new UnifiedRequest { Model = model };
        var extensions = new JsonObject();

        foreach (var field in body)
        {
            if (!KnownRequestFields.Contains(field.Key)) extensions[field.Key] = field.Value?.DeepClone();
        }

        foreach (var node in rawMessages)
        {
            if (node is not JsonObject msg) continue;
            var role = msg["role"] is JsonValue v && v.TryGetValue<string>(out var r) ? r : "";
            switch (role)
            {
                case "system":
                case "developer":
                    AddText(request.System, msg["content"]);
                    break;
                case "user":
                    request.Messages.Add(new UnifiedMessage(MessageRole.User, DecodeUserContent(msg["content"], request.Warnings)));
                    break;
                case "assistant":
                    request.Messages.Add(DecodeAssistant(msg, request.Warnings));
                    break;
                case "tool":
                    DecodeToolMessage(request, msg);
                    break;
                default:
                    request.Warnings.Add($"无法识别的消息角色「{role}」，该消息已被丢弃。");
                    break;
            }
        }

        request.Tools = DecodeTools(body["tools"]);
        request.ToolChoice = DecodeToolChoice(body["tool_choice"]);
        if (body["parallel_tool_calls"] is JsonValue ptc && ptc.TryGetValue<bool>(out var parallel)) request.ParallelToolCalls = parallel;
        request.Temperature = DoubleOrNull(body["temperature"]);
        request.TopP = DoubleOrNull(body["top_p"]);
        request.Stop = DecodeStop(body["stop"]);
        request.MaxOutputTokens = IntOrNull(body["max_completion_tokens"]) ?? IntOrNull(body["max_tokens"]);
        request.Reasoning = DecodeReasoning(body["reasoning_effort"]);
        request.ResponseFormat = DecodeResponseFormat(body["response_format"]);
        if (body["stream"] is JsonValue s && s.TryGetValue<bool>(out var stream)) request.Stream = stream;
        request.User = body["user"] is JsonValue u && u.TryGetValue<string>(out var user) ? user : null;
        request.Metadata = body["metadata"] as JsonObject;
        request.ServiceTier = body["service_tier"] is JsonValue t && t.TryGetValue<string>(out var tier) ? tier : null;

        // DecodeRequest only runs on the conversion path: everything below is dropped by the other encoders.
        if (IntOrNull(body["n"]) is { } n && n > 1)
            request.Warnings.Add($"Chat 的 n={n} 多候选仅同协议支持，转换路径只保留第一个候选。");
        var chatOnly = new[] { "frequency_penalty", "presence_penalty", "logprobs", "top_logprobs", "seed" }
            .Where(body.ContainsKey).ToArray();
        if (chatOnly.Length > 0)
            request.Warnings.Add($"Chat 专有字段 {string.Join("、", chatOnly)} 无法映射到其他协议，转换时已丢弃。");

        if (extensions.Count > 0) request.Extensions[ApiProtocol.OpenAIChat] = extensions;
        return request;
    }

    /// <summary>System texts (content may be a string or a parts array with text parts only).</summary>
    private static void AddText(List<ContentPart> target, JsonNode? content)
    {
        foreach (var text in TextsOf(content)) target.Add(new TextPart(text));
    }

    /// <summary>Collects plain text from a string content or a parts array ("text" and "refusal" parts).</summary>
    private static IEnumerable<string> TextsOf(JsonNode? content)
    {
        switch (content)
        {
            case JsonValue v when v.TryGetValue<string>(out var text):
                yield return text;
                break;
            case JsonArray parts:
                foreach (var part in parts)
                {
                    if (part is not JsonObject o) continue;
                    var type = o["type"] is JsonValue t && t.TryGetValue<string>(out var ty) ? ty : null;
                    if (type is "text" or "refusal" && o[type] is JsonValue tv && tv.TryGetValue<string>(out var s))
                        yield return s;
                }
                break;
        }
    }

    private static List<ContentPart> DecodeUserContent(JsonNode? content, List<string> warnings)
    {
        var parts = new List<ContentPart>();
        if (content is JsonValue plain && plain.TryGetValue<string>(out var plainText))
        {
            if (plainText.Length > 0) parts.Add(new TextPart(plainText));
            return parts;
        }
        if (content is not JsonArray rawParts) return parts;

        foreach (var node in rawParts)
        {
            if (node is not JsonObject part) continue;
            switch (StringOrNull(part, "type"))
            {
                case "text":
                    if (StringOrNull(part, "text") is { Length: > 0 } text) parts.Add(new TextPart(text));
                    break;
                case "image_url" when part["image_url"] is JsonObject image:
                    parts.Add(DecodeImage(image));
                    break;
                case "file" when part["file"] is JsonObject file:
                    parts.Add(DecodeFile(file));
                    break;
                case "input_audio":
                    // The IR has no audio input part yet; the audio is dropped rather than mis-sent as text.
                    warnings.Add("user 消息中的 input_audio 音频无法映射到其他协议，已丢弃。");
                    break;
            }
        }
        return parts;
    }

    private static ImagePart DecodeImage(JsonObject image)
    {
        var url = StringOrNull(image, "url") ?? "";
        string? mediaType = null, base64 = null;
        if (url.StartsWith("data:", StringComparison.Ordinal))
        {
            // data:<mediatype>;base64,<payload>
            var comma = url.IndexOf(',');
            if (comma > 0)
            {
                var meta = url[5..comma];
                base64 = url[(comma + 1)..];
                mediaType = meta.EndsWith(";base64", StringComparison.Ordinal) ? meta[..^7] : meta;
                url = "";
            }
        }
        return new ImagePart(
            url.Length > 0 ? url : null,
            mediaType,
            base64,
            StringOrNull(image, "detail"));
    }

    private static FilePart DecodeFile(JsonObject file)
    {
        var fileData = StringOrNull(file, "file_data");
        string? url = null, mediaType = null, base64 = null;
        if (fileData is { } data)
        {
            if (data.StartsWith("data:", StringComparison.Ordinal))
            {
                var comma = data.IndexOf(',');
                if (comma > 0)
                {
                    var meta = data[5..comma];
                    base64 = data[(comma + 1)..];
                    mediaType = meta.EndsWith(";base64", StringComparison.Ordinal) ? meta[..^7] : meta;
                }
                else
                {
                    base64 = data;
                }
            }
            else if (data.StartsWith("http://", StringComparison.Ordinal) || data.StartsWith("https://", StringComparison.Ordinal))
            {
                url = data;
            }
            else
            {
                base64 = data;
            }
        }
        return new FilePart(url, mediaType, base64, StringOrNull(file, "filename"), StringOrNull(file, "file_id"));
    }

    private static UnifiedMessage DecodeAssistant(JsonObject msg, List<string> warnings)
    {
        var parts = new List<ContentPart>();
        // Reasoning travels first: it precedes the answer text the model produced (DeepSeek/Kimi reasoning_content).
        var reasoning = StringOrNull(msg, "reasoning_content") ?? StringOrNull(msg, "reasoning");
        if (reasoning is { Length: > 0 }) parts.Add(new ReasoningPart(reasoning, Origin: ApiProtocol.OpenAIChat));
        foreach (var text in TextsOf(msg["content"]))
        {
            if (text.Length > 0) parts.Add(new TextPart(text));
        }
        if (msg["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var node in toolCalls)
            {
                if (node is not JsonObject call) continue;
                var function = call["function"] as JsonObject ?? new JsonObject();
                var args = StringOrNull(function, "arguments");
                parts.Add(new ToolCallPart(
                    StringOrNull(call, "id") ?? Ulid.NewUlid(),
                    StringOrNull(function, "name") ?? "",
                    string.IsNullOrEmpty(args) ? "{}" : args));
            }
        }
        if (msg["audio"] is not null || msg["function_call"] is not null)
            warnings.Add("assistant 消息中的 audio/function_call 字段无法映射，已丢弃。");
        return new UnifiedMessage(MessageRole.Assistant, parts);
    }

    /// <summary>Tool results become ToolResultParts; consecutive tool messages merge into one User message.</summary>
    private static void DecodeToolMessage(UnifiedRequest request, JsonObject msg)
    {
        var content = new List<ContentPart>();
        foreach (var text in TextsOf(msg["content"]))
        {
            if (text.Length > 0) content.Add(new TextPart(text));
        }
        if (msg["content"] is JsonArray rawParts)
        {
            foreach (var node in rawParts)
            {
                if (node is JsonObject { } part && StringOrNull(part, "type") == "image_url" && part["image_url"] is JsonObject image)
                    content.Add(DecodeImage(image));
            }
        }
        var result = new ToolResultPart(StringOrNull(msg, "tool_call_id") ?? "", StringOrNull(msg, "name"), content);
        var last = request.Messages.LastOrDefault();
        if (last is { Role: MessageRole.User } user && user.Parts.Count > 0 && user.Parts.All(p => p is ToolResultPart))
            user.Parts.Add(result); // merge consecutive tool results into the same User message
        else
            request.Messages.Add(new UnifiedMessage(MessageRole.User, [result]));
    }

    private static List<UnifiedTool> DecodeTools(JsonNode? tools)
    {
        var result = new List<UnifiedTool>();
        if (tools is not JsonArray array) return result;
        foreach (var node in array)
        {
            if (node is not JsonObject tool) continue;
            var type = StringOrNull(tool, "type");
            if (type == "function")
            {
                var function = tool["function"] as JsonObject ?? new JsonObject();
                result.Add(new UnifiedTool
                {
                    Name = StringOrNull(function, "name") ?? "",
                    Description = StringOrNull(function, "description"),
                    InputSchema = function["parameters"],
                    Strict = BoolOrNull(function["strict"]),
                });
            }
            else
            {
                // Unknown tool shape: kept verbatim so the same-protocol encoder can re-emit it (plan §6.4).
                result.Add(new UnifiedTool { BuiltinType = type ?? "unknown", BuiltinOrigin = ApiProtocol.OpenAIChat, BuiltinRaw = tool.DeepClone() as JsonObject });
            }
        }
        return result;
    }

    private static ToolChoice? DecodeToolChoice(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var mode) => mode switch
        {
            "auto" => new ToolChoice(ToolChoiceMode.Auto),
            "none" => new ToolChoice(ToolChoiceMode.None),
            "required" => new ToolChoice(ToolChoiceMode.Required),
            _ => null,
        },
        JsonObject { } o when o["function"] is JsonObject function && StringOrNull(function, "name") is { } name
            => new ToolChoice(ToolChoiceMode.Specific, name),
        _ => null,
    };

    private static ReasoningConfig? DecodeReasoning(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue<string>(out var effort) || effort.Length == 0) return null;
        if (effort == "none") return new ReasoningConfig { Enabled = false };
        return new ReasoningConfig { Effort = effort, Enabled = true };
    }

    private static ResponseFormat? DecodeResponseFormat(JsonNode? node)
    {
        if (node is not JsonObject o) return null;
        var format = new ResponseFormat { Type = StringOrNull(o, "type") ?? "text" };
        if (o["json_schema"] is JsonObject schema)
        {
            format.Name = StringOrNull(schema, "name");
            format.Schema = schema["schema"];
            format.Strict = BoolOrNull(schema["strict"]);
        }
        return format;
    }

    private static List<string>? DecodeStop(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<string>(out var stop) => [stop],
        JsonArray a => a.Select(n => n is JsonValue s && s.TryGetValue<string>(out var s2) ? s2 : null).OfType<string>().ToList() is { Count: > 0 } list ? list : null,
        _ => null,
    };

    // ---------------------------------------------------------------- request encode

    public JsonObject EncodeRequest(UnifiedRequest request, RequestEncodeContext ctx)
    {
        var body = new JsonObject { ["model"] = ctx.UpstreamModel };

        var systemTexts = request.System.OfType<TextPart>().Select(p => p.Text).Where(t => t.Length > 0).ToList();
        if (systemTexts.Count > 0)
            body["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = string.Join("\n\n", systemTexts) });
        else body["messages"] = new JsonArray();

        foreach (var message in request.Messages) EncodeMessage(body, message, request.Warnings);

        EncodeTools(body, request);

        if (request.ToolChoice is { } choice)
        {
            body["tool_choice"] = choice.Mode switch
            {
                ToolChoiceMode.Auto => "auto",
                ToolChoiceMode.None => "none",
                ToolChoiceMode.Required => "required",
                ToolChoiceMode.Specific => new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = choice.Name },
                },
                _ => null,
            };
        }
        if (request.ParallelToolCalls is { } parallel && request.Tools.Count > 0) body["parallel_tool_calls"] = parallel;
        if (request.Temperature is { } temperature) body["temperature"] = temperature;
        if (request.TopP is { } topP) body["top_p"] = topP;
        if (request.Stop is { Count: > 0 } stop)
            body["stop"] = stop.Count == 1 ? stop[0] : new JsonArray(stop.Select(s => (JsonNode)s).ToArray());
        if (request.MaxOutputTokens is { } max)
        {
            // gpt-5*/o1*/o3*/o4* reject max_tokens; older models reject max_completion_tokens.
            if (UsesMaxCompletionTokens(ctx.UpstreamModel)) body["max_completion_tokens"] = max;
            else body["max_tokens"] = max;
        }
        var effort = EffortOf(request, ctx.EffortBudgets);
        if (effort is not null) body["reasoning_effort"] = effort;
        if (request.ResponseFormat is { } format) body["response_format"] = EncodeResponseFormat(format);
        if (request.Stream)
        {
            body["stream"] = true;
            // Plan §4.2: always ask the upstream for usage; the extra chunk is filtered for the client.
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }
        if (request.User is { } user) body["user"] = user;
        if (request.Metadata is { Count: > 0 } metadata) body["metadata"] = metadata.DeepClone();
        if (request.ServiceTier is { } tier) body["service_tier"] = tier;

        if (request.Extensions.TryGetValue(ApiProtocol.OpenAIChat, out var extensions))
        {
            foreach (var (key, value) in extensions)
            {
                if (!body.ContainsKey(key)) body[key] = value?.DeepClone();
            }
        }
        return body;
    }

    /// <summary>gpt-5*/o1*/o3*/o4* (case-insensitive, optional "openai/" prefix) require max_completion_tokens.</summary>
    internal static bool UsesMaxCompletionTokens(string model)
    {
        var name = model.ToLowerInvariant();
        if (name.StartsWith("openai/", StringComparison.Ordinal)) name = name["openai/".Length..];
        return name.StartsWith("gpt-5", StringComparison.Ordinal)
               || name.StartsWith("o1", StringComparison.Ordinal)
               || name.StartsWith("o3", StringComparison.Ordinal)
               || name.StartsWith("o4", StringComparison.Ordinal);
    }

    private static string? EffortOf(UnifiedRequest request, EffortBudgets budgets)
    {
        if (request.Reasoning is not { } reasoning || !reasoning.Enabled) return null;
        if (reasoning.Effort is { Length: > 0 } effort) return effort;
        if (reasoning.BudgetTokens is { } budget)
            return budget <= budgets.Low ? "low" : budget <= budgets.Medium ? "medium" : "high";
        return null;
    }

    private static void EncodeMessage(JsonObject body, UnifiedMessage message, List<string> warnings)
    {
        switch (message.Role)
        {
            case MessageRole.User:
                // Tool results precede the rest of the user message (tool calls must be answered first).
                foreach (var part in message.Parts.OfType<ToolResultPart>())
                {
                    ((JsonArray)body["messages"]!).Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = part.CallId,
                        ["content"] = string.Join("\n\n", part.Content.OfType<TextPart>().Select(p => p.Text)),
                    });
                    if (part.Content.Any(p => p is ImagePart))
                        warnings.Add("tool 结果中的图片无法以 Chat 协议回传，已丢弃。");
                    if (part.IsError)
                        warnings.Add($"tool 结果 {part.CallId} 的错误标记（is_error）在 Chat 协议中没有对应字段，已丢弃。");
                }
                var rest = message.Parts.Where(p => p is not ToolResultPart).ToList();
                if (rest.Count > 0)
                {
                    var content = EncodeContent(rest, warnings);
                    if (content is JsonArray { Count: 0 })
                        warnings.Add("用户消息的内容全部无法映射到 Chat 协议，整条消息已跳过。");
                    else
                        ((JsonArray)body["messages"]!).Add(new JsonObject { ["role"] = "user", ["content"] = content });
                }
                break;
            case MessageRole.Assistant:
                var assistant = new JsonObject { ["role"] = "assistant" };
                var reasoning = string.Join("\n\n", message.Parts.OfType<ReasoningPart>()
                    .Where(p => p.Origin == ApiProtocol.OpenAIChat && p.Text is { Length: > 0 })
                    .Select(p => p.Text!));
                if (reasoning.Length > 0) assistant["reasoning_content"] = reasoning;
                var text = string.Join("\n\n", message.Parts.OfType<TextPart>().Select(p => p.Text).Where(t => t.Length > 0));
                assistant["content"] = text.Length > 0 ? text : null;
                var calls = message.Parts.OfType<ToolCallPart>().ToList();
                if (calls.Count > 0)
                {
                    assistant["tool_calls"] = new JsonArray(calls.Select(call => (JsonNode)new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.ArgumentsJson },
                    }).ToArray());
                }
                ((JsonArray)body["messages"]!).Add(assistant);
                break;
        }
    }

    private static JsonNode EncodeContent(List<ContentPart> parts, List<string> warnings)
    {
        if (parts.Count == 1 && parts[0] is TextPart { Text: { } only }) return only;
        var array = new JsonArray();
        foreach (var part in parts)
        {
            switch (part)
            {
                case TextPart { Text: { } text }:
                    array.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    break;
                case ImagePart image:
                    var url = image.Base64Data is { Length: > 0 } base64
                        ? $"data:{image.MediaType ?? "image/png"};base64,{base64}"
                        : image.Url;
                    if (url is null) break;
                    if (!IsHttpOrData(url))
                    {
                        warnings.Add($"图片引用「{Clip(url)}」不是 http(s)/data URL，Chat 上游无法读取，已丢弃。");
                        break;
                    }
                    var imagePart = new JsonObject { ["url"] = url };
                    if (image.Detail is { Length: > 0 } detail) imagePart["detail"] = detail;
                    array.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = imagePart });
                    break;
                case FilePart file:
                    var fileSpec = new JsonObject();
                    if (file.Base64Data is { Length: > 0 } data)
                        fileSpec["file_data"] = $"data:{file.MediaType ?? "application/octet-stream"};base64,{data}";
                    else if (file.Url is { } fileUrl)
                    {
                        if (!IsHttpOrData(fileUrl))
                        {
                            warnings.Add($"文件引用「{Clip(fileUrl)}」不是 http(s)/data URL，Chat 上游无法读取，已丢弃。");
                            break;
                        }
                        fileSpec["file_data"] = fileUrl;
                    }
                    else if (file.FileId is { } fileId) fileSpec["file_id"] = fileId;
                    if (fileSpec.Count == 0) break;
                    if (file.FileName is { Length: > 0 } name) fileSpec["filename"] = name;
                    array.Add(new JsonObject { ["type"] = "file", ["file"] = fileSpec });
                    break;
            }
        }
        return array;
    }

    private static bool IsHttpOrData(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private static string Clip(string text) => text.Length <= 80 ? text : text[..80];

    private static void EncodeTools(JsonObject body, UnifiedRequest request)
    {
        if (request.Tools.Count == 0) return;
        var array = new JsonArray();
        foreach (var tool in request.Tools)
        {
            if (tool.BuiltinType is null)
            {
                var function = new JsonObject { ["name"] = tool.Name };
                if (tool.Description is { } description) function["description"] = description;
                if (tool.InputSchema is { } schema) function["parameters"] = schema.DeepClone();
                if (tool.Strict is { } strict) function["strict"] = strict;
                array.Add(new JsonObject { ["type"] = "function", ["function"] = function });
            }
            else if (tool.BuiltinOrigin == ApiProtocol.OpenAIChat && tool.BuiltinRaw is { } raw)
            {
                array.Add(raw.DeepClone());
            }
            else
            {
                request.Warnings.Add($"工具「{tool.Name}」（类型 {tool.BuiltinType}）不是 Chat function 工具，已丢弃。");
            }
        }
        if (array.Count > 0) body["tools"] = array;
    }

    private static JsonObject EncodeResponseFormat(ResponseFormat format)
    {
        var result = new JsonObject { ["type"] = format.Type };
        if (format.Type == "json_schema")
        {
            var schema = new JsonObject();
            if (format.Name is { } name) schema["name"] = name;
            if (format.Strict is { } strict) schema["strict"] = strict;
            if (format.Schema is { } body) schema["schema"] = body.DeepClone();
            result["json_schema"] = schema;
        }
        return result;
    }

    // ---------------------------------------------------------------- errors

    public JsonObject EncodeError(int status, string type, string message) => new()
    {
        ["error"] = new JsonObject
        {
            ["message"] = message,
            ["type"] = type,
            ["param"] = null,
            ["code"] = null,
        },
    };

    public (string Type, string Message) DecodeError(int status, string body)
    {
        string? message = null, type = null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject root && root["error"] is JsonObject error)
            {
                message = StringOrNull(error, "message");
                type = StringOrNull(error, "type") ?? StringOrNull(error, "code");
            }
        }
        catch (JsonException)
        {
            // Not JSON: fall back to the raw body below.
        }
        message ??= body.Length > 0 ? (body.Length > 500 ? body[..500] : body) : $"HTTP {status}";
        return (type ?? TypeFromStatus(status), message);
    }

    private static string TypeFromStatus(int status) => status switch
    {
        400 or 404 or 422 => "invalid_request_error",
        401 => "authentication_error",
        403 => "permission_error",
        408 or 504 => "timeout",
        429 => "rate_limit_error",
        _ => "api_error",
    };

    // ---------------------------------------------------------------- responses

    public IResponseDecoder CreateResponseDecoder(ResponseDecodeContext ctx) => new ChatResponseDecoder(ctx);

    public IResponseEncoder CreateResponseEncoder(ResponseEncodeContext ctx) => new ChatResponseEncoder(ctx);

    // ---------------------------------------------------------------- json helpers

    internal static string? StringOrNull(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? BoolOrNull(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static int? IntOrNull(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static double? DoubleOrNull(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
