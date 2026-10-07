using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Gemini;

namespace Astra.Gateway.Tests.Codecs;

// Request-side tests, written from a Gemini CLI-like request (systemInstruction, tool call with
// thoughtSignature + functionResponse without ids, inlineData, functionDeclarations + googleSearch,
// toolConfig, thinkingConfig, safetySettings) with model / streaming carried by the URL.

public class GeminiRequestDecodeTests
{
    private static JsonObject LoadFixture(string name) =>
        (JsonObject)(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gemini", name)))
            ?? throw new InvalidOperationException("missing fixture " + name));

    private static UnifiedRequest DecodeFixture() => new GeminiCodec().DecodeRequest(
        LoadFixture("cli-request.json"),
        new RequestDecodeContext { PathModel = "models/gemini-2.5-flash", PathStream = true });

    [Fact]
    public void Gemini_Cli_Request_Decodes_To_Ir()
    {
        var ir = DecodeFixture();

        Assert.Equal("gemini-2.5-flash", ir.Model);
        Assert.True(ir.Stream);

        Assert.Equal("You are a weather assistant.", Assert.IsType<TextPart>(Assert.Single(ir.System)).Text);

        Assert.Equal(5, ir.Messages.Count);

        var userTurn = ir.Messages[0];
        Assert.Equal(MessageRole.User, userTurn.Role);
        Assert.Equal(2, userTurn.Parts.Count);
        Assert.Equal("What's the weather in Tokyo? The photo shows the sky here.", Assert.IsType<TextPart>(userTurn.Parts[0]).Text);
        var image = Assert.IsType<ImagePart>(userTurn.Parts[1]);
        Assert.Equal("image/png", image.MediaType);
        Assert.Null(image.Url);
        Assert.Equal("aGVsbG8=", image.Base64Data);

        // Model turn: thought text, then the signature reasoning part immediately before the tool call.
        var modelTurn = ir.Messages[1];
        Assert.Equal(MessageRole.Assistant, modelTurn.Role);
        Assert.Equal(3, modelTurn.Parts.Count);
        var thought = Assert.IsType<ReasoningPart>(modelTurn.Parts[0]);
        Assert.Equal("Checking the forecast first.", thought.Text);
        Assert.Null(thought.Signature);
        Assert.Equal(ApiProtocol.Gemini, thought.Origin);
        var signature = Assert.IsType<ReasoningPart>(modelTurn.Parts[1]);
        Assert.Null(signature.Text);
        Assert.Equal("sig-abc123", signature.Signature);
        Assert.Equal(ApiProtocol.Gemini, signature.Origin);
        var call = Assert.IsType<ToolCallPart>(modelTurn.Parts[2]);
        Assert.Equal("call_42", call.Id);
        Assert.Equal("get_weather", call.Name);
        Assert.Contains("Tokyo", call.ArgumentsJson);

        // functionResponse matched the explicit call id; content comes from response.output.
        var resultTurn = ir.Messages[2];
        Assert.Equal(MessageRole.User, resultTurn.Role);
        var result = Assert.IsType<ToolResultPart>(Assert.Single(resultTurn.Parts));
        Assert.Equal("call_42", result.CallId);
        Assert.Equal("get_weather", result.Name);
        Assert.False(result.IsError);
        Assert.Equal("22C, sunny", Assert.IsType<TextPart>(Assert.Single(result.Content)).Text);

        Assert.Equal("It is 22C and sunny in Tokyo.", Assert.IsType<TextPart>(Assert.Single(ir.Messages[3].Parts)).Text);
        Assert.Equal(MessageRole.User, ir.Messages[4].Role);

        Assert.Equal(0.7, ir.Temperature);
        Assert.Equal(0.9, ir.TopP);
        Assert.Equal(40, ir.TopK);
        Assert.Equal(2048, ir.MaxOutputTokens);
        Assert.Equal(["DONE"], ir.Stop);

        var format = ir.ResponseFormat!;
        Assert.Equal("json_schema", format.Type);
        Assert.NotNull(format.Schema);

        var reasoning = ir.Reasoning!;
        Assert.Equal(4096, reasoning.BudgetTokens);
        Assert.True(reasoning.IncludeThoughts);
        Assert.True(reasoning.Enabled);
        Assert.Null(reasoning.Effort);

        var functionTool = ir.Tools[0];
        Assert.Null(functionTool.BuiltinType);
        Assert.Equal("get_weather", functionTool.Name);
        Assert.Equal("Get current weather for a city", functionTool.Description);
        Assert.NotNull(functionTool.InputSchema);
        var builtin = ir.Tools[1];
        Assert.Equal("googleSearch", builtin.BuiltinType);
        Assert.Equal(ApiProtocol.Gemini, builtin.BuiltinOrigin);
        Assert.NotNull(builtin.BuiltinRaw);

        Assert.Equal(ToolChoiceMode.Specific, ir.ToolChoice!.Mode);
        Assert.Equal("get_weather", ir.ToolChoice.Name);
    }

