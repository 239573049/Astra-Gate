using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Anthropic;

namespace Astra.Gateway.Tests.Codecs;

/// <summary>Decoding a Claude Code-like Messages request (system blocks, tools, thinking, history) into the IR.</summary>
public class AnthropicRequestDecodeTests
{
    private static readonly AnthropicCodec Codec = new();

    private static JsonObject ClaudeCodeLikeRequest() => JsonNode.Parse("""
        {
          "model": "claude-sonnet-4-5",
          "max_tokens": 4096,
          "temperature": 0.7,
          "top_p": 0.99,
          "top_k": 40,
          "stop_sequences": ["STOP"],
          "stream": true,
          "service_tier": "auto",
          "metadata": { "user_id": "user-abc" },
          "thinking": { "type": "enabled", "budget_tokens": 2048 },
          "system": [
            { "type": "text", "text": "You are Claude Code.", "cache_control": { "type": "ephemeral" } },
            { "type": "text", "text": "Be concise." }
          ],
          "tools": [
            { "name": "get_weather", "description": "Get current weather", "input_schema": { "type": "object", "properties": { "city": { "type": "string" } } } },
            { "type": "web_search_20250305", "name": "web_search", "max_uses": 3 }
          ],
          "tool_choice": { "type": "auto", "disable_parallel_tool_use": true },
          "messages": [
            { "role": "user", "content": "Check the weather in Paris." },
            { "role": "assistant", "content": [
                { "type": "thinking", "thinking": "I should call get_weather.", "signature": "sig-abc" },
                { "type": "tool_use", "id": "toolu_01", "name": "get_weather", "input": { "city": "Paris" } }
            ]},
            { "role": "user", "content": [
                { "type": "tool_result", "tool_use_id": "toolu_01", "is_error": true, "content": [
                    { "type": "text", "text": "api down" },
                    { "type": "image", "source": { "type": "base64", "media_type": "image/png", "data": "aWNvbg==" } }
                ]},
                { "type": "text", "text": "See attachment." },
                { "type": "image", "source": { "type": "url", "url": "https://example.com/cat.png" } },
                { "type": "document", "source": { "type": "base64", "media_type": "application/pdf", "data": "JVBERi0xLjQ=" } },
                { "type": "server_tool_use", "id": "srvtoolu_02", "name": "web_search", "input": { "query": "weather" } }
            ]},
            { "role": "assistant", "content": [
                { "type": "redacted_thinking", "data": "encrypted-blob" },
                { "type": "text", "text": "Here you go." }
            ]}
          ],
          "custom_field": { "a": 1 }
        }
        """)!.AsObject()!;

