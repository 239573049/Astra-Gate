using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Gateway.Pipeline;
using Microsoft.AspNetCore.Http;

namespace Astra.Gateway.Tests;

public class StickyKeysTests
{
    private const string Session = "11111111-2222-3333-4444-555555555555";

    private static IHeaderDictionary Headers(params (string Name, string Value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        foreach (var (name, value) in headers) ctx.Request.Headers[name] = value;
        return ctx.Request.Headers;
    }

    private static JsonObject Body(string json) => JsonNode.Parse(json)!.AsObject();

    [Theory]
    [InlineData(ApiProtocol.OpenAIChat)]
    [InlineData(ApiProtocol.OpenAIResponses)]
    public void Prompt_Cache_Key_Is_The_Key_For_OpenAI_Protocols(ApiProtocol protocol)
    {
        var key = StickyKeys.Of(protocol, Body("""{"model":"gpt-5","prompt_cache_key":"conv-42","input":"hi"}"""), Headers());
        Assert.Equal("pck:conv-42", key);
        Assert.Null(StickyKeys.Of(protocol, Body("""{"model":"gpt-5","input":"hi"}"""), Headers()));
        Assert.Null(StickyKeys.Of(protocol, Body("""{"model":"gpt-5","prompt_cache_key":"","input":"hi"}"""), Headers()));
        Assert.Null(StickyKeys.Of(protocol, Body("""{"model":"gpt-5","prompt_cache_key":7}"""), Headers()));
    }

    [Fact]
    public void Gemini_And_Missing_Bodies_Have_No_Key()
    {
        Assert.Null(StickyKeys.Of(ApiProtocol.Gemini, Body("""{"prompt_cache_key":"k"}"""), Headers()));
        Assert.Null(StickyKeys.Of(ApiProtocol.OpenAIChat, null, Headers()));
    }

    [Fact]
    public void Anthropic_Prefers_The_Claude_Code_Session_Header()
    {
        var body = Body($$"""{"metadata":{"user_id":"user_{{new string('a', 64)}}_account__session_{{Session}}"},"messages":[{"role":"user","content":"hi"}]}""");
        Assert.Equal("cc:from-header", StickyKeys.Of(ApiProtocol.Anthropic, body, Headers(("x-claude-code-session-id", " from-header "))));
    }

    [Fact]
    public void Anthropic_Reads_The_Session_From_Both_User_Id_Formats()
    {
        var legacy = Body($$"""{"metadata":{"user_id":"user_{{new string('a', 64)}}_account_{{Session}}_session_{{Session}}"},"messages":[]}""");
        Assert.Equal("cc:" + Session, StickyKeys.Of(ApiProtocol.Anthropic, legacy, Headers()));

        var userId = new JsonObject { ["device_id"] = new string('b', 64), ["account_uuid"] = "", ["session_id"] = "sess-json" }.ToJsonString();
        var modern = new JsonObject { ["metadata"] = new JsonObject { ["user_id"] = userId }, ["messages"] = new JsonArray() };
        Assert.Equal("cc:sess-json", StickyKeys.Of(ApiProtocol.Anthropic, modern, Headers()));
    }

    [Fact]
    public void Anthropic_Falls_Back_To_A_Fingerprint_Of_System_And_First_User_Message()
    {
        string? Key(string json) => StickyKeys.Of(ApiProtocol.Anthropic, Body(json), Headers());

        var a = Key("""{"system":"be brief","messages":[{"role":"user","content":"hello"},{"role":"assistant","content":"hi"},{"role":"user","content":"more"}]}""");
        Assert.StartsWith("fp:", a);
        // Later turns of the same conversation keep the key; so do string vs block forms of the same text.
        Assert.Equal(a, Key("""{"system":[{"type":"text","text":"be brief"}],"messages":[{"role":"user","content":[{"type":"text","text":"hello"},{"type":"image","source":{}}]}]}"""));
        // A different conversation does not.
        Assert.NotEqual(a, Key("""{"system":"be brief","messages":[{"role":"user","content":"goodbye"}]}"""));
        Assert.NotEqual(a, Key("""{"system":"be verbose","messages":[{"role":"user","content":"hello"}]}"""));
        // Nothing to fingerprint.
        Assert.Null(Key("""{"messages":[{"role":"assistant","content":"hi"}]}"""));
        Assert.Null(Key("""{"model":"x"}"""));
    }

    [Fact]
    public void Overlong_Keys_Are_Hashed_To_A_Bounded_Size()
    {
        var key = StickyKeys.Of(ApiProtocol.OpenAIResponses, Body($$"""{"prompt_cache_key":"{{new string('k', 5000)}}"}"""), Headers());
        Assert.StartsWith("h:", key);
        Assert.True(key!.Length <= StickyKeys.MaxKeyLength);
        Assert.Equal(key, StickyKeys.Of(ApiProtocol.OpenAIResponses, Body($$"""{"prompt_cache_key":"{{new string('k', 5000)}}"}"""), Headers()));
    }
}
