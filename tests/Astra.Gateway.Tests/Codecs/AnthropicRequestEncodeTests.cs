using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Anthropic;

namespace Astra.Gateway.Tests.Codecs;

/// <summary>Encoding the IR into an upstream Anthropic Messages request body.</summary>
public class AnthropicRequestEncodeTests
{
    private static readonly AnthropicCodec Codec = new();

    private static RequestEncodeContext Ctx(bool autoCache = true, int? defaultMax = null) =>
        new() { UpstreamModel = "claude-sonnet-4-5", DefaultMaxOutputTokens = defaultMax, EffortBudgets = new EffortBudgets(), AutoCacheControl = autoCache };

    private static UnifiedRequest Request(params UnifiedMessage[] messages) => new()
    {
        Model = "claude-sonnet-4-5",
        Messages = [.. messages],
    };

    private static UnifiedMessage User(params ContentPart[] parts) => new(MessageRole.User, [.. parts]);
    private static UnifiedMessage Assistant(params ContentPart[] parts) => new(MessageRole.Assistant, [.. parts]);

    private static int CountCacheControls(JsonNode? node) => node switch
    {
        JsonObject obj => (obj.ContainsKey("cache_control") ? 1 : 0) + obj.Sum(kv => CountCacheControls(kv.Value)),
        JsonArray array => array.Sum(CountCacheControls),
        _ => 0,
    };

    [Fact]
    public void Consecutive_Same_Role_Messages_Merge_And_Tool_Results_Go_First()
    {
        var r = Request(
            User(new TextPart("a")),
            User(new TextPart("b"), new ToolResultPart("call_1", null, [new TextPart("r1")]),
                 new ImagePart("https://example.com/pic.png", null, null)),
            Assistant(new TextPart("answer"), new ToolCallPart("call_1", "get_weather", """{"city":"Paris"}""")),
            User(new TextPart("c"), new ToolResultPart("call_1", null, [new TextPart("r2")])));

        var body = Codec.EncodeRequest(r, Ctx());

        var messages = Assert.IsType<JsonArray>(body["messages"]);
        Assert.Equal(3, messages.Count);

        var first = Assert.IsType<JsonObject>(messages[0]);
        Assert.Equal("user", (string?)first["role"]);
        var firstBlocks = Assert.IsType<JsonArray>(first["content"]);
        // tool_result first, then the remaining parts in original order (text a, text b, image)
        Assert.Equal("tool_result", (string?)Assert.IsType<JsonObject>(firstBlocks[0])["type"]);
        Assert.Equal("a", (string?)Assert.IsType<JsonObject>(firstBlocks[1])["text"]);
        Assert.Equal("b", (string?)Assert.IsType<JsonObject>(firstBlocks[2])["text"]);
        Assert.Equal("image", (string?)Assert.IsType<JsonObject>(firstBlocks[3])["type"]);

        var second = Assert.IsType<JsonObject>(messages[1]);
        Assert.Equal("assistant", (string?)second["role"]);
        var secondBlocks = Assert.IsType<JsonArray>(second["content"]);
        Assert.Equal("text", (string?)Assert.IsType<JsonObject>(secondBlocks[0])["type"]);
        var call = Assert.IsType<JsonObject>(secondBlocks[1]);
        Assert.Equal("tool_use", (string?)call["type"]);
        Assert.Equal("call_1", (string?)call["id"]);
        Assert.Equal("Paris", call["input"]!["city"]!.GetValue<string>());

        var third = Assert.IsType<JsonObject>(messages[2]);
        var thirdBlocks = Assert.IsType<JsonArray>(third["content"]);
        Assert.Equal("tool_result", (string?)Assert.IsType<JsonObject>(thirdBlocks[0])["type"]);
        // single-text tool_result content is a plain string
        Assert.Equal("r2", (string?)Assert.IsType<JsonObject>(thirdBlocks[0])["content"]);
        Assert.Equal("text", (string?)Assert.IsType<JsonObject>(thirdBlocks[1])["type"]);
    }

