using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Data.Repositories;
using Astra.Server.Api;
using Astra.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

public sealed class ClaudeDirectTests
{
    private const string Api = "/api/clients/claude-code/direct";

    [Fact]
    public async Task Profiles_are_isolated_opt_in_and_do_not_change_gateway_or_native_credentials()
    {
        await using var host = await LocalHost();
        var initial = await host.GetJsonAsync(Api);
        Assert.Equal("gateway", initial["mode"]!.GetValue<string>());
        Assert.Empty(initial["profiles"]!.AsArray());
        Assert.False(Directory.Exists(Path.Combine(host.Root, "claude-profiles")));
        Assert.False(Directory.Exists(host.ClientHome));

        var id = await Create(host, "Personal");
        var other = await Create(host, "Work");
        Assert.NotEqual(id, other);
        var profile = (await host.GetJsonAsync(Api))["profiles"]![0]!;
        Assert.False(profile["telemetryEnabled"]!.GetValue<bool>());
        var dir = profile["configDirectory"]!.GetValue<string>();
        var nativeFile = Path.Combine(dir, ".credentials.json");
        await File.WriteAllTextAsync(nativeFile, "native-owned-test-placeholder");
        await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"), "{\"theme\":\"dark\"}");
        var launch = await Prepare(host, id, "login");
        Assert.Equal(dir, launch["configDirectory"]!.GetValue<string>());
        Assert.DoesNotContain("Bearer", launch.ToJsonString());
        Assert.Equal("native-owned-test-placeholder", await File.ReadAllTextAsync(nativeFile));
        Assert.Equal("{\"theme\":\"dark\"}", await File.ReadAllTextAsync(Path.Combine(dir, "settings.json")));
        Assert.Empty(await host.Db.Clients.ListAsync());
        Assert.False(Directory.Exists(host.ClientHome));
        var overlay = JsonNode.Parse(await File.ReadAllTextAsync(launch["settingsFile"]!.GetValue<string>()))!;
        Assert.Equal("none", overlay["env"]!["OTEL_LOGS_EXPORTER"]!.GetValue<string>());
        Assert.Equal("0", overlay["env"]!["OTEL_LOG_RAW_API_BODIES"]!.GetValue<string>());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(launch["settingsFile"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Telemetry_is_persisted_once_whitelisted_and_revoked_independently_per_profile()
    {
        await using var host = await LocalHost();
        var id = await Create(host, "First");
        var other = await Create(host, "Second");
        var (status, _) = await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", profileId = id, telemetryEnabled = true });
        Assert.Equal(HttpStatusCode.OK, status);
        var launch = await Prepare(host, id, "run");
        var file = launch["settingsFile"]!.GetValue<string>();
        var overlay = JsonNode.Parse(await File.ReadAllTextAsync(file))!;
        var authorization = overlay["env"]!["OTEL_EXPORTER_OTLP_LOGS_HEADERS"]!.GetValue<string>()["Authorization=".Length..];
        var payload = Payload();
        using (var response = await SendTelemetry(host, id, authorization, payload)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var response = await SendTelemetry(host, id, authorization, payload)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await host.GetJsonAsync($"{Api}/profiles/{id}/requests")).AsArray();
        var row = Assert.Single(rows)!;
        Assert.Equal(100, row["inputTokens"]!.GetValue<int>());
        Assert.Equal(40, row["cacheReadTokens"]!.GetValue<int>());
        Assert.Equal(123000, row["estimatedCostNanoUsd"]!.GetValue<long>());
        Assert.Equal("real-account", row["accountUuid"]!.GetValue<string>());
        Assert.DoesNotContain("secret-prompt", rows.ToJsonString());
        Assert.Empty((await host.GetJsonAsync($"{Api}/profiles/{other}/requests")).AsArray());
        Assert.Equal(0, (await host.Db.Requests.QueryAsync(new RequestQuery())).Total);
        Assert.DoesNotContain(authorization, (await host.GetJsonAsync(Api)).ToJsonString());
        Assert.DoesNotContain(authorization["Bearer ".Length..], Json.Serialize(await host.Db.Settings.GetAsync<JsonObject>("claude_direct")));

        // Selection changes do not reassign buffered events from an older running session.
        await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", profileId = other });
        using (var response = await SendTelemetry(host, id, authorization, payload)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", profileId = id, telemetryEnabled = false });
        using (var response = await SendTelemetry(host, id, authorization, payload)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", profileId = id, telemetryEnabled = true });
        await Prepare(host, id, "run");
        using (var response = await SendTelemetry(host, id, authorization, payload)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Control_and_collector_enforce_local_access_headers_credentials_and_size()
    {
        await using var host = await LocalHost();
        using (var client = host.App.GetTestClient())
        using (var response = await client.PostAsJsonAsync($"{Api}/profiles", new { name = "No header" }))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var id = await Create(host, "Local");
        using (var response = await SendTelemetry(host, id, "Bearer invalid", Payload())) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var (status, _) = await host.SendAsync(HttpMethod.Post, $"{Api}/prepare", new { profileId = "../../other", action = "login" });
        Assert.Equal(HttpStatusCode.NotFound, status);
        (status, _) = await host.SendAsync(HttpMethod.Post, $"{Api}/prepare", new { profileId = id, action = "run" });
        Assert.Equal(HttpStatusCode.Conflict, status);
        (status, _) = await host.SendAsync(HttpMethod.Post, $"{Api}/prepare", new { profileId = id, action = "arbitrary-command" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", profileId = id, telemetryEnabled = true });
        var launch = await Prepare(host, id, "run");
        var overlay = JsonNode.Parse(await File.ReadAllTextAsync(launch["settingsFile"]!.GetValue<string>()))!;
        var authorization = overlay["env"]!["OTEL_EXPORTER_OTLP_LOGS_HEADERS"]!.GetValue<string>()["Authorization=".Length..];
        using (var response = await SendTelemetry(host, id, authorization, new JsonObject { ["padding"] = new string('a', 1_048_577) }))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using (var response = await SendTelemetry(host, id, authorization, new JsonObject { ["resourceLogs"] = 12 }))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var remote = await TestHost.StartAsync(b => b.Services.AddSingleton<IStartupFilter>(new AddressFilter(IPAddress.Parse("192.0.2.10"))));
        using (var response = await remote.Client.GetAsync(Api)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await SendTelemetry(remote, id, authorization, Payload())) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Prepare_refuses_symlink_profile_redirection()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var host = await LocalHost();
        var id = await Create(host, "Linked");
        var dir = Path.Combine(host.Root, "claude-profiles", id);
        var target = Path.Combine(host.Root, "untouched");
        Directory.CreateDirectory(target);
        Directory.Delete(dir);
        Directory.CreateSymbolicLink(dir, target);
        var (status, _) = await host.SendAsync(HttpMethod.Post, $"{Api}/prepare", new { profileId = id, action = "login" });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Empty(Directory.GetFiles(target));
    }

    [Theory]
    [InlineData("api_request", false)]
    [InlineData("claude_code.api_request", false)]
    [InlineData("api_request", true)]
    [InlineData("claude_code.api_request", true)]
    public void Parser_accepts_attribute_or_body_event_names(string name, bool bodyOnly)
    {
        var payload = Payload();
        var log = payload["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]![0]!;
        var attributes = log["attributes"]!.AsArray();
        attributes.RemoveAt(0);
        if (!bodyOnly) attributes.Add(Attribute("event.name", name));
        log["body"] = new JsonObject { ["stringValue"] = name };
        Assert.Single(ClaudeDirectTelemetry.Parse("profile", payload));
    }

    [Fact]
    public void Parser_ignores_content_events_and_rejects_bad_numeric_usage()
    {
        var payload = Payload();
        var logs = payload["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]!.AsArray();
        var original = logs[0]!.DeepClone();
        logs.Add(new JsonObject { ["body"] = new JsonObject { ["stringValue"] = "claude_code.user_prompt" } });
        Assert.Single(ClaudeDirectTelemetry.Parse("profile", payload));
        logs[0]!["attributes"]!.AsArray().Add(Attribute("input_tokens", "-1"));
        Assert.Empty(ClaudeDirectTelemetry.Parse("profile", payload));
        logs[0] = original;
        logs[0]!["attributes"]!.AsArray().Add(Attribute("cost_usd_micros", "NaN"));
        Assert.Null(Assert.Single(ClaudeDirectTelemetry.Parse("profile", payload)).EstimatedCostNanoUsd);
    }

    internal static Task<TestHost> LocalHost() => TestHost.StartAsync(b => b.Services.AddSingleton<IStartupFilter>(new AddressFilter(IPAddress.Loopback)));
    private static async Task<string> Create(TestHost host, string name)
    {
        var (status, state) = await host.SendAsync(HttpMethod.Post, $"{Api}/profiles", new { name });
        Assert.Equal(HttpStatusCode.OK, status);
        return state!["profiles"]!.AsArray().Last()!["id"]!.GetValue<string>();
    }
    private static async Task<JsonNode> Prepare(TestHost host, string id, string action)
    {
        var (status, result) = await host.SendAsync(HttpMethod.Post, $"{Api}/prepare", new { profileId = id, action });
        Assert.Equal(HttpStatusCode.OK, status);
        return result!;
    }
    private static async Task<HttpResponseMessage> SendTelemetry(TestHost host, string id, string authorization, JsonObject payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/telemetry/claude/{id}/v1/logs");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        request.Content = new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        return await host.Client.SendAsync(request);
    }
    private static JsonObject Attribute(string key, string value) => new() { ["key"] = key, ["value"] = new JsonObject { ["stringValue"] = value } };
    private static JsonObject Payload() => new()
    {
        ["resourceLogs"] = new JsonArray(new JsonObject
        {
            ["resource"] = new JsonObject { ["attributes"] = new JsonArray(Attribute("session.id", "session-one"), Attribute("user.account_uuid", "real-account")) },
            ["scopeLogs"] = new JsonArray(new JsonObject
            {
                ["logRecords"] = new JsonArray(new JsonObject
                {
                    ["body"] = new JsonObject { ["stringValue"] = "claude_code.api_request" },
                    ["attributes"] = new JsonArray(Attribute("event.name", "api_request"), Attribute("event.timestamp", DateTimeOffset.UtcNow.ToString("O")),
                        Attribute("request_id", "req-one"), Attribute("model", "claude-test"), Attribute("input_tokens", "100"),
                        Attribute("output_tokens", "20"), Attribute("cache_read_tokens", "40"), Attribute("cache_creation_tokens", "5"),
                        Attribute("cost_usd_micros", "123"), Attribute("duration_ms", "1250"), Attribute("prompt", "secret-prompt")),
                }),
            }),
        }),
    };

    private sealed class AddressFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, continuation) => { ctx.Connection.RemoteIpAddress = address; await continuation(ctx); });
            next(app);
        };
    }
}
