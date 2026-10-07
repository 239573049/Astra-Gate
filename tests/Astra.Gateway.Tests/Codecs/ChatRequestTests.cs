using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Chat;

namespace Astra.Gateway.Tests.Codecs;

public class ChatRequestDecodeTests
{
    private static readonly ChatCodec Codec = new();
    private static readonly RequestDecodeContext Ctx = new();

    [Fact]
    public void Multi_Turn_Request_Decodes_To_Ir()
    {
        var body = (JsonObject)JsonNode.Parse(
            """
            {
              "model": "gpt-5",
              "messages": [
                { "role": "system", "content": "You are terse." },
                { "role": "developer", "content": "Always answer in French." },
                {
                  "role": "user",
                  "content": [
                    { "type": "text", "text": "Compare these two photos." },
                    { "type": "image_url", "image_url": { "url": "data:image/png;base64,aGVsbG8=", "detail": "high" } },
                    { "type": "image_url", "image_url": { "url": "https://example.com/cat.jpg" } },
                    { "type": "file", "file": { "file_data": "data:application/pdf;base64,Skd+zw==", "filename": "notes.pdf" } },
                    { "type": "input_audio", "input_audio": { "data": "c29uZw==", "format": "wav" } }
                  ]
                },
                {
                  "role": "assistant",
                  "content": null,
                  "reasoning_content": "Checking the images first.",
                  "tool_calls": [
                    { "id": "call_1", "type": "function", "function": { "name": "get_weather", "arguments": "{\"city\":\"Paris\"}" } },
                    { "id": "call_2", "type": "function", "function": { "name": "get_weather", "arguments": "" } }
                  ]
                },
                { "role": "tool", "tool_call_id": "call_1", "content": "18°C" },
                { "role": "tool", "tool_call_id": "call_2", "content": [ { "type": "text", "text": "21°C" } ] },
                { "role": "user", "content": "Thanks!" }
              ],
              "tools": [
                { "type": "function", "function": { "name": "get_weather", "description": "Weather lookup", "parameters": { "type": "object" }, "strict": true } }
              ],
              "tool_choice": { "type": "function", "function": { "name": "get_weather" } },
              "parallel_tool_calls": false,
              "temperature": 0.4,
              "top_p": 0.9,
              "stop": ["END", "\n\n"],
              "max_completion_tokens": 512,
              "reasoning_effort": "high",
              "response_format": { "type": "json_schema", "json_schema": { "name": "answer", "strict": true, "schema": { "type": "object" } } },
              "stream": true,
              "user": "user-42",
              "metadata": { "origin": "cli" },
              "service_tier": "priority",
              "seed": 7,
              "frequency_penalty": 0.5,
              "stream_options": { "include_usage": true }
            }
            """)!;

        var request = Codec.DecodeRequest(body, Ctx);

        Assert.Equal("gpt-5", request.Model);
        Assert.True(request.Stream);

        // system + developer merge into System in order.
        Assert.Equal(2, request.System.Count);
        Assert.Equal(new TextPart("You are terse."), request.System[0]);
        Assert.Equal(new TextPart("Always answer in French."), request.System[1]);

        // user: text + data-URL image + http image + file; input_audio dropped with a warning.
        var user = request.Messages[0];
        Assert.Equal(MessageRole.User, user.Role);
        Assert.Equal(4, user.Parts.Count);
        Assert.Equal(new TextPart("Compare these two photos."), user.Parts[0]);
        var dataImage = Assert.IsType<ImagePart>(user.Parts[1]);
        Assert.Equal("image/png", dataImage.MediaType);
        Assert.Equal("aGVsbG8=", dataImage.Base64Data);
        Assert.Null(dataImage.Url);
        Assert.Equal("high", dataImage.Detail);
        var httpImage = Assert.IsType<ImagePart>(user.Parts[2]);
        Assert.Equal("https://example.com/cat.jpg", httpImage.Url);
        var file = Assert.IsType<FilePart>(user.Parts[3]);
        Assert.Equal("application/pdf", file.MediaType);
        Assert.Equal("Skd+zw==", file.Base64Data);
        Assert.Equal("notes.pdf", file.FileName);
        Assert.Contains(request.Warnings, w => w.Contains("input_audio"));

        // assistant: reasoning first, then tool calls (empty arguments → "{}").
        var assistant = request.Messages[1];
        Assert.Equal(MessageRole.Assistant, assistant.Role);
        Assert.Equal(3, assistant.Parts.Count);
        var reasoning = Assert.IsType<ReasoningPart>(assistant.Parts[0]);
        Assert.Equal("Checking the images first.", reasoning.Text);
        Assert.Equal(ApiProtocol.OpenAIChat, reasoning.Origin);
        Assert.Equal(new ToolCallPart("call_1", "get_weather", "{\"city\":\"Paris\"}"), assistant.Parts[1]);
        Assert.Equal(new ToolCallPart("call_2", "get_weather", "{}"), assistant.Parts[2]);

        // consecutive tool messages merge into ONE User message, order kept.
        var toolResults = request.Messages[2];
        Assert.Equal(MessageRole.User, toolResults.Role);
        Assert.Equal(2, toolResults.Parts.Count);
        var first = Assert.IsType<ToolResultPart>(toolResults.Parts[0]);
        Assert.Equal("call_1", first.CallId);
        Assert.Equal([new TextPart("18°C")], first.Content);
        var second = Assert.IsType<ToolResultPart>(toolResults.Parts[1]);
        Assert.Equal("call_2", second.CallId);
        Assert.Equal([new TextPart("21°C")], second.Content);

        Assert.Equal(MessageRole.User, request.Messages[3].Role);
        Assert.Equal([new TextPart("Thanks!")], request.Messages[3].Parts);

        // tools / tool_choice / sampling / reasoning / format.
        var tool = Assert.Single(request.Tools);
        Assert.Equal("get_weather", tool.Name);
        Assert.Equal("Weather lookup", tool.Description);
        Assert.Equal("{\"type\":\"object\"}", tool.InputSchema!.ToJsonString());
        Assert.True(tool.Strict);
        Assert.Equal(ToolChoiceMode.Specific, request.ToolChoice!.Mode);
        Assert.Equal("get_weather", request.ToolChoice.Name);
        Assert.False(request.ParallelToolCalls!.Value);
        Assert.Equal(0.4, request.Temperature);
        Assert.Equal(0.9, request.TopP);
        Assert.Equal(["END", "\n\n"], request.Stop);
        Assert.Equal(512, request.MaxOutputTokens);
        Assert.Equal("high", request.Reasoning!.Effort);
        Assert.True(request.Reasoning.Enabled);
        Assert.Equal("json_schema", request.ResponseFormat!.Type);
        Assert.Equal("answer", request.ResponseFormat.Name);
        Assert.True(request.ResponseFormat.Strict);
        Assert.Equal("user-42", request.User);
        Assert.Equal("cli", request.Metadata!["origin"]!.GetValue<string>());
        Assert.Equal("priority", request.ServiceTier);

        // unmapped top-level fields land in Extensions[OpenAIChat].
        var extensions = request.Extensions[ApiProtocol.OpenAIChat];
        Assert.Equal(7, extensions["seed"]!.GetValue<int>());
        Assert.Equal(0.5, extensions["frequency_penalty"]!.GetValue<double>());
        Assert.True(extensions["stream_options"]!["include_usage"]!.GetValue<bool>());
        Assert.False(extensions.ContainsKey("model"));
        Assert.False(extensions.ContainsKey("reasoning_effort"));
    }

