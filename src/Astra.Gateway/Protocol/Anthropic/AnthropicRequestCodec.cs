using System.Text.Json.Nodes;
using Astra.Core;

namespace Astra.Gateway.Protocol.Anthropic;

/// <summary>
/// Anthropic Messages request mapping. <see cref="Decode"/> reads a client request body into the IR,
/// <see cref="Encode"/> writes the IR back into an upstream request body.
/// </summary>
internal static class AnthropicRequestCodec
{
    private const int MinBudgetTokens = 1024;

    /// <summary>Top-level request fields the IR expresses directly; everything else travels via Extensions.</summary>
    private static readonly HashSet<string> HandledTopLevel =
    [
        "model", "max_tokens", "system", "messages", "tools", "tool_choice", "temperature", "top_p", "top_k",
        "stop_sequences", "stream", "metadata", "thinking", "service_tier",
    ];

    // ---------------------------------------------------------------- decode (client → IR)

    public static UnifiedRequest Decode(JsonObject body)
    {
        var r = new UnifiedRequest
        {
            Model = Str(body, "model") ?? throw new ProtocolException("Anthropic 请求缺少 model。"),
            MaxOutputTokens = Int(body, "max_tokens"),
            Stream = Bool(body, "stream") ?? false,
            Temperature = Dbl(body, "temperature"),
            TopP = Dbl(body, "top_p"),
            TopK = Int(body, "top_k"),
            ServiceTier = Str(body, "service_tier"),
        };
        var warnings = r.Warnings;

        if (body["stop_sequences"] is JsonArray stops)
        {
            var list = new List<string>();
            foreach (var s in stops)
                if (s is JsonValue v && v.TryGetValue<string>(out var text) && text.Length > 0)
                    list.Add(text);
            if (list.Count > 0) r.Stop = list;
        }

        switch (body["system"])
        {
            case null:
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                r.System.Add(new TextPart(s));
                break;
            case JsonArray blocks:
                foreach (var b in blocks)
                {
                    if (b is not JsonObject o) continue;
                    if (Str(o, "type") == "text" && Str(o, "text") is { } text)
                        r.System.Add(new TextPart(text)); // cache_control on system blocks is ignored here
                    else if (Str(o, "type") is { } other)
                        AddWarning(warnings, $"dropped unsupported system block '{other}'");
                }
                break;
            default:
                throw new ProtocolException("system 必须是字符串或 content block 数组。");
        }

        if (body["messages"] is not JsonArray messages || messages.Count == 0)
            throw new ProtocolException("messages 必须是非空数组。");
        foreach (var m in messages)
        {
            if (m is not JsonObject msg)
                throw new ProtocolException("每条 message 必须是对象。");
            var role = Str(msg, "role") switch
            {
                "user" => MessageRole.User,
                "assistant" => MessageRole.Assistant,
                var other => throw new ProtocolException($"未知的 message role：{other ?? "(missing)"}。"),
            };
            var parts = new List<ContentPart>();
            switch (msg["content"])
            {
                case JsonValue v when v.TryGetValue<string>(out var s):
                    parts.Add(new TextPart(s));
                    break;
                case JsonArray blocks:
                    foreach (var b in blocks)
                        if (b is JsonObject o && ParseBlock(o, warnings) is { } part)
                            parts.Add(part);
                    break;
                default:
                    throw new ProtocolException("message.content 必须是字符串或 content block 数组。");
            }
            r.Messages.Add(new UnifiedMessage(role, parts));
        }

        if (body["tools"] is JsonArray tools)
        {
            foreach (var t in tools)
            {
                if (t is not JsonObject tool)
                    throw new ProtocolException("每个 tool 必须是对象。");
                var type = Str(tool, "type");
                if (type is null or "custom")
                {
                    r.Tools.Add(new UnifiedTool
                    {
                        Name = Str(tool, "name") ?? throw new ProtocolException("tool 缺少 name。"),
                        Description = Str(tool, "description"),
                        InputSchema = tool["input_schema"]?.DeepClone(),
                    });
                }
                else
                {
                    // Typed / server tools (web_search_*, bash_*, text_editor_*, code_execution_*, computer_* …):
                    // kept verbatim, only re-emitted to an Anthropic upstream (plan §6.4).
                    r.Tools.Add(new UnifiedTool
                    {
                        Name = Str(tool, "name") ?? type,
                        BuiltinType = type,
                        BuiltinOrigin = ApiProtocol.Anthropic,
                        BuiltinRaw = (JsonObject)tool.DeepClone(),
                    });
                }
            }
        }

        if (body["tool_choice"] is JsonObject tc)
        {
            var mode = Str(tc, "type") switch
            {
                "auto" => ToolChoiceMode.Auto,
                "any" => ToolChoiceMode.Required,
                "tool" => ToolChoiceMode.Specific,
                "none" => ToolChoiceMode.None,
                var other => throw new ProtocolException($"未知的 tool_choice.type：{other ?? "(missing)"}。"),
            };
            var name = Str(tc, "name");
            if (mode == ToolChoiceMode.Specific && name is null)
                throw new ProtocolException("tool_choice.type=tool 需要 name。");
            r.ToolChoice = new ToolChoice(mode, name);
            if (Bool(tc, "disable_parallel_tool_use") == true)
                r.ParallelToolCalls = false;
        }

        if (body["metadata"] is JsonObject meta)
        {
            r.User = Str(meta, "user_id");
            var rest = new JsonObject();
            foreach (var (key, value) in meta)
                if (key != "user_id" && value is not null)
                    rest[key] = value.DeepClone();
            if (rest.Count > 0)
                Extensions(r)["metadata"] = rest;
        }

        if (body["thinking"] is JsonObject th && Str(th, "type") is { } thType)
        {
            var cfg = r.Reasoning = new ReasoningConfig();
            switch (thType)
            {
                case "enabled":
                    cfg.BudgetTokens = Int(th, "budget_tokens");
                    cfg.IncludeThoughts = true;
                    break;
                case "disabled":
                    cfg.Enabled = false;
                    break;
                // Future types (e.g. adaptive): reasoning stays enabled, no budget mapping.
            }
        }

        foreach (var (key, value) in body)
            if (!HandledTopLevel.Contains(key) && value is not null)
                Extensions(r)[key] = value.DeepClone();

        return r;
    }

