using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;

namespace Astra.Gateway.Protocol.Responses;

/// <summary>
/// OpenAI Responses API codec (POST /v1/responses) — the wire protocol spoken by Codex CLI and Grok Build.
/// Stateless singleton; the response decoder / encoder hold the per-response state.
/// Known lossy mappings (plan §6.4):
///  - reasoning signatures / encrypted content only round-trip between Responses endpoints (Origin check);
///  - builtin tools (web_search …) are re-emitted only to Responses upstreams, dropped elsewhere with a warning;
///  - <c>input_image</c> by <c>file_id</c> cannot be represented in <see cref="ImagePart"/> (no FileId field) and is dropped;
///  - refusal content becomes plain <see cref="TextPart"/> and is re-emitted as <c>output_text</c>;
///  - <c>ToolResultPart.IsError</c> has no Responses representation (dropped, warning recorded).
/// </summary>
public sealed class ResponsesCodec : IProtocolCodec
{
    private static readonly HashSet<string> HandledRequestKeys =
    [
        "model", "instructions", "input", "tools", "tool_choice", "parallel_tool_calls",
        "max_output_tokens", "reasoning", "text", "temperature", "top_p", "stream",
        "previous_response_id", "service_tier", "metadata", "user", "safety_identifier",
    ];

    public ApiProtocol Protocol => ApiProtocol.OpenAIResponses;

    // ---------------------------------------------------------------- request decode

    public UnifiedRequest DecodeRequest(JsonObject body, RequestDecodeContext ctx)
    {
        var request = new UnifiedRequest();
        var extensions = new JsonObject();

        if (Str(body, "model") is not { Length: > 0 } model)
            throw new ProtocolException("model is required");
        request.Model = model;

        if (Str(body, "instructions") is { Length: > 0 } instructions)
            request.System.Add(new TextPart(instructions));

        switch (body["input"])
        {
            case null:
                break;
            case JsonValue v when v.TryGetValue<string>(out var text):
                AddPart(request, MessageRole.User, new TextPart(text));
                break;
            case JsonArray items:
                foreach (var item in items)
                    DecodeInputItem(item, request);
                break;
            default:
                throw new ProtocolException("input must be a string or an array of input items");
        }

        if (body["tools"] is JsonArray tools)
            foreach (var tool in tools)
                DecodeTool(tool, request);

        switch (body["tool_choice"])
        {
            case JsonValue v when v.TryGetValue<string>(out var mode):
                request.ToolChoice = mode switch
                {
                    "auto" => new ToolChoice(ToolChoiceMode.Auto),
                    "none" => new ToolChoice(ToolChoiceMode.None),
                    "required" => new ToolChoice(ToolChoiceMode.Required),
                    _ => null,
                };
                if (request.ToolChoice is null)
                    Warn(request, $"ignored tool_choice '{mode}'");
                break;
            case JsonObject o when Str(o, "type") == "function" && Str(o, "name") is { Length: > 0 } name:
                request.ToolChoice = new ToolChoice(ToolChoiceMode.Specific, name);
                break;
            case JsonObject:
                Warn(request, "ignored non-function tool_choice object");
                break;
        }

        if (Bool(body, "parallel_tool_calls") is { } parallel)
            request.ParallelToolCalls = parallel;
        if (Int(body, "max_output_tokens") is { } max)
            request.MaxOutputTokens = max;

        if (body["reasoning"] is JsonObject reasoning)
        {
            var config = new ReasoningConfig();
            if (Str(reasoning, "effort") is { } effort)
            {
                if (effort == "none")
                    config.Enabled = false;
                else
                    config.Effort = effort;
            }
            // summary: "auto" | "concise" | "detailed" (string in the spec; be lenient with arrays).
            if (reasoning["summary"] is { } summary && summary.GetValueKind() != JsonValueKind.Null)
                config.IncludeThoughts = true;
            if (config.Effort is not null || !config.Enabled || config.IncludeThoughts)
                request.Reasoning = config;
        }

        if (body["text"] is JsonObject textObj)
        {
            if (textObj["format"] is JsonObject format)
            {
                var rf = new ResponseFormat { Type = Str(format, "type") ?? "text" };
                if (rf.Type is "json_object" or "json_schema")
                {
                    rf.Name = Str(format, "name");
                    rf.Schema = format["schema"]?.DeepClone();
                    rf.Strict = Bool(format, "strict");
                }
                request.ResponseFormat = rf;
            }
            // Keep text minus format (verbosity …) for same-protocol round trips.
            var rest = new JsonObject();
            foreach (var (key, value) in textObj)
                if (key != "format")
                    rest[key] = value?.DeepClone();
            if (rest.Count > 0)
                extensions["text"] = rest;
        }

        if (Num(body, "temperature") is { } temperature)
            request.Temperature = temperature;
        if (Num(body, "top_p") is { } topP)
            request.TopP = topP;
        if (Bool(body, "stream") is { } stream)
            request.Stream = stream;
        if (Str(body, "previous_response_id") is { } previous)
            request.PreviousResponseId = previous;
        if (Str(body, "service_tier") is { } tier)
            request.ServiceTier = tier;
        if (body["metadata"] is JsonObject metadata)
            request.Metadata = metadata.DeepClone().AsObject();
        request.User = Str(body, "user") ?? Str(body, "safety_identifier");

        foreach (var (key, value) in body)
            if (!HandledRequestKeys.Contains(key))
                extensions[key] = value?.DeepClone();
        if (extensions.Count > 0)
            request.Extensions[ApiProtocol.OpenAIResponses] = extensions;

        return request;
    }