    [Fact]
    public void Reasoning_Effort_None_Disables_Reasoning()
    {
        var request = Codec.DecodeRequest(
            new JsonObject { ["model"] = "o3", ["messages"] = new JsonArray(), ["reasoning_effort"] = "none" }, Ctx);
        Assert.NotNull(request.Reasoning);
        Assert.False(request.Reasoning!.Enabled);
        Assert.Null(request.Reasoning.Effort);
    }

    [Fact]
    public void Stop_Accepts_A_Single_String()
    {
        var request = Codec.DecodeRequest(
            new JsonObject { ["model"] = "gpt-4o", ["messages"] = new JsonArray(), ["stop"] = "END" }, Ctx);
        Assert.Equal(["END"], request.Stop);
    }

    [Theory]
    [InlineData("auto", ToolChoiceMode.Auto)]
    [InlineData("none", ToolChoiceMode.None)]
    [InlineData("required", ToolChoiceMode.Required)]
    public void Tool_Choice_Modes_Are_Mapped(string wire, ToolChoiceMode mode)
    {
        var request = Codec.DecodeRequest(
            new JsonObject { ["model"] = "gpt-4o", ["messages"] = new JsonArray(), ["tool_choice"] = wire }, Ctx);
        Assert.Equal(mode, request.ToolChoice!.Mode);
    }