    private static ContentPart? ParseBlock(JsonObject b, List<string> warnings)
    {
        switch (Str(b, "type"))
        {
            case "text":
                return Str(b, "text") is { } text ? new TextPart(text) : null;
            case "image":
                return ImagePart(b) ?? throw new ProtocolException("image block 的 source 形状无效。");
            case "document":
                return DocumentPart(b);
            case "tool_use":
                var id = Str(b, "id") ?? throw new ProtocolException("tool_use 缺少 id。");
                var name = Str(b, "name") ?? throw new ProtocolException("tool_use 缺少 name。");
                var input = b["input"] as JsonObject ?? [];
                return new ToolCallPart(id, name, input.ToJsonString(GatewayJson.Options));
            case "tool_result":
                return ToolResult(b);
            case "thinking":
                return new ReasoningPart(Str(b, "thinking"), Str(b, "signature"), Origin: ApiProtocol.Anthropic);
            case "redacted_thinking":
                return new ReasoningPart(null, EncryptedContent: Str(b, "data"), Origin: ApiProtocol.Anthropic);
            case null:
                return null;
            default:
                // server_tool_use / web_search_tool_result / anything new: dropped, not round-tripped.
                AddWarning(warnings, $"dropped unsupported content block '{Str(b, "type")}'");
                return null;
        }
    }

    private static ImagePart? ImagePart(JsonObject b)
    {
        if (b["source"] is not JsonObject src) return null;
        switch (Str(src, "type"))
        {
            case "base64":
                return new ImagePart(null, Str(src, "media_type") ?? "image/png", Str(src, "data") ?? "");
            case "url":
                return Str(src, "url") is { } url ? new ImagePart(url, null, null) : null;
            default:
                return null;
        }
    }

    private static ContentPart DocumentPart(JsonObject b)
    {
        if (b["source"] is not JsonObject src)
            throw new ProtocolException("document block 缺少 source。");
        switch (Str(src, "type"))
        {
            case "base64":
                return new FilePart(null, Str(src, "media_type") ?? "application/pdf", Str(src, "data") ?? "");
            case "url":
                return Str(src, "url") is { } url
                    ? new FilePart(url, null, null)
                    : throw new ProtocolException("document url source 缺少 url。");
            case "text":
                return Str(src, "data") is { } text
                    ? new TextPart(text)
                    : throw new ProtocolException("document text source 缺少 data。");
            case "file":
                return new FilePart(null, null, null, FileId: Str(src, "file_id"));
            default:
                throw new ProtocolException("document block 的 source 形状无效。");
        }
    }

