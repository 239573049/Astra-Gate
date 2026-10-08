using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;

namespace Astra.Gateway.Protocol.Gemini;

/// <summary>
/// Request side of the Gemini codec: DecodeRequest (client body → IR) and EncodeRequest (IR → upstream
/// body). The model and the streaming choice live in the URL, so the encoded body contains neither.
/// </summary>
internal static class GeminiRequests
{
    // Top-level body keys this codec maps into the IR; everything else is preserved in Extensions[Gemini].
    private static readonly HashSet<string> KnownBodyKeys =
        ["model", "contents", "systemInstruction", "system_instruction", "tools", "toolConfig", "tool_config", "generationConfig", "generation_config"];

    // generationConfig keys this codec maps; the rest travel in Extensions[Gemini].generationConfig.
    private static readonly HashSet<string> KnownGenKeys =
    [
        "temperature", "topP", "top_p", "topK", "top_k", "maxOutputTokens", "max_output_tokens",
        "stopSequences", "stop_sequences", "responseMimeType", "response_mime_type",
        "responseSchema", "response_schema", "responseJsonSchema", "response_json_schema",
        "thinkingConfig", "thinking_config",
    ];

    // ---------------------------------------------------------------- decode

    public static UnifiedRequest Decode(JsonObject body, RequestDecodeContext ctx)
    {
        var req = new UnifiedRequest();
        var model = ctx.PathModel ?? Fields.Str(body, "model");
        req.Model = model is null ? "" : StripModelsPrefix(model);
        req.Stream = ctx.PathStream ?? false;

        var state = new DecodeState();
        var ext = new JsonObject();
        var extGen = new JsonObject();

        if (Fields.Node(body, "systemInstruction", "system_instruction") is { } system)
            DecodeSystem(system, req);

        if (Fields.Node(body, "contents") is JsonArray contents)
        {
            foreach (var content in contents.OfType<JsonObject>())
                DecodeContent(content, req, state);
        }

        if (Fields.Node(body, "tools") is JsonArray tools)
            DecodeTools(tools, req);

        if (Fields.Node(body, "toolConfig", "tool_config") is JsonObject toolConfig &&
            Fields.Node(toolConfig, "functionCallingConfig", "function_calling_config") is JsonObject fcc)
            req.ToolChoice = DecodeToolChoice(fcc);

        if (Fields.Node(body, "generationConfig", "generation_config") is JsonObject gen)
            DecodeGenerationConfig(gen, req, extGen);

        // Everything the IR cannot express (safetySettings, cachedContent, labels, …) is merged back by this
        // same encoder when the upstream speaks Gemini again.
        foreach (var (key, value) in body)
        {
            if (!KnownBodyKeys.Contains(key)) ext[key] = value?.DeepClone();
        }
        if (extGen.Count > 0) ext["generationConfig"] = extGen;
        if (ext.Count > 0) req.Extensions[ApiProtocol.Gemini] = ext;

        return req;
    }

    private static string StripModelsPrefix(string model) =>
        model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model;

    private static void DecodeSystem(JsonNode system, UnifiedRequest req)
    {
        switch (system)
        {
            case JsonValue v when v.TryGetValue<string>(out var text):
                if (text.Length > 0) req.System.Add(new TextPart(text));
                break;
            case JsonObject obj when Fields.Node(obj, "parts") is JsonArray parts:
                foreach (var part in parts.OfType<JsonObject>())
                {
                    if (Fields.Str(part, "text") is { Length: > 0 } text) req.System.Add(new TextPart(text));
                }
                break;
            case JsonObject obj when Fields.Str(obj, "text") is { Length: > 0 } text:
                req.System.Add(new TextPart(text));
                break;
        }
    }

    private static void DecodeContent(JsonObject content, UnifiedRequest req, DecodeState state)
    {
        var role = Fields.Str(content, "role") switch
        {
            { } r when r.Equals("model", StringComparison.OrdinalIgnoreCase) => MessageRole.Assistant,
            { } r when r.Equals("assistant", StringComparison.OrdinalIgnoreCase) => MessageRole.Assistant,
            _ => MessageRole.User, // Gemini omits role for user turns.
        };
        var parts = new List<ContentPart>();
        var results = new List<ContentPart>(); // functionResponses always become User tool-result messages.
        if (Fields.Node(content, "parts") is JsonArray partsJson)
        {
            foreach (var part in partsJson.OfType<JsonObject>())
                DecodePart(part, req, state, parts, results);
        }
        if (parts.Count > 0) req.Messages.Add(new UnifiedMessage(role, parts));
        if (results.Count > 0) req.Messages.Add(new UnifiedMessage(MessageRole.User, results));
    }