    private static void DecodeInputItem(JsonNode? item, UnifiedRequest request)
    {
        if (item is not JsonObject o)
        {
            Warn(request, "ignored non-object input item");
            return;
        }
        switch (Str(o, "type"))
        {
            case "message" or null:
                if (Str(o, "role") is not { } role)
                {
                    Warn(request, "dropped input item without type and role");
                    return;
                }
                DecodeMessageItem(o, role, request);
                break;

            case "function_call":
            {
                var callId = Str(o, "call_id") ?? Str(o, "id") ?? "call_" + Ulid.NewUlid();
                var args = Str(o, "arguments");
                AddPart(request, MessageRole.Assistant,
                    new ToolCallPart(callId, Str(o, "name") ?? "", string.IsNullOrEmpty(args) ? "{}" : args));
                break;
            }

            case "function_call_output":
            {
                var content = new List<ContentPart>();
                switch (o["output"])
                {
                    case JsonValue v when v.TryGetValue<string>(out var text):
                        content.Add(new TextPart(text));
                        break;
                    case JsonArray parts:
                        foreach (var part in parts)
                            if (DecodeContentPart(part, request) is { } parsed)
                                content.Add(parsed);
                        break;
                    case JsonObject single:
                        if (DecodeContentPart(single, request) is { } one)
                            content.Add(one);
                        break;
                }
                AddPart(request, MessageRole.User,
                    new ToolResultPart(Str(o, "call_id") ?? "", null, content));
                break;
            }

            case "reasoning":
            {
                var summaries = new List<string>();
                if (o["summary"] is JsonArray summary)
                    foreach (var s in summary)
                        if (s is JsonObject so && Str(so, "text") is { Length: > 0 } st)
                            summaries.Add(st);
                AddPart(request, MessageRole.Assistant, new ReasoningPart(
                    summaries.Count > 0 ? string.Join("\n\n", summaries) : null,
                    Signature: null,
                    EncryptedContent: Str(o, "encrypted_content"),
                    Origin: ApiProtocol.OpenAIResponses,
                    Id: Str(o, "id")));
                break;
            }

            // web_search_call, file_search_call, item_reference, custom_tool_call, local_shell_call, …
            case { } other:
                Warn(request, $"dropped input item type '{other}' (not representable in the IR)");
                break;
        }
    }

    private static void DecodeMessageItem(JsonObject o, string role, UnifiedRequest request)
    {
        switch (role)
        {
            case "user" or "assistant":
                break;
            case "system" or "developer":
                foreach (var part in DecodeContent(o, request))
                {
                    if (part is TextPart text)
                        request.System.Add(text);
                    else
                        Warn(request, "dropped non-text content part in a system/developer message");
                }
                return;
            default:
                Warn(request, $"dropped message with unknown role '{role}'");
                return;
        }
        var irRole = role == "assistant" ? MessageRole.Assistant : MessageRole.User;
        foreach (var part in DecodeContent(o, request))
            AddPart(request, irRole, part);
    }