    [Fact]
    public void Missing_Model_Or_Messages_Is_Rejected()
    {
        Assert.Throws<ProtocolException>(() => Codec.DecodeRequest(new JsonObject { ["messages"] = new JsonArray() }, Ctx));
        Assert.Throws<ProtocolException>(() => Codec.DecodeRequest(new JsonObject { ["model"] = "gpt-4o" }, Ctx));
        Assert.Throws<ProtocolException>(() => Codec.DecodeRequest(
            new JsonObject { ["model"] = "gpt-4o", ["messages"] = "not an array" }, Ctx));
    }

    [Fact]
    public void Assistant_Text_And_Refusal_Decode_As_Text()
    {
        var body = new JsonObject
        {
            ["model"] = "gpt-4o",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "refusal", ["refusal"] = "I can't do that." },
                        new JsonObject { ["type"] = "text", ["text"] = "But here is why." },
                    },
                },
            },
        };
        var request = Codec.DecodeRequest(body, Ctx);
        var assistant = request.Messages.Single();
        Assert.Equal(
            [new TextPart("I can't do that."), new TextPart("But here is why.")],
            assistant.Parts);
    }

    [Fact]
    public void Chat_Only_Fields_And_Multi_Candidates_Warn_On_The_Conversion_Path()
    {
        // DecodeRequest only runs when converting: fields with no cross-protocol equivalent must say so.
        var body = new JsonObject
        {
            ["model"] = "gpt-4o",
            ["n"] = 3,
            ["seed"] = 7,
            ["frequency_penalty"] = 0.5,
            ["presence_penalty"] = 0.2,
            ["logprobs"] = true,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        };

        var request = Codec.DecodeRequest(body, Ctx);

        Assert.Contains(request.Warnings, w => w.Contains("n=3"));
        Assert.Contains(request.Warnings, w => w.Contains("seed"));
        Assert.Contains(request.Warnings, w => w.Contains("frequency_penalty"));
        Assert.Contains(request.Warnings, w => w.Contains("presence_penalty"));
        Assert.Contains(request.Warnings, w => w.Contains("logprobs"));
    }
}

public class ChatRequestEncodeTests
{
    private static readonly ChatCodec Codec = new();
    private static readonly RequestEncodeContext Ctx = new()
    {
        UpstreamModel = "gpt-4o",
        EffortBudgets = new EffortBudgets { Low = 1024, Medium = 4096, High = 16384 },
    };

    private static RequestEncodeContext CtxFor(string model) => new()
    {
        UpstreamModel = model,
        EffortBudgets = new EffortBudgets { Low = 1024, Medium = 4096, High = 16384 },
    };

    private static JsonArray MessagesOf(JsonObject body) => (JsonArray)body["messages"]!;

    [Fact]
    public void Max_Tokens_Choice_Depends_On_Model_Family()
    {
        UnifiedRequest Request(int max) => new() { MaxOutputTokens = max };

        foreach (var model in new[] { "gpt-5", "gpt-5-mini", "GPT-5.1", "o1", "o1-mini", "o3", "O4-MINI", "openai/o3", "openai/o4-mini" })
        {
            var body = Codec.EncodeRequest(Request(77), CtxFor(model));
            Assert.True(body.ContainsKey("max_completion_tokens"), $"{model} should use max_completion_tokens");
            Assert.False(body.ContainsKey("max_tokens"), $"{model} must not use max_tokens");
            Assert.Equal(77, body["max_completion_tokens"]!.GetValue<int>());
        }
        foreach (var model in new[] { "gpt-4o", "gpt-4.1-mini", "deepseek-chat", "openai/gpt-4o" })
        {
            var body = Codec.EncodeRequest(Request(77), CtxFor(model));
            Assert.False(body.ContainsKey("max_completion_tokens"), $"{model} should use max_tokens");
            Assert.Equal(77, body["max_tokens"]!.GetValue<int>());
        }
    }

