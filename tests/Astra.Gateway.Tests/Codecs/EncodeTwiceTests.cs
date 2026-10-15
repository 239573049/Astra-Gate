using System.Text.Json.Nodes;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Anthropic;
using Astra.Gateway.Protocol.Chat;
using Astra.Gateway.Protocol.Gemini;
using Astra.Gateway.Protocol.Responses;

namespace Astra.Gateway.Tests.Codecs;

/// <summary>
/// A provider-pool failover (issue #2) encodes the same decoded request once per member: encoding must leave the IR as
/// it was, so the second encode equals the first.
/// </summary>
public class EncodeTwiceTests
{
    private const string ChatBody = """
        {
          "model": "gpt-x",
          "max_tokens": 300,
          "messages": [
            { "role": "system", "content": "Be terse." },
            { "role": "user", "content": [ { "type": "text", "text": "Look:" }, { "type": "image_url", "image_url": { "url": "data:image/png;base64,aGVsbG8=" } } ] },
            { "role": "assistant", "content": null, "tool_calls": [ { "id": "call_1", "type": "function", "function": { "name": "read", "arguments": "{\"path\":\"a\"}" } } ] },
            { "role": "tool", "tool_call_id": "call_1", "content": "file a" },
            { "role": "user", "content": "And now?" }
          ],
          "tools": [ { "type": "function", "function": { "name": "read", "parameters": { "type": "object", "properties": { "path": { "type": "string" } } } } } ]
        }
        """;

    public static TheoryData<string> Upstreams => ["anthropic", "chat", "responses", "gemini"];

    [Theory]
    [MemberData(nameof(Upstreams))]
    public void Encoding_The_Same_Request_Twice_Gives_The_Same_Body(string upstream)
    {
        IProtocolCodec codec = upstream switch
        {
            "anthropic" => new AnthropicCodec(),
            "chat" => new ChatCodec(),
            "responses" => new ResponsesCodec(),
            _ => new GeminiCodec(),
        };
        var ir = new ChatCodec().DecodeRequest((JsonObject)JsonNode.Parse(ChatBody)!, new RequestDecodeContext());
        var context = new RequestEncodeContext { UpstreamModel = "up-model" };
        var first = codec.EncodeRequest(ir, context).ToJsonString();
        var warnings = ir.Warnings.Count;
        var second = codec.EncodeRequest(ir, context).ToJsonString();
        Assert.Equal(first, second);
        // Only the warnings list grows (the pipeline trims it back between attempts).
        Assert.True(ir.Warnings.Count >= warnings);
    }
}