    [Fact]
    public void Auto_Cache_Control_Hits_Last_System_Block_And_Last_Tool_Only()
    {
        var r = Request(User(new TextPart("hi")));
        r.System.Add(new TextPart("s1"));
        r.System.Add(new TextPart("s2"));
        r.Tools.Add(new UnifiedTool { Name = "t1", InputSchema = new JsonObject { ["type"] = "object" } });
        r.Tools.Add(new UnifiedTool { Name = "t2", InputSchema = new JsonObject { ["type"] = "object" } });

        var on = Codec.EncodeRequest(r, Ctx(autoCache: true));
        var system = Assert.IsType<JsonArray>(on["system"]);
        Assert.Null(Assert.IsType<JsonObject>(system[0])["cache_control"]);
        Assert.NotNull(Assert.IsType<JsonObject>(system[1])["cache_control"]);
        var tools = Assert.IsType<JsonArray>(on["tools"]);
        Assert.Null(Assert.IsType<JsonObject>(tools[0])["cache_control"]);
        Assert.Equal("ephemeral", Assert.IsType<JsonObject>(tools[1])["cache_control"]!["type"]!.GetValue<string>());
        Assert.True(CountCacheControls(on) <= 4);
        Assert.Equal(2, CountCacheControls(on));

        var off = Codec.EncodeRequest(r, Ctx(autoCache: false));
        Assert.Equal(0, CountCacheControls(off));
    }

    [Fact]
    public void Thinking_Disabled_When_Tool_History_Has_No_Signed_Anthropic_Thinking()
    {
        // History translated from another protocol: assistant tool_use turn carries foreign reasoning only.
        var r = Request(
            User(new TextPart("go")),
            Assistant(new ReasoningPart("hmm", Origin: ApiProtocol.OpenAIResponses),
                      new ToolCallPart("call_1", "get_weather", "{}")),
            User(new ToolResultPart("call_1", null, [new TextPart("ok")])));
        r.Reasoning = new ReasoningConfig { BudgetTokens = 2048, IncludeThoughts = true };
        r.Temperature = 0.5;

        var body = Codec.EncodeRequest(r, Ctx());

        Assert.Null(body["thinking"]);
        Assert.Equal(0.5, body["temperature"]!.GetValue<double>()); // thinking is off → sampling knobs survive
        Assert.Contains(r.Warnings, w => w.Contains("thinking disabled"));
    }

    [Fact]
    public void Signed_Anthropic_Thinking_In_Tool_History_Keeps_Thinking_On()
    {
        var r = Request(
            User(new TextPart("go")),
            Assistant(new ReasoningPart("hmm", "sig-1", Origin: ApiProtocol.Anthropic),
                      new ToolCallPart("call_1", "get_weather", "{}")),
            User(new ToolResultPart("call_1", null, [new TextPart("ok")])));
        r.Reasoning = new ReasoningConfig { BudgetTokens = 2048, IncludeThoughts = true };

        var body = Codec.EncodeRequest(r, Ctx());

        Assert.Equal(2048, body["thinking"]!["budget_tokens"]!.GetValue<int>());
        var assistantBlocks = Assert.IsType<JsonArray>(Assert.IsType<JsonArray>(body["messages"])[1]!["content"]);
        var thinking = Assert.IsType<JsonObject>(assistantBlocks[0]);
        Assert.Equal("thinking", (string?)thinking["type"]);
        Assert.Equal("sig-1", (string?)thinking["signature"]);
    }

    [Fact]
    public void Budget_Below_Max_Tokens_Raises_Max_And_Drops_Forbidden_Sampling()
    {
        var r = Request(User(new TextPart("go")));
        r.MaxOutputTokens = 1024;
        r.Reasoning = new ReasoningConfig { BudgetTokens = 4096, IncludeThoughts = true };
        r.Temperature = 0.5;
        r.TopK = 40;
        r.TopP = 0.5;

        var body = Codec.EncodeRequest(r, Ctx());

        Assert.Equal(5120, body["max_tokens"]!.GetValue<int>()); // budget + 1024
        Assert.Equal(4096, body["thinking"]!["budget_tokens"]!.GetValue<int>());
        Assert.Null(body["temperature"]);
        Assert.Null(body["top_k"]);
        Assert.Null(body["top_p"]);
        Assert.Contains(r.Warnings, w => w.Contains("max_tokens"));
        Assert.Contains(r.Warnings, w => w.Contains("temperature"));
        Assert.Contains(r.Warnings, w => w.Contains("top_k"));
        Assert.Contains(r.Warnings, w => w.Contains("top_p"));
    }