    private static ToolResultPart ToolResult(JsonObject b)
    {
        var callId = Str(b, "tool_use_id") ?? throw new ProtocolException("tool_result 缺少 tool_use_id。");
        var content = new List<ContentPart>();
        switch (b["content"])
        {
            case null:
                break;
            case JsonValue v when v.TryGetValue<string>(out var s):
                content.Add(new TextPart(s));
                break;
            case JsonArray blocks:
                foreach (var c in blocks)
                {
                    if (c is not JsonObject o) continue;
                    switch (Str(o, "type"))
                    {
                        case "text" when Str(o, "text") is { } text:
                            content.Add(new TextPart(text));
                            break;
                        case "image" when ImagePart(o) is { } image:
                            content.Add(image);
                            break;
                    }
                }
                break;
            default:
                throw new ProtocolException("tool_result.content 必须是字符串或 content block 数组。");
        }
        return new ToolResultPart(callId, null, content, Bool(b, "is_error") ?? false);
    }

    private static JsonObject Extensions(UnifiedRequest r) =>
        r.Extensions.TryGetValue(ApiProtocol.Anthropic, out var ext)
            ? ext
            : r.Extensions[ApiProtocol.Anthropic] = [];

    private static void AddWarning(List<string> warnings, string message)
    {
        if (!warnings.Contains(message)) warnings.Add(message);
    }