    private static List<ContentPart> DecodeContent(JsonObject o, UnifiedRequest request)
    {
        var parts = new List<ContentPart>();
        switch (o["content"])
        {
            case JsonValue v when v.TryGetValue<string>(out var text):
                parts.Add(new TextPart(text));
                break;
            case JsonArray arr:
                foreach (var part in arr)
                    if (DecodeContentPart(part, request) is { } parsed)
                        parts.Add(parsed);
                break;
        }
        return parts;
    }

    private static ContentPart? DecodeContentPart(JsonNode? node, UnifiedRequest request)
    {
        if (node is not JsonObject o)
            return null;
        switch (Str(o, "type"))
        {
            case "input_text" or "output_text":
                return Str(o, "text") is { } text ? new TextPart(text) : null;
            case "refusal":
                return Str(o, "refusal") is { } refusal ? new TextPart(refusal) : null;
            case "input_image":
            {
                string? url = null;
                switch (o["image_url"])
                {
                    case JsonValue v when v.TryGetValue<string>(out var s):
                        url = s;
                        break;
                    case JsonObject io:
                        url = Str(io, "url");
                        break;
                }
                var detail = Str(o, "detail");
                if (url is not null)
                    return new ImagePart(url, null, null, detail);
                if (Str(o, "file_id") is { } fileId)
                {
                    // IR gap: ImagePart has no FileId, so a file-id image cannot survive the IR.
                    Warn(request, $"dropped input_image file_id '{fileId}' (ImagePart cannot carry upstream file ids)");
                    return null;
                }
                Warn(request, "ignored input_image without image_url and file_id");
                return null;
            }
            case "input_file":
            {
                string? mediaType = null, base64 = null;
                if (Str(o, "file_data") is { } fileData)
                {
                    if (fileData.StartsWith("data:", StringComparison.Ordinal))
                    {
                        var semi = fileData.IndexOf(';');
                        var comma = fileData.IndexOf(',');
                        if (semi > 0 && comma > semi)
                        {
                            mediaType = fileData[5..semi];
                            base64 = fileData[(comma + 1)..];
                        }
                        else
                        {
                            base64 = fileData;
                        }
                    }
                    else
                    {
                        base64 = fileData;
                    }
                }
                var url = Str(o, "file_url");
                var fileId = Str(o, "file_id");
                if (base64 is null && url is null && fileId is null)
                {
                    Warn(request, "ignored input_file without file_data, file_url and file_id");
                    return null;
                }
                return new FilePart(url, mediaType, base64, Str(o, "filename"), fileId);
            }
            default:
                Warn(request, $"dropped content part type '{Str(o, "type")}'");
                return null;
        }
    }

    private static void DecodeTool(JsonNode? node, UnifiedRequest request)
    {
        if (node is not JsonObject o)
        {
            Warn(request, "ignored non-object tool entry");
            return;
        }
        var type = Str(o, "type");
        if (type is null)
        {
            Warn(request, "ignored tool entry without type");
            return;
        }
        if (type == "function")
        {
            // Responses function tools are flat: {type, name, description, parameters, strict}.
            request.Tools.Add(new UnifiedTool
            {
                Name = Str(o, "name") ?? "",
                Description = Str(o, "description"),
                InputSchema = o["parameters"]?.DeepClone(),
                Strict = Bool(o, "strict"),
            });
            return;
        }
        // Builtin (web_search, file_search, code_interpreter, image_generation, mcp, …):
        // re-emitted verbatim only to Responses upstreams.
        request.Tools.Add(new UnifiedTool
        {
            Name = type,
            BuiltinType = type,
            BuiltinOrigin = ApiProtocol.OpenAIResponses,
            BuiltinRaw = o.DeepClone().AsObject(),
        });
    }

    private static void AddPart(UnifiedRequest request, MessageRole role, ContentPart part)
    {
        var last = request.Messages.Count > 0 ? request.Messages[^1] : null;
        if (last is not null && last.Role == role)
            last.Parts.Add(part);
        else
            request.Messages.Add(new UnifiedMessage(role, [part]));
    }

    private static void Warn(UnifiedRequest request, string message) =>
        request.Warnings.Add($"openai-responses: {message}");

    // ---------------------------------------------------------------- request encode