    private static void DecodePart(JsonObject part, UnifiedRequest req, DecodeState state, List<ContentPart> parts, List<ContentPart> results)
    {
        // A thoughtSignature becomes its own reasoning part, placed immediately before the part that
        // carried it — the position the encoder needs to re-attach it on the way back out.
        if (Fields.Str(part, "thoughtSignature", "thought_signature") is { Length: > 0 } signature)
            parts.Add(new ReasoningPart(null, signature, Origin: ApiProtocol.Gemini));

        if (Fields.Str(part, "text") is { } text)
        {
            if (text.Length == 0) return;
            if (Fields.Bool(part, "thought") == true)
                parts.Add(new ReasoningPart(text, Origin: ApiProtocol.Gemini));
            else
                parts.Add(new TextPart(text));
            return;
        }

        if (Fields.Node(part, "inlineData", "inline_data") is JsonObject inline)
        {
            var mime = Fields.Str(inline, "mimeType", "mime_type");
            if (Fields.Str(inline, "data") is { Length: > 0 } data)
            {
                if (mime is not null && mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    parts.Add(new ImagePart(null, mime, data));
                else
                    parts.Add(new FilePart(null, mime, data));
            }
            return;
        }

        if (Fields.Node(part, "fileData", "file_data") is JsonObject fileData)
        {
            var uri = Fields.Str(fileData, "fileUri", "file_uri");
            if (uri is not null)
            {
                var mime = Fields.Str(fileData, "mimeType", "mime_type") ?? GuessMimeType(uri);
                if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    parts.Add(new ImagePart(uri, mime, null));
                else
                    parts.Add(new FilePart(uri, mime, null));
            }
            return;
        }

        if (Fields.Node(part, "functionCall", "function_call") is JsonObject call)
        {
            var name = Fields.Str(call, "name") ?? "";
            var n = state.CallCounts.GetValueOrDefault(name);
            state.CallCounts[name] = n + 1;
            var id = Fields.Str(call, "id") is { Length: > 0 } explicitId ? explicitId : $"call_{name}_{n}";
            // Gemini args are always an object; anything else (or missing) round-trips as "{}".
            var args = Fields.Node(call, "args", "arguments") is JsonObject argsObj
                ? argsObj.ToJsonString(GatewayJson.Options)
                : "{}";
            parts.Add(new ToolCallPart(id, name, args));
            state.Calls.Add((id, name));
            return;
        }

        if (Fields.Node(part, "functionResponse", "function_response") is JsonObject responsePart)
        {
            DecodeFunctionResponse(responsePart, state, results);
            return;
        }

        if (Fields.Node(part, "executableCode", "executable_code") is not null ||
            Fields.Node(part, "codeExecutionResult", "code_execution_result") is not null)
        {
            req.Warnings.Add("Gemini executableCode / codeExecutionResult parts are dropped (no IR representation).");
        }
    }

    private static void DecodeFunctionResponse(JsonObject responsePart, DecodeState state, List<ContentPart> results)
    {
        var name = Fields.Str(responsePart, "name") ?? "";
        string callId;
        if (Fields.Str(responsePart, "id") is { Length: > 0 } explicitId)
        {
            callId = explicitId;
        }
        else
        {
            // Match the earliest earlier tool call with this name that no response has claimed yet.
            var match = state.Calls.FirstOrDefault(c => c.Name == name && !state.Matched.Contains(c.Id));
            if (match.Id is not null)
            {
                callId = match.Id;
                state.Matched.Add(match.Id);
            }
            else
            {
                callId = $"call_{name}_{state.CallCounts.GetValueOrDefault(name)}";
            }
        }

        string contentText = "";
        var isError = false;
        if (Fields.Node(responsePart, "response") is JsonObject response)
        {
            isError = response.ContainsKey("error") && !response.ContainsKey("output");
            if (Fields.Str(response, "output") is { } output)
                contentText = output;
            else if (Fields.Str(response, "content") is { } content)
                contentText = content;
            else
                contentText = response.ToJsonString(GatewayJson.Options);
        }
        results.Add(new ToolResultPart(callId, name, [new TextPart(contentText)], isError));
    }

    private static void DecodeTools(JsonArray tools, UnifiedRequest req)
    {
        foreach (var tool in tools.OfType<JsonObject>())
        {
            foreach (var (key, value) in tool)
            {
                if (key is "functionDeclarations" or "function_declarations" && value is JsonArray declarations)
                {
                    foreach (var declaration in declarations.OfType<JsonObject>())
                    {
                        // parametersJsonSchema is the standard JSON-schema form and wins over the legacy
                        // OpenAPI-subset `parameters`.
                        var schema = Fields.Node(declaration, "parametersJsonSchema", "parameters_json_schema")
                                     ?? Fields.Node(declaration, "parameters");
                        req.Tools.Add(new UnifiedTool
                        {
                            Name = Fields.Str(declaration, "name") ?? "",
                            Description = Fields.Str(declaration, "description"),
                            InputSchema = schema?.DeepClone(),
                        });
                    }
                }
                else
                {
                    // googleSearch, google_search_retrieval, codeExecution, urlContext, …: builtin tool,
                    // re-emitted verbatim only when the upstream speaks Gemini again.
                    req.Tools.Add(new UnifiedTool
                    {
                        BuiltinType = key,
                        BuiltinOrigin = ApiProtocol.Gemini,
                        BuiltinRaw = new JsonObject { [key] = value?.DeepClone() },
                    });
                }
            }
        }
    }

    private static ToolChoice DecodeToolChoice(JsonObject fcc)
    {
        var mode = (Fields.Str(fcc, "mode") ?? "").ToUpperInvariant().Replace("MODE_", "", StringComparison.Ordinal);
        var allowed = Fields.Node(fcc, "allowedFunctionNames", "allowed_function_names") is JsonArray names
            ? names.OfType<JsonValue>().Where(v => v.TryGetValue<string>(out _)).Select(v => v.GetValue<string>()).ToArray()
            : [];
        return mode switch
        {
            "NONE" => new ToolChoice(ToolChoiceMode.None),
            "ANY" when allowed.Length == 1 => new ToolChoice(ToolChoiceMode.Specific, allowed[0]),
            "ANY" => new ToolChoice(ToolChoiceMode.Required),
            // VALIDATED only asks the model to validate against the list; AUTO is the closest IR mode.
            "VALIDATED" => new ToolChoice(ToolChoiceMode.Auto),
            _ => new ToolChoice(ToolChoiceMode.Auto),
        };
    }

    private static void DecodeGenerationConfig(JsonObject gen, UnifiedRequest req, JsonObject extGen)
    {
        if (Fields.Double(gen, "temperature") is { } temperature) req.Temperature = temperature;
        if (Fields.Double(gen, "topP", "top_p") is { } topP) req.TopP = topP;
        if (Fields.Int(gen, "topK", "top_k") is { } topK) req.TopK = topK;
        if (Fields.Int(gen, "maxOutputTokens", "max_output_tokens") is { } max) req.MaxOutputTokens = max;
        if (Fields.Node(gen, "stopSequences", "stop_sequences") is JsonArray stops)
        {
            req.Stop = stops.OfType<JsonValue>()
                .Where(v => v.TryGetValue<string>(out _))
                .Select(v => v.GetValue<string>())
                .ToList();
        }

        if (Fields.Str(gen, "responseMimeType", "response_mime_type") is { } mime)
        {
            if (mime == "application/json")
            {
                var schema = Fields.Node(gen, "responseJsonSchema", "response_json_schema")
                             ?? Fields.Node(gen, "responseSchema", "response_schema");
                req.ResponseFormat = new ResponseFormat
                {
                    Type = schema is null ? "json_object" : "json_schema",
                    Schema = schema?.DeepClone(),
                };
            }
            else
            {
                extGen["responseMimeType"] = mime;
            }
        }

        if (Fields.Node(gen, "thinkingConfig", "thinking_config") is JsonObject thinking)
        {
            var reasoning = new ReasoningConfig();
            if (Fields.Int(thinking, "thinkingBudget", "thinking_budget") is { } budget)
            {
                if (budget == 0) reasoning.Enabled = false;      // thinking explicitly off
                else if (budget > 0) reasoning.BudgetTokens = budget; // -1 = dynamic: on without a fixed budget
            }
            if (Fields.Bool(thinking, "includeThoughts", "include_thoughts") is { } include) reasoning.IncludeThoughts = include;
            if (Fields.Str(thinking, "thinkingLevel", "thinking_level") is { } level)
            {
                reasoning.Effort = level.StartsWith("THINKING_LEVEL_", StringComparison.OrdinalIgnoreCase)
                    ? level["THINKING_LEVEL_".Length..].ToLowerInvariant()
                    : level.ToLowerInvariant();
            }
            req.Reasoning = reasoning;
        }

        foreach (var (key, value) in gen)
        {
            if (!KnownGenKeys.Contains(key)) extGen[key] = value?.DeepClone();
        }
    }

    // ---------------------------------------------------------------- encode

    public static JsonObject Encode(UnifiedRequest req, RequestEncodeContext ctx)
    {
        var root = new JsonObject();

        EncodeSystem(req, root);
        root["contents"] = EncodeContents(req);

        EncodeTools(req, root);

        if (req.ToolChoice is { } choice)
        {
            var fcc = new JsonObject();
            switch (choice.Mode)
            {
                case ToolChoiceMode.Required:
                    fcc["mode"] = "ANY";
                    break;
                case ToolChoiceMode.Specific:
                    fcc["mode"] = "ANY";
                    if (choice.Name is not null) fcc["allowedFunctionNames"] = new JsonArray(choice.Name);
                    break;
                case ToolChoiceMode.None:
                    fcc["mode"] = "NONE";
                    break;
                case ToolChoiceMode.Auto:
                    fcc["mode"] = "AUTO";
                    break;
            }
            root["toolConfig"] = new JsonObject { ["functionCallingConfig"] = fcc };
        }

        var gen = EncodeGenerationConfig(req, ctx);

        // Merge back what the Gemini client sent and the IR could not express; fields set above win.
        if (req.Extensions.TryGetValue(ApiProtocol.Gemini, out var ext))
        {
            foreach (var (key, value) in ext)
            {
                if (key == "generationConfig")
                {
                    gen ??= new JsonObject();
                    if (value is JsonObject extGen)
                    {
                        foreach (var (genKey, genValue) in extGen)
                        {
                            if (!gen.ContainsKey(genKey)) gen[genKey] = genValue?.DeepClone();
                        }
                    }
                }
                else if (!root.ContainsKey(key))
                {
                    root[key] = value?.DeepClone();
                }
            }
        }
        if (gen is { Count: > 0 }) root["generationConfig"] = gen;

        return root;
    }

    private static void EncodeSystem(UnifiedRequest req, JsonObject root)
    {
        if (req.System.Count == 0) return;
        var parts = new JsonArray();
        foreach (var part in req.System)
        {
            if (part is TextPart { Text: { Length: > 0 } text })
                parts.AddNode(new JsonObject { ["text"] = text });
            else
                req.Warnings.Add("Only text system parts can be sent to Gemini; non-text part dropped.");
        }
        if (parts.Count > 0) root["systemInstruction"] = new JsonObject { ["parts"] = parts };
    }

    private static JsonArray EncodeContents(UnifiedRequest req)
    {
        var contents = new JsonArray();
        // id → name, filled in as tool calls are walked so later tool results can recover their function name.
        var callNames = new Dictionary<string, string>();
        var currentRole = "";
        JsonArray? currentParts = null;

        foreach (var message in req.Messages)
        {
            var role = message.Role == MessageRole.Assistant ? "model" : "user";
            if (currentParts is null || currentRole != role)
            {
                // Consecutive IR messages with the same role merge into one Gemini content.
                currentParts = [];
                currentRole = role;
                contents.AddNode(new JsonObject { ["role"] = role, ["parts"] = currentParts });
            }
            var messageStart = currentParts.Count;
            string? pendingSignature = null;
            foreach (var part in message.Parts)
            {
                if (part is ReasoningPart reasoning)
                {
                    // Thought text is never sent back; only this protocol's signature survives, attached to
                    // the next emitted part of the message.
                    if (reasoning.Origin == ApiProtocol.Gemini && reasoning.Signature is { Length: > 0 } signature)
                        pendingSignature = signature;
                    else if (reasoning.Origin is { } origin && origin != ApiProtocol.Gemini)
                        req.Warnings.Add($"Dropped {origin} reasoning (text and signature) not supported by Gemini upstream.");
                    continue;
                }
                if (EncodePart(part, req, callNames) is not { } wire) continue;
                if (pendingSignature is not null)
                {
                    wire["thoughtSignature"] = pendingSignature;
                    pendingSignature = null;
                }
                currentParts.AddNode(wire);
            }
            if (pendingSignature is { } leftover)
            {
                // Nothing followed the signature: put it back on the previous part of this message, or emit
                // a bare signature part so the upstream keeps its prompt-state anchor.
                if (currentParts.Count > messageStart)
                    ((JsonObject)currentParts[currentParts.Count - 1]!)["thoughtSignature"] = leftover;
                else
                    currentParts.AddNode(new JsonObject { ["thoughtSignature"] = leftover });
            }
        }

        for (var i = contents.Count - 1; i >= 0; i--)
        {
            if (contents[i] is JsonObject content && content["parts"] is JsonArray { Count: 0 }) contents.RemoveAt(i);
        }
        return contents;
    }

    private static JsonObject? EncodePart(ContentPart part, UnifiedRequest req, Dictionary<string, string> callNames)
    {
        switch (part)
        {
            case TextPart { Text: { Length: > 0 } text }:
                return new JsonObject { ["text"] = text };

            case ImagePart image:
                return EncodeMedia(image.Url, image.MediaType, image.Base64Data, req);

            case FilePart file:
                if (file.Url is null && file.Base64Data is null)
                {
                    if (file.FileId is not null)
                        req.Warnings.Add($"Gemini cannot address upstream file id '{file.FileId}'; file part dropped.");
                    return null;
                }
                return EncodeMedia(file.Url, file.MediaType, file.Base64Data, req);

            case ToolCallPart call:
                callNames[call.Id] = call.Name;
                JsonObject args;
                try
                {
                    args = JsonNode.Parse(call.ArgumentsJson) as JsonObject ?? [];
                }
                catch (JsonException)
                {
                    args = [];
                }
                return new JsonObject { ["functionCall"] = new JsonObject { ["name"] = call.Name, ["args"] = args } };

            case ToolResultPart result:
                callNames.TryGetValue(result.CallId, out var recovered);
                var name = result.Name ?? recovered ?? "tool";
                var resultText = string.Join("\n", result.Content.OfType<TextPart>().Select(p => p.Text));
                if (result.Content.Any(p => p is not TextPart))
                    req.Warnings.Add("Non-text tool result content dropped (Gemini functionResponse carries text only).");
                var response = new JsonObject();
                response[result.IsError ? "error" : "output"] = resultText;
                return new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = name, ["response"] = response } };

            default:
                return null; // ReasoningPart handled by the caller.
        }
    }

    private static JsonObject? EncodeMedia(string? url, string? mediaType, string? base64, UnifiedRequest req)
    {
        // data: URL → inlineData (same payload as Base64Data).
        if (url is not null && url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var (mime, data) = ParseDataUrl(url);
            if (data is not null) return new JsonObject { ["inlineData"] = InlineData(mime ?? mediaType, data) };
        }
        if (base64 is { Length: > 0 }) return new JsonObject { ["inlineData"] = InlineData(mediaType, base64) };
        if (url is not null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                                || url.StartsWith("gs://", StringComparison.OrdinalIgnoreCase)))
        {
            return new JsonObject
            {
                ["fileData"] = new JsonObject
                {
                    ["mimeType"] = mediaType ?? GuessMimeType(url),
                    ["fileUri"] = url,
                },
            };
        }
        req.Warnings.Add($"Media part with unsupported reference dropped: {Truncate(url ?? "(base64)", 80)}");
        return null;
    }

    private static JsonObject InlineData(string? mediaType, string data) => new()
    {
        ["mimeType"] = mediaType ?? "image/png", // Gemini requires an explicit MIME type.
        ["data"] = data,
    };

    private static (string? Mime, string? Data) ParseDataUrl(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0) return (null, null);
        var header = url[5..comma]; // after "data:"
        var semi = header.IndexOf(';');
        var mime = semi <= 0 ? null : header[..semi];
        return (mime, url[(comma + 1)..]);
    }

    private static void EncodeTools(UnifiedRequest req, JsonObject root)
    {
        var functions = new JsonArray();
        var builtins = new List<JsonObject>();
        foreach (var tool in req.Tools)
        {
            if (tool.BuiltinType is null)
            {
                var declaration = new JsonObject { ["name"] = tool.Name };
                if (tool.Description is { Length: > 0 } description) declaration["description"] = description;
                declaration["parametersJsonSchema"] = tool.InputSchema?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
                functions.AddNode(declaration);
            }
            else if (tool.BuiltinOrigin == ApiProtocol.Gemini && tool.BuiltinRaw is not null)
            {
                builtins.Add((JsonObject)tool.BuiltinRaw.DeepClone());
            }
            else
            {
                req.Warnings.Add($"Builtin tool '{tool.BuiltinType}' cannot be sent to a Gemini upstream; dropped.");
            }
        }
        var tools = new JsonArray();
        if (functions.Count > 0) tools.AddNode(new JsonObject { ["functionDeclarations"] = functions });
        foreach (var builtin in builtins) tools.AddNode(builtin);
        if (tools.Count > 0) root["tools"] = tools;
    }

    private static JsonObject? EncodeGenerationConfig(UnifiedRequest req, RequestEncodeContext ctx)
    {
        var gen = new JsonObject();
        if (req.MaxOutputTokens is { } max) gen["maxOutputTokens"] = max;
        if (req.Temperature is { } temperature) gen["temperature"] = temperature;
        if (req.TopP is { } topP) gen["topP"] = topP;
        if (req.TopK is { } topK) gen["topK"] = topK;
        if (req.Stop is { Count: > 0 } stop) gen["stopSequences"] = new JsonArray(stop.Select(s => JsonValue.Create(s)).ToArray());

        if (req.ResponseFormat is { Type: "json_object" or "json_schema" } format)
        {
            gen["responseMimeType"] = "application/json";
            if (format.Type == "json_schema" && format.Schema is not null)
                gen["responseJsonSchema"] = format.Schema.DeepClone();
        }

        if (req.Reasoning is { } reasoning)
        {
            var thinking = new JsonObject();
            if (!reasoning.Enabled) thinking["thinkingBudget"] = 0;
            else if (reasoning.BudgetTokens is { } budget) thinking["thinkingBudget"] = budget;
            else if (reasoning.Effort is { } effort) thinking["thinkingBudget"] = EffortBudget(effort, ctx.EffortBudgets);
            if (reasoning.IncludeThoughts) thinking["includeThoughts"] = true;
            if (thinking.Count > 0) gen["thinkingConfig"] = thinking;
        }
        return gen.Count > 0 ? gen : null;
    }

    /// <summary>Effort → thinking budget (minimal/low → Low, medium → Medium, high → High).</summary>
    private static int EffortBudget(string effort, EffortBudgets budgets) => effort switch
    {
        "minimal" or "low" => budgets.Low,
        "medium" => budgets.Medium,
        _ => budgets.High,
    };

    private static string GuessMimeType(string uri)
    {
        var clean = uri;
        var cut = clean.IndexOfAny(['?', '#']);
        if (cut >= 0) clean = clean[..cut];
        var dot = clean.LastIndexOf('.');
        if (dot < 0 || dot == clean.Length - 1) return "application/octet-stream";
        return clean[(dot + 1)..].ToLowerInvariant() switch
        {
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "heic" => "image/heic",
            "heif" => "image/heif",
            "bmp" => "image/bmp",
            "svg" => "image/svg+xml",
            "pdf" => "application/pdf",
            "txt" => "text/plain",
            "md" => "text/markdown",
            "html" or "htm" => "text/html",
            "csv" => "text/csv",
            "json" => "application/json",
            "xml" => "application/xml",
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "ogg" => "audio/ogg",
            "mp4" => "video/mp4",
            "mov" => "video/quicktime",
            "webm" => "video/webm",
            _ => "application/octet-stream",
        };
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>Per-request decode state: deterministic tool-call ids and call/response matching.</summary>
    private sealed class DecodeState
    {
        public Dictionary<string, int> CallCounts { get; } = new();
        public List<(string Id, string Name)> Calls { get; } = [];
        public HashSet<string> Matched { get; } = [];
    }
}
