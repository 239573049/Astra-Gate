using System.Net;
using Astra.Desktop.Core.Update;

namespace Astra.Desktop.Tests.Update;

internal sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : this(r => Task.FromResult(respond(r))) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return respond(request);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body) };
}

public class UpdateFeedTests
{
    [Theory]
    [InlineData(null, "stable")]
    [InlineData("", "stable")]
    [InlineData("stable", "stable")]
    [InlineData("preview", "preview")]
    [InlineData(" Preview ", "preview")]
    [InlineData("BETA", "preview")]
    [InlineData("nightly", "stable")]
    public void Channel_is_normalized(string? input, string expected) => Assert.Equal(expected, UpdateFeed.NormalizeChannel(input));

    [Fact]
    public void Manifest_url_trims_slashes_and_defaults_the_feed()
    {
        Assert.Equal("https://astra-gate.si/api/client-releases/stable/latest.json", UpdateFeed.ManifestUrl(null, null));
        Assert.Equal("https://f.example/x/preview/latest.json", UpdateFeed.ManifestUrl(" https://f.example/x// ", "beta"));
    }

    [Fact]
    public async Task Fetch_sends_accept_and_user_agent_and_parses_the_manifest()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""
            {"version":"0.12.0","apiVersion":"1.0","notes":"n","minUpdateable":"0.10.0","extra":1,
             "platforms":{"osx-arm64":{"server":"@aidotnet/server-osx-arm64","serverSha256":"ab","serverSize":123},"bad":1}}
            """));
        using var http = new HttpClient(h);

        var m = await UpdateFeed.FetchManifestAsync(http, "https://f.example/", "preview", "Astra/0.13.0");

        Assert.Equal("https://f.example/preview/latest.json", h.Requests[0].RequestUri!.ToString());
        Assert.Equal("Astra/0.13.0", h.Requests[0].Headers.UserAgent.ToString());
        Assert.Contains("application/json", h.Requests[0].Headers.Accept.ToString());
        Assert.Equal("0.12.0", m.Version);
        Assert.Equal("n", m.Notes);
        Assert.Equal("0.10.0", m.MinUpdateable);
        Assert.Equal(new UpdatePlatformInfo("@aidotnet/server-osx-arm64", null, "ab", 123), m.Platform("osx-arm64"));
        Assert.Null(m.Platform("bad"));
    }

    [Theory]
    [InlineData("{not json", "invalid JSON")]
    [InlineData("[]", "no version")]
    [InlineData("""{"version":"  "}""", "no version")]
    [InlineData("""{"version":3}""", "no version")]
    [InlineData("""{"notes":"x"}""", "no version")]
    public async Task Fetch_rejects_a_manifest_without_a_version(string body, string message)
    {
        using var http = new HttpClient(new FakeHandler(_ => FakeHandler.Json(body)));
        var e = await Assert.ThrowsAsync<UpdateFeedException>(() => UpdateFeed.FetchManifestAsync(http));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public async Task Fetch_reports_http_errors_and_network_failures_and_timeouts()
    {
        using var notFound = new HttpClient(new FakeHandler(_ => FakeHandler.Json("", HttpStatusCode.NotFound)));
        Assert.Contains("responded 404", (await Assert.ThrowsAsync<UpdateFeedException>(() => UpdateFeed.FetchManifestAsync(notFound))).Message);

        using var down = new HttpClient(new FakeHandler((Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new HttpRequestException("refused"))));
        Assert.Contains("Cannot reach the update feed", (await Assert.ThrowsAsync<UpdateFeedException>(() => UpdateFeed.FetchManifestAsync(down))).Message);

        using var hang = new HttpClient(new HangHandler());
        var e = await Assert.ThrowsAsync<UpdateFeedException>(() => UpdateFeed.FetchManifestAsync(hang, timeout: TimeSpan.FromMilliseconds(100)));
        Assert.Contains("timed out", e.Message);
    }

    private sealed class HangHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var http = new HttpClient(new HangHandler());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpdateFeed.FetchManifestAsync(http, ct: cts.Token));
    }

    [Fact]
    public async Task Check_prefers_the_running_servers_status()
    {
        var h = new FakeHandler(r => FakeHandler.Json("""{"available":"0.12.0","notes":"hi","channel":"beta"}"""));
        using var http = new HttpClient(h);

        var c = await ServerUpdateChecker.CheckAsync(http, "0.11.7", baseUrl: "http://127.0.0.1:17321/", channel: "stable");

        Assert.Equal("http://127.0.0.1:17321/api/update/status", h.Requests.Single().RequestUri!.ToString());
        Assert.Equal(new ServerUpdateCheck("0.12.0", "hi", "server", "preview", null), c);
    }

    [Fact]
    public async Task Check_falls_back_to_the_feed_and_compares_versions()
    {
        var h = new FakeHandler(r => r.RequestUri!.AbsolutePath.EndsWith("/api/update/status")
            ? FakeHandler.Json("", HttpStatusCode.InternalServerError)
            : FakeHandler.Json("""{"version":"0.12.0","notes":"n"}"""));
        using var http = new HttpClient(h);

        var newer = await ServerUpdateChecker.CheckAsync(http, "0.11.7", baseUrl: "http://127.0.0.1:1");
        Assert.Equal(("0.12.0", "feed", "stable"), (newer.AvailableVersion, newer.Source, newer.Channel));
        Assert.Equal("0.12.0", newer.Manifest!.Version);

        var same = await ServerUpdateChecker.CheckAsync(http, "0.12.0");
        Assert.Null(same.AvailableVersion);
        Assert.Equal("n", same.Notes);
    }

    [Fact]
    public async Task Check_falls_back_when_the_server_is_down()
    {
        var h = new FakeHandler(r => r.RequestUri!.Port == 1 ? throw new HttpRequestException("refused") : FakeHandler.Json("""{"version":"1.0.0"}"""));
        using var http = new HttpClient(h);
        var c = await ServerUpdateChecker.CheckAsync(http, "0.9.0", baseUrl: "http://127.0.0.1:1");
        Assert.Equal("feed", c.Source);
    }
}
