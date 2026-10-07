using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Responses;

namespace Astra.Gateway.Tests.Codecs;

/// <summary>Decodes client requests (Codex CLI shapes) into the IR.</summary>
public class ResponsesRequestDecodeTests
{
    private static readonly ResponsesCodec Codec = new();

    private static JsonObject FixtureBody(string name) =>
        (JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses", name))) as JsonObject)!;

    [Fact]
    public void Codex_Request_Decodes_To_Ir()
    {
        var request = Codec.DecodeRequest(FixtureBody("codex-request.json"), new RequestDecodeContext());

        Assert.Equal("gpt-5.1-codex", request.Model);
        Assert.True(request.Stream);

        // instructions + the developer message → System, in order.
        Assert.Equal(2, request.System.Count);
        Assert.Equal("You are Codex, a coding agent.", Assert.IsType<TextPart>(request.System[0]).Text);
        Assert.Equal("Be terse.", Assert.IsType<TextPart>(request.System[1]).Text);

        // user message | (reasoning + function_call merge into one assistant message) | tool result.
        Assert.Equal(3, request.Messages.Count);

        var user = request.Messages[0];
        Assert.Equal(MessageRole.User, user.Role);
        var text = Assert.Single(user.Parts.OfType<TextPart>());
        Assert.Equal("List the files in src/", text.Text);

        var assistant = request.Messages[1];
        Assert.Equal(MessageRole.Assistant, assistant.Role);
        var reasoning = Assert.IsType<ReasoningPart>(assistant.Parts[0]);
        Assert.Equal("Inspect the repository layout first.", reasoning.Text);
        Assert.Equal("gAAAAABprev123", reasoning.EncryptedContent);
        Assert.Equal(ApiProtocol.OpenAIResponses, reasoning.Origin);
        Assert.Equal("rs_prev_1", reasoning.Id);
        var call = Assert.IsType<ToolCallPart>(assistant.Parts[1]);
        Assert.Equal("call_ls", call.Id);
        Assert.Equal("shell", call.Name);
        Assert.Equal("""{"cmd":["ls","src"]}""", call.ArgumentsJson);

        var result = Assert.IsType<ToolResultPart>(Assert.Single(request.Messages[2].Parts));
        Assert.Equal(MessageRole.User, request.Messages[2].Role);
        Assert.Equal("call_ls", result.CallId);
        Assert.Equal("Astra.Gateway\nAstra.Core", Assert.IsType<TextPart>(Assert.Single(result.Content)).Text);

        // Tools: flat function + builtin web_search (kept verbatim for its own protocol).
        Assert.Equal(2, request.Tools.Count);
        var fn = request.Tools[0];
        Assert.Equal("shell", fn.Name);
        Assert.Equal("Runs a shell command", fn.Description);
        Assert.NotNull(fn.InputSchema);
        Assert.False(fn.Strict);
        Assert.Null(fn.BuiltinType);
        var builtin = request.Tools[1];
        Assert.Equal("web_search", builtin.BuiltinType);
        Assert.Equal(ApiProtocol.OpenAIResponses, builtin.BuiltinOrigin);
        Assert.NotNull(builtin.BuiltinRaw);

        Assert.NotNull(request.ToolChoice);
        Assert.Equal(ToolChoiceMode.Auto, request.ToolChoice.Mode);
        Assert.False(request.ParallelToolCalls!.Value);

        Assert.NotNull(request.Reasoning);
        Assert.Equal("high", request.Reasoning.Effort);
        Assert.True(request.Reasoning.Enabled);
        Assert.True(request.Reasoning.IncludeThoughts);

        Assert.Equal(0.2, request.Temperature);
        Assert.Null(request.PreviousResponseId);

        // store / include / prompt_cache_key land in Extensions for the same-protocol encoder.
        var extensions = Assert.Single(request.Extensions).Value;
        Assert.False(extensions["store"]!.GetValue<bool>());
        Assert.Equal("codex-session-1", extensions["prompt_cache_key"]!.GetValue<string>());
        Assert.Equal("reasoning.encrypted_content", extensions["include"]!.AsArray()[0]!.GetValue<string>());

        // The web_search_call input item is not representable in the IR → dropped with a warning.
        Assert.Contains(request.Warnings, w => w.Contains("web_search_call"));
    }

    [Fact]
    public void String_Input_Becomes_One_User_Message()
    {
        var request = Codec.DecodeRequest((JsonNode.Parse("""
            {"model":"gpt-5.1","input":"hello","stream":false}
            """) as JsonObject)!, new RequestDecodeContext());

        var message = Assert.Single(request.Messages);
        Assert.Equal(MessageRole.User, message.Role);
        Assert.Equal("hello", Assert.IsType<TextPart>(Assert.Single(message.Parts)).Text);
        Assert.False(request.Stream);
        // No unmapped fields → no Extensions entry → EncodeRequest treats it as not-from-Responses.
        Assert.Empty(request.Extensions);
    }

    [Fact]
    public void Bare_Role_Items_And_Media_Parts_Decode()
    {
        var request = Codec.DecodeRequest((JsonNode.Parse("""
            {
              "model": "gpt-5.1",
              "input": [
                { "role": "system", "content": "sys" },
                { "role": "developer", "content": "dev" },
                { "role": "user", "content": [
                    { "type": "input_text", "text": "look" },
                    { "type": "input_image", "image_url": "https://example.com/x.png", "detail": "low" },
                    { "type": "input_file", "file_id": "file-pdf-1", "filename": "doc.pdf" }
                ] },
                { "role": "assistant", "content": [ { "type": "refusal", "refusal": "No." } ] }
              ],
              "tool_choice": { "type": "function", "name": "shell" },
              "reasoning": { "effort": "none" },
              "text": { "verbosity": "short", "format": { "type": "json_schema", "name": "out", "schema": { "type": "object" }, "strict": true } },
              "previous_response_id": "resp_old",
              "safety_identifier": "user-9",
              "metadata": { "k": "v" }
            }
            """) as JsonObject)!, new RequestDecodeContext());

        Assert.Equal(["sys", "dev"], request.System.Select(p => Assert.IsType<TextPart>(p).Text));
        Assert.Equal(2, request.Messages.Count);

        var user = request.Messages[0];
        Assert.Equal(MessageRole.User, user.Role);
        Assert.IsType<TextPart>(user.Parts[0]);
        var image = Assert.IsType<ImagePart>(user.Parts[1]);
        Assert.Equal("https://example.com/x.png", image.Url);
        Assert.Equal("low", image.Detail);
        var file = Assert.IsType<FilePart>(user.Parts[2]);
        Assert.Equal("file-pdf-1", file.FileId);
        Assert.Equal("doc.pdf", file.FileName);

        var assistant = request.Messages[1];
        Assert.Equal(MessageRole.Assistant, assistant.Role);
        Assert.Equal("No.", Assert.IsType<TextPart>(Assert.Single(assistant.Parts)).Text);

        Assert.Equal(ToolChoiceMode.Specific, request.ToolChoice!.Mode);
        Assert.Equal("shell", request.ToolChoice.Name);

        // effort "none" → reasoning explicitly disabled.
        Assert.NotNull(request.Reasoning);
        Assert.False(request.Reasoning.Enabled);

        Assert.Equal("json_schema", request.ResponseFormat!.Type);
        Assert.Equal("out", request.ResponseFormat.Name);
        Assert.NotNull(request.ResponseFormat.Schema);
        Assert.True(request.ResponseFormat.Strict);

        Assert.Equal("resp_old", request.PreviousResponseId);
        Assert.Equal("user-9", request.User);
        Assert.Equal("v", request.Metadata!["k"]!.GetValue<string>());

        // text.verbosity is not representable → kept under Extensions.text minus format.
        var extensions = Assert.Single(request.Extensions).Value;
        Assert.Equal("short", extensions["text"]!["verbosity"]!.GetValue<string>());
        Assert.Null(extensions["text"]!["format"]);
    }

    [Fact]
    public void Missing_Model_Throws_ProtocolException()
    {
        var body = (JsonNode.Parse("""{"input":"hi"}""") as JsonObject)!;
        var ex = Assert.Throws<ProtocolException>(() => Codec.DecodeRequest(body, new RequestDecodeContext()));
        Assert.Equal(400, ex.Status);
    }
}

/// <summary>Encodes the IR back into upstream Responses requests.</summary>
public class ResponsesRequestEncodeTests
{
    private static UnifiedRequest Decoded() =>
        new ResponsesCodec().DecodeRequest(
            (JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses", "codex-request.json"))) as JsonObject)!,
            new RequestDecodeContext());

    [Fact]
    public void Responses_Ir_Round_Trips()
    {
        var ir = Decoded();
        var body = new ResponsesCodec().EncodeRequest(ir, new RequestEncodeContext
        {
            UpstreamModel = "gpt-5.1-codex",
            EffortBudgets = new EffortBudgets(),
        });

        Assert.Equal("gpt-5.1-codex", body["model"]!.GetValue<string>());
        Assert.Equal("You are Codex, a coding agent.\n\nBe terse.", body["instructions"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(0.2, body["temperature"]!.GetValue<double>());
        Assert.Equal("auto", body["tool_choice"]!.GetValue<string>());
        Assert.False(body["parallel_tool_calls"]!.GetValue<bool>());

        var reasoning = body["reasoning"]!.AsObject();
        Assert.Equal("high", reasoning["effort"]!.GetValue<string>());
        Assert.Equal("auto", reasoning["summary"]!.GetValue<string>());

        // Tools: flat function + the builtin re-emitted verbatim.
        var tools = body["tools"]!.AsArray();
        Assert.Equal(2, tools.Count);
        Assert.Equal("function", tools[0]!["type"]!.GetValue<string>());
        Assert.Equal("shell", tools[0]!["name"]!.GetValue<string>());
        Assert.Equal("web_search", tools[1]!["type"]!.GetValue<string>());

        var input = body["input"]!.AsArray();
        Assert.Equal(4, input.Count);

        Assert.Equal("message", input[0]!["type"]!.GetValue<string>());
        Assert.Equal("user", input[0]!["role"]!.GetValue<string>());
        Assert.Equal("input_text", input[0]!["content"]!.AsArray()[0]!["type"]!.GetValue<string>());
        Assert.Equal("List the files in src/", input[0]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>());

        var reasoningItem = input[1]!.AsObject();
        Assert.Equal("reasoning", reasoningItem["type"]!.GetValue<string>());
        Assert.Equal("rs_prev_1", reasoningItem["id"]!.GetValue<string>());
        Assert.Equal("gAAAAABprev123", reasoningItem["encrypted_content"]!.GetValue<string>());
        Assert.Equal("Inspect the repository layout first.",
            reasoningItem["summary"]!.AsArray()[0]!["text"]!.GetValue<string>());

        var call = input[2]!.AsObject();
        Assert.Equal("function_call", call["type"]!.GetValue<string>());
        Assert.Equal("call_ls", call["call_id"]!.GetValue<string>());
        Assert.Equal("shell", call["name"]!.GetValue<string>());
        Assert.Equal("""{"cmd":["ls","src"]}""", call["arguments"]!.GetValue<string>());

        // Text-only output is sent in the string form.
        var output = input[3]!.AsObject();
        Assert.Equal("function_call_output", output["type"]!.GetValue<string>());
        Assert.Equal("call_ls", output["call_id"]!.GetValue<string>());
        Assert.Equal("Astra.Gateway\nAstra.Core", output["output"]!.GetValue<string>());

        // Client-sent store:false survives via Extensions.
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal("codex-session-1", body["prompt_cache_key"]!.GetValue<string>());
        Assert.Equal(["reasoning.encrypted_content"], body["include"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Foreign_Ir_Gets_Store_False_And_Loses_Foreign_Builtins_And_Reasoning()
    {
        var ir = new UnifiedRequest
        {
            Model = "gpt-5.1",
            Messages =
            [
                new(MessageRole.User, [new TextPart("hi")]),
                new(MessageRole.Assistant, [
                    new ReasoningPart("thought", Signature: "sigX", EncryptedContent: "encX", Origin: ApiProtocol.Anthropic),
                    new ToolCallPart("call_1", "echo", "{}"),
                ]),
                new(MessageRole.User, [new ToolResultPart("call_1", "echo", [new TextPart("done")], IsError: true)]),
            ],
            Tools =
            [
                new UnifiedTool { Name = "echo", Description = "Echo", InputSchema = (JsonNode.Parse("""{"type":"object"}""")) },
                new UnifiedTool { Name = "bash_20250124", BuiltinType = "bash_20250124", BuiltinOrigin = ApiProtocol.Anthropic },
            ],
            Reasoning = new ReasoningConfig { BudgetTokens = 2000, IncludeThoughts = true },
        };

        var body = new ResponsesCodec().EncodeRequest(ir, new RequestEncodeContext
        {
            UpstreamModel = "gpt-5.1",
            EffortBudgets = new EffortBudgets { Low = 1024, Medium = 4096, High = 16384 },
        });

        // Not from a Responses client → never opt the upstream conversation store in.
        Assert.False(body["store"]!.GetValue<bool>());

        // Anthropic builtin tool and foreign reasoning (signature/encrypted payload) are dropped with warnings.
        var tools = body["tools"]!.AsArray();
        Assert.Single(tools);
        Assert.Equal("echo", tools[0]!["name"]!.GetValue<string>());
        Assert.Contains(ir.Warnings, w => w.Contains("bash_20250124"));
        Assert.Contains(ir.Warnings, w => w.Contains("Anthropic"));

        var input = body["input"]!.AsArray();
        Assert.Equal(3, input.Count);
        Assert.Equal("message", input[0]!["type"]!.GetValue<string>());
        Assert.Equal("function_call", input[1]!["type"]!.GetValue<string>()); // reasoning dropped
        Assert.Equal("function_call_output", input[2]!["type"]!.GetValue<string>());

        // budget 2000 → "medium" (≤ Medium); IncludeThoughts → summary "auto".
        var reasoning = body["reasoning"]!.AsObject();
        Assert.Equal("medium", reasoning["effort"]!.GetValue<string>());
        Assert.Equal("auto", reasoning["summary"]!.GetValue<string>());

        // ToolResultPart.IsError has no Responses representation → warning.
        Assert.Contains(ir.Warnings, w => w.Contains("IsError"));
    }

    [Fact]
    public void Disabled_Reasoning_And_Text_Format_Mapping()
    {
        var body = new ResponsesCodec().EncodeRequest(new UnifiedRequest
        {
            Model = "gpt-5.1",
            Reasoning = new ReasoningConfig { Enabled = false },
            ResponseFormat = new ResponseFormat
            {
                Type = "json_schema",
                Name = "out",
                Schema = (JsonNode.Parse("""{"type":"object"}"""))!,
                Strict = true,
            },
        }, new RequestEncodeContext { UpstreamModel = "gpt-5.1" });

        Assert.Null(body["reasoning"]); // Enabled=false → omit entirely
        var format = body["text"]!["format"]!.AsObject();
        Assert.Equal("json_schema", format["type"]!.GetValue<string>());
        Assert.Equal("out", format["name"]!.GetValue<string>());
        Assert.True(format["strict"]!.GetValue<bool>());
        Assert.NotNull(format["schema"]);
    }
}
