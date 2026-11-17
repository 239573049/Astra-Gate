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
        Assert.Equal("99.0.0", check["available"]!.GetValue<string>());
        Assert.Equal("big release", check["notes"]!.GetValue<string>());
        // Same shape as /status — the web UI caches this response as the status.
        Assert.Equal(ServerOptions.Version, check["current"]!.GetValue<string>());
        Assert.True(check["feedConfigured"]!.GetValue<bool>());

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
        Assert.True(check!["available"] is null);
        Assert.True(check["notes"] is null);
        Assert.NotNull(check["lastCheckAt"]);
    }

    [Fact]
    public async Task Unreachable_feed_records_error_and_invalid_channel_is_rejected()
    {
        await using var host = await TestHost.StartAsync();

        // "beta" is the old name of the preview channel and is stored as preview.
        await host.SendAsync(HttpMethod.Patch, "/api/settings",
            new JsonObject { ["updateFeedUrl"] = "http://127.0.0.1:9/astra", ["updateChannel"] = "beta" });
        var (_, check) = await host.SendAsync(HttpMethod.Post, "/api/update/check");
        Assert.NotNull(check!["error"]);

        var status = await host.GetJsonAsync("/api/update/status");
        Assert.Equal("preview", status["channel"]!.GetValue<string>());
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
    public async Task Preview_channel_reads_the_preview_feed_and_defaults_to_stable()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapGet("/astra/{channel}/latest.json", (string channel) => channel switch
        {
            "stable" => Results.Json(new { version = "98.0.0", apiVersion = "1.0" }),
            "preview" => Results.Json(new { version = "99.0.0-preview.1", apiVersion = "1.0" }),
            _ => Results.NotFound(),
        });
        await app.StartAsync();
        using var feed = app.GetTestServer();
        await using var host = await TestHost.StartAsync(PointFeedAt(feed));

        await host.SendAsync(HttpMethod.Patch, "/api/settings", new JsonObject { ["updateFeedUrl"] = "http://feed.test/astra" });
        var (_, stable) = await host.SendAsync(HttpMethod.Post, "/api/update/check");
        Assert.Equal("stable", stable!["channel"]!.GetValue<string>());
        Assert.Equal("98.0.0", stable["available"]!.GetValue<string>());

        await host.SendAsync(HttpMethod.Patch, "/api/settings", new JsonObject { ["updateChannel"] = "preview" });
        var (_, preview) = await host.SendAsync(HttpMethod.Post, "/api/update/check");
        Assert.Equal("preview", preview!["channel"]!.GetValue<string>());
        Assert.Equal("99.0.0-preview.1", preview["available"]!.GetValue<string>());
        Assert.Equal("preview", (await host.GetJsonAsync("/api/settings"))["updateChannel"]!.GetValue<string>());
    }

    /// <summary>A fake container updater (Watchtower's <c>POST /v1/update</c>): records each call and answers <paramref name="status"/>.</summary>
    private static async Task<(TestServer Server, List<(string Query, string? Auth)> Calls)> UpdaterAsync(int status)
    {
        var calls = new List<(string, string?)>();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.MapPost("/v1/update", (HttpRequest req) =>
        {
            lock (calls) calls.Add((req.QueryString.Value ?? "", req.Headers.Authorization.ToString()));
            return Results.StatusCode(status);
        });
        await app.StartAsync();
        return (app.GetTestServer(), calls);
    }

    private static Action<WebApplicationBuilder> WithUpdater(TestServer? feed, TestServer updater) => b =>
    {
        if (feed is not null) PointFeedAt(feed)(b);
        var server = (ServerOptions)b.Services.Last(d => d.ServiceType == typeof(ServerOptions)).ImplementationInstance!;
        server.UpdaterUrl = "http://updater:8080";
        server.UpdaterToken = "updater-secret";
        b.Services.Configure<HttpClientFactoryOptions>(ContainerUpdater.HttpClientName,
            o => o.HttpMessageHandlerBuilderActions.Add(hb => hb.PrimaryHandler = updater.CreateHandler()));
    };

    [Fact]
    public async Task Apply_without_a_container_updater_is_refused()
    {
        await using var host = await TestHost.StartAsync();
        var status = await host.GetJsonAsync("/api/update/status");
        Assert.False(status["containerUpdater"]!.GetValue<bool>());
        Assert.True(status["autoApply"]!.GetValue<bool>());
        var (code, body) = await host.SendAsync(HttpMethod.Post, "/api/update/apply");
        Assert.Equal(HttpStatusCode.Conflict, code);
        Assert.Contains("ASTRA_UPDATER_URL", body!["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Apply_asks_the_container_updater_and_records_the_request()
    {
        using var feed = await FeedServerAsync("99.0.0", "big release");
        var (updater, calls) = await UpdaterAsync(202);
        using var _ = updater;
        await using var host = await TestHost.StartAsync(WithUpdater(feed, updater));
        await host.SendAsync(HttpMethod.Patch, "/api/settings", new JsonObject { ["updateFeedUrl"] = "http://feed.test/astra" });
        await host.SendAsync(HttpMethod.Post, "/api/update/check");

        var (code, applied) = await host.SendAsync(HttpMethod.Post, "/api/update/apply");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.True(applied!["containerUpdater"]!.GetValue<bool>());
        Assert.Equal("99.0.0", applied["applyVersion"]!.GetValue<string>());
        Assert.NotNull(applied["applyRequestedAt"]);
        Assert.True(applied["applyError"] is null);
        var call = Assert.Single(calls);
        Assert.Equal("?async=true", call.Query);
        Assert.Equal("Bearer updater-secret", call.Auth);
        // The token never reaches the console.
        Assert.DoesNotContain("updater-secret", (await host.GetJsonAsync("/api/update/status")).ToJsonString());

        // The setting that lets the worker do it on its own.
        await host.SendAsync(HttpMethod.Patch, "/api/settings", new JsonObject { ["updateAutoApply"] = false });
        Assert.False((await host.GetJsonAsync("/api/update/status"))["autoApply"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(401, "updater-unauthorized")]
    [InlineData(500, "updater-http-500")]
    [InlineData(429, null)]
    public async Task A_refusing_updater_is_reported_in_the_status(int answer, string? error)
    {
        var (updater, _) = await UpdaterAsync(answer);
        using var _ = updater;
        await using var host = await TestHost.StartAsync(WithUpdater(null, updater));
        var (code, applied) = await host.SendAsync(HttpMethod.Post, "/api/update/apply");
        Assert.Equal(HttpStatusCode.OK, code);
        Assert.Equal(error, (string?)applied!["applyError"]);
        Assert.Equal(error, (string?)(await host.GetJsonAsync("/api/update/status"))["applyError"]);
    }

    [Fact]
    public void The_worker_asks_again_for_a_version_only_after_an_hour()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.False(ContainerUpdater.Due(new UpdateCheckState(), now));
        Assert.True(ContainerUpdater.Due(new UpdateCheckState { AvailableVersion = "2.0.0" }, now));
        var asked = new UpdateCheckState { AvailableVersion = "2.0.0", ApplyVersion = "2.0.0", ApplyRequestedAt = now.AddMinutes(-10) };
        Assert.False(ContainerUpdater.Due(asked, now));
        Assert.True(ContainerUpdater.Due(asked, now.AddMinutes(51)));
        // A newer version than the one asked for is asked for at once.
        Assert.True(ContainerUpdater.Due(new UpdateCheckState { AvailableVersion = "2.0.1", ApplyVersion = "2.0.0", ApplyRequestedAt = now }, now));
    }

    [Fact]
    public void IsNewer_follows_semver_precedence()
    {
        Assert.True(UpdateCheckService.IsNewer("0.2.0", "0.1.0"));
        Assert.True(UpdateCheckService.IsNewer("0.10.0", "0.9.5"));
        Assert.True(UpdateCheckService.IsNewer("1.0.0-rc.1+build", "0.9.9"));
        Assert.False(UpdateCheckService.IsNewer("0.1.0", "0.1.0"));
        Assert.False(UpdateCheckService.IsNewer("0.1.0+build.7", "0.1.0"));
        Assert.False(UpdateCheckService.IsNewer("0.0.9", "0.1.0"));
        Assert.False(UpdateCheckService.IsNewer("not-a-version", "0.1.0"));
        Assert.False(UpdateCheckService.IsNewer(null, "0.1.0"));

        // A release beats its previews; previews order by their number, not as text.
        Assert.True(UpdateCheckService.IsNewer("0.10.0", "0.10.0-preview.3"));
        Assert.False(UpdateCheckService.IsNewer("0.10.0-preview.3", "0.10.0"));
        Assert.True(UpdateCheckService.IsNewer("0.10.0-preview.2", "0.10.0-preview.1"));
        Assert.True(UpdateCheckService.IsNewer("0.10.0-preview.10", "0.10.0-preview.9"));
        Assert.True(UpdateCheckService.IsNewer("0.10.0-preview.1", "0.9.5"));
        // Turning previews off never downgrades: the older stable is not "newer".
        Assert.False(UpdateCheckService.IsNewer("0.9.5", "0.10.0-preview.1"));
    }
}