    internal static string? Str(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool? Bool(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static int? Int(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : null;

    private static double? Dbl(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    // ---------------------------------------------------------------- encode (IR → upstream)

    public static JsonObject Encode(UnifiedRequest r, RequestEncodeContext ctx)
    {
        var warnings = r.Warnings;
        var root = new JsonObject();

        // Thinking first: it constrains max_tokens and the sampling knobs (Anthropic constraints a–c).
        var budget = ThinkingBudget(r, ctx);
        if (budget is not null && ForeignToolHistoryWithoutThinking(r))
        {
            // (a) Anthropic requires every assistant tool_use turn to replay its signed thinking block.
            // History translated from another protocol has no Anthropic signature → thinking must go off.
            budget = null;
            AddWarning(warnings, "thinking disabled: an assistant tool-use turn has no signed Anthropic thinking block (history from another protocol)");
        }

        root["model"] = ctx.UpstreamModel;
        // Anthropic requires max_tokens (plan §6.4): request value → effective model limit → 8192.
        var maxTokens = r.MaxOutputTokens ?? ctx.DefaultMaxOutputTokens ?? 8192;
        if (budget is { } b && b >= maxTokens)
        {
            // (b) budget_tokens must be < max_tokens; leave ~1k of room for the visible answer.
            maxTokens = b + 1024;
            AddWarning(warnings, $"raised max_tokens to {maxTokens} so it stays above budget_tokens");
        }
        root["max_tokens"] = maxTokens;
        if (budget is { } enabled)
            root["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = enabled };

        if (r.System.OfType<TextPart>().ToList() is { Count: > 0 } system)
        {
            var blocks = new JsonArray();
            foreach (var part in system)
                blocks.AddNode(new JsonObject { ["type"] = "text", ["text"] = part.Text });
            if (ctx.AutoCacheControl)
                ((JsonObject)blocks[^1]!)["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            root["system"] = blocks;
        }

        if (r.Tools.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in r.Tools)
            {
                if (t.BuiltinType is null)
                {
                    var tool = new JsonObject { ["name"] = t.Name };
                    if (t.Description is { Length: > 0 } description) tool["description"] = description;
                    // Anthropic requires an input_schema object on custom tools.
                    tool["input_schema"] = t.InputSchema?.DeepClone() ?? new JsonObject { ["type"] = "object" };
                    tools.AddNode(tool);
                }
                else if (t.BuiltinOrigin == ApiProtocol.Anthropic && t.BuiltinRaw is { } raw)
                {
                    tools.AddNode(raw.DeepClone());
                }
                else
                {
                    AddWarning(warnings, $"dropped builtin tool '{t.BuiltinType}': only Anthropic builtins can be sent to an Anthropic upstream");
                }
            }
            if (tools.Count > 0)
            {
                // One 5m breakpoint on the last tool (plus system and the message tail below) fills the
                // 4-breakpoint limit.
                if (ctx.AutoCacheControl)
                    ((JsonObject)tools[^1]!)["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
                root["tools"] = tools;
            }
        }

        var merged = MergeMessages(r.Messages, warnings);
        var messages = new JsonArray();
        foreach (var m in merged)
        {
            // Anthropic replays tool results in the user turn that follows the assistant tool_use turn,
            // so tool_result blocks go first; everything else keeps its original order.
            var parts = new List<ContentPart>();
            if (m.Role == MessageRole.User)
            {
                parts.AddRange(m.Parts.Where(p => p is ToolResultPart));
                parts.AddRange(m.Parts.Where(p => p is not ToolResultPart));
            }
            else
            {
                parts = m.Parts;
            }
            var blocks = new JsonArray();
            foreach (var part in parts)
                if (PartBlock(part, warnings) is { } block)
                    blocks.AddNode(block);
            messages.AddNode(new JsonObject
            {
                ["role"] = m.Role == MessageRole.User ? "user" : "assistant",
                ["content"] = blocks,
            });
        }
        root["messages"] = messages;

        // Incremental conversation caching: together with the system/tool marks above, the two
        // message-tail marks fill the 4-breakpoint budget. Each request writes the growing history at
        // the tail, so the next request reads it as a cache hit; the second-to-last mark gives
        // Anthropic's 20-block lookback an earlier entry to find when one turn appends many blocks.
        if (ctx.AutoCacheControl && messages.Count > 0)
        {
            if (messages.Count > 1) MarkLastCacheableBlock(messages[^2] as JsonObject);
            MarkLastCacheableBlock(messages[^1] as JsonObject);
        }

        if (r.ToolChoice is { } choice)
        {
            var toolChoice = new JsonObject
            {
                ["type"] = choice.Mode switch
                {
                    ToolChoiceMode.None => "none",
                    ToolChoiceMode.Required => "any",
                    ToolChoiceMode.Specific => "tool",
                    _ => "auto",
                },
            };
            if (choice.Mode == ToolChoiceMode.Specific)
                toolChoice["name"] = choice.Name ?? "";
            if (r.ParallelToolCalls == false)
                toolChoice["disable_parallel_tool_use"] = true;
            root["tool_choice"] = toolChoice;
        }
        else if (r.ParallelToolCalls == false && r.Tools.Count > 0)
        {
            // Anthropic has no top-level parallel_tool_calls; disable_parallel_tool_use lives inside tool_choice.
            root["tool_choice"] = new JsonObject { ["type"] = "auto", ["disable_parallel_tool_use"] = true };
        }

        if (budget is null)
        {
            if (r.Temperature is { } temperature) root["temperature"] = temperature;
            if (r.TopP is { } topP) root["top_p"] = topP;
            if (r.TopK is { } topK) root["top_k"] = topK;
        }
        else
        {
            // (c) Thinking forbids temperature/top_k and only accepts top_p >= 0.95.
            if (r.Temperature is not null)
                AddWarning(warnings, "dropped temperature: unsupported while thinking is enabled");
            if (r.TopK is not null)
                AddWarning(warnings, "dropped top_k: unsupported while thinking is enabled");
            if (r.TopP is { } thinkingTopP)
            {
                if (thinkingTopP >= 0.95)
                    root["top_p"] = thinkingTopP;
                else
                    AddWarning(warnings, $"dropped top_p {thinkingTopP}: Anthropic only accepts top_p >= 0.95 while thinking is enabled");
            }
        }

        if (r.Stop is { Count: > 0 } stop)
        {
            var sequences = new JsonArray();
            foreach (var s in stop)
                sequences.AddNode(JsonValue.Create(s));
            root["stop_sequences"] = sequences;
        }
        if (r.User is { Length: > 0 } user)
            root["metadata"] = new JsonObject { ["user_id"] = user };
        if (r.Stream)
            root["stream"] = true;
        if (r.ServiceTier is { Length: > 0 } tier)
            root["service_tier"] = tier;

        if (r.ResponseFormat is not null)
            AddWarning(warnings, "dropped response_format: Anthropic Messages has no equivalent");

        MergeExtensions(root, r);
        return root;
    }

    /// <summary>Effort/budget → thinking budget_tokens, null when thinking is off or unspecified.</summary>
    private static int? ThinkingBudget(UnifiedRequest r, RequestEncodeContext ctx)
    {
        if (r.Reasoning is not { } cfg || !cfg.Enabled) return null;
        if (cfg.BudgetTokens is { } budget) return Math.Max(MinBudgetTokens, budget);
        if (cfg.Effort is not { Length: > 0 } effort) return null;
        var budgets = ctx.EffortBudgets;
        return Math.Max(MinBudgetTokens, effort switch
        {
            "minimal" or "low" => budgets.Low,
            "medium" => budgets.Medium,
            "high" => budgets.High,
            _ => budgets.Medium,
        });
    }

    private static bool ForeignToolHistoryWithoutThinking(UnifiedRequest r)
    {
        foreach (var m in r.Messages)
        {
            if (m.Role != MessageRole.Assistant || !m.Parts.Any(p => p is ToolCallPart)) continue;
            var signed = m.Parts.OfType<ReasoningPart>().Any(p =>
                p.Origin == ApiProtocol.Anthropic &&
                (p.Signature is { Length: > 0 } || p.EncryptedContent is { Length: > 0 }));
            if (!signed) return true;
        }
        return false;
    }

    /// <summary>
    /// Marks the last cacheable block of one message with a 5m cache_control breakpoint, walking back
    /// past blocks Anthropic refuses to mark (thinking) or cannot cache (empty text). Skips the message
    /// when none qualifies.
    /// </summary>
    private static void MarkLastCacheableBlock(JsonObject? message)
    {
        if (message?["content"] is not JsonArray blocks) return;
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i] is not JsonObject block) continue;
            var cacheable = Str(block, "type") switch
            {
                "text" => (string?)block["text"] is { Length: > 0 }, // empty text blocks cannot be cached
                "image" or "document" or "tool_use" or "tool_result" => true,
                _ => false,
            };
            if (!cacheable) continue;
            block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            return;
        }
    }

    /// <summary>Anthropic combines consecutive same-role turns, so we merge them up front.</summary>
    private static List<UnifiedMessage> MergeMessages(IReadOnlyList<UnifiedMessage> messages, List<string> warnings)
    {
        var merged = new List<UnifiedMessage>();
        foreach (var m in messages)
        {
            if (merged.Count > 0 && merged[^1].Role == m.Role)
                merged[^1].Parts.AddRange(m.Parts);
            else
                merged.Add(new UnifiedMessage(m.Role, [.. m.Parts]));
        }
        if (merged.Count > 0 && merged[0].Role == MessageRole.Assistant)
            // Kept as-is (prefill style) — the IR cannot invent a user turn; the upstream may reject it.
            AddWarning(warnings, "first message is assistant; Anthropic normally requires the conversation to start with a user message");
        return merged;
    }

    private static JsonObject? PartBlock(ContentPart p, List<string> warnings)
    {
        switch (p)
        {
            case TextPart t:
                return new JsonObject { ["type"] = "text", ["text"] = t.Text };
            case ImagePart image:
                return ImageBlock(image, warnings);
            case FilePart file:
                return DocumentBlock(file, warnings);
            case ToolCallPart call:
                return new JsonObject
                {
                    ["type"] = "tool_use",
                    ["id"] = call.Id,
                    ["name"] = call.Name,
                    ["input"] = ParseArguments(call.ArgumentsJson),
                };
            case ToolResultPart result:
            {
                var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = result.CallId };
                switch (result.Content)
                {
                    case [TextPart alone]:
                        // Single text content is written as a plain string, the most common client shape.
                        block["content"] = alone.Text;
                        break;
                    case { Count: > 0 } parts:
                    {
                        var content = new JsonArray();
                        foreach (var c in parts)
                        {
                            switch (c)
                            {
                                case TextPart text:
                                    content.AddNode(new JsonObject { ["type"] = "text", ["text"] = text.Text });
                                    break;
                                case ImagePart image when ImageBlock(image, warnings) is { } imageBlock:
                                    content.AddNode(imageBlock);
                                    break;
                            }
                        }
                        block["content"] = content;
                        break;
                    }
                }
                if (result.IsError) block["is_error"] = true;
                return block;
            }
            case ReasoningPart reasoning:
            {
                if (reasoning.Origin != ApiProtocol.Anthropic || reasoning.Signature is not { Length: > 0 } && reasoning.EncryptedContent is not { Length: > 0 })
                {
                    // Signatures / encrypted reasoning never cross protocols (plan §6.4); unsigned thinking
                    // cannot be replayed to Anthropic either, so it is dropped rather than corrupted.
                    AddWarning(warnings, "dropped reasoning: only signed Anthropic thinking can be replayed to an Anthropic upstream");
                    return null;
                }
                if (reasoning.Signature is { Length: > 0 } signature)
                    return new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning.Text ?? "", ["signature"] = signature };
                return new JsonObject { ["type"] = "redacted_thinking", ["data"] = reasoning.EncryptedContent! };
            }
            default:
                return null;
        }
    }

    private static JsonObject? ImageBlock(ImagePart image, List<string> warnings)
    {
        if (image.Base64Data is { Length: > 0 } data)
        {
            return new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.MediaType ?? "image/png", ["data"] = data },
            };
        }
        if (image.Url is { } url)
        {
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (ParseDataUrl(url) is { } parsed)
                {
                    return new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = parsed.mediaType, ["data"] = parsed.data },
                    };
                }
                AddWarning(warnings, "dropped image: unsupported data URL");
                return null;
            }
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "url", ["url"] = url },
                };
            }
        }
        AddWarning(warnings, "dropped image: neither base64 data nor an http(s) URL");
        return null;
    }

    private static (string mediaType, string data)? ParseDataUrl(string url)
    {
        var comma = url.IndexOf(',');
        if (comma < 0) return null;
        var header = url[5..comma]; // after "data:"
        var payload = url[(comma + 1)..];
        var semicolon = header.IndexOf(';');
        var mediaType = semicolon < 0 ? header : header[..semicolon];
        if (mediaType.Length == 0 || !header.Contains("base64", StringComparison.OrdinalIgnoreCase)) return null;
        return (mediaType, payload);
    }

    private static JsonObject? DocumentBlock(FilePart file, List<string> warnings)
    {
        // IR FilePart models documents; treat it as a PDF unless it is clearly something else.
        var isPdf = file.MediaType is null
                    || file.MediaType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                    || (file.FileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isPdf)
        {
            AddWarning(warnings, file.FileName is { } name
                ? $"dropped file '{name}': Anthropic documents only support PDFs"
                : "dropped file: Anthropic documents only support PDFs");
            return null;
        }
        JsonObject source;
        if (file.Base64Data is { Length: > 0 } data)
        {
            source = new JsonObject { ["type"] = "base64", ["media_type"] = file.MediaType ?? "application/pdf", ["data"] = data };
        }
        else if (file.FileId is { Length: > 0 } fileId)
        {
            source = new JsonObject { ["type"] = "file", ["file_id"] = fileId };
        }
        else if (file.Url is { Length: > 0 } url)
        {
            source = new JsonObject { ["type"] = "url", ["url"] = url };
        }
        else
        {
            AddWarning(warnings, "dropped document: no usable source");
            return null;
        }
        var block = new JsonObject { ["type"] = "document", ["source"] = source };
        if (file.FileName is { Length: > 0 } fileName)
            block["title"] = fileName;
        return block;
    }

    private static JsonNode ParseArguments(string argumentsJson)
    {
        try
        {
            return JsonNode.Parse(argumentsJson) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>
    /// Merges Extensions[Anthropic] back in: keys the encoder already produced win; when both sides hold
    /// objects (e.g. metadata) the encoder's inner fields win and the rest is merged.
    /// </summary>
    private static void MergeExtensions(JsonObject root, UnifiedRequest r)
    {
        if (!r.Extensions.TryGetValue(ApiProtocol.Anthropic, out var ext)) return;
        foreach (var (key, value) in ext)
        {
            if (value is null) continue;
            if (root.TryGetPropertyValue(key, out var existing) && existing is JsonObject into && value is JsonObject from)
            {
                foreach (var (innerKey, innerValue) in from)
                    if (innerValue is not null && !into.ContainsKey(innerKey))
                        into[innerKey] = innerValue.DeepClone();
            }
            else if (existing is null)
            {
                root[key] = value.DeepClone();
            }
        }
    }
}
