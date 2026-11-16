using System.Text.Json.Nodes;
using Astra.Core.Billing;
using Astra.Gateway.Pipeline;
using Astra.Gateway.Protocol;

namespace Astra.Gateway.Tests;

/// <summary>The pieces of the broader gateway surface: usage of embeddings / rerank / audio, Gemini embeddings, the native path rules.</summary>
public class ForwardedCallsTests
{
    private static NormalizedUsage Usage(string json) => UsageNormalizer.FromForwarded((JsonObject)JsonNode.Parse(json)!);

    [Fact]
    public void Embedding_usage_is_prompt_tokens()
    {
        var usage = Usage("""{"prompt_tokens":8,"total_tokens":8}""");
        Assert.Equal(8, usage.Get(TokenTypes.Input));
        Assert.Equal(0, usage.TotalOutput);
    }

    [Fact]
    public void Rerank_usage_is_total_tokens_or_coheres_billed_tokens_never_search_units()
    {
        Assert.Equal(42, Usage("""{"total_tokens":42}""").Get(TokenTypes.Input));
        Assert.Equal(17, Usage("""{"billed_units":{"input_tokens":17,"search_units":1},"tokens":{"input_tokens":99}}""").Get(TokenTypes.Input));
        Assert.Equal(99, Usage("""{"billed_units":{"search_units":1},"tokens":{"input_tokens":99}}""").Get(TokenTypes.Input));
        Assert.True(Usage("""{"billed_units":{"search_units":1}}""").IsEmpty);
    }

    [Fact]
    public void Transcription_usage_splits_audio_tokens_and_ignores_duration()
    {
        var usage = Usage("""{"type":"tokens","input_tokens":100,"input_token_details":{"text_tokens":10,"audio_tokens":90},"output_tokens":20,"total_tokens":120}""");
        Assert.Equal(90, usage.Get(TokenTypes.InputAudio));
        Assert.Equal(10, usage.Get(TokenTypes.Input));
        Assert.Equal(20, usage.Get(TokenTypes.Output));
        Assert.True(Usage("""{"type":"duration","seconds":12}""").IsEmpty);
    }

    [Fact]
    public void Usage_is_read_from_the_top_level_usage_or_meta_object_only()
    {
        Assert.Equal(5, GatewayPipeline.ImageUsageOf("""{"data":[{"usage":{"x":1}}],"usage":{"total_tokens":5}}"""u8)!["total_tokens"]!.GetValue<int>());
        Assert.NotNull(GatewayPipeline.ImageUsageOf("""{"results":[],"meta":{"billed_units":{"input_tokens":1}}}"""u8));
        Assert.Null(GatewayPipeline.ImageUsageOf("1\n00:00:00,000 --> 00:00:01,000\nhi"u8));
    }

    [Fact]
    public void Gemini_embeddings_encode_one_input_as_embedContent_and_a_list_as_batch()
    {
        var (path, body) = GeminiEmbeddings.Encode((JsonObject)JsonNode.Parse("""{"model":"x","input":"hi","dimensions":256}""")!, "gemini-embedding-001");
        Assert.Equal("models/gemini-embedding-001:embedContent", path);
        Assert.Equal("models/gemini-embedding-001", body["model"]!.GetValue<string>());
        Assert.Equal(256, body["outputDimensionality"]!.GetValue<int>());

        var (batchPath, batch) = GeminiEmbeddings.Encode((JsonObject)JsonNode.Parse("""{"input":["a","b"]}""")!, "models/gemini-embedding-001");
        Assert.Equal("models/gemini-embedding-001:batchEmbedContents", batchPath);
        Assert.Equal(2, batch["requests"]!.AsArray().Count);

        Assert.Throws<ProtocolException>(() => GeminiEmbeddings.Encode((JsonObject)JsonNode.Parse("""{"input":[[1,2,3]]}""")!, "m"));
        Assert.Throws<ProtocolException>(() => GeminiEmbeddings.Encode((JsonObject)JsonNode.Parse("""{}""")!, "m"));
    }

    [Fact]
    public void Gemini_embeddings_decode_to_openais_list_and_an_error_body_is_left_alone()
    {
        var one = GeminiEmbeddings.Decode((JsonObject)JsonNode.Parse("""{"embedding":{"values":[0.5,1.5]}}""")!, "m", base64: false)!;
        Assert.Equal(1.5, one["data"]![0]!["embedding"]![1]!.GetValue<double>());
        Assert.Null(GeminiEmbeddings.Decode((JsonObject)JsonNode.Parse("""{"error":{"message":"no"}}""")!, "m", base64: false));
    }

    [Theory]
    [InlineData("/v1/astra/native/p1/api/v3/tasks", "api/v3/tasks")]
    [InlineData("/v1/astra/native/p1/models/x:generate", "models/x:generate")]
    [InlineData("/v1/astra/native/p1/a%20b/c", "a%20b/c")]
    public void Native_paths_that_stay_on_the_host_are_forwarded_as_written(string raw, string expected) =>
        Assert.Equal(expected, GatewayPipeline.NativePath(raw, "p1"));

    [Theory]
    [InlineData("/v1/astra/native/p1/")]
    [InlineData("/v1/astra/native/p1")]
    [InlineData("/v1/astra/native/p1//evil.test/x")]
    [InlineData("/v1/astra/native/p1/a/../b")]
    [InlineData("/v1/astra/native/p1/a/./b")]
    [InlineData("/v1/astra/native/p1/a/%2e%2e/b")]
    [InlineData("/v1/astra/native/p1/a/%2E%2E/b")]
    [InlineData("/v1/astra/native/p1/a/%252e%252e/b")]
    [InlineData("/v1/astra/native/p1/a%2fb")]
    [InlineData("/v1/astra/native/p1/a%2Fb")]
    [InlineData("/v1/astra/native/p1/a%5cb")]
    [InlineData("/v1/astra/native/p1/a\\b")]
    [InlineData("/v1/astra/native/p1/https:/evil.test/x")]
    [InlineData("/v1/astra/native/p1/a%00b")]
    [InlineData("/v1/astra/native/p1/a b")]
    [InlineData("/other/p1/x")]
    public void Native_paths_that_could_leave_the_host_or_climb_out_are_refused(string raw) =>
        Assert.Throws<GatewayException>(() => GatewayPipeline.NativePath(raw, "p1"));

    [Fact]
    public void The_gateways_own_key_never_goes_on_in_the_query()
    {
        Assert.Equal("page=2&x=1", GatewayPipeline.NativeQuery("key=sk-astra&page=2&KEY=again&x=1"));
        Assert.Equal("", GatewayPipeline.NativeQuery("key=secret"));
        Assert.Equal("monkey=1", GatewayPipeline.NativeQuery("monkey=1"));
    }
}
