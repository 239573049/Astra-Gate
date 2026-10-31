using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Privacy;
using Astra.Core.Requests;
using Astra.Gateway.Pipeline;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Astra.Server.IntegrationTests;

/// <summary>An upstream that keeps the exact bytes it was sent (a form upload is binary) and answers with a scripted response.</summary>
public sealed class RawUpstream(Func<RawUpstream.Seen, HttpResponseMessage> respond) : HttpMessageHandler
{
    public sealed record Seen(Uri Url, Dictionary<string, string> Headers, long? ContentLength, byte[] Body);

    public List<Seen> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => string.Join(",", h.Value));
        // What a real transport would send as Content-Length (the content computes it).
        var length = request.Content?.Headers.ContentLength;
        var seen = new Seen(request.RequestUri!, headers, length, request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct));
        lock (Requests) Requests.Add(seen);
        return respond(seen);
    }
}

/// <summary>OpenAI Images API (issue #17): forwarded to an OpenAI-compatible provider with token auth, request log and billing.</summary>
public sealed class GatewayImagesTests
{
    // A recognisable stand-in for an image (~110 KB of base64): it must never reach the request log, the trace or the captures.
    private static readonly string B64 = string.Concat(Enumerable.Repeat("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJ", 2500));
    private const string Prompt = "A watercolor fox reading a newspaper";
    private const string Boundary = "----astra-images-test";

    private static string ImagesResponse(string b64 = "") =>
        $$$$"""{"created":1713833628,"data":[{"b64_json":"{{{{(b64.Length == 0 ? B64 : b64)}}}}"}],"background":"opaque","output_format":"png","quality":"high","size":"1024x1024","usage":{"total_tokens":4210,"input_tokens":50,"output_tokens":4160,"input_tokens_details":{"text_tokens":10,"image_tokens":40}}}""";

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>$5 text input, $10 image input, $40 image output per 1M tokens.</summary>
    private static Task PriceAsync(GatewayFixture gw, string model = "gpt-image-1") => gw.Host.Db.Providers.InsertModelAsync(new ProviderModel
    {
        ProviderId = gw.Provider.Id,
        ModelId = model,
        Enabled = true,
        Overrides = new ModelOverrides
        {
            Pricing = new PricingSchedule
            {
                Base = new PriceSet { [TokenTypes.Input] = 5m, [TokenTypes.InputImage] = 10m, [TokenTypes.Output] = 10m, [TokenTypes.OutputImage] = 40m },
            },
        },
    });

    // 10 × $5 + 40 × $10 + 4160 × $40, per 1M tokens.
    private const long ExpectedCostNanos = 50_000 + 400_000 + 166_400_000;

