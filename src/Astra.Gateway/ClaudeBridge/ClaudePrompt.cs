using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Astra.Gateway.ClaudeBridge;

/// <summary>One of the caller's tools, as the run is offered it.</summary>
public sealed record ClaudeToolDef(string Name, string BridgeName, string? Description, JsonObject Schema);

/// <summary>
/// A Messages request, read for the Claude bridge (ported from magpie's <c>bridgeRequest</c> / <c>renderClaudePrompt</c>, MIT): the
/// gateway has already translated whatever the caller spoke into Anthropic's Messages format, so this is the one shape the bridge reads.
/// It says which messages the request's conversation holds (<see cref="Digests"/>, how a turn finds the run that answered the
/// conversation so far) and how a run that has to start anew is told the conversation.
/// </summary>
public sealed class ClaudeRequest
{
    private const string BillingPrefix = "x-anthropic-billing-header:";
    private const string HistoryOpen = "<conversation_history>\n";
    private const string PastCallsNote =
        "The tool calls and results above are a record of what already happened in this conversation; to call a tool now, use the tools you are offered.";
    private const string ImageNote =
        "Images in the messages above were sent with those messages; the notes of where they are kept belong to the program, not to the conversation.";

    public string Model { get; private init; } = "";
    public bool Stream { get; private init; }
    public string System { get; private init; } = "";
    public IReadOnlyList<ClaudeToolDef> Tools { get; private init; } = [];
    public JsonArray Messages { get; private init; } = [];

    /// <summary><c>low</c> … <c>max</c> as the request asked, else null.</summary>
    public string? Effort { get; private init; }

    /// <summary>The caller turned thinking off (Claude Code's small side asks do).</summary>
    public bool ThinkingOff { get; private init; }

    /// <summary>"none" | "required" | "name:&lt;tool&gt;" | null.</summary>
    public string? ToolChoice { get; private init; }

    /// <summary>One digest per message: role and the words and call ids in it, not whitespace, thinking or signatures.</summary>
    public IReadOnlyList<string> Digests { get; private init; } = [];

    /// <summary>The tools' identity: a run is only given a turn that offers the tools it was started with.</summary>
    public string ToolsKey { get; private init; } = "";

    /// <summary>The caller's instructions' identity (with the tool choice): part of what a run was started with.</summary>
    public string SystemKey { get; private init; } = "";

    public IReadOnlyDictionary<string, string> DeclaredByBridgeName { get; private init; } = new Dictionary<string, string>();