    public JsonObject EncodeRequest(UnifiedRequest request, RequestEncodeContext ctx)
    {
        var body = new JsonObject { ["model"] = ctx.UpstreamModel };

        if (request.System is { Count: > 0 })
        {
            var instructions = string.Join("\n\n",
                request.System.OfType<TextPart>().Select(p => p.Text).Where(t => !string.IsNullOrEmpty(t)));
            if (instructions.Length > 0)
                body["instructions"] = instructions;
        }

        var input = new JsonArray();
        foreach (var message in request.Messages)
            EncodeMessage(message, input, request);
        body["input"] = input;

        WriteTools(body, request.Tools, request.Warnings);
        if (ToolChoiceToJson(request.ToolChoice) is { } choice)
            body["tool_choice"] = choice;
        if (request.ParallelToolCalls is { } parallel)
            body["parallel_tool_calls"] = parallel;
        if (request.MaxOutputTokens is { } max)
            body["max_output_tokens"] = max;
        if (ReasoningToJson(request.Reasoning, ctx.EffortBudgets) is { } reasoning)
            body["reasoning"] = reasoning;
        if (request.ResponseFormat is { Type: not "text" } format)
            body["text"] = FormatToJson(format);
        if (request.Temperature is { } temperature)
            body["temperature"] = temperature;
        if (request.TopP is { } topP)
            body["top_p"] = topP;
        if (request.Stream)
            body["stream"] = true;
        if (request.ServiceTier is { } tier)
            body["service_tier"] = tier;
        if (request.Metadata is { Count: > 0 } metadata)
            body["metadata"] = metadata.DeepClone();
        if (request.User is { } user)
            body["user"] = user;
        if (request.PreviousResponseId is { } previous)
            body["previous_response_id"] = previous;

        if (request.Extensions.TryGetValue(ApiProtocol.OpenAIResponses, out var extensions))
            DeepMerge(body, extensions);
        // Responses upstreams store conversations by default; never let the gateway opt the client in
        // (an explicit store:true from the client survives the merge above).
        if (body["store"] is not JsonValue storeValue || !storeValue.TryGetValue<bool>(out _))
            body["store"] = false;

        return body;
    }

    private static void EncodeMessage(UnifiedMessage message, JsonArray input, UnifiedRequest request)
    {
        var buffer = new List<ContentPart>();
        void Flush()
        {
            if (buffer.Count == 0)
                return;
            input.Add(MessageItem(message.Role, buffer, request));
            buffer.Clear();
        }
        foreach (var part in message.Parts)
        {
            switch (part)
            {
                case ToolResultPart result:
                    Flush();
                    input.Add(FunctionCallOutputItem(result, request));
                    break;
                case ToolCallPart call:
                    Flush();
                    input.Add(new JsonObject
                    {
                        ["type"] = "function_call",
                        ["call_id"] = call.Id,
                        ["name"] = call.Name,
                        ["arguments"] = string.IsNullOrEmpty(call.ArgumentsJson) ? "{}" : call.ArgumentsJson,
                    });
                    break;
                case ReasoningPart reasoning:
                    Flush();
                    if (ReasoningItem(reasoning) is { } item)
                        input.Add(item);
                    else if (reasoning.Origin != ApiProtocol.OpenAIResponses)
                        Warn(request, $"dropped reasoning from '{reasoning.Origin}' (encrypted content only round-trips to its own protocol)");
                    break;
                default:
                    buffer.Add(part);
                    break;
            }
        }
        Flush();
    }

    private static JsonObject MessageItem(MessageRole role, List<ContentPart> parts, UnifiedRequest request)
    {
        var content = new JsonArray();
        var item = new JsonObject
        {
            ["type"] = "message",
            ["role"] = role == MessageRole.User ? "user" : "assistant",
            ["content"] = content,
        };
        if (role == MessageRole.User)
        {
            foreach (var part in parts)
            {
                switch (part)
                {
                    case TextPart text:
                        content.Add(new JsonObject { ["type"] = "input_text", ["text"] = text.Text });
                        break;
                    case ImagePart image:
                        if (ImagePartJson(image, request) is { } imageJson)
                            content.Add(imageJson);
                        break;
                    case FilePart file:
                        if (FilePartJson(file, request) is { } fileJson)
                            content.Add(fileJson);
                        break;
                    default:
                        Warn(request, $"dropped {part.GetType().Name} in a user message");
                        break;
                }
            }
        }
        else
        {
            foreach (var part in parts)
            {
                if (part is TextPart text)
                    content.Add(new JsonObject { ["type"] = "output_text", ["text"] = text.Text, ["annotations"] = new JsonArray() });
                else
                    Warn(request, $"dropped {part.GetType().Name} in an assistant message");
            }
        }
        return item;
    }

