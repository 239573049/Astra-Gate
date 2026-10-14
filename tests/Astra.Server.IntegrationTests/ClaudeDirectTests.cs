using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data.Repositories;
using Astra.Gateway.Pipeline;
using Astra.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

public sealed class ClaudeDirectTests
{
    private const string Api = "/api/clients/claude-code/direct";
    private const string AccessToken = "at-secret-access-token";
    private const string RefreshToken = "rt-secret-refresh-token";

    [Fact]
    public async Task Only_usable_claude_subscription_accounts_can_be_selected_and_no_token_is_ever_listed()
    {
        await using var host = await LocalHost();
        var provider = await AddClaudeProviderAsync(host);
        await AddAccountAsync(host, provider, "acc-a", "Personal");
        await AddAccountAsync(host, provider, "acc-expired", "Expired", status: AccountStatus.Expired);
        await AddAccountAsync(host, provider, "acc-off", "Disabled", enabled: false);
        var other = await AddProviderAsync(host, "Codex", templateId: "openai-subscription");
        await AddAccountAsync(host, other, "acc-other", "Other provider");

        var state = await host.GetJsonAsync(Api);
        Assert.Equal("gateway", state["mode"]!.GetValue<string>());
        Assert.Equal(["acc-a", "acc-expired", "acc-off"], state["accounts"]!.AsArray().Select(a => a!["id"]!.GetValue<string>()).Order());
        Assert.DoesNotContain(AccessToken, state.ToJsonString());
        Assert.DoesNotContain(RefreshToken, state.ToJsonString());

        // Another provider's account, an expired account and a disabled account cannot be used.
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", accountId = "acc-other" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", accountId = "missing" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", accountId = "acc-expired" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", accountId = "acc-off" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.SendAsync(HttpMethod.Put, Api, new { mode = "bogus" })).Status);

        var (status, selected) = await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", accountId = "acc-a" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("direct", selected!["mode"]!.GetValue<string>());
        Assert.Equal("acc-a", selected["accountId"]!.GetValue<string>());
        // Selecting an account never edits the user's Claude configuration.
        Assert.False(Directory.Exists(host.ClientHome));
        Assert.Empty(await host.Db.Clients.ListAsync());
    }

    [Fact]
    public async Task Direct_mode_refuses_to_coexist_with_the_gateway_takeover()
    {
        await using var host = await LocalHost();
        await AddAccountAsync(host, await AddClaudeProviderAsync(host), "acc-a", "Personal");
        await host.Db.Clients.UpsertAsync(new ClientRecord { Kind = ClientKinds.ClaudeCode, Enabled = true });

        var (status, body) = await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", accountId = "acc-a" });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Contains("gateway takeover", body!["error"]!.GetValue<string>());
        Assert.Equal("gateway", (await host.GetJsonAsync(Api))["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task Prepare_hands_over_only_the_access_token_in_the_environment_and_stores_nothing()
    {
        await using var host = await LocalHost();
        await AddAccountAsync(host, await AddClaudeProviderAsync(host), "acc-a", "Personal");

        // Gateway mode: nothing to launch.
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Post, $"{Api}/prepare", new { accountId = "acc-a" })).Status);
        await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", accountId = "acc-a" });

        using var raw = await host.Client.PostAsJsonAsync($"{Api}/prepare", new { }, TestHost.JsonOptions);
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Contains("no-store", raw.Headers.CacheControl!.ToString());
        var launch = JsonNode.Parse(await raw.Content.ReadAsStringAsync())!;
        var env = launch["env"]!.AsObject();
        Assert.Equal("acc-a", launch["accountId"]!.GetValue<string>());
        Assert.Equal("Personal", launch["accountName"]!.GetValue<string>());
        Assert.Equal(AccessToken, env["CLAUDE_CODE_OAUTH_TOKEN"]!.GetValue<string>());
        Assert.DoesNotContain(RefreshToken, launch.ToJsonString());
        // Whatever else could outrank the subscription token is neutralised, and statistics are off by default.
        Assert.Equal("", env["ANTHROPIC_API_KEY"]!.GetValue<string>());
        Assert.Equal("", env["ANTHROPIC_AUTH_TOKEN"]!.GetValue<string>());
        Assert.Equal("https://api.anthropic.com", env["ANTHROPIC_BASE_URL"]!.GetValue<string>());
        Assert.Equal("none", env["OTEL_LOGS_EXPORTER"]!.GetValue<string>());
        Assert.Equal("0", env["OTEL_LOG_RAW_API_BODIES"]!.GetValue<string>());
        Assert.False(env.ContainsKey("OTEL_EXPORTER_OTLP_LOGS_HEADERS"));

        // The token only lives in the response: not in the settings blob, the state API, or on disk.
        Assert.DoesNotContain(AccessToken, Json.Serialize(await host.Db.Settings.GetAsync<JsonObject>("claude_direct")));
        Assert.DoesNotContain(AccessToken, (await host.GetJsonAsync(Api)).ToJsonString());
        Assert.False(Directory.Exists(Path.Combine(host.Root, "claude-profiles")));
        Assert.False(Directory.Exists(host.ClientHome));

        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Post, $"{Api}/prepare", new { accountId = "../../other" })).Status);
        // An account that stops being usable stops being launchable.
        var account = (await host.Db.Accounts.GetAsync("acc-a"))!;
        await host.Db.Accounts.SetStatusAsync(account.Id, AccountStatus.Revoked);
        Assert.Equal(HttpStatusCode.Conflict, (await host.SendAsync(HttpMethod.Post, $"{Api}/prepare", new { accountId = "acc-a" })).Status);
    }

    [Fact]
    public async Task Telemetry_is_persisted_once_whitelisted_and_revoked_independently_per_account()
    {
        await using var host = await LocalHost();
        var provider = await AddClaudeProviderAsync(host);
        await AddAccountAsync(host, provider, "acc-a", "First");
        await AddAccountAsync(host, provider, "acc-b", "Second");
        var (status, _) = await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", accountId = "acc-a", telemetryEnabled = true });
        Assert.Equal(HttpStatusCode.OK, status);
        var env = (await Prepare(host, "acc-a"))["env"]!;
        Assert.Equal("otlp", env["OTEL_LOGS_EXPORTER"]!.GetValue<string>());
        Assert.EndsWith("/telemetry/claude/acc-a/v1/logs", env["OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"]!.GetValue<string>());
        var authorization = env["OTEL_EXPORTER_OTLP_LOGS_HEADERS"]!.GetValue<string>()["Authorization=".Length..];
        var secret = authorization["Bearer ".Length..];

        var payload = Payload();
        using (var response = await SendTelemetry(host, "acc-a", authorization, payload)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var response = await SendTelemetry(host, "acc-a", authorization, payload)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = (await host.GetJsonAsync($"{Api}/accounts/acc-a/requests")).AsArray();
        var row = Assert.Single(rows)!; // replayed batch deduplicated
        Assert.Equal(100, row["inputTokens"]!.GetValue<int>());
        Assert.Equal(40, row["cacheReadTokens"]!.GetValue<int>());
        Assert.Equal(123000, row["estimatedCostNanoUsd"]!.GetValue<long>());
        Assert.Equal("real-account", row["accountUuid"]!.GetValue<string>());
        Assert.DoesNotContain("secret-prompt", rows.ToJsonString());
        Assert.Empty((await host.GetJsonAsync($"{Api}/accounts/acc-b/requests")).AsArray());
        Assert.Equal(0, (await host.Db.Requests.QueryAsync(new RequestQuery())).Total);
        Assert.DoesNotContain(secret, (await host.GetJsonAsync(Api)).ToJsonString());
        Assert.DoesNotContain(secret, Json.Serialize(await host.Db.Settings.GetAsync<JsonObject>("claude_direct")));

        // acc-a's credential does not open acc-b's collector, and switching accounts keeps it valid for acc-a.
        using (var response = await SendTelemetry(host, "acc-b", authorization, payload)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", accountId = "acc-b" });
        using (var response = await SendTelemetry(host, "acc-a", authorization, payload)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Revoking consent rejects the old exporter at once, and re-granting needs a freshly prepared launch.
        await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", accountId = "acc-a", telemetryEnabled = false });
        using (var response = await SendTelemetry(host, "acc-a", authorization, payload)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", accountId = "acc-a", telemetryEnabled = true });
        await Prepare(host, "acc-a");
        using (var response = await SendTelemetry(host, "acc-a", authorization, payload)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Control_and_collector_enforce_local_access_headers_credentials_and_size()
    {
        await using var host = await LocalHost();
        await AddAccountAsync(host, await AddClaudeProviderAsync(host), "acc-a", "Local");
        using (var client = host.App.GetTestClient())
        using (var response = await client.PutAsJsonAsync(Api, new { mode = "gateway" }))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await SendTelemetry(host, "acc-a", "Bearer invalid", Payload())) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        await host.SendAsync(HttpMethod.Put, Api, new { mode = "direct", accountId = "acc-a", telemetryEnabled = true });
        var authorization = (await Prepare(host, "acc-a"))["env"]!["OTEL_EXPORTER_OTLP_LOGS_HEADERS"]!.GetValue<string>()["Authorization=".Length..];
        using (var response = await SendTelemetry(host, "acc-a", authorization, new JsonObject { ["padding"] = new string('a', 1_048_577) }))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using (var response = await SendTelemetry(host, "acc-a", authorization, new JsonObject { ["resourceLogs"] = 12 }))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // A server reachable from elsewhere never hands out tokens or accepts statistics.
        await using var remote = await TestHost.StartAsync(b => b.Services.AddSingleton<IStartupFilter>(new AddressFilter(IPAddress.Parse("192.0.2.10"))));
        using (var response = await remote.Client.GetAsync(Api)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await remote.Client.PostAsJsonAsync($"{Api}/prepare", new { }, TestHost.JsonOptions)) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await SendTelemetry(remote, "acc-a", authorization, Payload())) Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
        Assert.Single(ClaudeDirectTelemetry.Parse("acc", payload));
    }

    [Fact]
    public void Parser_ignores_content_events_and_rejects_bad_numeric_usage()
    {
        var payload = Payload();
        var logs = payload["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]!.AsArray();
        var original = logs[0]!.DeepClone();
        logs.Add(new JsonObject { ["body"] = new JsonObject { ["stringValue"] = "claude_code.user_prompt" } });
        Assert.Single(ClaudeDirectTelemetry.Parse("acc", payload));
        logs[0]!["attributes"]!.AsArray().Add(Attribute("input_tokens", "-1"));
        Assert.Empty(ClaudeDirectTelemetry.Parse("acc", payload));
        logs[0] = original;
        logs[0]!["attributes"]!.AsArray().Add(Attribute("cost_usd_micros", "NaN"));
        Assert.Null(Assert.Single(ClaudeDirectTelemetry.Parse("acc", payload)).EstimatedCostNanoUsd);
    }

    [Fact]
    public void Parser_clamps_a_clock_that_runs_ahead_instead_of_dropping_the_event()
    {
        var payload = Payload();
        var attributes = payload["resourceLogs"]![0]!["scopeLogs"]![0]!["logRecords"]![0]!["attributes"]!.AsArray();
        attributes.Add(Attribute("event.timestamp", DateTimeOffset.UtcNow.AddHours(2).ToString("O")));
        var row = Assert.Single(ClaudeDirectTelemetry.Parse("acc", payload));
        Assert.True(row.OccurredAtUtc <= DateTimeOffset.UtcNow);
    }

    internal static Task<TestHost> LocalHost(Action<WebApplicationBuilder>? configure = null) => TestHost.StartAsync(b =>
    {
        b.Services.AddSingleton<IStartupFilter>(new AddressFilter(IPAddress.Loopback));
        configure?.Invoke(b);
    });

    internal static Task<Provider> AddClaudeProviderAsync(TestHost host) =>
        AddProviderAsync(host, "Claude 订阅", SubscriptionSupport.ClaudeSubscriptionKey);

    internal static async Task<Provider> AddProviderAsync(TestHost host, string name, string templateId)
    {
        var provider = new Provider
        {
            Id = Ulid.NewUlid(),
            Name = name,
            AuthScheme = AuthSchemes.OAuthSubscription,
            TemplateId = templateId,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.Anthropic, BaseUrl = "https://example.invalid" }],
            PreferredUpstreamProtocols = [ApiProtocol.Anthropic],
        };
        await host.Db.Providers.InsertAsync(provider);
        return provider;
    }

    internal static async Task AddAccountAsync(
        TestHost host, Provider provider, string id, string name, string status = AccountStatus.Active, bool enabled = true)
    {
        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        await host.Db.Accounts.InsertAsync(new ProviderAccount
        {
            Id = id,
            ProviderId = provider.Id,
            DisplayName = name,
            AccountEmail = name.ToLowerInvariant() + "@example.com",
            Plan = "claude_max",
            AccessTokenEnc = protector.Protect(AccessToken),
            RefreshTokenEnc = protector.Protect(RefreshToken),
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(4),
            Status = status,
            Enabled = enabled,
        });
    }

    private static async Task<JsonNode> Prepare(TestHost host, string accountId)
    {
        var (status, result) = await host.SendAsync(HttpMethod.Post, $"{Api}/prepare", new { accountId });
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