    public static ClaudeRequest Parse(JsonObject body)
    {
        var tools = new List<ClaudeToolDef>();
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (body["tools"] is JsonArray list)
        {
            foreach (var t in list.OfType<JsonObject>())
            {
                // server tools (web search, code execution …) carry a "type" other than custom and no schema: nothing to bridge
                if (t["name"] is not JsonValue nv || !nv.TryGetValue<string>(out var name) || name.Length == 0) continue;
                if (t["type"] is JsonValue tv && tv.TryGetValue<string>(out var type) && type != "custom" && t["input_schema"] is null) continue;
                var bridge = ClaudeNames.BridgeName(name);
                names[bridge] = name;
                tools.Add(new ClaudeToolDef(name, bridge, t["description"]?.GetValue<string>(), ClaudeNames.ObjectSchema(t["input_schema"])));
            }
        }
        var messages = body["messages"] as JsonArray ?? [];
        var choice = ToolChoiceOf(body["tool_choice"]);
        // a turn that may call no tool (a title or summary asked on the side) is offered none: its own run, since the tools are part of what a run was started with
        if (choice == "none") { tools.Clear(); names.Clear(); }
        var system = SystemOf(body["system"]);
        string? effort = null;
        if (body["output_config"]?["effort"] is JsonValue ev && ev.TryGetValue<string>(out var e) && e is "low" or "medium" or "high" or "xhigh" or "max") effort = e;
        var thinking = body["thinking"] as JsonObject;
        var off = thinking?["type"] is JsonValue tt && tt.TryGetValue<string>(out var tk) && tk == "disabled";
        if (effort is null && thinking?["budget_tokens"] is JsonValue bv && bv.TryGetValue<int>(out var budget))
            effort = budget <= 2048 ? "low" : budget <= 8192 ? "medium" : budget <= 24576 ? "high" : "xhigh";
        return new ClaudeRequest
        {
            Model = body["model"]?.GetValue<string>() ?? "",
            Stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var st) && st,
            System = system,
            Tools = tools,
            Messages = (JsonArray)messages.DeepClone(),
            Effort = effort,
            ThinkingOff = off,
            ToolChoice = choice,
            Digests = messages.OfType<JsonObject>().Select(Digest).ToList(),
            ToolsKey = Hash(string.Join('\n', tools.Select(t => t.Name + "\0" + t.Description + "\0" + t.Schema.ToJsonString()))),
            SystemKey = Hash(system + "\0" + choice),
            DeclaredByBridgeName = names,
        };
    }

    // ------------------------------------------------------------------ what the request holds

    private static string? ToolChoiceOf(JsonNode? node)
    {
        if (node is not JsonObject o || o["type"] is not JsonValue t || !t.TryGetValue<string>(out var type)) return null;
        return type switch
        {
            "none" => "none",
            "any" => "required",
            "tool" when o["name"] is JsonValue n && n.TryGetValue<string>(out var name) => "name:" + name,
            _ => null,
        };
    }

    private static string SystemOf(JsonNode? node)
    {
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return WithoutBillingHeader(s);
        if (node is not JsonArray a) return "";
        var parts = a.OfType<JsonObject>().Select(b => b["text"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : "")
            .Where(t => t.Length > 0 && !t.StartsWith(BillingPrefix, StringComparison.Ordinal));
        return string.Join("\n\n", parts).Trim();
    }

    private static string WithoutBillingHeader(string text) =>
        text.StartsWith(BillingPrefix, StringComparison.Ordinal) ? (text.IndexOf('\n') is var i and >= 0 ? text[(i + 1)..] : "").Trim() : text;

    private static IEnumerable<JsonObject> Blocks(JsonObject message)
    {
        if (message["content"] is JsonValue v && v.TryGetValue<string>(out var text))
            return [new JsonObject { ["type"] = "text", ["text"] = text }];
        return message["content"] is JsonArray a ? a.OfType<JsonObject>() : [];
    }

    private static string TypeOf(JsonObject block) => block["type"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : "";

    private static string Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    /// <summary>The words and call ids of a message, whitespace-insensitive (a client may re-wrap what it was told).</summary>
    private static string Digest(JsonObject message)
    {
        var sb = new StringBuilder(Str(message, "role")).Append('\n');
        foreach (var b in Blocks(message))
        {
            switch (TypeOf(b))
            {
                case "text":
                    foreach (var word in Str(b, "text").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) sb.Append(word);
                    break;
                case "tool_use": sb.Append("\u0001use:").Append(Str(b, "id")); break;
                case "tool_result": sb.Append("\u0001res:").Append(Str(b, "tool_use_id")); break;
            }
        }
        return Hash(sb.ToString())[..16];
    }

    /// <summary>The digest of a reply the run gave (the assistant message the caller will send back).</summary>
    public static string DigestOfReply(string text, IEnumerable<string> toolUseIds)
    {
        var message = new JsonObject { ["role"] = "assistant" };
        var content = new JsonArray();
        if (text.Length > 0) content.Push(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var id in toolUseIds) content.Push(new JsonObject { ["type"] = "tool_use", ["id"] = id });
        message["content"] = content;
        return Digest(message);
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>The tool results the last message carries, by tool_use id; empty when it carries none.</summary>
    public IReadOnlyDictionary<string, ClaudeToolResult> ToolResults(int fromMessage)
    {
        var results = new Dictionary<string, ClaudeToolResult>(StringComparer.Ordinal);
        for (var i = fromMessage; i < Messages.Count; i++)
        {
            if (Messages[i] is not JsonObject m || Str(m, "role") != "user") continue;
            foreach (var b in Blocks(m))
            {
                if (TypeOf(b) != "tool_result") continue;
                var id = Str(b, "tool_use_id");
                if (id.Length == 0) continue;
                results[id] = new ClaudeToolResult(McpContent(b["content"]), b["is_error"] is JsonValue ev && ev.TryGetValue<bool>(out var err) && err);
            }
        }
        return results;
    }

    /// <summary>A tool result's content as MCP content blocks (text and image).</summary>
    private static JsonArray McpContent(JsonNode? content)
    {
        var result = new JsonArray();
        if (content is JsonValue v && v.TryGetValue<string>(out var s))
        {
            result.Push(new JsonObject { ["type"] = "text", ["text"] = s });
            return result;
        }
        if (content is JsonArray a)
        {
            foreach (var b in a.OfType<JsonObject>())
            {
                switch (TypeOf(b))
                {
                    case "text": result.Push(new JsonObject { ["type"] = "text", ["text"] = Str(b, "text") }); break;
                    case "image" when b["source"] is JsonObject src && Str(src, "type") == "base64":
                        result.Push(new JsonObject { ["type"] = "image", ["data"] = Str(src, "data"), ["mimeType"] = Str(src, "media_type") });
                        break;
                    default: result.Push(new JsonObject { ["type"] = "text", ["text"] = b.ToJsonString() }); break;
                }
            }
        }
        if (result.Count == 0) result.Push(new JsonObject { ["type"] = "text", ["text"] = "" });
        return result;
    }

    // ------------------------------------------------------------------ what a run is told

    /// <summary>The caller's instructions as a run reads them: after <c>claude</c>'s own, wrapped, with the tool choice in words.</summary>
    private string Instructions()
    {
        var sb = new StringBuilder(System);
        if (ToolChoice == "required") sb.Append("\nYou must call at least one available tool before answering.");
        else if (ToolChoice is { } c && c.StartsWith("name:", StringComparison.Ordinal))
            sb.Append("\nYou must call the ").Append(DeclaredByBridgeName.FirstOrDefault(p => p.Value == c[5..]).Key ?? c[5..]).Append(" tool.");
        return sb.ToString().Trim();
    }

    /// <summary>
    /// The first user message of a run that is told the whole conversation: the caller's instructions, the turns already answered as
    /// history and the turn to answer (from the message after the last reply that calls no tool), told as one stretch of
    /// Human:/Assistant: text with images as image blocks.
    /// </summary>
    public JsonArray RenderWhole()
    {
        var pieces = new List<JsonNode>();
        var text = new StringBuilder();
        var instructions = Instructions();
        if (instructions.Length > 0)
            text.Append("<external_system_instructions>\n").Append(instructions).Append("\n</external_system_instructions>\n\n");
        var split = TurnStart();
        for (var i = 0; i < Messages.Count; i++)
        {
            if (Messages[i] is not JsonObject m) continue;
            if (split > 0 && i == 0) text.Append(HistoryOpen);
            if (split > 0 && i == split) text.Append("</conversation_history>\n\n<current_turn>\n");
            text.Append(Str(m, "role") == "assistant" ? "Assistant: " : "Human: ");
            Render(m, pieces, text);
            text.Append("\n\n");
        }
        if (Messages.OfType<JsonObject>().Any(m => Blocks(m).Any(b => TypeOf(b) is "tool_use" or "tool_result")))
            text.Append(PastCallsNote).Append("\n\n");
        if (CountImages(Messages) > 0) text.Append(ImageNote).Append("\n\n");
        if (split > 0) text.Append("</current_turn>");
        return Flush(pieces, text);
    }

    /// <summary>The messages from <paramref name="from"/> on, for a run that has the conversation before them (a user's next turn).</summary>
    public JsonArray RenderSince(int from)
    {
        var pieces = new List<JsonNode>();
        var text = new StringBuilder();
        var first = true;
        for (var i = from; i < Messages.Count; i++)
        {
            if (Messages[i] is not JsonObject m) continue;
            if (!first) text.Append("\n\n");
            first = false;
            Render(m, pieces, text);
        }
        if (Messages.Skip(from).OfType<JsonObject>().Any(m => Blocks(m).Any(b => TypeOf(b) == "tool_result")))
            text.Append("\n\n").Append(PastCallsNote);
        return Flush(pieces, text);
    }

    /// <summary>The first message after the last reply that calls no tool; 0 when there is none (nothing to tell as history).</summary>
    private int TurnStart()
    {
        for (var i = Messages.Count - 1; i >= 0; i--)
            if (Messages[i] is JsonObject m && Str(m, "role") == "assistant" && !Blocks(m).Any(b => TypeOf(b) == "tool_use"))
                return i + 1 < Messages.Count ? i + 1 : 0;
        return 0;
    }

    private static int CountImages(JsonArray messages) =>
        messages.OfType<JsonObject>().Sum(m => Blocks(m).Count(b => TypeOf(b) == "image"));

    private static void Render(JsonObject message, List<JsonNode> pieces, StringBuilder text)
    {
        foreach (var b in Blocks(message))
        {
            switch (TypeOf(b))
            {
                case "text": text.Append(Str(b, "text")); break;
                case "image": AddImage(b, pieces, text); break;
                // never told as words: written as the assistant's text, earlier reasoning reached the model in a user message and was refused
                case "thinking" or "redacted_thinking": break;
                case "tool_use":
                    text.Append("\n[Called tool ").Append(Str(b, "name")).Append(" (").Append(Str(b, "id")).Append(") with arguments ")
                        .Append(b["input"]?.ToJsonString() ?? "{}").Append("]\n");
                    break;
                case "tool_result":
                    text.Append("\n[Result of tool call ").Append(Str(b, "tool_use_id")).Append(b["is_error"] is JsonValue e && e.TryGetValue<bool>(out var err) && err ? " (an error)" : "").Append("]\n");
                    if (b["content"] is JsonValue sv && sv.TryGetValue<string>(out var s)) text.Append(s);
                    else if (b["content"] is JsonArray inner)
                        foreach (var ib in inner.OfType<JsonObject>())
                        {
                            if (TypeOf(ib) == "image") AddImage(ib, pieces, text);
                            else text.Append(Str(ib, "text"));
                        }
                    text.Append('\n');
                    break;
                default: break;
            }
        }
    }

    private static void AddImage(JsonObject block, List<JsonNode> pieces, StringBuilder text)
    {
        if (block["source"] is JsonObject src && Str(src, "type") == "base64")
        {
            if (text.Length > 0) { pieces.Add(new JsonObject { ["type"] = "text", ["text"] = text.ToString() }); text.Clear(); }
            pieces.Add(new JsonObject { ["type"] = "image", ["source"] = src.DeepClone() });
        }
        else if (block["source"] is JsonObject url && Str(url, "type") == "url") text.Append("[image: ").Append(Str(url, "url")).Append(']');
    }

    private static JsonArray Flush(List<JsonNode> pieces, StringBuilder text)
    {
        if (text.Length > 0) pieces.Add(new JsonObject { ["type"] = "text", ["text"] = text.ToString() });
        var result = new JsonArray();
        foreach (var p in pieces) result.Add(p);
        if (result.Count == 0) result.Push(new JsonObject { ["type"] = "text", ["text"] = "" });
        return result;
    }

    /// <summary>
    /// The model name <c>claude</c> is asked for. A model of the 5 generation or later that the program doesn't know it holds to a 200K
    /// window; its <c>[1m]</c> name gets the long one the model has (magpie's <c>claudeRunName</c>, MIT). One it knows keeps its name, as does every
    /// 4 generation model (a 1M window there is refused on an account without usage credits).
    /// </summary>
    public static string RunName(string model)
    {
        var id = model.ToLowerInvariant();
        if (KnownLong.Contains(id) || id.EndsWith("]", StringComparison.Ordinal)) return model;
        var parts = id.Split('-');
        if (parts.Length < 3 || parts[0] != "claude" || parts[1] is not ("opus" or "sonnet" or "haiku" or "fable" or "mythos")) return model;
        if (!int.TryParse(parts[2], out var generation) || generation < 5) return model;
        // a dated id (…-20251001) is not one of the generation's aliases
        if (parts.Length > 4 || parts.Length == 4 && (!int.TryParse(parts[3], out _) || parts[3].Length > 2)) return model;
        return model + "[1m]";
    }

    private static readonly HashSet<string> KnownLong =
    [
        "claude-fable-5", "claude-fable-5-1", "claude-mythos-5", "claude-mythos-5-1",
        "claude-opus-5", "claude-opus-5-5", "claude-sonnet-5", "claude-sonnet-5-5", "claude-haiku-5-5",
    ];
}

/// <summary>A tool result the caller returned, as MCP content.</summary>
public sealed record ClaudeToolResult(JsonArray Content, bool IsError);