    private static JsonObject? ImagePartJson(ImagePart image, UnifiedRequest request)
    {
        string? url = null;
        if (image.Base64Data is { } base64)
            url = $"data:{image.MediaType ?? "image/png"};base64,{base64}";
        else if (image.Url is { } reference)
        {
            if (!IsHttpOrData(reference))
            {
                Warn(request, $"dropped image url with unsupported scheme '{Clip(reference)}' (the upstream cannot fetch it)");
                return null;
            }
            url = reference;
        }
        if (url is null)
        {
            Warn(request, "dropped image with neither URL nor inline data (file-id images cannot cross the IR)");
            return null;
        }
        var json = new JsonObject { ["type"] = "input_image", ["image_url"] = url };
        if (!string.IsNullOrEmpty(image.Detail))
            json["detail"] = image.Detail;
        return json;
    }

    private static JsonObject? FilePartJson(FilePart file, UnifiedRequest request)
    {
        var json = new JsonObject { ["type"] = "input_file" };
        if (file.Base64Data is { } base64)
            json["file_data"] = $"data:{file.MediaType ?? "application/octet-stream"};base64,{base64}";
        if (file.Url is { } url)
        {
            if (IsHttpOrData(url))
                json["file_url"] = url;
            else
                Warn(request, $"dropped file url with unsupported scheme '{Clip(url)}' (the upstream cannot fetch it)");
        }
        if (file.FileId is { } fileId)
            json["file_id"] = fileId;
        if (file.FileName is { } name)
            json["filename"] = name;
        if (json.Count == 1)
        {
            Warn(request, "dropped file with no content reference");
            return null;
        }
        return json;
    }

