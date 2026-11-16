using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Privacy;
using Astra.Core.Requests;
using Astra.Core.Tokens;
using Astra.Gateway.Pipeline;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Astra.Server.IntegrationTests;

/// <summary>
/// The broader gateway surface (magpie parity): embeddings, rerank and audio forwarded like images; the vendor-native passthrough;
/// the Ollama face; the Responses WebSocket. Each has token auth, a request-log row of its own protocol and, where the answer
/// reports usage, a bill.
/// </summary>
public sealed class GatewayForwardedTests
{
    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Task PriceAsync(GatewayFixture gw, string model, decimal input) => gw.Host.Db.Providers.InsertModelAsync(new ProviderModel
    {
        ProviderId = gw.Provider.Id,
        ModelId = model,
        Enabled = true,
        Overrides = new ModelOverrides { Pricing = new PricingSchedule { Base = new PriceSet { [TokenTypes.Input] = input, [TokenTypes.Output] = input } } },
    });

    // ---------------------------------------------------------------- embeddings, rerank

    [Fact]
    public async Task Embeddings_are_forwarded_untranslated_logged_as_their_own_protocol_and_billed_by_input_tokens()
    {
        const string answer = """{"object":"list","data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],"model":"text-embedding-3-small","usage":{"prompt_tokens":8,"total_tokens":8}}""";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(answer));
        await PriceAsync(gw, "text-embedding-3-small", 20m);
        var body = """{"model":"text-embedding-3-small","input":["hello","world"],"dimensions":2}""";

        var response = await gw.PostAsync("/v1/embeddings", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(answer, await response.Content.ReadAsStringAsync());
        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/embeddings", seen.Url.ToString());
        Assert.Equal("Bearer sk-upstream-secret", seen.Headers["authorization"]);
        Assert.Equal(body, seen.Body);
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.Equal("openai-embeddings", record.InboundProtocol);
        Assert.Equal("openai-embeddings", record.UpstreamProtocol);
        Assert.Equal(8, record.TotalInputTokens);
        Assert.Equal(8 * 20 * 1000L, record.CostNanoUsd); // 8 tokens at $20 per million
        var spans = await gw.Host.Db.Traces.ListSpansAsync(record.Id);
        Assert.Equal("embeddings", JsonNode.Parse(spans[0].AttributesJson!)!["gen_ai.operation.name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Embeddings_need_a_token_and_a_model()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => Json("{}"));
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings") { Content = new StringContent("{}", Encoding.UTF8, "application/json") })
            Assert.Equal(HttpStatusCode.Unauthorized, (await gw.Host.Client.SendAsync(request)).StatusCode);
        var missing = await gw.PostAsync("/v1/embeddings", """{"input":"x"}""");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("text-embedding-3-small", await missing.Content.ReadAsStringAsync());
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task A_gemini_only_provider_gets_embeddings_translated_to_embedContent_and_back()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.GeminiCli, ApiProtocol.Gemini, request =>
            request.Url.AbsolutePath.EndsWith(":batchEmbedContents", StringComparison.Ordinal)
                ? FakeUpstream.Json("""{"embeddings":[{"values":[1.5,2.5]},{"values":[3.5,4.5]}]}""")
                : FakeUpstream.Json("""{"embedding":{"values":[0.25,0.5]}}"""),
            AuthSchemes.XGoogApiKey);

        var one = await gw.PostAsync("/v1/embeddings", """{"model":"gemini-embedding-001","input":"hello","dimensions":2}""");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        var single = JsonNode.Parse(await one.Content.ReadAsStringAsync())!;
        Assert.Equal("list", single["object"]!.GetValue<string>());
        Assert.Equal(0.5, single["data"]![0]!["embedding"]![1]!.GetValue<double>());
        var sent = gw.Upstream.Requests[0];
        Assert.Equal("https://upstream.test/v1beta/models/gemini-embedding-001:embedContent", sent.Url.ToString());
        Assert.Equal("models/gemini-embedding-001", sent.Json["model"]!.GetValue<string>());
        Assert.Equal("hello", sent.Json["content"]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(2, sent.Json["outputDimensionality"]!.GetValue<int>());
        Assert.Equal("sk-upstream-secret", sent.Headers["x-goog-api-key"]);

        var batch = await gw.PostAsync("/v1/embeddings", """{"model":"gemini-embedding-001","input":["a","b"],"encoding_format":"base64"}""");
        var many = JsonNode.Parse(await batch.Content.ReadAsStringAsync())!["data"]!.AsArray();
        Assert.Equal(2, many.Count);
        Assert.Equal(1, many[1]!["index"]!.GetValue<int>());
        var floats = new float[2];
        Buffer.BlockCopy(Convert.FromBase64String(many[0]!["embedding"]!.GetValue<string>()), 0, floats, 0, 8);
        Assert.Equal([1.5f, 2.5f], floats);
        Assert.EndsWith(":batchEmbedContents", gw.Upstream.Requests[1].Url.AbsolutePath);
        Assert.Equal(2, gw.Upstream.Requests[1].Json["requests"]!.AsArray().Count);

        var record = await gw.RecordOfAsync(one);
        Assert.Equal("openai-embeddings", record.InboundProtocol);
        Assert.Equal(RequestStatus.Success, record.Status);
    }

    [Theory]
    [InlineData("""{"results":[{"index":1,"relevance_score":0.9}],"usage":{"total_tokens":42}}""", 42)] // Jina / Voyage
    [InlineData("""{"results":[{"index":1,"relevance_score":0.9}],"meta":{"billed_units":{"input_tokens":17,"search_units":1}}}""", 17)] // Cohere
    public async Task Rerank_is_forwarded_and_billed_from_whichever_usage_the_vendor_reports(string answer, long tokens)
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(answer));
        var body = """{"model":"rerank-3","query":"what eats seeds","documents":["a magpie","a stone"],"top_n":1}""";

        var response = await gw.PostAsync("/v1/rerank", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(answer, await response.Content.ReadAsStringAsync());
        Assert.Equal("https://upstream.test/v1/rerank", Assert.Single(gw.Upstream.Requests).Url.ToString());
        var record = await gw.RecordOfAsync(response);
        Assert.Equal("rerank", record.InboundProtocol);
        Assert.Equal(tokens, record.TotalInputTokens);
    }

    [Fact]
    public async Task Embeddings_and_rerank_bodies_pass_through_the_privacy_guard()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => Json("""{"data":[]}"""));
        await gw.Host.App.Services.GetRequiredService<PrivacyGuardService>()
            .UpdateAsync(new PrivacySettings { Enabled = true, DryRun = false, DefaultAction = PrivacyActions.Block });