    [Fact]
    public void Unmapped_Fields_Are_Kept_In_Extensions()
    {
        var ir = DecodeFixture();

        var ext = ir.Extensions[ApiProtocol.Gemini];
        Assert.True(ext.ContainsKey("safetySettings"));
        Assert.True(ext.ContainsKey("labels"));
        // The nested generationConfig only holds the keys the IR could not map.
        var extGen = Assert.IsType<JsonObject>(ext["generationConfig"]);
        Assert.Single(extGen);
        Assert.Equal(7, extGen["seed"]!.GetValue<int>());
        Assert.Empty(ir.Warnings);
    }

    [Fact]
    public void Body_Model_And_Stream_Fallbacks_Are_Used_Without_Path_Values()
    {
        var body = new JsonObject
        {
            ["model"] = "gemini-2.5-pro",
            ["contents"] = new JsonArray(),
        };
        var ir = new GeminiCodec().DecodeRequest(body, new RequestDecodeContext());

        Assert.Equal("gemini-2.5-pro", ir.Model);
        Assert.False(ir.Stream);
    }

    [Fact]
    public void Tool_Calls_Without_Ids_Get_Deterministic_Ids_And_Match_Responses()
    {
        var call = new JsonObject { ["functionCall"] = new JsonObject { ["name"] = "get_weather", ["args"] = new JsonObject { ["city"] = "Osaka" } } };
        var response = new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = "get_weather", ["response"] = new JsonObject { ["output"] = "rain" } } };
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(
                new JsonObject { ["parts"] = new JsonArray(call.DeepClone()) },                                  // role missing = user
                new JsonObject { ["role"] = "model", ["parts"] = new JsonArray(call.DeepClone()) },
                new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(response.DeepClone()) },
                new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(response.DeepClone()) }),
        };

        var ir = new GeminiCodec().DecodeRequest(body, new RequestDecodeContext { PathModel = "m", PathStream = false });

        var firstCall = Assert.IsType<ToolCallPart>(ir.Messages[0].Parts[0]);
        Assert.Equal("call_get_weather_0", firstCall.Id);
        var secondCall = Assert.IsType<ToolCallPart>(ir.Messages[1].Parts[0]);
        Assert.Equal("call_get_weather_1", secondCall.Id);
        // Responses without ids claim the earliest earlier call with the same name, in order.
        Assert.Equal("call_get_weather_0", Assert.IsType<ToolResultPart>(Assert.Single(ir.Messages[2].Parts)).CallId);
        Assert.Equal("call_get_weather_1", Assert.IsType<ToolResultPart>(Assert.Single(ir.Messages[3].Parts)).CallId);
    }

    [Fact]
    public void Function_Response_With_Error_And_No_Output_Is_Marked_Error()
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"] = "lookup",
                        ["response"] = new JsonObject { ["error"] = "boom" },
                    },
                }),
            }),
        };

        var ir = new GeminiCodec().DecodeRequest(body, new RequestDecodeContext { PathModel = "m" });

        var result = Assert.IsType<ToolResultPart>(Assert.Single(ir.Messages[0].Parts));
        Assert.True(result.IsError);
        Assert.Contains("boom", Assert.IsType<TextPart>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void File_Data_Maps_To_Image_Or_File_Part_By_Mime()
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["parts"] = new JsonArray(
                    new JsonObject { ["fileData"] = new JsonObject { ["mimeType"] = "image/jpeg", ["fileUri"] = "https://example.com/cat.jpg" } },
                    new JsonObject { ["fileData"] = new JsonObject { ["mimeType"] = "application/pdf", ["fileUri"] = "gs://bucket/report.pdf" } }),
            }),
        };

        var ir = new GeminiCodec().DecodeRequest(body, new RequestDecodeContext { PathModel = "m" });

        var image = Assert.IsType<ImagePart>(ir.Messages[0].Parts[0]);
        Assert.Equal("https://example.com/cat.jpg", image.Url);
        Assert.Equal("image/jpeg", image.MediaType);
        var file = Assert.IsType<FilePart>(ir.Messages[0].Parts[1]);
        Assert.Equal("gs://bucket/report.pdf", file.Url);
        Assert.Equal("application/pdf", file.MediaType);
    }

    [Fact]
    public void Snake_Case_Aliases_Are_Accepted()
    {
        var body = new JsonObject
        {
            ["system_instruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = "sys" }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["parts"] = new JsonArray(new JsonObject
                {
                    ["inline_data"] = new JsonObject { ["mime_type"] = "application/pdf", ["data"] = "ZGF0YQ==" },
                }),
            }),
            ["generation_config"] = new JsonObject
            {
                ["max_output_tokens"] = 100,
                ["stop_sequences"] = new JsonArray("END"),
                ["thinking_config"] = new JsonObject { ["thinking_budget"] = 0 },
            },
            ["tool_config"] = new JsonObject
            {
                ["function_calling_config"] = new JsonObject { ["mode"] = "NONE" },
            },
        };

        var ir = new GeminiCodec().DecodeRequest(body, new RequestDecodeContext { PathModel = "m" });

        Assert.Equal("sys", Assert.IsType<TextPart>(Assert.Single(ir.System)).Text);
        var file = Assert.IsType<FilePart>(Assert.Single(ir.Messages[0].Parts));
        Assert.Equal("application/pdf", file.MediaType);
        Assert.Equal("ZGF0YQ==", file.Base64Data);
        Assert.Equal(100, ir.MaxOutputTokens);
        Assert.Equal(["END"], ir.Stop);
        Assert.False(ir.Reasoning!.Enabled); // thinkingBudget 0 disables thinking
        Assert.Equal(ToolChoiceMode.None, ir.ToolChoice!.Mode);
    }

    [Theory]
    [InlineData(-1, null, true)]   // -1 = dynamic thinking: enabled without a fixed budget
    [InlineData(1024, 1024, true)]
    public void Thinking_Budgets_Map_To_Reasoning_Config(int budget, int? expectedBudget, bool expectedEnabled)
    {
        var body = new JsonObject
        {
            ["generationConfig"] = new JsonObject
            {
                ["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = budget },
            },
        };

        var ir = new GeminiCodec().DecodeRequest(body, new RequestDecodeContext { PathModel = "m" });

        Assert.Equal(expectedBudget, ir.Reasoning!.BudgetTokens);
        Assert.Equal(expectedEnabled, ir.Reasoning.Enabled);
    }

    [Fact]
    public void Tool_Mode_Mappings()
    {
        JsonObject WithMode(string mode) => new()
        {
            ["toolConfig"] = new JsonObject { ["functionCallingConfig"] = new JsonObject { ["mode"] = mode } },
        };
        var codec = new GeminiCodec();

        Assert.Equal(ToolChoiceMode.Auto, codec.DecodeRequest(WithMode("AUTO"), new RequestDecodeContext { PathModel = "m" }).ToolChoice!.Mode);
        Assert.Equal(ToolChoiceMode.Auto, codec.DecodeRequest(WithMode("VALIDATED"), new RequestDecodeContext { PathModel = "m" }).ToolChoice!.Mode);
        Assert.Equal(ToolChoiceMode.Required, codec.DecodeRequest(WithMode("ANY"), new RequestDecodeContext { PathModel = "m" }).ToolChoice!.Mode);
        var any = new JsonObject
        {
            ["toolConfig"] = new JsonObject
            {
                ["function_calling_config"] = new JsonObject { ["mode"] = "ANY", ["allowed_function_names"] = new JsonArray("only_one") },
            },
        };
        var choice = codec.DecodeRequest(any, new RequestDecodeContext { PathModel = "m" }).ToolChoice!;
        Assert.Equal(ToolChoiceMode.Specific, choice.Mode);
        Assert.Equal("only_one", choice.Name);
    }
}