    private static bool IsHttpOrData(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    private static string Clip(string text) => text.Length <= 80 ? text : text[..80];

    private static JsonObject FunctionCallOutputItem(ToolResultPart result, UnifiedRequest request)
    {
        var item = new JsonObject
        {
            ["type"] = "function_call_output",
            ["call_id"] = result.CallId,
        };
        if (result.Content.Any(p => p is ImagePart))
        {
            var output = new JsonArray();
            foreach (var part in result.Content)
            {
                switch (part)
                {
                    case TextPart text:
                        output.Add(new JsonObject { ["type"] = "input_text", ["text"] = text.Text });
                        break;
                    case ImagePart image:
                        if (ImagePartJson(image, request) is { } imageJson)
                            output.Add(imageJson);
                        break;
                }
            }
            item["output"] = output;
        }
        else
        {
            item["output"] = string.Join("\n\n", result.Content.OfType<TextPart>().Select(p => p.Text));
        }
        if (result.IsError)
            Warn(request, $"function_call_output {result.CallId}: IsError has no Responses representation");
        return item;
    }

    private static JsonObject? ReasoningItem(ReasoningPart reasoning)
    {
        // Signatures / encrypted payloads are only valid for their Origin protocol (plan §6.4).
        if (reasoning.Origin != ApiProtocol.OpenAIResponses)
            return null;
        if (reasoning.EncryptedContent is null && reasoning.Id is null)
            return null;
        var item = new JsonObject { ["type"] = "reasoning" };
        if (reasoning.Id is { } id)
            item["id"] = id;
        if (!string.IsNullOrEmpty(reasoning.Text))
            item["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = reasoning.Text });
        if (reasoning.EncryptedContent is { } encrypted)
            item["encrypted_content"] = encrypted;
        return item;
    }

    internal static void WriteTools(JsonObject body, IReadOnlyList<UnifiedTool> tools, List<string>? warnings)
    {
        if (tools.Count == 0)
            return;
        var array = new JsonArray();
        foreach (var tool in tools)
        {
            if (tool.BuiltinType is null)
            {
                var function = new JsonObject { ["type"] = "function", ["name"] = tool.Name };
                if (tool.Description is { } description)
                    function["description"] = description;
                if (tool.InputSchema is { } schema)
                    function["parameters"] = schema.DeepClone();
                if (tool.Strict is { } strict)
                    function["strict"] = strict;
                array.Add(function);
            }
            else if (tool.BuiltinOrigin == ApiProtocol.OpenAIResponses)
            {
                array.Add(tool.BuiltinRaw is { } raw ? raw.DeepClone() : new JsonObject { ["type"] = tool.BuiltinType });
            }
            else
            {
                warnings?.Add($"openai-responses: dropped builtin tool '{tool.BuiltinType}' from '{tool.BuiltinOrigin}' (builtin tools only round-trip to their own protocol)");
            }
        }
        if (array.Count > 0)
            body["tools"] = array;
    }

    internal static JsonNode? ToolChoiceToJson(ToolChoice? choice) => choice switch
    {
        null => null,
        { Mode: ToolChoiceMode.Auto } => JsonValue.Create("auto"),
        { Mode: ToolChoiceMode.None } => JsonValue.Create("none"),
        { Mode: ToolChoiceMode.Required } => JsonValue.Create("required"),
        { Mode: ToolChoiceMode.Specific, Name: { Length: > 0 } name } =>
            new JsonObject { ["type"] = "function", ["name"] = name },
        _ => null,
    };

    internal static JsonObject? FormatToJson(ResponseFormat format)
    {
        var json = new JsonObject { ["type"] = format.Type };
        if (format.Name is { } name)
            json["name"] = name;
        if (format.Schema is { } schema)
            json["schema"] = schema.DeepClone();
        if (format.Strict is { } strict)
            json["strict"] = strict;
        return new JsonObject { ["format"] = json };
    }

    internal static JsonObject? ReasoningToJson(ReasoningConfig? reasoning, EffortBudgets budgets)
    {
        if (reasoning is null || !reasoning.Enabled)
            return null;
        var json = new JsonObject();
        var effort = reasoning.Effort;
        if (effort is null && reasoning.BudgetTokens is { } budget)
            effort = budget <= budgets.Low ? "low" : budget <= budgets.Medium ? "medium" : "high";
        if (effort is { } e)
            json["effort"] = e;
        if (reasoning.IncludeThoughts)
            json["summary"] = "auto";
        return json.Count > 0 ? json : null;
    }

    internal static JsonObject? ClientUsage(NormalizedUsage? usage)
    {
        if (usage is null)
            return null;
        var input = usage.Get(TokenTypes.Input) + usage.Get(TokenTypes.CacheRead) + usage.Get(TokenTypes.CacheWrite5m)
                    + usage.Get(TokenTypes.CacheWrite1h) + usage.Get(TokenTypes.InputAudio) + usage.Get(TokenTypes.InputImage);
        var output = usage.Get(TokenTypes.Output) + usage.Get(TokenTypes.Reasoning)
                     + usage.Get(TokenTypes.OutputAudio) + usage.Get(TokenTypes.OutputImage);
        return new JsonObject
        {
            ["input_tokens"] = input,
            ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = usage.Get(TokenTypes.CacheRead) },
            ["output_tokens"] = output,
            ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = usage.Get(TokenTypes.Reasoning) },
            ["total_tokens"] = input + output,
        };
    }

    private static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (target.TryGetPropertyValue(key, out var existing) && existing is not null)
            {
                // Fields the encoder already set win; objects merge so nested keys (text.verbosity …) survive.
                if (existing is JsonObject existingObj && value is JsonObject sourceObj)
                    DeepMerge(existingObj, sourceObj);
                continue;
            }
            target[key] = value?.DeepClone();
        }
    }

    // ---------------------------------------------------------------- plumbing

    internal static string? Str(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static bool? Bool(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    internal static int? Int(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    internal static double? Num(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    public IResponseDecoder CreateResponseDecoder(ResponseDecodeContext ctx) => new ResponsesResponseDecoder(ctx);

    public IResponseEncoder CreateResponseEncoder(ResponseEncodeContext ctx) => new ResponsesResponseEncoder(ctx);

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
        try
        {
            if (JsonNode.Parse(body) is JsonObject o && o["error"] is JsonObject error)
            {
                var type = Str(error, "type") ?? Str(error, "code");
                if (Str(error, "message") is { } message)
                    return (type ?? "upstream_error", message);
                if (type is not null)
                    return (type, Truncate(body));
            }
        }
        catch (JsonException)
        {
            // fall through to the raw-text fallback
        }
        return ("upstream_error", Truncate(body));
    }

    private static string Truncate(string body) => body.Length <= 500 ? body : body[..500];
}