    [Fact]
    public void Stream_Setting_Adds_Stream_Options_Include_Usage()
    {
        var streaming = Codec.EncodeRequest(new UnifiedRequest { Stream = true }, Ctx);
        Assert.True(streaming["stream"]!.GetValue<bool>());
        Assert.True(streaming["stream_options"]!["include_usage"]!.GetValue<bool>());

        var buffered = Codec.EncodeRequest(new UnifiedRequest(), Ctx);
        Assert.False(buffered.ContainsKey("stream"));
        Assert.False(buffered.ContainsKey("stream_options"));
    }

    [Fact]
    public void Tool_Results_Are_Emitted_Before_The_Rest_Of_The_User_Message()
    {
        var request = new UnifiedRequest
        {
            Messages =
            [
                new(MessageRole.User,
                [
                    new ToolResultPart("call_1", null, [new TextPart("18°C")]),
                    new ToolResultPart("call_2", null, [new TextPart("21°C")]),
                    new TextPart("Compare them."),
                ]),
            ],
        };
        var body = Codec.EncodeRequest(request, Ctx);

        var messages = MessagesOf(body);
        Assert.Equal(3, messages.Count);
        Assert.Equal("tool", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", messages[0]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("18°C", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("tool", messages[1]!["role"]!.GetValue<string>());
        Assert.Equal("call_2", messages[1]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("user", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("Compare them.", messages[2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void Images_In_Tool_Results_Are_Dropped_With_A_Warning()
    {
        var request = new UnifiedRequest
        {
            Messages =
            [
                new(MessageRole.User,
                [
                    new ToolResultPart("call_1", null,
                        [new TextPart("seen"), new ImagePart("https://example.com/x.png", null, null)]),
                ]),
            ],
        };
        var warnings = request.Warnings;
        var body = Codec.EncodeRequest(request, Ctx);

        var toolMessage = MessagesOf(body)[0]!;
        Assert.Equal("seen", toolMessage["content"]!.GetValue<string>());
        Assert.Contains(warnings, w => w.Contains("tool"));
    }

    [Fact]
    public void Reasoning_Content_Is_Emitted_Only_For_OpenAIChat_Origin()
    {
        var request = new UnifiedRequest
        {
            Messages =
            [
                new(MessageRole.Assistant,
                [
                    new ReasoningPart("chat-origin thought", Origin: ApiProtocol.OpenAIChat),
                    new ReasoningPart("anthropic thought", Origin: ApiProtocol.Anthropic),
                    new TextPart("Answer"),
                ]),
            ],
        };
        var body = Codec.EncodeRequest(request, Ctx);

        var assistant = MessagesOf(body)[0]!;
        Assert.Equal("assistant", assistant["role"]!.GetValue<string>());
        Assert.Equal("chat-origin thought", assistant["reasoning_content"]!.GetValue<string>());
        Assert.DoesNotContain("anthropic thought", assistant.ToJsonString());
        Assert.Equal("Answer", assistant["content"]!.GetValue<string>());
    }

    [Fact]
    public void Builtin_Tools_Are_Dropped_With_A_Warning_And_Chat_Ones_Re_Emitted()
    {
        var request = new UnifiedRequest
        {
            Tools =
            [
                new() { Name = "lookup", Description = "d", InputSchema = JsonNode.Parse("{\"type\":\"object\"}"), Strict = true },
                new()
                {
                    BuiltinType = "web_search",
                    BuiltinOrigin = ApiProtocol.OpenAIResponses,
                    BuiltinRaw = (JsonObject)JsonNode.Parse("{ \"type\": \"web_search\" }")!,
                },
                new()
                {
                    BuiltinType = "custom",
                    BuiltinOrigin = ApiProtocol.OpenAIChat,
                    BuiltinRaw = (JsonObject)JsonNode.Parse("{ \"type\": \"custom\", \"custom\": { \"format\": \"text\" } }")!,
                },
            ],
            ParallelToolCalls = false,
        };
        var body = Codec.EncodeRequest(request, Ctx);

        var tools = (JsonArray)body["tools"]!;
        Assert.Equal(2, tools.Count);
        var function = tools[0]!["function"]!;
        Assert.Equal("lookup", function["name"]!.GetValue<string>());
        Assert.Equal("d", function["description"]!.GetValue<string>());
        Assert.True(function["strict"]!.GetValue<bool>());
        Assert.Equal("custom", tools[1]!["type"]!.GetValue<string>());
        Assert.True(body["parallel_tool_calls"]!.GetValue<bool>() == false);
        Assert.Contains(request.Warnings, w => w.Contains("web_search"));
    }

    [Fact]
    public void Tool_Choice_And_Messages_Are_Encoded()
    {
        var request = new UnifiedRequest
        {
            System = [new TextPart("sys one"), new TextPart("sys two")],
            Messages =
            [
                new(MessageRole.User, [new TextPart("hello")]),
                new(MessageRole.Assistant,
                [
                    new TextPart("calling"),
                    new ToolCallPart("call_9", "get_weather", "{\"city\":\"Paris\"}"),
                ]),
                new(MessageRole.User, [new ToolResultPart("call_9", null, [new TextPart("sunny")])]),
                new(MessageRole.User, [new TextPart("thanks")]),
            ],
            Tools = [new() { Name = "get_weather" }],
            ToolChoice = new ToolChoice(ToolChoiceMode.Specific, "get_weather"),
        };
        var body = Codec.EncodeRequest(request, Ctx);

        var messages = MessagesOf(body);
        Assert.Equal("sys one\n\nsys two", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("hello", messages[1]!["content"]!.GetValue<string>());
        var assistant = messages[2]!;
        Assert.Equal("calling", assistant["content"]!.GetValue<string>());
        var call = assistant["tool_calls"]![0]!;
        Assert.Equal("call_9", call["id"]!.GetValue<string>());
        Assert.Equal("function", call["type"]!.GetValue<string>());
        Assert.Equal("get_weather", call["function"]!["name"]!.GetValue<string>());
        Assert.Equal("{\"city\":\"Paris\"}", call["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("sunny", messages[3]!["content"]!.GetValue<string>());
        Assert.Equal("thanks", messages[4]!["content"]!.GetValue<string>());
        Assert.Equal("get_weather", body["tool_choice"]!["function"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void User_Message_Uses_Parts_Only_When_Needed()
    {
        var single = Codec.EncodeRequest(
            new UnifiedRequest { Messages = [new(MessageRole.User, [new TextPart("just text")])] }, Ctx);
        Assert.Equal("just text", MessagesOf(single)[0]!["content"]!.GetValue<string>());

        var multi = Codec.EncodeRequest(
            new UnifiedRequest
            {
                Messages =
                [
                    new(MessageRole.User,
                    [
                        new TextPart("look"),
                        new ImagePart(null, "image/jpeg", "aGk=", "low"),
                        new FilePart(null, null, "cGRm", "doc.pdf", null),
                    ]),
                ],
            }, Ctx);
        var parts = (JsonArray)MessagesOf(multi)[0]!["content"]!;
        Assert.Equal(3, parts.Count);
        Assert.Equal("look", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,aGk=", parts[1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("low", parts[1]!["image_url"]!["detail"]!.GetValue<string>());
        Assert.Equal("data:application/octet-stream;base64,cGRm", parts[2]!["file"]!["file_data"]!.GetValue<string>());
        Assert.Equal("doc.pdf", parts[2]!["file"]!["filename"]!.GetValue<string>());
    }

    [Fact]
    public void Reasoning_Effort_Comes_From_Effort_Or_Budget()
    {
        var effort = Codec.EncodeRequest(
            new UnifiedRequest { Reasoning = new ReasoningConfig { Effort = "minimal" } }, Ctx);
        Assert.Equal("minimal", effort["reasoning_effort"]!.GetValue<string>());

        var fromLowBudget = Codec.EncodeRequest(
            new UnifiedRequest { Reasoning = new ReasoningConfig { BudgetTokens = 900 } }, Ctx);
        Assert.Equal("low", fromLowBudget["reasoning_effort"]!.GetValue<string>());

        var fromHighBudget = Codec.EncodeRequest(
            new UnifiedRequest { Reasoning = new ReasoningConfig { BudgetTokens = 10000 } }, Ctx);
        Assert.Equal("high", fromHighBudget["reasoning_effort"]!.GetValue<string>());

        var disabled = Codec.EncodeRequest(
            new UnifiedRequest { Reasoning = new ReasoningConfig { Effort = "low", Enabled = false } }, Ctx);
        Assert.False(disabled.ContainsKey("reasoning_effort"));

        var absent = Codec.EncodeRequest(new UnifiedRequest(), Ctx);
        Assert.False(absent.ContainsKey("reasoning_effort"));
    }

    [Fact]
    public void Response_Format_And_Other_Fields_Are_Encoded()
    {
        var request = new UnifiedRequest
        {
            Temperature = 0.2,
            TopP = 0.8,
            Stop = ["END"],
            User = "u1",
            Metadata = new JsonObject { ["k"] = "v" },
            ServiceTier = "flex",
            ResponseFormat = new ResponseFormat { Type = "json_schema", Name = "out", Strict = true, Schema = JsonNode.Parse("{\"type\":\"object\"}") },
        };
        var body = Codec.EncodeRequest(request, Ctx);

        Assert.Equal("END", body["stop"]!.GetValue<string>());
        Assert.Equal(0.2, body["temperature"]!.GetValue<double>());
        Assert.Equal(0.8, body["top_p"]!.GetValue<double>());
        Assert.Equal("u1", body["user"]!.GetValue<string>());
        Assert.Equal("v", body["metadata"]!["k"]!.GetValue<string>());
        Assert.Equal("flex", body["service_tier"]!.GetValue<string>());
        var format = body["response_format"]!;
        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
        Assert.Equal("out", format["json_schema"]!["name"]!.GetValue<string>());
        Assert.True(format["json_schema"]!["strict"]!.GetValue<bool>());
        Assert.NotNull(format["json_schema"]!["schema"]);

        // stop with several entries stays an array.
        request.Stop = ["A", "B"];
        var arrayBody = Codec.EncodeRequest(request, Ctx);
        Assert.Equal(2, ((JsonArray)arrayBody["stop"]!).Count);
    }

    [Fact]
    public void Extensions_Merge_Only_Keys_The_Encoder_Did_Not_Set()
    {
        var request = new UnifiedRequest
        {
            Model = "gpt-4o",
            Stream = true,
            Temperature = 0.5,
            Extensions =
            {
                [ApiProtocol.OpenAIChat] = new JsonObject
                {
                    ["seed"] = 42,
                    ["temperature"] = 1.5, // must NOT override the IR temperature
                    ["logit_bias"] = new JsonObject { ["50256"] = -100 },
                    ["stream_options"] = new JsonObject { ["include_usage"] = false }, // stream_options already set
                },
            },
        };
        var body = Codec.EncodeRequest(request, Ctx);

        Assert.Equal(0.5, body["temperature"]!.GetValue<double>());
        Assert.Equal(42, body["seed"]!.GetValue<int>());
        Assert.Equal(-100, body["logit_bias"]!["50256"]!.GetValue<int>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public void Parallel_Tool_Calls_Is_Sent_Only_With_Tools()
    {
        var withoutTools = Codec.EncodeRequest(new UnifiedRequest { ParallelToolCalls = true }, Ctx);
        Assert.False(withoutTools.ContainsKey("parallel_tool_calls"));

        var withTools = Codec.EncodeRequest(
            new UnifiedRequest { ParallelToolCalls = true, Tools = [new() { Name = "f" }] }, Ctx);
        Assert.True(withTools["parallel_tool_calls"]!.GetValue<bool>());
    }

    [Fact]
    public void Tool_Result_Error_Flag_And_Unfetchable_Urls_Warn_When_Encoding()
    {
        var request = new UnifiedRequest
        {
            Messages =
            [
                new(MessageRole.User,
                [
                    new ToolResultPart("call_1", null, [new TextPart("boom")], IsError: true),
                    new ImagePart("gs://bucket/cat.png", "image/png", null),
                    new FilePart("gopher://example.com/notes", null, null, "notes.txt"),
                ]),
            ],
        };

        var body = Codec.EncodeRequest(request, Ctx);

        Assert.Contains(request.Warnings, w => w.Contains("is_error"));
        Assert.Contains(request.Warnings, w => w.Contains("gs://bucket/cat.png"));
        Assert.Contains(request.Warnings, w => w.Contains("gopher://example.com/notes"));
        var messages = MessagesOf(body);
        Assert.Single(messages); // only the tool message: the image and file parts were dropped
        Assert.Equal("tool", messages[0]!["role"]!.GetValue<string>());
    }
}