public class GeminiRequestEncodeTests
{
    private static readonly RequestEncodeContext Ctx = new() { UpstreamModel = "gemini-2.5-flash", EffortBudgets = new EffortBudgets() };

    private static JsonObject EncodeFixture()
    {
        var decodeCtx = new RequestDecodeContext { PathModel = "models/gemini-2.5-flash", PathStream = true };
        var ir = new GeminiCodec().DecodeRequest(
            (JsonObject)(JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gemini", "cli-request.json")))
                ?? throw new InvalidOperationException("missing fixture")),
            decodeCtx);
        return new GeminiCodec().EncodeRequest(ir, Ctx);
    }

    [Fact]
    public void Fixture_Round_Trips_To_A_Gemini_Body_Without_Model_Or_Stream()
    {
        var body = EncodeFixture();

        Assert.False(body.ContainsKey("model"));   // the model travels in the URL
        Assert.False(body.ContainsKey("stream"));

        Assert.Equal("You are a weather assistant.",
            ((JsonArray)((JsonObject)body["systemInstruction"]!)["parts"]!)[0]!["text"]!.GetValue<string>());

        var contents = (JsonArray)body["contents"]!;
        Assert.Equal(5, contents.Count);
        Assert.Equal("user", ((JsonObject)contents[0]!)["role"]!.GetValue<string>());
        var userParts = (JsonArray)((JsonObject)contents[0]!)["parts"]!;
        Assert.Equal("aGVsbG8=", userParts[1]!["inlineData"]!["data"]!.GetValue<string>());
        Assert.Equal("image/png", userParts[1]!["inlineData"]!["mimeType"]!.GetValue<string>());

        // Thought text is not sent back; the thoughtSignature lands on the functionCall part.
        var modelParts = (JsonArray)((JsonObject)contents[1]!)["parts"]!;
        Assert.Single(modelParts);
        var callPart = (JsonObject)modelParts[0]!;
        Assert.Equal("sig-abc123", callPart["thoughtSignature"]!.GetValue<string>());
        var call = (JsonObject)callPart["functionCall"]!;
        Assert.Equal("get_weather", call["name"]!.GetValue<string>());
        Assert.Equal("Tokyo", call["args"]!["city"]!.GetValue<string>());

        var responsePart = (JsonObject)((JsonArray)((JsonObject)contents[2]!)["parts"]!)[0]!;
        var functionResponse = (JsonObject)responsePart["functionResponse"]!;
        Assert.Equal("get_weather", functionResponse["name"]!.GetValue<string>());
        Assert.Equal("22C, sunny", functionResponse["response"]!["output"]!.GetValue<string>());

        var tools = (JsonArray)body["tools"]!;
        Assert.Equal(2, tools.Count);
        var declaration = (JsonObject)((JsonArray)((JsonObject)tools[0]!)["functionDeclarations"]!)[0]!;
        Assert.Equal("get_weather", declaration["name"]!.GetValue<string>());
        Assert.NotNull(declaration["parametersJsonSchema"]);
        Assert.False(declaration.ContainsKey("parameters")); // standard JSON-schema form on the way out
        Assert.True(((JsonObject)tools[1]!).ContainsKey("googleSearch")); // builtin re-emitted verbatim

        var calling = (JsonObject)((JsonObject)body["toolConfig"]!)["functionCallingConfig"]!;
        Assert.Equal("ANY", calling["mode"]!.GetValue<string>());
        Assert.Equal("get_weather", ((JsonArray)calling["allowedFunctionNames"]!)[0]!.GetValue<string>());

        var gen = (JsonObject)body["generationConfig"]!;
        Assert.Equal(2048, gen["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(0.7, gen["temperature"]!.GetValue<double>());
        Assert.Equal(0.9, gen["topP"]!.GetValue<double>());
        Assert.Equal(40, gen["topK"]!.GetValue<int>());
        Assert.Equal("DONE", ((JsonArray)gen["stopSequences"]!)[0]!.GetValue<string>());
        Assert.Equal("application/json", gen["responseMimeType"]!.GetValue<string>());
        Assert.NotNull(gen["responseJsonSchema"]);
        var thinking = (JsonObject)gen["thinkingConfig"]!;
        Assert.Equal(4096, thinking["thinkingBudget"]!.GetValue<int>());
        Assert.True(thinking["includeThoughts"]!.GetValue<bool>());

        // Extensions merged back: top level and nested generationConfig.
        Assert.Equal("test", ((JsonObject)body["labels"]!)["env"]!.GetValue<string>());
        Assert.Single((JsonArray)body["safetySettings"]!);
        Assert.Equal(7, gen["seed"]!.GetValue<int>());
    }

    [Fact]
    public void Consecutive_Same_Role_Messages_Merge_Into_One_Content()
    {
        var ir = new UnifiedRequest
        {
            Messages =
            [
                new(MessageRole.User, [new TextPart("one")]),
                new(MessageRole.User, [new TextPart("two")]),
                new(MessageRole.Assistant, [new TextPart("answer")]),
            ],
        };

        var body = new GeminiCodec().EncodeRequest(ir, Ctx);

        var contents = (JsonArray)body["contents"]!;
        Assert.Equal(2, contents.Count);
        var first = (JsonArray)((JsonObject)contents[0]!)["parts"]!;
        Assert.Equal(2, first.Count);
        Assert.Equal("one", first[0]!["text"]!.GetValue<string>());
        Assert.Equal("two", first[1]!["text"]!.GetValue<string>());
        Assert.Equal("model", ((JsonObject)contents[1]!)["role"]!.GetValue<string>());
    }

    [Fact]
    public void Tool_Result_Names_Are_Recovered_And_Errors_Use_Error_Field()
    {
        var ir = new UnifiedRequest
        {
            Messages =
            [
                new(MessageRole.Assistant, [new ToolCallPart("call_1", "lookup", "{}")]),
                new(MessageRole.User, [new ToolResultPart("call_1", null, [new TextPart("found")])]),
                new(MessageRole.User, [new ToolResultPart("call_missing", null, [new TextPart("nope")], IsError: true)]),
            ],
        };

        var body = new GeminiCodec().EncodeRequest(ir, Ctx);

        var contents = (JsonArray)body["contents"]!;
        Assert.Equal(2, contents.Count); // the two user results merge
        var parts = (JsonArray)((JsonObject)contents[1]!)["parts"]!;
        var recovered = (JsonObject)((JsonObject)parts[0]!)["functionResponse"]!;
        Assert.Equal("lookup", recovered["name"]!.GetValue<string>());
        Assert.Equal("found", recovered["response"]!["output"]!.GetValue<string>());
        var unknown = (JsonObject)((JsonObject)parts[1]!)["functionResponse"]!;
        Assert.Equal("tool", unknown["name"]!.GetValue<string>());
        Assert.Equal("nope", unknown["response"]!["error"]!.GetValue<string>());
    }

    [Fact]
    public void Foreign_Reasoning_And_Foreign_Builtins_Are_Dropped_With_Warnings()
    {
        var ir = new UnifiedRequest
        {
            Messages =
            [
                new(MessageRole.Assistant,
                [
                    new ReasoningPart("secret thoughts", Signature: "sig-anthropic", Origin: ApiProtocol.Anthropic),
                    new TextPart("answer"),
                ]),
            ],
            Tools =
            [
                new UnifiedTool { Name = "no_schema" },
                new UnifiedTool { BuiltinType = "web_search", BuiltinOrigin = ApiProtocol.OpenAIResponses, BuiltinRaw = new JsonObject { ["web_search"] = new JsonObject() } },
            ],
        };

        var body = new GeminiCodec().EncodeRequest(ir, Ctx);

        var parts = (JsonArray)((JsonArray)body["contents"]!)[0]!["parts"]!;
        Assert.Single(parts); // reasoning dropped entirely
        Assert.Equal("answer", parts[0]!["text"]!.GetValue<string>());

        var tools = (JsonArray)body["tools"]!;
        Assert.Single(tools); // only the function declaration survives
        var declaration = (JsonObject)((JsonArray)((JsonObject)tools[0]!)["functionDeclarations"]!)[0]!;
        var schema = (JsonObject)declaration["parametersJsonSchema"]!;
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.Empty((JsonObject)schema["properties"]!);

        Assert.Equal(2, ir.Warnings.Count);
    }

    [Fact]
    public void Gemini_Signature_Without_Following_Part_Attaches_To_Previous_Part()
    {
        var ir = new UnifiedRequest
        {
            Messages =
            [
                new(MessageRole.Assistant, [new TextPart("done"), new ReasoningPart(null, Signature: "sig-tail", Origin: ApiProtocol.Gemini)]),
            ],
        };

        var body = new GeminiCodec().EncodeRequest(ir, Ctx);

        var parts = (JsonArray)((JsonArray)body["contents"]!)[0]!["parts"]!;
        Assert.Single(parts);
        Assert.Equal("done", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal("sig-tail", parts[0]!["thoughtSignature"]!.GetValue<string>());
    }

    [Fact]
    public void Media_References_Map_To_InlineData_Or_FileData()
    {
        var ir = new UnifiedRequest
        {
            Messages =
            [
                new(MessageRole.User,
                [
                    new ImagePart(null, "image/webp", "QUJD"),
                    new ImagePart("data:image/jpeg;base64,REVG", null, null),
                    new ImagePart("https://example.com/pic.jpg?size=1", null, null),
                    new FilePart("gs://bucket/doc", null, null),
                ]),
            ],
        };

        var body = new GeminiCodec().EncodeRequest(ir, Ctx);

        var parts = (JsonArray)((JsonArray)body["contents"]!)[0]!["parts"]!;
        Assert.Equal("image/webp", parts[0]!["inlineData"]!["mimeType"]!.GetValue<string>());
        Assert.Equal("QUJD", parts[0]!["inlineData"]!["data"]!.GetValue<string>());
        Assert.Equal("image/jpeg", parts[1]!["inlineData"]!["mimeType"]!.GetValue<string>());
        Assert.Equal("REVG", parts[1]!["inlineData"]!["data"]!.GetValue<string>());
        var fileData = (JsonObject)parts[2]!["fileData"]!;
        Assert.Equal("https://example.com/pic.jpg?size=1", fileData["fileUri"]!.GetValue<string>());
        Assert.Equal("image/jpeg", fileData["mimeType"]!.GetValue<string>()); // guessed from the extension
        var gsFile = (JsonObject)parts[3]!["fileData"]!;
        Assert.Equal("application/octet-stream", gsFile["mimeType"]!.GetValue<string>());
    }

    [Fact]
    public void Invalid_Tool_Arguments_Encode_As_Empty_Object()
    {
        var ir = new UnifiedRequest
        {
            Messages = [new(MessageRole.Assistant, [new ToolCallPart("call_1", "f", "{not json")])],
        };

        var body = new GeminiCodec().EncodeRequest(ir, Ctx);

        var call = (JsonObject)((JsonArray)((JsonArray)body["contents"]!)[0]!["parts"]!)[0]!["functionCall"]!;
        Assert.Equal("{}", call["args"]!.ToJsonString(GatewayJson.Options));
    }

    [Theory]
    [InlineData("minimal", 1024)]
    [InlineData("low", 1024)]
    [InlineData("medium", 4096)]
    [InlineData("high", 16384)]
    public void Effort_Converts_To_Thinking_Budget(string effort, int expected)
    {
        var ir = new UnifiedRequest { Reasoning = new ReasoningConfig { Effort = effort } };

        var body = new GeminiCodec().EncodeRequest(ir, Ctx);

        var thinking = (JsonObject)((JsonObject)body["generationConfig"]!)["thinkingConfig"]!;
        Assert.Equal(expected, thinking["thinkingBudget"]!.GetValue<int>());
    }

    [Fact]
    public void Explicit_Budget_Wins_Over_Effort_And_Disabled_Sends_Zero()
    {
        var withBoth = new UnifiedRequest { Reasoning = new ReasoningConfig { Effort = "high", BudgetTokens = 777 } };
        var body = new GeminiCodec().EncodeRequest(withBoth, Ctx);
        Assert.Equal(777, ((JsonObject)((JsonObject)body["generationConfig"]!)["thinkingConfig"]!)["thinkingBudget"]!.GetValue<int>());

        var disabled = new UnifiedRequest { Reasoning = new ReasoningConfig { Enabled = false } };
        var off = new GeminiCodec().EncodeRequest(disabled, Ctx);
        Assert.Equal(0, ((JsonObject)((JsonObject)off["generationConfig"]!)["thinkingConfig"]!)["thinkingBudget"]!.GetValue<int>());

        var thoughtsOnly = new UnifiedRequest { Reasoning = new ReasoningConfig { IncludeThoughts = true } };
        var include = new GeminiCodec().EncodeRequest(thoughtsOnly, Ctx);
        var thinking = (JsonObject)((JsonObject)include["generationConfig"]!)["thinkingConfig"]!;
        Assert.True(thinking["includeThoughts"]!.GetValue<bool>());
        Assert.False(thinking.ContainsKey("thinkingBudget"));
    }

    [Fact]
    public void Json_Object_Format_Sends_Mime_Type_Only()
    {
        var ir = new UnifiedRequest { ResponseFormat = new ResponseFormat { Type = "json_object" } };

        var body = new GeminiCodec().EncodeRequest(ir, Ctx);

        var gen = (JsonObject)body["generationConfig"]!;
        Assert.Equal("application/json", gen["responseMimeType"]!.GetValue<string>());
        Assert.False(gen.ContainsKey("responseJsonSchema"));
    }

    [Fact]
    public void Encoder_Fields_Win_Over_Extensions()
    {
        var ir = new UnifiedRequest
        {
            Temperature = 0.9,
            Extensions =
            {
                [ApiProtocol.Gemini] = new JsonObject
                {
                    ["cachedContent"] = "cached-xyz",
                    ["generationConfig"] = new JsonObject { ["temperature"] = 0.1, ["candidateCount"] = 3 },
                },
            },
        };

        var body = new GeminiCodec().EncodeRequest(ir, Ctx);

        Assert.Equal("cached-xyz", body["cachedContent"]!.GetValue<string>());
        var gen = (JsonObject)body["generationConfig"]!;
        Assert.Equal(0.9, gen["temperature"]!.GetValue<double>());
        Assert.Equal(3, gen["candidateCount"]!.GetValue<int>());
    }
}