    [Fact]
    public void TopP_At_Least_095_Survives_Thinking()
    {
        var r = Request(User(new TextPart("go")));
        r.Reasoning = new ReasoningConfig { BudgetTokens = 2048, IncludeThoughts = true };
        r.TopP = 0.95;

        var body = Codec.EncodeRequest(r, Ctx());

        Assert.Equal(0.95, body["top_p"]!.GetValue<double>());
        Assert.Null(body["temperature"]);
    }

    [Fact]
    public void Max_Tokens_Fallback_Chain_Request_Model_8192()
    {
        var plain = Request(User(new TextPart("hi")));
        Assert.Equal(8192, Codec.EncodeRequest(plain, Ctx(defaultMax: null))["max_tokens"]!.GetValue<int>());
        Assert.Equal(64000, Codec.EncodeRequest(plain, Ctx(defaultMax: 64000))["max_tokens"]!.GetValue<int>());
        plain.MaxOutputTokens = 512;
        Assert.Equal(512, Codec.EncodeRequest(plain, Ctx(defaultMax: 64000))["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void Effort_Maps_To_Budgets()
    {
        var budgets = new EffortBudgets { Low = 1024, Medium = 4096, High = 16384 };
        foreach (var (effort, expected) in new[] { ("low", 1024), ("medium", 4096), ("high", 16384) })
        {
            var r = Request(User(new TextPart("hi")));
            r.Reasoning = new ReasoningConfig { Effort = effort };
            var body = Codec.EncodeRequest(r, new RequestEncodeContext { UpstreamModel = "m", EffortBudgets = budgets });
            Assert.Equal(expected, body["thinking"]!["budget_tokens"]!.GetValue<int>());
        }
    }

    [Fact]
    public void Builtin_Tools_Only_Re_Emitted_For_Anthropic_Origin()
    {
        var r = Request(User(new TextPart("hi")));
        r.Tools.Add(new UnifiedTool { Name = "search", BuiltinType = "web_search_preview", BuiltinOrigin = ApiProtocol.OpenAIResponses, BuiltinRaw = [] });
        r.Tools.Add(new UnifiedTool
        {
            Name = "web_search",
            BuiltinType = "web_search_20250305",
            BuiltinOrigin = ApiProtocol.Anthropic,
            BuiltinRaw = (JsonObject)JsonNode.Parse("""{"type":"web_search_20250305","name":"web_search","max_uses":3}""")!,
        });
        r.Tools.Add(new UnifiedTool { Name = "no_schema" });

        var body = Codec.EncodeRequest(r, Ctx());

        var tools = Assert.IsType<JsonArray>(body["tools"]);
        Assert.Equal(2, tools.Count);
        var kept = Assert.IsType<JsonObject>(tools[0]);
        Assert.Equal("web_search_20250305", (string?)kept["type"]);
        Assert.Equal(3, kept["max_uses"]!.GetValue<int>());
        Assert.Equal("ephemeral", Assert.IsType<JsonObject>(tools[1])["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("object", Assert.IsType<JsonObject>(tools[1])["input_schema"]!["type"]!.GetValue<string>());
        Assert.Contains(r.Warnings, w => w.Contains("web_search_preview"));
    }

    [Fact]
    public void Images_Documents_And_Foreign_Reasoning_Encode_Lossily_As_Documented()
    {
        var r = Request(User(
            new ImagePart(null, "image/jpeg", "QUJD"),
            new ImagePart("data:image/jpeg;base64,REVG", null, null),
            new ImagePart("https://example.com/x.png", null, null),
            new FilePart(null, "application/pdf", "JVBERi0xLjQ=", "report.pdf"),
            new FilePart(null, "text/plain", "aGVsbG8=", "notes.txt"),
            new ReasoningPart("foreign", Origin: ApiProtocol.OpenAIResponses),
            new ReasoningPart("anthropic", "sig", Origin: ApiProtocol.Anthropic),
            new ReasoningPart(null, EncryptedContent: "blob", Origin: ApiProtocol.Anthropic)));
        r.ResponseFormat = new ResponseFormat { Type = "json_object" };

        var body = Codec.EncodeRequest(r, Ctx());

        var blocks = Assert.IsType<JsonArray>(Assert.IsType<JsonArray>(body["messages"])[0]!["content"]);
        Assert.Equal(6, blocks.Count); // notes.txt + foreign reasoning dropped
        Assert.Equal("image/jpeg", Assert.IsType<JsonObject>(blocks[0])["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal("REVG", Assert.IsType<JsonObject>(blocks[1])["source"]!["data"]!.GetValue<string>());
        Assert.Equal("url", (string?)Assert.IsType<JsonObject>(blocks[2])["source"]!["type"]);
        var document = Assert.IsType<JsonObject>(blocks[3]);
        Assert.Equal("document", (string?)document["type"]);
        Assert.Equal("report.pdf", (string?)document["title"]);
        var thinking = Assert.IsType<JsonObject>(blocks[4]);
        Assert.Equal("thinking", (string?)thinking["type"]);
        Assert.Equal("sig", (string?)thinking["signature"]);
        Assert.Equal("redacted_thinking", (string?)Assert.IsType<JsonObject>(blocks[5])["type"]);
        Assert.Equal("blob", (string?)Assert.IsType<JsonObject>(blocks[5])["data"]);

        Assert.Contains(r.Warnings, w => w.Contains("notes.txt"));
        Assert.Contains(r.Warnings, w => w.Contains("dropped reasoning"));
        Assert.Contains(r.Warnings, w => w.Contains("response_format"));
    }

    [Fact]
    public void Tool_Choice_And_Metadata_And_Extensions_Encode()
    {
        var r = Request(User(new TextPart("hi")));
        r.ToolChoice = new ToolChoice(ToolChoiceMode.Specific, "get_weather");
        r.ParallelToolCalls = false;
        r.User = "user-abc";
        r.Stop = ["STOP", "END"];
        r.Stream = true;
        r.ServiceTier = "auto";
        r.Extensions[ApiProtocol.Anthropic] = new JsonObject
        {
            ["metadata"] = new JsonObject { ["session_id"] = "s1" },
            ["betas"] = new JsonArray("context-1m"),
        };

        var body = Codec.EncodeRequest(r, Ctx());

        var toolChoice = Assert.IsType<JsonObject>(body["tool_choice"]);
        Assert.Equal("tool", (string?)toolChoice["type"]);
        Assert.Equal("get_weather", (string?)toolChoice["name"]);
        Assert.True(toolChoice["disable_parallel_tool_use"]!.GetValue<bool>());
        var metadata = Assert.IsType<JsonObject>(body["metadata"]);
        Assert.Equal("user-abc", (string?)metadata["user_id"]);
        Assert.Equal("s1", (string?)metadata["session_id"]); // extension merged under the encoder's key
        Assert.Equal("context-1m", Assert.IsType<JsonArray>(body["betas"])[0]!.GetValue<string>());
        Assert.Equal(["STOP", "END"], Assert.IsType<JsonArray>(body["stop_sequences"]).Select(x => x!.GetValue<string>()).ToArray());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("auto", (string?)body["service_tier"]);
    }

    [Fact]
    public void Assistant_First_History_Is_Kept_With_A_Warning()
    {
        var r = Request(Assistant(new TextPart("prefill")));

        var body = Codec.EncodeRequest(r, Ctx());

        Assert.Equal("assistant", (string?)Assert.IsType<JsonArray>(body["messages"])[0]!["role"]);
        Assert.Contains(r.Warnings, w => w.Contains("first message is assistant"));
    }

    [Fact]
    public void Parallel_Tool_Calls_Disabled_Without_A_Choice_Encodes_An_Auto_ToolChoice()
    {
        // Anthropic only knows disable_parallel_tool_use inside tool_choice: synthesize auto when the
        // client sent parallel_tool_calls=false without any explicit tool_choice.
        var r = Request(User(new TextPart("hi")));
        r.Tools.Add(new UnifiedTool { Name = "t1", InputSchema = new JsonObject { ["type"] = "object" } });
        r.ParallelToolCalls = false;

        var body = Codec.EncodeRequest(r, Ctx());

        var toolChoice = Assert.IsType<JsonObject>(body["tool_choice"]);
        Assert.Equal("auto", (string?)toolChoice["type"]);
        Assert.True(toolChoice["disable_parallel_tool_use"]!.GetValue<bool>());

        // An explicit choice keeps its own type; the flag rides on it.
        r.ToolChoice = new ToolChoice(ToolChoiceMode.Required);
        var withChoice = Assert.IsType<JsonObject>(Codec.EncodeRequest(r, Ctx())["tool_choice"]);
        Assert.Equal("any", (string?)withChoice["type"]);
        Assert.True(withChoice["disable_parallel_tool_use"]!.GetValue<bool>());
    }
}