        var response = await gw.PostAsync("/v1/embeddings", """{"model":"m","input":"key AKIAIOSFODNN7EXAMPLE"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task The_relay_forwards_rerank_and_speech_with_the_callers_own_credential()
    {
        var upstream = new RelayUpstream
        {
            Respond = (sent, _) => Task.FromResult(sent.Uri.AbsolutePath switch
            {
                "/rerank" => RelayUpstream.Json("""{"results":[{"index":0,"relevance_score":0.5}],"usage":{"total_tokens":3}}"""),
                "/audio/speech" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("audio/mpeg") } } },
                _ => RelayUpstream.Default(sent),
            }),
        };
        await using var relay = await RelayHost.StartAsync(upstream);

        using var rerank = await relay.SendAsync("/v1/rerank", """{"model":"rerank-3","query":"q","documents":["a"]}""");
        Assert.Equal(HttpStatusCode.OK, rerank.StatusCode);
        Assert.Equal("Bearer gho_test_alice", Assert.Single(relay.Upstream.Requests, r => r.Uri.AbsolutePath == "/rerank").Authorization);

        using var speech = await relay.SendAsync("/audio/speech", """{"model":"gpt-4o-mini-tts","input":"hi","voice":"alloy"}""");
        Assert.Equal(HttpStatusCode.OK, speech.StatusCode);
        Assert.Equal([1, 2, 3], await speech.Content.ReadAsByteArrayAsync());
        Assert.Equal("Bearer gho_test_alice", Assert.Single(relay.Upstream.Requests, r => r.Uri.AbsolutePath == "/audio/speech").Authorization);
    }

    // ---------------------------------------------------------------- audio

    private const string Boundary = "----astra-audio-test";

    private static async Task<(byte[] Bytes, string ContentType)> AudioFormAsync(string model, byte[] audio)
    {
        using var form = new MultipartFormDataContent(Boundary);
        form.Add(new StringContent(model), "model");
        var file = new ByteArrayContent(audio);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "clip.wav");
        form.Add(new StringContent("srt"), "response_format");
        return (await form.ReadAsByteArrayAsync(), form.Headers.ContentType!.ToString());
    }

    [Fact]
    public async Task A_transcription_upload_goes_upstream_byte_for_byte_with_the_mapped_model_and_the_vendors_text_comes_back()
    {
        var upstream = new RawUpstream(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("1\n00:00:00,000 --> 00:00:01,000\nhello\n", Encoding.UTF8, "text/plain"),
        });
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => Json("{}"));
        gw.Host.App.Services.GetRequiredService<GatewayHttpClients>().HandlerFactory = _ => upstream;
        gw.Provider.Settings[Provider.ModelMapKey] = new JsonObject { ["house-whisper"] = "whisper-1" };
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        var audio = new byte[200_000];
        new Random(3).NextBytes(audio);
        var (bytes, contentType) = await AudioFormAsync("house-whisper", audio);
        var (expected, _) = await AudioFormAsync("whisper-1", audio);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/transcriptions") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.TryAddWithoutValidation(HeaderNames.ContentType, contentType);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        var response = await gw.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hello", await response.Content.ReadAsStringAsync());
        var seen = Assert.Single(upstream.Requests);
        Assert.Equal("https://upstream.test/v1/audio/transcriptions", seen.Url.ToString());
        Assert.Equal(expected, seen.Body);
        var record = await gw.RecordOfAsync(response);
        Assert.Equal("openai-audio", record.InboundProtocol);
        Assert.Equal("house-whisper", record.RequestedModel);
        Assert.Equal("whisper-1", record.UpstreamModel);
        Assert.Equal(RequestStatus.Success, record.Status);
    }

    [Fact]
    public async Task Speech_audio_is_copied_through_as_the_vendor_sent_it()
    {
        var mp3 = new byte[] { 0x49, 0x44, 0x33, 0x04, 0x00, 0xff, 0xfb, 0x90, 0x00 };
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat,
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(mp3) { Headers = { ContentType = new("audio/mpeg") } } });

        var response = await gw.PostAsync("/v1/audio/speech", """{"model":"gpt-4o-mini-tts","input":"hello","voice":"alloy"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(mp3, await response.Content.ReadAsByteArrayAsync());
        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/audio/speech", seen.Url.ToString());
        Assert.Equal("*/*", seen.Headers["accept"]);
        Assert.Equal("openai-audio", (await gw.RecordOfAsync(response)).InboundProtocol);
    }

    // ---------------------------------------------------------------- native

    [Fact]
    public async Task A_native_call_goes_to_the_providers_own_origin_with_its_key_and_never_the_callers()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => Json("""{"id":"task_1","usage":{"input_tokens":5,"output_tokens":7}}"""));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/astra/native/{gw.Provider.Id}/api/v3/contents/generations/tasks?key=leak&page=2")
        {
            Content = new StringContent("""{"model":"seedance","content":"x"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        request.Headers.TryAddWithoutValidation("x-goog-api-key", "caller-secret");
        request.Headers.TryAddWithoutValidation("Cookie", "session=1");
        request.Headers.TryAddWithoutValidation("X-DashScope-Async", "enable");

        var response = await gw.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("task_1", await response.Content.ReadAsStringAsync());
        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/api/v3/contents/generations/tasks?page=2", seen.Url.ToString()); // origin + path; no ?key=
        Assert.Equal("Bearer sk-upstream-secret", seen.Headers["authorization"]);
        Assert.False(seen.Headers.ContainsKey("x-goog-api-key"));
        Assert.False(seen.Headers.ContainsKey("cookie"));
        Assert.Equal("enable", seen.Headers["x-dashscope-async"]);
        var record = await gw.RecordOfAsync(response);
        Assert.Equal("native", record.InboundProtocol);
        Assert.Equal(gw.Provider.Id, record.ProviderId);
        Assert.Equal("seedance", record.RequestedModel);
        Assert.Equal(5, record.TotalInputTokens);
        Assert.Equal(7, record.TotalOutputTokens);
    }

    [Theory]
    [InlineData("a/%252e%252e/b")]
    [InlineData("a%5cb")]
    [InlineData("https:/evil.test/x")]
    public async Task A_native_path_that_could_leave_the_providers_host_is_refused(string path)
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => Json("{}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/astra/native/{gw.Provider.Id}/{path}");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");

        var response = await gw.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task A_native_call_reaches_only_providers_the_token_can_route_to_and_never_a_subscription()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => Json("{}"));
        var other = new Provider
        {
            Id = Ulid.NewUlid(), Name = "Unbound", AuthScheme = AuthSchemes.Bearer,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://other.test/v1" }],
        };
        await gw.Host.Db.Providers.InsertAsync(other);

        async Task<HttpStatusCode> Call(string provider, string? key = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/astra/native/{provider}/v1/files");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key ?? gw.Key}");
            return (await gw.Host.Client.SendAsync(request)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.NotFound, await Call(other.Id));
        Assert.Equal(HttpStatusCode.NotFound, await Call("nope"));
        Assert.Equal(HttpStatusCode.Unauthorized, await Call(gw.Provider.Id, "sk-not-an-astra-key"));
        Assert.Equal(HttpStatusCode.OK, await Call(gw.Provider.Id));

        gw.Provider.AuthScheme = AuthSchemes.OAuthSubscription;
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        Assert.Equal(HttpStatusCode.Forbidden, await Call(gw.Provider.Id));
        Assert.Single(gw.Upstream.Requests);
    }

    [Fact]
    public async Task A_token_with_a_cost_budget_is_refused_the_native_route()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => Json("{}"));
        var token = (await gw.Host.Db.Tokens.GetAsync(TokenIds.Default))!;
        token.LimitsJson = """{"budgets":[{"metric":"cost","period":"day","limit":5000000000}]}""";
        await gw.Host.Db.Tokens.UpdateAsync(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/astra/native/{gw.Provider.Id}/v1/files");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");

        var response = await gw.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(gw.Upstream.Requests);

    }

    // ---------------------------------------------------------------- Ollama

    private const string ChatCompletion =
        """{"id":"c1","object":"chat.completion","created":1,"model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"Hi there"},"finish_reason":"stop"}],"usage":{"prompt_tokens":11,"completion_tokens":3,"total_tokens":14}}""";

    private static async Task<GatewayFixture> OllamaFixtureAsync(Func<FakeUpstream.Seen, HttpResponseMessage> respond)
    {
        var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, respond);
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel
        {
            ProviderId = gw.Provider.Id, ModelId = "gpt-5", Enabled = true,
            Overrides = new ModelOverrides { ContextWindow = 128000, Capabilities = new ModelCapabilities { Vision = true, Reasoning = true } },
        });
        return gw;
    }

    private static async Task<HttpResponseMessage> OllamaAsync(GatewayFixture gw, HttpMethod method, string path, string? body = null, bool key = true)
    {
        using var request = new HttpRequestMessage(method, "/ollama" + path);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (key) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        return await gw.Host.Client.SendAsync(request);
    }

    [Fact]
    public async Task The_ollama_face_lists_models_with_their_capabilities_and_window()
    {
        await using var gw = await OllamaFixtureAsync(_ => Json(ChatCompletion));

        var version = JsonNode.Parse(await (await OllamaAsync(gw, HttpMethod.Get, "/api/version")).Content.ReadAsStringAsync())!;
        Assert.Equal("0.12.6", version["version"]!.GetValue<string>());

        var tags = JsonNode.Parse(await (await OllamaAsync(gw, HttpMethod.Get, "/api/tags")).Content.ReadAsStringAsync())!["models"]!.AsArray();
        var model = Assert.Single(tags);
        Assert.Equal("gpt-5", model!["name"]!.GetValue<string>());
        Assert.Equal(["completion", "tools", "vision", "thinking"], model["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()));

        var show = JsonNode.Parse(await (await OllamaAsync(gw, HttpMethod.Post, "/api/show", """{"model":"gpt-5:latest"}""")).Content.ReadAsStringAsync())!;
        Assert.Equal(128000, show["model_info"]!["astra.context_length"]!.GetValue<int>());
        var missing = await OllamaAsync(gw, HttpMethod.Post, "/api/show", """{"model":"nope"}""");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("not found", await missing.Content.ReadAsStringAsync());

        // A caller without a token gets nothing — the Ollama face is a gateway path, not an open one.
        Assert.Equal(HttpStatusCode.Unauthorized, (await OllamaAsync(gw, HttpMethod.Get, "/api/tags", key: false)).StatusCode);
    }

    [Fact]
    public async Task Ollama_chat_is_the_chat_completions_request_it_stands_for_answered_as_one_json_object()
    {
        await using var gw = await OllamaFixtureAsync(_ => Json(ChatCompletion));
        var body = """{"model":"gpt-5","stream":false,"messages":[{"role":"system","content":"be brief"},{"role":"user","content":"hi","images":["iVBORw0KGgo="]}],"options":{"temperature":0.2,"num_predict":50,"num_ctx":4096},"think":"low"}""";

        var response = await OllamaAsync(gw, HttpMethod.Post, "/api/chat", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reply = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("Hi there", reply["message"]!["content"]!.GetValue<string>());
        Assert.True(reply["done"]!.GetValue<bool>());
        Assert.Equal("stop", reply["done_reason"]!.GetValue<string>());
        Assert.Equal(11, reply["prompt_eval_count"]!.GetValue<int>());
        Assert.Equal(3, reply["eval_count"]!.GetValue<int>());

        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal("https://upstream.test/v1/chat/completions", seen.Url.ToString());
        var sent = seen.Json;
        Assert.False(sent["stream"]!.GetValue<bool>());
        Assert.Equal(0.2, sent["temperature"]!.GetValue<double>());
        Assert.Equal(50, sent["max_tokens"]!.GetValue<int>());
        Assert.Null(sent["num_ctx"]);
        Assert.Equal("low", sent["reasoning_effort"]!.GetValue<string>());
        Assert.StartsWith("data:image/png;base64,", sent["messages"]![1]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>());
        var record = await gw.RecordOfAsync(response);
        Assert.Equal("openai-chat", record.InboundProtocol);
        Assert.Equal(11, record.TotalInputTokens);
    }

    [Fact]
    public async Task Ollama_chat_streams_ndjson_lines_ending_in_done_with_the_token_counts_and_tool_calls()
    {
        var sse =
            "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hel\"}}]}\n\n"
            + "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"lo\"}}]}\n\n"
            + "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_9\",\"function\":{\"name\":\"get_time\",\"arguments\":\"{\\\"tz\\\":\"}}]}}]}\n\n"
            + "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\"UTC\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n"
            + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":4}}\n\n"
            + "data: [DONE]\n\n";
        await using var gw = await OllamaFixtureAsync(_ => FakeUpstream.Sse(sse));

        var response = await OllamaAsync(gw, HttpMethod.Post, "/api/chat", """{"model":"gpt-5","messages":[{"role":"user","content":"time?"}]}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        var lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal("Hel", lines[0]["message"]!["content"]!.GetValue<string>());
        Assert.Equal("lo", lines[1]["message"]!["content"]!.GetValue<string>());
        var calls = lines[2]["message"]!["tool_calls"]!.AsArray();
        Assert.Equal("get_time", calls[0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("UTC", calls[0]!["function"]!["arguments"]!["tz"]!.GetValue<string>());
        var done = lines[^1];
        Assert.True(done["done"]!.GetValue<bool>());
        Assert.Equal(9, done["prompt_eval_count"]!.GetValue<int>());
        Assert.Equal(4, done["eval_count"]!.GetValue<int>());
        Assert.True(Assert.Single(gw.Upstream.Requests).Json["stream_options"]!["include_usage"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Ollama_generate_and_errors_use_ollamas_words()
    {
        await using var gw = await OllamaFixtureAsync(request => request.Json["max_tokens"] is null && request.Body.Contains("boom")
            ? FakeUpstream.Json("""{"error":{"message":"upstream exploded","type":"api_error"}}""", HttpStatusCode.BadGateway)
            : Json(ChatCompletion));

        var generated = JsonNode.Parse(await (await OllamaAsync(gw, HttpMethod.Post, "/api/generate", """{"model":"gpt-5","prompt":"hi","system":"s","stream":false}""")).Content.ReadAsStringAsync())!;
        Assert.Equal("Hi there", generated["response"]!.GetValue<string>());
        var sent = gw.Upstream.Requests[0].Json["messages"]!.AsArray();
        Assert.Equal("system", sent[0]!["role"]!.GetValue<string>());

        var failed = await OllamaAsync(gw, HttpMethod.Post, "/api/chat", """{"model":"gpt-5","stream":false,"messages":[{"role":"user","content":"boom"}]}""");
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Contains("upstream exploded", JsonNode.Parse(await failed.Content.ReadAsStringAsync())!["error"]!.GetValue<string>());

        // An empty request is a model load: what a client sends to warm one up.
        var load = JsonNode.Parse(await (await OllamaAsync(gw, HttpMethod.Post, "/api/chat", """{"model":"gpt-5","stream":false}""")).Content.ReadAsStringAsync())!;
        Assert.Equal("load", load["done_reason"]!.GetValue<string>());
        Assert.Equal(2, gw.Upstream.Requests.Count);
    }

    [Fact]
    public async Task The_admin_api_keeps_its_protections_beside_the_ollama_prefix()
    {
        await using var gw = await OllamaFixtureAsync(_ => Json(ChatCompletion));
        // /api is still the admin API: a mutating call without X-Astra-Admin is refused, and the Ollama routes are not there.
        using var plain = gw.Host.App.GetTestClient();
        using var post = new HttpRequestMessage(HttpMethod.Post, "/api/chat") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.SendAsync(post)).StatusCode);
        using var tags = new HttpRequestMessage(HttpMethod.Get, "/api/tags");
        var response = await plain.SendAsync(tags);
        Assert.NotEqual("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("\"models\"", await response.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------- Responses over a WebSocket

    private static string ResponsesStream(string id, string text) =>
        $"event: response.created\ndata: {{\"type\":\"response.created\",\"response\":{{\"id\":\"{id}\",\"object\":\"response\",\"status\":\"in_progress\"}}}}\n\n"
        + $"event: response.output_item.done\ndata: {{\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{{\"type\":\"output_text\",\"text\":\"{text}\"}}]}}}}\n\n"
        + $"event: response.completed\ndata: {{\"type\":\"response.completed\",\"response\":{{\"id\":\"{id}\",\"object\":\"response\",\"status\":\"completed\",\"output\":[],\"usage\":{{\"input_tokens\":4,\"output_tokens\":2,\"total_tokens\":6}}}}}}\n\n";

    private static async Task<WebSocket> ConnectAsync(GatewayFixture gw, string? key = null, Action<HttpRequestMessage>? configure = null)
    {
        var client = gw.Host.App.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = r =>
        {
            if (key != "") r.Headers["Authorization"] = $"Bearer {key ?? gw.Key}";
        };
        return await client.ConnectAsync(new Uri("ws://localhost/v1/responses"), CancellationToken.None);
    }

    private static async Task SendAsync(WebSocket socket, string json) =>
        await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Frames up to and including the one whose type is a turn's end (completed / incomplete / failed / error).</summary>
    private static async Task<List<JsonNode>> TurnAsync(WebSocket socket)
    {
        var frames = new List<JsonNode>();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return frames;
                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
            var frame = JsonNode.Parse(message.ToArray())!;
            frames.Add(frame);
            if (frame["type"]!.GetValue<string>() is "response.completed" or "response.incomplete" or "response.failed" or "error") return frames;
        }
    }

    [Fact]
    public async Task The_responses_websocket_serves_each_turn_through_the_http_pipeline_and_continues_a_conversation_by_previous_response_id()
    {
        var turns = 0;
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses,
            _ => FakeUpstream.Sse(ResponsesStream($"resp_{++turns}", turns == 1 ? "first" : "second")));
        using var socket = await ConnectAsync(gw);

        await SendAsync(socket, """{"type":"response.create","model":"gpt-5","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"one"}]}],"store":false}""");
        var first = await TurnAsync(socket);
        Assert.Equal(["response.created", "response.output_item.done", "response.completed"], first.Select(f => f["type"]!.GetValue<string>()));
        var upstream = gw.Upstream.Requests[0].Json;
        Assert.True(upstream["stream"]!.GetValue<bool>());
        Assert.Null(upstream["type"]);

        // The second turn sends only what is new, naming the reply it continues: the pipeline gets the conversation whole.
        await SendAsync(socket, """{"type":"response.create","model":"gpt-5","previous_response_id":"resp_1","input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"two"}]}]}""");
        var second = await TurnAsync(socket);
        Assert.Equal("response.completed", second[^1]["type"]!.GetValue<string>());
        var whole = gw.Upstream.Requests[1].Json["input"]!.AsArray();
        Assert.Equal(3, whole.Count);
        Assert.Equal("one", whole[0]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("first", whole[1]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("two", whole[2]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Null(gw.Upstream.Requests[1].Json["previous_response_id"]);

        // A reply the socket does not hold: Codex opens a new socket and sends the conversation whole.
        await SendAsync(socket, """{"type":"response.create","model":"gpt-5","previous_response_id":"resp_nope","input":"x"}""");
        var unknown = Assert.Single(await TurnAsync(socket));
        Assert.Equal("error", unknown["type"]!.GetValue<string>());
        Assert.Equal("previous_response_not_found", unknown["error"]!["code"]!.GetValue<string>());
        Assert.Equal(2, gw.Upstream.Requests.Count);

        // Every turn is a request-log row of the Responses protocol, billed like an HTTP turn.
        await gw.Host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var rows = await gw.Host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery());
        Assert.Equal(2, rows.Items.Count);
        Assert.All(rows.Items, r => Assert.Equal("openai-responses", r.InboundProtocol));
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
    }

    [Fact]
    public async Task A_websocket_turn_for_a_chat_only_provider_is_translated_and_a_warmup_is_answered_locally()
    {
        const string chat = """{"id":"c1","object":"chat.completion","created":1,"model":"gpt-5","choices":[{"index":0,"message":{"role":"assistant","content":"translated"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1,"total_tokens":4}}""";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIChat, _ => Json(chat));
        using var socket = await ConnectAsync(gw);

        await SendAsync(socket, """{"type":"response.create","model":"gpt-5","generate":false,"input":"warm"}""");
        var warm = await TurnAsync(socket);
        Assert.Equal(["response.created", "response.completed"], warm.Select(f => f["type"]!.GetValue<string>()));
        Assert.Empty(gw.Upstream.Requests);

        await SendAsync(socket, """{"type":"response.create","model":"gpt-5","input":"hi"}""");
        var turn = await TurnAsync(socket);
        Assert.Equal("response.completed", turn[^1]["type"]!.GetValue<string>());
        Assert.Contains(turn, f => f["type"]!.GetValue<string>() == "response.output_text.delta");
        Assert.Equal("https://upstream.test/v1/chat/completions", Assert.Single(gw.Upstream.Requests).Url.ToString());
    }

    [Fact]
    public async Task A_websocket_turn_refused_by_the_pipeline_is_an_error_frame_and_the_socket_stays_open()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses,
            _ => FakeUpstream.Json("""{"error":{"message":"bad model","type":"invalid_request_error"}}""", HttpStatusCode.BadRequest));
        using var socket = await ConnectAsync(gw);

        await SendAsync(socket, """{"type":"response.create","model":"nope","input":"x"}""");
        var error = Assert.Single(await TurnAsync(socket));

        Assert.Equal("error", error["type"]!.GetValue<string>());
        Assert.Equal(400, error["status"]!.GetValue<int>());
        Assert.Equal("bad model", error["error"]!["message"]!.GetValue<string>());
        await SendAsync(socket, """{"type":"unknown"}""");
        Assert.Equal("unsupported_message", Assert.Single(await TurnAsync(socket))["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_responses_websocket_needs_a_token_and_refuses_other_sites_pages()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.Codex, ApiProtocol.OpenAIResponses, _ => Json("{}"));

        // Without a token nothing upgrades.
        var anonymous = gw.Host.App.GetTestServer().CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.ConnectAsync(new Uri("ws://localhost/v1/responses"), CancellationToken.None));

        // A browser page from another site (it sends Origin) cannot open one even on this machine.
        var foreign = gw.Host.App.GetTestServer().CreateWebSocketClient();
        foreign.ConfigureRequest = r =>
        {
            r.Headers["Authorization"] = $"Bearer {gw.Key}";
            r.Headers["Origin"] = "https://evil.example";
        };
        await Assert.ThrowsAnyAsync<Exception>(() => foreign.ConnectAsync(new Uri("ws://localhost/v1/responses"), CancellationToken.None));

        // A plain GET says what the route is for.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/responses");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {gw.Key}");
        Assert.Equal(HttpStatusCode.UpgradeRequired, (await gw.Host.Client.SendAsync(request)).StatusCode);
    }
}