    [Fact]
    public void Claude_Code_Like_Request_Decodes_To_Ir()
    {
        var r = Codec.DecodeRequest(ClaudeCodeLikeRequest(), new RequestDecodeContext());

        Assert.Equal("claude-sonnet-4-5", r.Model);
        Assert.Equal(4096, r.MaxOutputTokens);
        Assert.True(r.Stream);
        Assert.Equal(0.7, r.Temperature);
        Assert.Equal(0.99, r.TopP);
        Assert.Equal(40, r.TopK);
        Assert.Equal(["STOP"], r.Stop);
        Assert.Equal("auto", r.ServiceTier);
        Assert.Equal("user-abc", r.User);
        Assert.Equal(2048, r.Reasoning!.BudgetTokens);
        Assert.True(r.Reasoning.Enabled);
        Assert.True(r.Reasoning.IncludeThoughts);

        // system blocks; cache_control is ignored
        Assert.Equal(2, r.System.Count);
        Assert.Equal("You are Claude Code.", Assert.IsType<TextPart>(r.System[0]).Text);
        Assert.Equal("Be concise.", Assert.IsType<TextPart>(r.System[1]).Text);

        // tools: custom + typed builtin (kept verbatim for Anthropic upstreams)
        Assert.Equal(2, r.Tools.Count);
        Assert.Null(r.Tools[0].BuiltinType);
        Assert.Equal("get_weather", r.Tools[0].Name);
        Assert.Equal("Get current weather", r.Tools[0].Description);
        Assert.Equal("object", r.Tools[0].InputSchema!["type"]!.GetValue<string>());
        Assert.Equal("web_search_20250305", r.Tools[1].BuiltinType);
        Assert.Equal(ApiProtocol.Anthropic, r.Tools[1].BuiltinOrigin);
        Assert.Equal(3, r.Tools[1].BuiltinRaw!["max_uses"]!.GetValue<int>());

        // tool_choice auto + parallel tool calls disabled
        Assert.Equal(ToolChoiceMode.Auto, r.ToolChoice!.Mode);
        Assert.False(r.ParallelToolCalls);

        // message 1: plain text
        Assert.Equal(MessageRole.User, r.Messages[0].Role);
        Assert.Equal("Check the weather in Paris.", Assert.IsType<TextPart>(r.Messages[0].Parts[0]).Text);

        // message 2: signed thinking + tool_use
        Assert.Equal(MessageRole.Assistant, r.Messages[1].Role);
        var thinking = Assert.IsType<ReasoningPart>(r.Messages[1].Parts[0]);
        Assert.Equal("I should call get_weather.", thinking.Text);
        Assert.Equal("sig-abc", thinking.Signature);
        Assert.Equal(ApiProtocol.Anthropic, thinking.Origin);
        var call = Assert.IsType<ToolCallPart>(r.Messages[1].Parts[1]);
        Assert.Equal("toolu_01", call.Id);
        Assert.Equal("get_weather", call.Name);
        Assert.Equal("""{"city":"Paris"}""", call.ArgumentsJson);

        // message 3: tool_result (is_error, text + image), text, image url, PDF document, dropped server block
        var parts = r.Messages[2].Parts;
        var result = Assert.IsType<ToolResultPart>(parts[0]);
        Assert.Equal("toolu_01", result.CallId);
        Assert.True(result.IsError);
        Assert.Equal(2, result.Content.Count);
        Assert.Equal("api down", Assert.IsType<TextPart>(result.Content[0]).Text);
        Assert.Equal("aWNvbg==", Assert.IsType<ImagePart>(result.Content[1]).Base64Data);
        Assert.Equal("See attachment.", Assert.IsType<TextPart>(parts[1]).Text);
        var image = Assert.IsType<ImagePart>(parts[2]);
        Assert.Equal("https://example.com/cat.png", image.Url);
        var document = Assert.IsType<FilePart>(parts[3]);
        Assert.Equal("application/pdf", document.MediaType);
        Assert.Equal("JVBERi0xLjQ=", document.Base64Data);
        Assert.Equal(4, parts.Count);

        // message 4: redacted thinking + text
        var redacted = Assert.IsType<ReasoningPart>(r.Messages[3].Parts[0]);
        Assert.Null(redacted.Text);
        Assert.Equal("encrypted-blob", redacted.EncryptedContent);
        Assert.Equal(ApiProtocol.Anthropic, redacted.Origin);
        Assert.Equal("Here you go.", Assert.IsType<TextPart>(r.Messages[3].Parts[1]).Text);

        // unsupported content block is dropped with a warning; unknown top-level field is preserved
        Assert.Contains(r.Warnings, w => w.Contains("server_tool_use"));
        Assert.Equal(1, r.Extensions[ApiProtocol.Anthropic]["custom_field"]!["a"]!.GetValue<int>());
    }

    [Fact]
    public void System_String_And_Disabled_Thinking_Decode()
    {
        var body = JsonNode.Parse("""
            {
              "model": "claude-haiku-4-5",
              "max_tokens": 100,
              "system": "Be brief.",
              "thinking": { "type": "disabled" },
              "messages": [{ "role": "user", "content": "hi" }]
            }
            """)!.AsObject()!;

        var r = Codec.DecodeRequest(body, new RequestDecodeContext());

        Assert.Equal("Be brief.", Assert.IsType<TextPart>(r.System.Single()).Text);
        Assert.NotNull(r.Reasoning);
        Assert.False(r.Reasoning!.Enabled);
    }

    [Fact]
    public void Tool_Choice_Modes_Map()
    {
        static ToolChoice Decode(string choice) => new AnthropicCodec().DecodeRequest(
            JsonNode.Parse($$"""
                {
                  "model": "m",
                  "max_tokens": 1,
                  "tool_choice": {{choice}},
                  "messages": [{ "role": "user", "content": "hi" }]
                }
                """)!.AsObject()!, new RequestDecodeContext()).ToolChoice!;

        Assert.Equal(ToolChoiceMode.Auto, Decode("""{"type":"auto"}""").Mode);
        Assert.Equal(ToolChoiceMode.Required, Decode("""{"type":"any"}""").Mode);
        Assert.Equal(ToolChoiceMode.None, Decode("""{"type":"none"}""").Mode);
        var specific = Decode("""{"type":"tool","name":"get_weather"}""");
        Assert.Equal(ToolChoiceMode.Specific, specific.Mode);
        Assert.Equal("get_weather", specific.Name);
    }

    [Theory]
    [InlineData("""{"model":"m","max_tokens":1,"messages":"nope"}""", "messages")]
    [InlineData("""{"model":"m","max_tokens":1,"messages":[{"role":"wizard","content":"x"}]}""", "role")]
    [InlineData("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":[{"type":"image"}]}]}""", "source")]
    [InlineData("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":"x"}],"tools":[{"input_schema":{}}]}""", "name")]
    [InlineData("""{"model":"m","max_tokens":1,"messages":[{"role":"user","content":"x"}],"tool_choice":{"type":"tool"}}""", "name")]
    public void Invalid_Shapes_Throw_ProtocolException(string body, string expectedFragment)
    {
        var ex = Assert.Throws<ProtocolException>(() =>
            Codec.DecodeRequest(JsonNode.Parse(body)!.AsObject()!, new RequestDecodeContext()));
        Assert.Contains(expectedFragment, ex.Message);
    }
}