    private static async Task<GatewayFixture> StartWithRawUpstreamAsync(RawUpstream upstream, ApiProtocol protocol = ApiProtocol.OpenAIChat)
    {
        var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, protocol, _ => Json("{}"));
        // Before the first gateway request, so the cached upstream client uses this transport.
        gw.Host.App.Services.GetRequiredService<GatewayHttpClients>().HandlerFactory = _ => upstream;
        return gw;
    }

    private static async Task<(byte[] Bytes, string ContentType)> FormAsync(string model, byte[] image, string prompt = Prompt)
    {
        using var form = new MultipartFormDataContent(Boundary);
        // As the OpenAI docs and SDKs send it: the model first, the images, the prompt last.
        form.Add(new StringContent(model), "model");
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(file, "image[]", "fox.png");
        form.Add(new StringContent(prompt), "prompt");
        form.Add(new StringContent("1024x1024"), "size");
        return (await form.ReadAsByteArrayAsync(), form.Headers.ContentType!.ToString());
    }

    private static byte[] Png(int size)
    {
        var bytes = new byte[size];
        new Random(17).NextBytes(bytes);
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(GatewayFixture gw, byte[] bytes, string contentType, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/images/edits") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.TryAddWithoutValidation(HeaderNames.ContentType, contentType);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key ?? gw.Key}");
        return await gw.Host.Client.SendAsync(request);
    }

    private static void AssertNothingStored(RequestRecord record, IEnumerable<TraceSpan> spans, string detail)
    {
        Assert.Null(record.BodyRef);
        Assert.DoesNotContain("iVBORw0KGgo", record.UsageRawJson ?? "");
        Assert.DoesNotContain("iVBORw0KGgo", detail);
        Assert.DoesNotContain(Prompt, detail);
        foreach (var span in spans)
        {
            Assert.DoesNotContain("iVBORw0KGgo", (span.AttributesJson ?? "") + span.EventsJson);
            Assert.DoesNotContain(Prompt, (span.AttributesJson ?? "") + span.EventsJson);
        }
    }

    [Fact]
    public async Task Generations_are_forwarded_with_the_providers_key_returned_as_is_logged_and_billed()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ImagesResponse()));
        await PriceAsync(gw);
        // Debug capture on: an images call is still never captured.
        await gw.Host.App.Services.GetRequiredService<SettingsService>().UpdateAsync(s => s.DebugBodies = true);
        var body = $$"""{"model":"gpt-image-1","prompt":"{{Prompt}}","size":"1024x1024","quality":"high"}""";

        var response = await gw.PostAsync("/v1/images/generations", body);
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ImagesResponse(), text);
        var seen = Assert.Single(gw.Upstream.Requests);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("https://upstream.test/v1/images/generations", seen.Url.ToString());
        Assert.Equal("Bearer sk-upstream-secret", seen.Headers["authorization"]);
        Assert.Equal(body, seen.Body); // nothing to rewrite: the client's own bytes

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.Equal(GatewayPipeline.ImagesProtocol, record.InboundProtocol);
        Assert.Equal(GatewayPipeline.ImagesProtocol, record.UpstreamProtocol);
        Assert.True(record.Passthrough);
        Assert.False(record.Stream);
        Assert.Equal(200, record.HttpStatus);
        Assert.Equal(gw.Provider.Id, record.ProviderId);
        Assert.Equal("gpt-image-1", record.RequestedModel);
        Assert.Equal("gpt-image-1", record.UpstreamModel);
        Assert.Equal("reported", record.UsageSource);
        Assert.Equal(50, record.TotalInputTokens);
        Assert.Equal(4160, record.TotalOutputTokens);
        Assert.Equal(40, Assert.Single(record.UsageItems, i => i.TokenType == TokenTypes.InputImage).Tokens);
        Assert.Equal(4160, Assert.Single(record.UsageItems, i => i.TokenType == TokenTypes.OutputImage).Tokens);
        Assert.Equal(ExpectedCostNanos, record.CostNanoUsd);

        var spans = await gw.Host.Db.Traces.ListSpansAsync(record.Id);
        Assert.Equal("image_generation", JsonNode.Parse(spans[0].AttributesJson!)!["gen_ai.operation.name"]!.GetValue<string>());
        var attempt = Assert.Single(spans, s => s.Name == TraceSpanNames.Attempt);
        Assert.Equal(200, JsonNode.Parse(attempt.AttributesJson!)!["http.response.status_code"]!.GetValue<int>());
        var detail = await gw.Host.Client.GetStringAsync($"/api/requests/{record.Id}");
        AssertNothingStored(record, spans, detail);
    }

    [Fact]
    public async Task A_streamed_generation_is_passed_through_and_billed_from_its_completed_event()
    {
        var events =
            $"event: image_generation.partial_image\ndata: {{\"type\":\"image_generation.partial_image\",\"b64_json\":\"{B64[..4000]}\",\"partial_image_index\":0}}\n\n"
            + $"event: image_generation.completed\ndata: {{\"type\":\"image_generation.completed\",\"b64_json\":\"{B64}\",\"usage\":{{\"total_tokens\":4210,\"input_tokens\":50,\"output_tokens\":4160,\"input_tokens_details\":{{\"text_tokens\":10,\"image_tokens\":40}}}}}}\n\n";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Sse(events));
        await PriceAsync(gw);

        var response = await gw.PostAsync("/v1/images/generations", """{"model":"gpt-image-1","prompt":"a fox","stream":true,"partial_images":1}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(events, await response.Content.ReadAsStringAsync());
        Assert.Equal("text/event-stream", Assert.Single(gw.Upstream.Requests).Headers["accept"]);
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.True(record.Stream);
        Assert.Equal(4160, record.TotalOutputTokens);
        Assert.Equal(ExpectedCostNanos, record.CostNanoUsd);
    }

    [Theory]
    [InlineData("gpt-image-1")]
    [InlineData("house-image")] // the provider maps it to gpt-image-1: only that field's bytes change
    public async Task An_edit_upload_is_streamed_on_byte_for_byte(string model)
    {
        var upstream = new RawUpstream(_ => Json(ImagesResponse()));
        await using var gw = await StartWithRawUpstreamAsync(upstream);
        gw.Provider.Settings[Provider.ModelMapKey] = new JsonObject { ["house-image"] = "gpt-image-1" };
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        await PriceAsync(gw);
        var image = Png(300_000);
        var (bytes, contentType) = await FormAsync(model, image);
        var (expected, _) = await FormAsync("gpt-image-1", image);

        var response = await PostFormAsync(gw, bytes, contentType);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ImagesResponse(), await response.Content.ReadAsStringAsync());
        var seen = Assert.Single(upstream.Requests);
        Assert.Equal("https://upstream.test/v1/images/edits", seen.Url.ToString());
        Assert.Equal("Bearer sk-upstream-secret", seen.Headers["authorization"]);
        Assert.Equal(contentType, seen.Headers["content-type"]);
        Assert.Equal(expected.Length, seen.ContentLength);
        Assert.Equal(expected, seen.Body);

        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.Equal(model, record.RequestedModel);
        Assert.Equal("gpt-image-1", record.UpstreamModel);
        Assert.Equal(ExpectedCostNanos, record.CostNanoUsd);
        var spans = await gw.Host.Db.Traces.ListSpansAsync(record.Id);
        Assert.Equal("image_edit", JsonNode.Parse(spans[0].AttributesJson!)!["gen_ai.operation.name"]!.GetValue<string>());
        // Only the start of the upload (up to its model) was read before it went upstream.
        var body = JsonNode.Parse(Assert.Single(spans, s => s.Name == TraceSpanNames.Body).AttributesJson!)!;
        Assert.True(body["astra.body.streamed"]!.GetValue<bool>());
        Assert.True(body["astra.body.bytes"]!.GetValue<long>() < bytes.Length);
        AssertNothingStored(record, spans, await gw.Host.Client.GetStringAsync($"/api/requests/{record.Id}"));
    }

    [Fact]
    public async Task With_the_privacy_guard_on_an_edit_is_read_whole_and_its_prompt_redacted_but_its_images_untouched()
    {
        var upstream = new RawUpstream(_ => Json(ImagesResponse()));
        await using var gw = await StartWithRawUpstreamAsync(upstream);
        await gw.Host.App.Services.GetRequiredService<PrivacyGuardService>()
            .UpdateAsync(new PrivacySettings { Enabled = true, DryRun = false, DefaultAction = PrivacyActions.Redact });
        var image = Png(50_000);
        var (bytes, contentType) = await FormAsync("gpt-image-1", image, "a sign that reads AKIAIOSFODNN7EXAMPLE");

        var response = await PostFormAsync(gw, bytes, contentType);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var seen = Assert.Single(upstream.Requests);
        var fields = new Dictionary<string, byte[]>();
        var reader = new MultipartReader(Boundary, new MemoryStream(seen.Body));
        while (await reader.ReadNextSectionAsync() is { } section)
        {
            using var value = new MemoryStream();
            await section.Body.CopyToAsync(value);
            fields[ContentDispositionHeaderValue.Parse(section.ContentDisposition).Name.ToString().Trim('"')] = value.ToArray();
        }
        Assert.Equal(image, fields["image[]"]);
        Assert.Equal("gpt-image-1", Encoding.UTF8.GetString(fields["model"]));
        var prompt = Encoding.UTF8.GetString(fields["prompt"]);
        Assert.StartsWith("a sign that reads ", prompt);
        Assert.DoesNotContain("AKIAIOSFODNN7EXAMPLE", prompt);
        var record = await gw.RecordOfAsync(response);
        Assert.Contains("aws_key", record.PrivacyJson);
    }

    [Fact]
    public async Task A_blocked_prompt_never_leaves_the_machine()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ImagesResponse()));
        await gw.Host.App.Services.GetRequiredService<PrivacyGuardService>()
            .UpdateAsync(new PrivacySettings { Enabled = true, DryRun = false, DefaultAction = PrivacyActions.Block });

        var response = await gw.PostAsync("/v1/images/generations", """{"model":"gpt-image-1","prompt":"a sign that reads AKIAIOSFODNN7EXAMPLE"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(gw.Upstream.Requests);
        Assert.Equal(RequestStatus.Blocked, (await gw.RecordOfAsync(response)).Status);
    }

    [Fact]
    public async Task Gateway_refusals_are_openai_shaped_and_nothing_is_sent()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ImagesResponse()));

        var missing = await gw.PostAsync("/v1/images/generations", """{"prompt":"a fox"}""");
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var error = JsonNode.Parse(await missing.Content.ReadAsStringAsync())!["error"]!;
        Assert.Equal("invalid_request_error", error["type"]!.GetValue<string>());
        Assert.Contains("model", error["message"]!.GetValue<string>());
        Assert.Equal(RequestStatus.GatewayError, (await gw.RecordOfAsync(missing)).Status);

        var notJson = await gw.PostAsync("/v1/images/generations", "prompt=a fox");
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Equal("invalid_request_error", JsonNode.Parse(await notJson.Content.ReadAsStringAsync())!["error"]!["type"]!.GetValue<string>());

        var virtualModel = await gw.PostAsync("/v1/images/generations", """{"model":"astra-auto","prompt":"a fox"}""");
        Assert.Equal(HttpStatusCode.BadRequest, virtualModel.StatusCode);

        Assert.Empty(gw.Upstream.Requests);
    }

    [Fact]
    public async Task A_provider_without_an_openai_endpoint_cannot_serve_images()
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.ClaudeCode, ApiProtocol.Anthropic, _ => FakeUpstream.Json(ImagesResponse()));

        var response = await gw.PostAsync("/v1/images/generations", """{"model":"gpt-image-1","prompt":"a fox"}""");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var error = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!.AsObject();
        Assert.Equal("api_error", error["type"]!.GetValue<string>());
        Assert.Contains("OpenAI", error["message"]!.GetValue<string>());
        Assert.True(error.ContainsKey("param") && error.ContainsKey("code"));
        Assert.Empty(gw.Upstream.Requests);
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.GatewayError, record.Status);
        Assert.Equal("gpt-image-1", record.RequestedModel);
    }

    [Fact]
    public async Task An_unknown_model_gets_the_providers_own_openai_error()
    {
        const string notFound = """{"error":{"message":"The model 'gpt-image-9' does not exist.","type":"invalid_request_error","param":"model","code":"model_not_found"}}""";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat,
            _ => FakeUpstream.Json(notFound, HttpStatusCode.NotFound));

        var response = await gw.PostAsync("/v1/images/generations", """{"model":"gpt-image-9","prompt":"a fox"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(notFound, await response.Content.ReadAsStringAsync());
        var record = await gw.RecordOfAsync(response);
        Assert.Equal(RequestStatus.UpstreamError, record.Status);
        Assert.Equal("invalid_request_error", record.ErrorType);
        Assert.Equal(0, record.CostNanoUsd);
    }

    [Fact]
    public async Task The_relay_forwards_images_with_the_callers_own_credential()
    {
        var upstream = new RelayUpstream
        {
            Respond = (sent, _) => Task.FromResult(sent.Uri.AbsolutePath.StartsWith("/images/", StringComparison.Ordinal)
                ? RelayUpstream.Json(ImagesResponse())
                : RelayUpstream.Default(sent)),
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        const string body = """{"model":"gpt-image-1","prompt":"a fox"}""";

        using var generation = await relay.SendAsync("/v1/images/generations", body);

        Assert.Equal(HttpStatusCode.OK, generation.StatusCode);
        Assert.Equal(ImagesResponse(), await generation.Content.ReadAsStringAsync());
        var post = Assert.Single(relay.Upstream.Requests, r => r.Uri.AbsolutePath == "/images/generations");
        Assert.Equal("Bearer gho_test_alice", post.Authorization);
        Assert.Equal(body, post.Text);

        // A form upload is read whole within the relay's body deadline, then sent on byte for byte.
        var (bytes, contentType) = await FormAsync("gpt-image-1", Png(20_000));
        using var edit = await relay.SendAsync("/images/edits", body: null,
            content: new ByteArrayContent(bytes) { Headers = { { HeaderNames.ContentType, contentType } } });

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        var upload = Assert.Single(relay.Upstream.Requests, r => r.Uri.AbsolutePath == "/images/edits");
        Assert.Equal("Bearer gho_test_alice", upload.Authorization);
        Assert.Equal(contentType, upload.Headers["Content-Type"]);
        Assert.Equal(bytes, upload.Body);
    }

    [Theory]
    [InlineData("/v1/images/generations", null)]
    [InlineData("/v1/images/generations", "not-an-astra-token")]
    [InlineData("/v1/images/generations", "stale")]
    [InlineData("/v1/images/edits", null)]
    [InlineData("/v1/images/edits", "stale")]
    public async Task Missing_or_invalid_tokens_get_401_before_anything_is_read_or_sent(string path, string? key)
    {
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat, _ => FakeUpstream.Json(ImagesResponse()));
        // A well-formed token that is not (or no longer) the one Astra issued.
        if (key == "stale") key = gw.Token[..^4] + (gw.Token.EndsWith("aaaa", StringComparison.Ordinal) ? "bbbb" : "aaaa");
        var (bytes, contentType) = await FormAsync("gpt-image-1", Png(1_000));
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = path.EndsWith("edits", StringComparison.Ordinal)
                ? new ByteArrayContent(bytes) { Headers = { { HeaderNames.ContentType, contentType } } }
                : new StringContent("""{"model":"gpt-image-1","prompt":"a fox"}""", Encoding.UTF8, "application/json"),
        };
        if (key is not null) request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

        var response = await gw.Host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("authentication_error", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["type"]!.GetValue<string>());
        Assert.Empty(gw.Upstream.Requests);
    }
}
