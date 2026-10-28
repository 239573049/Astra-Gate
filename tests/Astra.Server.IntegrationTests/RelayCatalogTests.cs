using System.Net;
using System.Text;

namespace Astra.Server.IntegrationTests;

/// <summary>The relay's model list: fetched with each caller's credential, cached by its hash, never trusted when malformed.</summary>
public sealed class RelayCatalogTests
{
    [Fact]
    public async Task Concurrent_first_requests_with_one_credential_share_one_model_list_fetch()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RelayUpstream
        {
            Respond = async (request, _) =>
            {
                if (request.Uri.AbsolutePath == "/models") await release.Task;
                return RelayUpstream.Default(request);
            },
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        var calls = Enumerable.Range(0, 10).Select(_ => relay.SendAsync()).ToArray();
        await WaitForAsync(() => upstream.Requests.Any(request => request.Uri.AbsolutePath == "/models"));
        await Task.Delay(300); // the other requests reach the fetch that is already running
        release.SetResult();
        foreach (var response in await Task.WhenAll(calls))
            using (response)
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(upstream.Requests, request => request.Uri.AbsolutePath == "/models");
        Assert.Equal(10, upstream.Requests.Count(request => request.Uri.AbsolutePath == "/chat/completions"));
    }

    [Fact]
    public async Task A_caller_leaving_does_not_fail_others_waiting_for_the_same_list()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new RelayUpstream
        {
            Respond = async (request, _) =>
            {
                if (request.Uri.AbsolutePath == "/models") await release.Task;
                return RelayUpstream.Default(request);
            },
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var leaving = new CancellationTokenSource();
        using var first = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = new StringContent(RelayHost.ChatBody, Encoding.UTF8, "application/json"),
        };
        first.Headers.Authorization = new("Bearer", "gho_test_alice");
        var started = relay.Client.SendAsync(first, leaving.Token);
        await WaitForAsync(() => upstream.Requests.Any(request => request.Uri.AbsolutePath == "/models"));
        var waiting = relay.SendAsync();
        await Task.Delay(300);
        leaving.Cancel(); // the request whose arrival started the fetch goes away
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => started);
        release.SetResult();
        using var response = await waiting;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(upstream.Requests, request => request.Uri.AbsolutePath == "/models");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition());
    }

    [Fact]
    public async Task The_model_list_is_cached_per_credential_and_variant()
    {
        await using var relay = await RelayHost.StartAsync();
        foreach (var (token, variant) in new[] { ("alice", "default"), ("alice", "default"), ("bob", "default"), ("alice", "enterprise") })
        {
            using var response = await relay.SendAsync(token: token, headers: new() { ["x-astra-variant"] = [variant] });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        var lists = relay.Upstream.Requests.Where(request => request.Uri.AbsolutePath == "/models").ToArray();
        Assert.Equal(3, lists.Length);
        Assert.Equal(2, lists.Count(request => request.Authorization == "Bearer alice"));
        Assert.Single(lists, request => request.Authorization == "Bearer bob");
        // The listing's own headers, and none of the per-request ids generation requests carry.
        Assert.All(lists, request =>
        {
            Assert.Equal("model-access", request.Headers["OpenAI-Intent"]);
            Assert.False(request.Headers.ContainsKey("X-Request-Id"));
        });
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":[{"id":""}]}""")]
    [InlineData("""{"data":[{"id":"gpt-5"},{"id":"gpt-5"}]}""")]
    [InlineData("""{"data":[{"id":"gpt-5","capabilities":[]}]}""")]
    [InlineData("""{"data":[{"id":"gpt-5","capabilities":{"limits":[]}}]}""")]
    public async Task Invalid_model_list_never_becomes_a_successful_empty_one(string body)
    {
        var upstream = new RelayUpstream { Respond = (_, _) => Task.FromResult(RelayUpstream.Json(body)) };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var response = await relay.SendAsync();
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Single(upstream.Requests);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    public async Task A_failed_model_list_is_not_cached_or_bypassed(int status)
    {
        var upstream = new RelayUpstream
        {
            Respond = (_, _) =>
            {
                var response = RelayUpstream.Json("""{"error":"rejected"}""", (HttpStatusCode)status);
                response.Headers.TryAddWithoutValidation("Retry-After", "19");
                return Task.FromResult(response);
            },
        };
        await using var relay = await RelayHost.StartAsync(upstream);
        for (var i = 0; i < 2; i++)
        {
            using var response = await relay.SendAsync();
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal("19", Assert.Single(response.Headers.GetValues("Retry-After")));
        }
        Assert.Equal(2, upstream.Requests.Count);
        Assert.All(upstream.Requests, request => Assert.Equal("/models", request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task The_model_list_is_read_up_to_a_bound()
    {
        var upstream = new RelayUpstream { Respond = (_, _) => Task.FromResult(RelayUpstream.Json(new string(' ', 4 * 1024 * 1024 + 1))) };
        await using var relay = await RelayHost.StartAsync(upstream);
        using var response = await relay.SendAsync();
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("4 MiB", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_gemini_style_list_is_read_by_name()
    {
        var upstream = new RelayUpstream
        {
            Respond = (request, _) => Task.FromResult(request.Uri.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                ? RelayUpstream.Json("""{"models":[{"name":"models/gemini-test","supportedGenerationMethods":["generateContent"]}]}""")
                : RelayUpstream.Json("""{"candidates":[{"content":{"role":"model","parts":[{"text":"hello"}]},"finishReason":"STOP"}]}""")),
        };
        await using var relay = await RelayHost.StartAsync(upstream, template: "google");
        using var response = await relay.SendAsync("/v1beta/models/gemini-test:generateContent",
            """{"contents":[{"role":"user","parts":[{"text":"Hi"}]}]}""", token: null,
            headers: new() { ["x-goog-api-key"] = ["AIza-test"] });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(upstream.Requests, request => Assert.Equal("AIza-test", request.Headers["x-goog-api-key"]));
        Assert.Equal("/v1beta/models/gemini-test:generateContent", upstream.Requests.Last().Uri.AbsolutePath);
    }
}
