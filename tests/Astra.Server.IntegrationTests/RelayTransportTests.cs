using System.Diagnostics;
using System.Net;
using Astra.Core;
using Astra.Core.Models;
using Astra.Server.Relay;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Astra.Server.IntegrationTests;

/// <summary>Where a relay request's credential may go, and how the relay treats a slow or failing upstream.</summary>
public sealed class RelayTransportTests
{
    [Fact]
    public async Task A_request_body_must_keep_a_minimum_data_rate()
    {
        // A request holds a concurrency slot while its body arrives: a sender that stalls is cut off within seconds.
        await using var relay = await RelayHost.StartAsync();
        var limits = relay.App.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits;
        Assert.Equal(240, limits.MinRequestBodyDataRate!.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(5), limits.MinRequestBodyDataRate.GracePeriod);
        Assert.Null(limits.MinResponseDataRate);
    }

    [Theory]
    [InlineData("https://up.test/models", "?sample=1", "key", "https://up.test/models?sample=1")]
    [InlineData("https://up.test/models?page=2", "?sample=1", "key", "https://up.test/models?page=2&sample=1")]
    [InlineData("https://up.test/models", "", "key", "https://up.test/models")]
    [InlineData("https://up.test/models", "?key=theirs&alt=sse", "key", "https://up.test/models?alt=sse")]
    [InlineData("https://up.test/models", "?KEY=theirs", "key", "https://up.test/models")]
    [InlineData("https://up.test/models", "?key=theirs", null, "https://up.test/models?key=theirs")]
    public void The_callers_query_is_merged_without_the_credential_parameter(string url, string query, string? credential, string expected)
    {
        Assert.Equal(expected, RelayHttp.WithClientQuery(url, new QueryCollection(QueryHelpers.ParseQuery(query)), credential));
    }

    [Fact]
    public void A_query_key_credential_goes_upstream_once_whatever_the_caller_sent()
    {
        var provider = new Provider
        {
            Id = "p",
            Name = "p",
            AuthScheme = AuthSchemes.QueryKey,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.Gemini, BaseUrl = "https://up.test/v1beta" }],
        };
        var caller = new RelayCaller("secret", RelayTarget.DefaultVariant, provider, null, null, new HeaderDictionary());
        var query = new QueryCollection(QueryHelpers.ParseQuery("?key=theirs&alt=sse"));
        using var request = caller.Request(HttpMethod.Post,
            RelayHttp.WithClientQuery("https://up.test/v1beta/models/m:countTokens", query, caller.Auth.QueryName), perRequest: false);
        Assert.Equal("?alt=sse&key=secret", request.RequestUri!.Query);
        Assert.True(caller.Carries(request));
    }

    [Fact]
    public async Task The_transport_refuses_to_send_a_credential_to_another_origin()
    {
        await using var relay = await RelayHost.StartAsync();
        var target = relay.App.Services.GetRequiredService<RelayTarget>();
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer gho_test_alice";
        var caller = target.Caller(context.Request);
        context.Features.Set(caller);
        var upstream = new RelayUpstream();
        using var transport = new RelayTransport(new RelayOptions(), new HttpContextAccessor { HttpContext = context }, upstream);
        using var client = new HttpClient(transport);
        using var message = caller.Request(HttpMethod.Get, "https://other.example.invalid/models", perRequest: false);
        var error = await Assert.ThrowsAsync<Astra.Gateway.Pipeline.GatewayException>(() => client.SendAsync(message));
        Assert.Equal(502, error.Status);
        Assert.Empty(upstream.Requests);
    }

    [Fact]
    public void The_network_handler_never_follows_redirects_shares_cookies_or_sends_trace_context()
    {
        using var handler = Assert.IsType<SocketsHttpHandler>(RelayTransport.NetworkHandler());
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        var propagated = new Dictionary<string, string>();
        using var activity = new Activity("test").Start();
        handler.ActivityHeadersPropagator!.Inject(activity, propagated, (_, name, value) => propagated.Add(name, value));
        Assert.Empty(propagated);
    }

    [Fact]
    public async Task Relayed_errors_keep_status_body_and_safe_diagnostics_but_no_hop_headers()
    {
        const string body = """{"error":{"message":"quota exceeded"}}""";
        var upstream = new RelayUpstream
        {
            Respond = (_, _) =>
            {
                var response = RelayUpstream.Json(body, HttpStatusCode.TooManyRequests);
                response.Headers.TryAddWithoutValidation("Retry-After", "25");
                response.Headers.TryAddWithoutValidation("x-request-id", "upstream-id");
                response.Headers.TryAddWithoutValidation("x-ratelimit-remaining", "0");
                response.Headers.TryAddWithoutValidation("Connection", "x-ratelimit-remaining, X-Remove");
                response.Content.Headers.TryAddWithoutValidation("X-Remove", "not-for-client");
                response.Headers.TryAddWithoutValidation("Set-Cookie", "session=not-for-client");
                return Task.FromResult(response);
            },
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var response = await relay.SendAsync("/v1/embeddings", """{"model":"embedding-model","input":"Hi"}""");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal("25", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal("upstream-id", Assert.Single(response.Headers.GetValues("x-request-id")));
        foreach (var header in new[] { "Connection", "x-ratelimit-remaining", "X-Remove", "Set-Cookie" })
            Assert.False(response.Headers.Contains(header) || response.Content.Headers.Contains(header));
    }

    [Fact]
    public async Task A_header_timeout_is_an_error_not_an_empty_success()
    {
        var upstream = new RelayUpstream
        {
            Respond = async (request, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return RelayUpstream.Default(request);
            },
        };
        await using var relay = await RelayHost.StartAsync(upstream, o => o.HeadersTimeoutSeconds = 1);
        using var response = await relay.SendAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    }

    [Fact]
    public async Task An_opaque_bearer_credential_is_sent_as_it_came()
    {
        const string token = "tid=test;exp=123;sig=abc";
        await using var relay = await RelayHost.StartAsync();
        using var response = await relay.SendAsync(token: token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(relay.Upstream.Requests, request => Assert.Equal("Bearer " + token, request.Authorization));
    }

    [Theory]
    [InlineData("headers", "/v1/chat/completions")]
    [InlineData("catalog", "/v1/chat/completions")]
    [InlineData("relay", "/v1/embeddings")]
    public async Task Internal_transport_cancellation_is_not_a_client_disconnect(string stage, string path)
    {
        var upstream = new RelayUpstream
        {
            Respond = (_, _) =>
            {
                if (stage == "headers")
                    return Task.FromException<HttpResponseMessage>(new TaskCanceledException("Synthetic connect timeout", new TimeoutException()));
                var content = new StreamContent(new CancelledStream());
                content.Headers.ContentType = new("application/json");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            },
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var response = await relay.SendAsync(path, """{"model":"gpt-5","messages":[],"input":"Hi"}""");
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Contains("error", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_saturated_relay_refuses_at_once_but_keeps_health_available()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RelayUpstream
        {
            Respond = async (request, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return RelayUpstream.Default(request);
            },
        };
        await using var relay = await RelayHost.StartAsync(upstream, o => o.MaxConcurrentRequests = 1);
        var first = relay.SendAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var second = await relay.SendAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
            Assert.Equal("1", Assert.Single(second.Headers.GetValues("Retry-After")));
            using var health = await relay.SendAsync("/healthz", body: null, token: null);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
        finally
        {
            release.TrySetResult();
        }
        using var completed = await first;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
    }

    private sealed class CancelledStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new TaskCanceledException("Synthetic stream timeout", new TimeoutException()));
    }
}
