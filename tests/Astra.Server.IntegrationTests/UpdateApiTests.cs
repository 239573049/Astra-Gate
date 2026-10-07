using System.Net;
using System.Text.Json.Nodes;
using Astra.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Astra.Server.IntegrationTests;

public class UpdateApiTests
{
    /// <summary>
    /// A minimal feed app serving latest.json under /astra/&lt;channel&gt;/. Runs on TestServer
    /// (no port bound); the caller disposes the returned server.
    /// </summary>
    private static async Task<TestServer> FeedServerAsync(string version, string notes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapGet("/astra/{channel}/latest.json", (string channel) => Results.Json(
            new { version, apiVersion = "1.0", releasedAt = DateTimeOffset.UtcNow, notes, minUpdateable = "0.1.0" }));
        await app.StartAsync();
        return app.GetTestServer();
    }

    private static Action<WebApplicationBuilder> PointFeedAt(TestServer feed)
    {
        return b => b.Services.Configure<HttpClientFactoryOptions>(UpdateCheckService.HttpClientName,
            o => o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = feed.CreateHandler()));
    }

    [Fact]
    public async Task Manual_check_detects_newer_version_and_status_reflects_it()
    {
        using var feed = await FeedServerAsync("99.0.0", "big release");
        await using var host = await TestHost.StartAsync(PointFeedAt(feed));

        await host.SendAsync(HttpMethod.Patch, "/api/settings", new JsonObject { ["updateFeedUrl"] = "http://feed.test/astra" });

        var before = await host.GetJsonAsync("/api/update/status");
        Assert.Equal(ServerOptions.Version, before["current"]!.GetValue<string>());
        Assert.True(before["available"] is null);
        Assert.True(before["feedConfigured"]!.GetValue<bool>());

        var (_, check) = await host.SendAsync(HttpMethod.Post, "/api/update/check");
        Assert.Null((string?)check!["error"]);
        Assert.Equal("99.0.0", check["availableVersion"]!.GetValue<string>());
        Assert.Equal("big release", check["notes"]!.GetValue<string>());

        var status = await host.GetJsonAsync("/api/update/status");
        Assert.Equal("99.0.0", status["available"]!.GetValue<string>());
        Assert.Equal("stable", status["channel"]!.GetValue<string>());
        Assert.True(status["lastCheckAt"] is not null);
    }

    [Fact]
    public async Task Check_clears_availability_when_feed_is_not_newer()
    {
        using var feed = await FeedServerAsync("0.0.1", "older");
        await using var host = await TestHost.StartAsync(PointFeedAt(feed));

        await host.SendAsync(HttpMethod.Patch, "/api/settings", new JsonObject { ["updateFeedUrl"] = "http://feed.test/astra" });
        var (_, check) = await host.SendAsync(HttpMethod.Post, "/api/update/check");
        Assert.True(check!["availableVersion"] is null);
        Assert.True(check["notes"] is null);
        Assert.NotNull(check["lastCheckAt"]);
    }

    [Fact]
    public async Task Unreachable_feed_records_error_and_invalid_channel_is_rejected()
    {
        await using var host = await TestHost.StartAsync();

        await host.SendAsync(HttpMethod.Patch, "/api/settings",
            new JsonObject { ["updateFeedUrl"] = "http://127.0.0.1:9/astra", ["updateChannel"] = "beta" });
        var (_, check) = await host.SendAsync(HttpMethod.Post, "/api/update/check");
        Assert.NotNull(check!["error"]);

        var status = await host.GetJsonAsync("/api/update/status");
        Assert.Equal("beta", status["channel"]!.GetValue<string>());
        Assert.NotNull(status["error"]);

        var (badStatus, bad) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new JsonObject { ["updateChannel"] = "canary" });
        Assert.Equal(HttpStatusCode.BadRequest, badStatus);
        Assert.NotNull(bad!["error"]);
    }

    [Fact]
    public async Task Disabled_feed_reports_feedConfigured_false()
    {
        await using var host = await TestHost.StartAsync();

        await host.SendAsync(HttpMethod.Patch, "/api/settings", new JsonObject { ["updateFeedUrl"] = "" });
        var (_, check) = await host.SendAsync(HttpMethod.Post, "/api/update/check");
        Assert.Equal("feed-not-configured", check!["error"]!.GetValue<string>());

        var status = await host.GetJsonAsync("/api/update/status");
        Assert.False(status["feedConfigured"]!.GetValue<bool>());
    }

    [Fact]
    public void IsNewer_ignores_prerelease_and_build_suffixes()
    {
        Assert.True(UpdateCheckService.IsNewer("0.2.0", "0.1.0"));
        Assert.True(UpdateCheckService.IsNewer("1.0.0-rc.1+build", "0.9.9"));
        Assert.False(UpdateCheckService.IsNewer("0.1.0", "0.1.0"));
        Assert.False(UpdateCheckService.IsNewer("0.0.9", "0.1.0"));
        Assert.False(UpdateCheckService.IsNewer("not-a-version", "0.1.0"));
        Assert.False(UpdateCheckService.IsNewer(null, "0.1.0"));
    }
}
