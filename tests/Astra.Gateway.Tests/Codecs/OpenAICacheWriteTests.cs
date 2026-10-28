using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Gateway.Protocol;
using Astra.Gateway.Protocol.Chat;
using Astra.Gateway.Protocol.Responses;

namespace Astra.Gateway.Tests.Codecs;

public class OpenAICacheWriteTests
{
    private static IProtocolCodec Codec(ApiProtocol protocol) => protocol == ApiProtocol.OpenAIResponses
        ? new ResponsesCodec()
        : new ChatCodec();

    [Theory]
    [InlineData(ApiProtocol.OpenAIResponses, false)]
    [InlineData(ApiProtocol.OpenAIResponses, true)]
    [InlineData(ApiProtocol.OpenAIChat, false)]
    [InlineData(ApiProtocol.OpenAIChat, true)]
    public void Decoder_Extracts_Cache_Writes_Without_Double_Counting(ApiProtocol protocol, bool stream)
    {
        var folder = protocol == ApiProtocol.OpenAIResponses ? "responses" : "chat";
        var body = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", folder, "response-cache-write.json")))!.AsObject();
        var raw = body["usage"]!.DeepClone();
        var decoder = Codec(protocol).CreateResponseDecoder(new ResponseDecodeContext { Stream = stream, Model = "gpt-5" });
        var events = new List<UnifiedStreamEvent>();
        if (!stream)
        {
            events.AddRange(decoder.DecodeJson(body));
        }
        else if (protocol == ApiProtocol.OpenAIResponses)
        {
            var terminal = new JsonObject { ["type"] = "response.completed", ["response"] = body };
            events.AddRange(decoder.DecodeSse(new SseEvent("response.completed", terminal.ToJsonString())));
        }
        else
        {
            body["object"] = "chat.completion.chunk";
            body["choices"] = new JsonArray();
            events.AddRange(decoder.DecodeSse(new SseEvent(null, body.ToJsonString())));
            events.AddRange(decoder.DecodeSse(new SseEvent(null, "[DONE]")));
        }
        events.AddRange(decoder.Complete());

        var usage = Assert.Single(events.OfType<UsageEvent>());
        Assert.Equal(protocol, usage.Protocol);
        Assert.True(JsonNode.DeepEquals(raw, usage.Raw));
        Assert.Equal("default", usage.Usage.ServiceTier);
        Assert.Equal(3, usage.Usage.Get(TokenTypes.Input));
        Assert.Equal(92_226, usage.Usage.Get(TokenTypes.CacheRead));
        Assert.Equal(9_290, usage.Usage.Get(TokenTypes.CacheWrite5m));
        Assert.Equal(0, usage.Usage.Get(TokenTypes.CacheWrite1h));
        Assert.Equal(396, usage.Usage.Get(TokenTypes.Output));
        Assert.Equal(516, usage.Usage.Get(TokenTypes.Reasoning));
        Assert.Equal(101_519, usage.Usage.TotalInput);
        Assert.Equal(912, usage.Usage.TotalOutput);
    }

