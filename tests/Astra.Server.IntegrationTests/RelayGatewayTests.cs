using System.Net;
using System.Text.Json.Nodes;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>`astra-server relay`: routes, per-request credentials and the shared pipeline's protocol handling.</summary>
public sealed class RelayGatewayTests
{
    [Theory]
    [InlineData("/healthz", 200)]
    [InlineData("/readyz", 200)]
    [InlineData("/", 404)]
    [InlineData("/api/health", 404)]
    [InlineData("/v1/dashboard/billing/subscription", 404)]
    [InlineData("/v1/dashboard/billing/usage", 404)]
    [InlineData("/v1/models", 401)]
    public async Task Only_explicit_routes_are_exposed(string path, int status)
    {
        await using var relay = await RelayHost.StartAsync();
        using var response = await relay.SendAsync(path, body: null, token: null);
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Empty(relay.Upstream.Requests);
        Assert.Null(relay.App.Services.GetService<AstraDatabase>());
        Assert.Null(relay.App.Services.GetService<SettingsService>());
        Assert.Null(relay.App.Services.GetService<SubscriptionTokenService>());
    }

    [Fact]
    public async Task Request_credentials_remain_isolated_with_concurrent_callers()
    {
        await using var relay = await RelayHost.StartAsync();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            using var response = await relay.SendAsync(token: $"gho_test_{i}",
                body: $$"""{"model":"gpt-5","messages":[{"role":"user","content":"caller-{{i}}"}]}""");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }));
        var posts = relay.Upstream.Requests.Where(request => request.Body.Length > 0).ToArray();
        Assert.Equal(20, posts.Length);
        foreach (var post in posts)
        {
            var caller = JsonNode.Parse(post.Text)!["messages"]![0]!["content"]!.GetValue<string>();
            Assert.Equal($"Bearer gho_test_{caller["caller-".Length..]}", post.Authorization);
            // The template's own headers and the per-request ones its client sends, as on the server.
            Assert.Equal("copilot-developer-cli", post.Headers["Copilot-Integration-Id"]);
            Assert.True(post.Headers.ContainsKey("X-Request-Id"));
        }
        Assert.All(relay.Upstream.Requests, request =>
            Assert.Contains(request.Uri.AbsolutePath, new[] { "/models", "/chat/completions" }));
    }

    [Theory]
    [InlineData(null, "api.githubcopilot.com")]
    [InlineData("default", "api.githubcopilot.com")]
    [InlineData("business", "api.business.githubcopilot.com")]
    [InlineData("enterprise", "api.enterprise.githubcopilot.com")]
    public async Task A_variant_named_by_the_request_selects_its_hosts_without_exchanging_the_credential(string? variant, string host)
    {
        await using var relay = await RelayHost.StartAsync();
        using var response = await relay.SendAsync(headers: variant is null ? null : new() { ["x-astra-variant"] = [variant] });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, relay.Upstream.Requests.Count);
        Assert.All(relay.Upstream.Requests, request =>
        {
            Assert.Equal(host, request.Uri.Host);
            Assert.Equal("Bearer gho_test_alice", request.Authorization);
        });
    }

    [Fact]
    public async Task The_startup_variant_is_the_default_and_a_request_can_go_back_to_the_template()
    {
        await using var relay = await RelayHost.StartAsync(configure: o => o.Variant = "business");
        using (var business = await relay.SendAsync()) Assert.Equal(HttpStatusCode.OK, business.StatusCode);
        Assert.All(relay.Upstream.Requests, request => Assert.Equal("api.business.githubcopilot.com", request.Uri.Host));
        using var own = await relay.SendAsync(headers: new() { ["x-astra-variant"] = ["default"] });
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("api.githubcopilot.com", relay.Upstream.Requests.Last().Uri.Host);
    }

    [Theory]
    [InlineData("personal")]
    [InlineData("Business")]
    public async Task An_unknown_variant_is_refused_before_anything_is_sent(string variant)
    {
        await using var relay = await RelayHost.StartAsync();
        using var response = await relay.SendAsync(headers: new() { ["x-astra-variant"] = [variant] });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("business", await response.Content.ReadAsStringAsync());
        Assert.Empty(relay.Upstream.Requests);
    }

    [Theory]
    [InlineData("/chat/completions", """{"model":"gpt-5","messages":[{"role":"user","content":"Hi"}]}""", "/chat/completions", "choices")]
    [InlineData("/v1/responses", """{"model":"gpt-5","input":"Hi"}""", "/responses", "output")]
    [InlineData("/messages", """{"model":"claude-sonnet-4.5","max_tokens":32,"messages":[{"role":"user","content":"Hi"}]}""", "/v1/messages", "content")]
    [InlineData("/v1beta/models/chat-only:generateContent", """{"contents":[{"role":"user","parts":[{"text":"Hi"}]}]}""", "/chat/completions", "candidates")]
    [InlineData("/v1/messages", """{"model":"chat-only","max_tokens":32,"messages":[{"role":"user","content":"Hi"}]}""", "/chat/completions", "content")]
    public async Task The_pipeline_passes_through_or_converts_by_the_models_endpoints(
        string path, string body, string upstreamPath, string responseField)
    {
        await using var relay = await RelayHost.StartAsync();
        using var response = await relay.SendAsync(path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(JsonNode.Parse(await response.Content.ReadAsStringAsync())![responseField]);
        var post = Assert.Single(relay.Upstream.Requests, request => request.Body.Length > 0);
        Assert.Equal(upstreamPath, post.Uri.AbsolutePath);
    }

    [Theory]
    [InlineData("/v1/models", null, "/models")]
    [InlineData("/models", null, "/models")]
    [InlineData("/embeddings", """{"model":"embedding-model","input":["Hi"]}""", "/embeddings")]
    [InlineData("/v1/embeddings", """{"model":"embedding-model","input":["Hi"]}""", "/embeddings")]
    [InlineData("/messages/count_tokens", """{"model":"claude-sonnet-4.5","messages":[]}""", "/v1/messages/count_tokens")]
    [InlineData("/v1/messages/count_tokens", """{"model":"claude-sonnet-4.5","messages":[]}""", "/v1/messages/count_tokens")]
    public async Task Auxiliary_routes_are_relayed_without_a_model_list_lookup(string path, string? body, string upstreamPath)
    {
        await using var relay = await RelayHost.StartAsync();
        using var response = await relay.SendAsync(path + "?sample=1", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(relay.Upstream.Requests);
        Assert.Equal(upstreamPath, sent.Uri.AbsolutePath);
        Assert.Equal("?sample=1", sent.Uri.Query);
        Assert.Equal(body ?? "", sent.Text);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task The_model_list_is_the_upstreams_own_answer_and_carries_the_list_headers()
    {
        const string listing = """{"data":[{"id":"gpt-5","policy":{"state":"enabled"},"model_picker_enabled":true,"vendor":"OpenAI"}],"object":"list"}""";
        var upstream = new RelayUpstream { Respond = (_, _) => Task.FromResult(RelayUpstream.Json(listing)) };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var response = await relay.SendAsync("/models", body: null, headers: new() { ["OpenAI-Intent"] = ["conversation-agent"] });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(listing, await response.Content.ReadAsStringAsync());
        var sent = Assert.Single(upstream.Requests);
        Assert.Equal("model-access", sent.Headers["OpenAI-Intent"]);
        Assert.Equal("Bearer gho_test_alice", sent.Authorization);
    }

    [Fact]
    public async Task A_rejected_credential_reaches_the_caller_as_the_upstream_said_it()
    {
        const string denied = """{"error":{"message":"unauthorized: token authentication failed"}}""";
        var upstream = new RelayUpstream { Respond = (_, _) => Task.FromResult(RelayUpstream.Json(denied, HttpStatusCode.Unauthorized)) };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var response = await relay.SendAsync("/v1/models", body: null, token: "not-a-real-token");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(denied, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("X-Initiator", "agent")]
    [InlineData("Copilot-Vision-Request", "true")]
    public async Task Client_headers_the_template_names_are_passed_through(string name, string value)
    {
        await using var relay = await RelayHost.StartAsync();
        using var response = await relay.SendAsync(headers: new() { [name] = [value] });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(relay.Upstream.Requests, request => Assert.Equal(value, request.Headers[name]));
        using var other = await relay.SendAsync(headers: new() { ["X-Unlisted"] = ["1"] });
        Assert.DoesNotContain(relay.Upstream.Requests, request => request.Headers.ContainsKey("X-Unlisted"));
    }

    [Fact]
    public async Task Unknown_model_is_forwarded_and_native_response_model_is_not_overwritten()
    {
        var upstream = new RelayUpstream
        {
            Respond = (request, _) => Task.FromResult(request.Uri.AbsolutePath == "/models"
                ? RelayUpstream.Json(RelayUpstream.Catalog)
                : RelayUpstream.Json("""{"id":"chat_1","model":"served-model","choices":[{"index":0,"message":{"role":"assistant","content":"Hi"},"finish_reason":"stop"}]}""")),
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var response = await relay.SendAsync(body: """{"model":"custom-model","messages":[]}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("custom-model", JsonNode.Parse(upstream.Requests.Last().Text)!["model"]!.GetValue<string>());
        Assert.Equal("served-model", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["model"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task Rejected_inference_is_not_refreshed_or_retried(int status)
    {
        var upstream = new RelayUpstream
        {
            Respond = (request, _) =>
            {
                var response = request.Uri.AbsolutePath == "/models" ? RelayUpstream.Json(RelayUpstream.Catalog)
                    : RelayUpstream.Json("""{"error":{"message":"rejected"}}""", (HttpStatusCode)status);
                response.Headers.TryAddWithoutValidation("Retry-After", "17");
                return Task.FromResult(response);
            },
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var response = await relay.SendAsync();
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("17", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal(2, upstream.Requests.Count);
    }

    [Fact]
    public async Task Responses_stream_keeps_the_pipelines_item_id_stabilization()
    {
        var fixture = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "responses", "stream-copilot-rotating-ids.sse"));
        var upstream = new RelayUpstream
        {
            Respond = (request, _) => Task.FromResult(request.Uri.AbsolutePath == "/models"
                ? RelayUpstream.Json(RelayUpstream.Catalog) : RelayUpstream.Sse(fixture)),
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var response = await relay.SendAsync("/v1/responses", """{"model":"gpt-5.6-sol","input":"Hi","stream":true}""");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = (await response.Content.ReadAsStringAsync()).Split('\n')
            .Where(line => line.StartsWith("data: {", StringComparison.Ordinal)).Select(line => JsonNode.Parse(line[6..])!).ToArray();
        var expected = new[] { "rs_enc_a1", "msg_enc_b1", "fc_enc_c1" };
        foreach (var item in events)
        {
            if (item["output_index"] is { } index)
            {
                if (item["item_id"] is { } id) Assert.Equal(expected[index.GetValue<int>()], id.GetValue<string>());
                if (item["item"] is { } value) Assert.Equal(expected[index.GetValue<int>()], value["id"]!.GetValue<string>());
            }
        }
        var completed = Assert.Single(events, item => item["type"]?.GetValue<string>() == "response.completed");
        // Per-item events are pinned; the response-level terminal snapshot is passed on as sent.
        Assert.Equal("resp_enc_2", completed["response"]!["id"]!.GetValue<string>());
        Assert.Equal(new[] { "rs_enc_a5", "msg_enc_b7", "fc_enc_c5" },
            completed["response"]!["output"]!.AsArray().Select(item => item!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Chat_stream_filters_only_the_usage_chunk_the_client_did_not_request()
    {
        const string events = """
            : heartbeat

            data: {"id":"chat_1","model":"gpt-5","choices":[{"index":0,"delta":{"content":"hello"},"finish_reason":null}]}

            data: {"choices":[],"usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}

            data: [DONE]


            """;
        var upstream = new RelayUpstream
        {
            Respond = (request, _) => Task.FromResult(request.Uri.AbsolutePath == "/models"
                ? RelayUpstream.Json(RelayUpstream.Catalog) : RelayUpstream.Sse(events)),
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        foreach (var includeUsage in new[] { false, true })
        {
            using var response = await relay.SendAsync(body: $$$"""
                {"model":"gpt-5","messages":[],"stream":true,"stream_options":{"include_usage":{{{includeUsage.ToString().ToLowerInvariant()}}}}}
                """);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("hello", body);
            Assert.Contains("[DONE]", body);
            Assert.Equal(includeUsage, body.Contains("\"choices\":[]", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task A_heartbeat_before_the_first_event_reaches_the_client_at_once()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RelayUpstream
        {
            Respond = (request, ct) =>
            {
                if (request.Uri.AbsolutePath == "/models") return Task.FromResult(RelayUpstream.Json(RelayUpstream.Catalog));
                // A model still thinking: a comment line now, the events only later.
                var pipe = new System.IO.Pipelines.Pipe();
                _ = Task.Run(async () =>
                {
                    await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(": ready\n\n"));
                    await release.Task;
                    await pipe.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(
                        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello\"}}]}\n\ndata: [DONE]\n\n"));
                    await pipe.Writer.CompleteAsync();
                });
                var content = new StreamContent(pipe.Reader.AsStream());
                content.Headers.ContentType = new("text/event-stream");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            },
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"gpt-5","messages":[],"stream":true,"stream_options":{"include_usage":true}}""",
                System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new("Bearer", "gho_test_alice");
        try
        {
            using var response = await relay.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(5));
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[256];
            var read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(": ready", System.Text.Encoding.UTF8.GetString(buffer, 0, read).Trim());
            release.TrySetResult();
            using var rest = new StreamReader(stream);
            Assert.Contains("[DONE]", await rest.ReadToEndAsync());
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Smart_routing_fails_explicitly_without_instantiating_stateful_services()
    {
        await using var relay = await RelayHost.StartAsync();
        using var response = await relay.SendAsync(body: """{"model":"astra-auto","messages":[]}""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("智能路由", await response.Content.ReadAsStringAsync());
        Assert.Empty(relay.Upstream.Requests);
    }

    [Fact]
    public async Task An_api_key_template_gets_the_callers_key_where_its_scheme_puts_it()
    {
        var upstream = new RelayUpstream
        {
            Respond = (request, _) => Task.FromResult(request.Uri.AbsolutePath == "/v1/models"
                ? RelayUpstream.Json("""{"data":[{"id":"claude-sonnet-4-5","type":"model"}],"has_more":false}""")
                : RelayUpstream.Default(request)),
        };
        await using var relay = await RelayHost.StartAsync(upstream, template: "anthropic");
        using var response = await relay.SendAsync("/v1/chat/completions",
            """{"model":"claude-sonnet-4-5","max_tokens":16,"messages":[{"role":"user","content":"Hi"}]}""", token: "sk-ant-api-test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.All(upstream.Requests, request =>
        {
            Assert.Equal("api.anthropic.com", request.Uri.Host);
            Assert.Equal("sk-ant-api-test", request.Headers["x-api-key"]);
            Assert.Null(request.Authorization);
        });
        Assert.Equal("/v1/messages", upstream.Requests.Last().Uri.AbsolutePath);
    }

    [Fact]
    public async Task A_subscription_without_a_model_list_keeps_its_subscription_handling()
    {
        await using var relay = await RelayHost.StartAsync(template: "claude-subscription");
        using var models = await relay.SendAsync("/v1/models", body: null, token: "sk-ant-oat01-test");
        Assert.Equal(HttpStatusCode.NotFound, models.StatusCode);
        Assert.Empty(relay.Upstream.Requests);
        // Not Claude Code: the subscription's client policy refuses before anything is sent, as on the server.
        using var refused = await relay.SendAsync("/v1/messages",
            """{"model":"claude-sonnet-4-5","max_tokens":16,"messages":[{"role":"user","content":"Hi"}]}""", token: "sk-ant-oat01-test");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Empty(relay.Upstream.Requests);
    }

    [Fact]
    public async Task A_regional_variant_of_an_api_key_template_sends_to_its_hosts()
    {
        await using var relay = await RelayHost.StartAsync(template: "moonshot", configure: o => o.Variant = "intl");
        using var response = await relay.SendAsync(token: "sk-moonshot-test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(relay.Upstream.Requests, request => Assert.Equal("api.moonshot.ai", request.Uri.Host));
        Assert.Equal(["/v1/models", "/v1/chat/completions"], relay.Upstream.Requests.Select(request => request.Uri.AbsolutePath));
    }
}