    [Theory]
    [InlineData(ApiProtocol.OpenAIResponses, false)]
    [InlineData(ApiProtocol.OpenAIResponses, true)]
    [InlineData(ApiProtocol.OpenAIChat, false)]
    [InlineData(ApiProtocol.OpenAIChat, true)]
    public void Encoder_Preserves_The_Sum_Of_Cache_Write_Ttls(ApiProtocol protocol, bool stream)
    {
        var usage = new NormalizedUsage();
        usage.Add(TokenTypes.Input, 3);
        usage.Add(TokenTypes.CacheRead, 92_226);
        usage.Add(TokenTypes.CacheWrite5m, 8_000);
        usage.Add(TokenTypes.CacheWrite1h, 1_290);
        usage.Add(TokenTypes.Output, 396);
        usage.Add(TokenTypes.Reasoning, 516);
        var encoder = Codec(protocol).CreateResponseEncoder(new ResponseEncodeContext
        {
            Stream = stream, IncludeUsage = true, Model = "gpt-5", ResponseId = "cache-write",
        });
        var frames = new List<SseEvent>();
        foreach (var e in new UnifiedStreamEvent[]
                 {
                     new MessageStartEvent("cache-write", "gpt-5"),
                     new UsageEvent(ApiProtocol.Anthropic, new JsonObject(), usage),
                     new MessageStopEvent(FinishReason.Stop),
                 })
            frames.AddRange(encoder.OnEvent(e));

        var encoded = stream
            ? frames.Where(f => f.Data != "[DONE]").Select(f => JsonNode.Parse(f.Data)!)
                .Select(b => protocol == ApiProtocol.OpenAIResponses ? b["response"]?["usage"] : b["usage"])
                .Last(u => u is not null)!
            : encoder.BuildJson()["usage"]!;
        var responses = protocol == ApiProtocol.OpenAIResponses;
        var details = encoded[responses ? "input_tokens_details" : "prompt_tokens_details"]!;
        Assert.Equal(9_290, details["cache_write_tokens"]?.GetValue<long>());
        Assert.Equal(92_226, details["cached_tokens"]!.GetValue<long>());
        Assert.Equal(101_519, encoded[responses ? "input_tokens" : "prompt_tokens"]!.GetValue<long>());
        Assert.Equal(912, encoded[responses ? "output_tokens" : "completion_tokens"]!.GetValue<long>());
        Assert.Equal(102_431, encoded["total_tokens"]!.GetValue<long>());
        var normalized = UsageNormalizer.Normalize(protocol, encoded)!;
        Assert.Equal(3, normalized.Get(TokenTypes.Input));
        Assert.Equal(9_290, normalized.Get(TokenTypes.CacheWrite5m));
        Assert.Null(details["cache_write_tokens_details"]); // Anthropic TTLs have no OpenAI split
    }

    [Theory]
    [InlineData(ApiProtocol.OpenAIResponses)]
    [InlineData(ApiProtocol.OpenAIChat)]
    public void Encoder_Reports_The_OpenAI_Ttl_Split(ApiProtocol protocol)
    {
        var usage = new NormalizedUsage();
        usage.Add(TokenTypes.Input, 1_000);
        usage.Add(TokenTypes.CacheRead, 4_000);
        usage.Add(TokenTypes.CacheWrite30m, 3_000);
        usage.Add(TokenTypes.CacheWrite12h, 4_000);
        usage.Add(TokenTypes.Output, 10);
        var encoder = Codec(protocol).CreateResponseEncoder(new ResponseEncodeContext
        {
            IncludeUsage = true, Model = "gpt-6.1-sol", ResponseId = "cache-ttl",
        });
        foreach (var e in new UnifiedStreamEvent[]
                 {
                     new MessageStartEvent("cache-ttl", "gpt-6.1-sol"),
                     new UsageEvent(protocol, new JsonObject(), usage),
                     new MessageStopEvent(FinishReason.Stop),
                 })
            encoder.OnEvent(e).ToList();

        var encoded = encoder.BuildJson()["usage"]!;
        var responses = protocol == ApiProtocol.OpenAIResponses;
        var details = encoded[responses ? "input_tokens_details" : "prompt_tokens_details"]!;
        Assert.Equal(12_000, encoded[responses ? "input_tokens" : "prompt_tokens"]!.GetValue<long>());
        Assert.Equal(7_000, details["cache_write_tokens"]!.GetValue<long>());
        Assert.Equal(3_000, details["cache_write_tokens_details"]!["ttl_30m_tokens"]!.GetValue<long>());
        Assert.Equal(4_000, details["cache_write_tokens_details"]!["ttl_12h_tokens"]!.GetValue<long>());
        var normalized = UsageNormalizer.Normalize(protocol, encoded)!;
        Assert.Equal(1_000, normalized.Get(TokenTypes.Input));
        Assert.Equal(3_000, normalized.Get(TokenTypes.CacheWrite30m));
        Assert.Equal(4_000, normalized.Get(TokenTypes.CacheWrite12h));
        Assert.Empty(normalized.Notes);
    }
}
