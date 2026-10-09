using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;
using Astra.Core.Requests;
using Microsoft.AspNetCore.TestHost;

namespace Astra.Server.IntegrationTests;

public class AdminApiTests
{
    private static readonly object ClaudeLikePricing = new Dictionary<string, object>
    {
        ["currency"] = "USD",
        ["unit"] = "per_1m_tokens",
        ["base"] = new Dictionary<string, object> { ["input"] = 3.0, ["output"] = 15.0, ["cache_write_5m"] = 3.75, ["cache_write_1h"] = 6.0 },
    };

    [Fact]
    public async Task Mutations_require_admin_header()
    {
        await using var host = await TestHost.StartAsync();
        using var raw = host.App.GetTestClient();
        var res = await raw.PostAsync("/api/billing/simulate", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Models_list_comes_from_seed_with_provider_price_keys()
    {
        await using var host = await TestHost.StartAsync();
        var models = (await host.GetJsonAsync("/api/models?search=deepseek-v4-flash")).AsArray();
        var flash = models.Single(m => m!["id"]!.GetValue<string>() == "deepseek-v4-flash")!;
        Assert.Equal("seed", flash["source"]!.GetValue<string>());
        var keys = flash["providerPriceKeys"]!.AsArray().Select(k => k!.GetValue<string>()).ToList();
        Assert.Contains("siliconflow", keys);
        Assert.Equal(0.15m, flash["pricing"]!["base"]!["input"]!.GetValue<decimal>());

        var detail = await host.GetJsonAsync("/api/models/deepseek-v4-flash");
        Assert.Contains(detail["providerPrices"]!.AsArray(), p => p!["priceKey"]!.GetValue<string>() == "siliconflow");
    }

    [Fact]
    public async Task Editing_a_seed_model_marks_fields_and_reset_restores_them()
    {
        await using var host = await TestHost.StartAsync();
        var m = await host.GetJsonAsync("/api/models/deepseek-v4-flash");
        var original = m["displayName"]!.GetValue<string>();
        var input = new JsonObject
        {
            ["displayName"] = "My Flash",
            ["vendor"] = m["vendor"]!.GetValue<string>(),
            ["family"] = m["family"]?.DeepClone(),
            ["aliases"] = m["aliases"]!.DeepClone(),
            ["contextWindow"] = m["contextWindow"]?.DeepClone(),
            ["maxOutputTokens"] = m["maxOutputTokens"]?.DeepClone(),
            ["pricing"] = m["pricing"]!.DeepClone(),
        };
        var (status, body) = await host.SendAsync(HttpMethod.Put, "/api/models/deepseek-v4-flash", input);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("My Flash", body!["displayName"]!.GetValue<string>());
        Assert.Equal(["displayName"], body["userModifiedFields"]!.AsArray().Select(f => f!.GetValue<string>()));

        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/models/deepseek-v4-flash/reset-field", new { field = "displayName" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(original, body!["displayName"]!.GetValue<string>());
        Assert.Empty(body["userModifiedFields"]!.AsArray());
    }

    [Fact]
    public async Task User_model_crud_and_invalid_pricing_rejected()
    {
        await using var host = await TestHost.StartAsync();
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/models",
            new { id = "my-model", displayName = "Mine", vendor = "me", pricing = ClaudeLikePricing });
        Assert.Equal(HttpStatusCode.OK, status);

        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/models",
            new { id = "my-model", displayName = "Mine", vendor = "me" });
        Assert.Equal(HttpStatusCode.Conflict, status);

        var bad = new { currency = "USD", unit = "per_1m_tokens", @base = new { input = 1.0 },
            time_windows = new[] { new { name = "x", timezone = "Not/AZone", start = "25:00", end = "01:00" } } };
        (status, var body) = await host.SendAsync(HttpMethod.Put, "/api/models/my-model/prices/acme", new { pricing = bad });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Invalid pricing", body!["error"]!.GetValue<string>());

        (status, body) = await host.SendAsync(HttpMethod.Put, "/api/models/my-model/prices/acme",
            new { upstreamModelId = "acme/my-model", pricing = ClaudeLikePricing });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["userModified"]!.GetValue<bool>());

        (status, _) = await host.SendAsync(HttpMethod.Delete, "/api/models/my-model");
        Assert.Equal(HttpStatusCode.NoContent, status);
        (status, _) = await host.SendAsync(HttpMethod.Get, "/api/models/my-model");
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    [Fact]
    public async Task Billing_simulator_prices_raw_schedule()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/billing/simulate", new
        {
            pricing = ClaudeLikePricing,
            usage = new { tokens = new Dictionary<string, long> { ["input"] = 1_000_000, ["cache_write_1h"] = 1_000_000, ["output"] = 100_000 } },
            locale = "en",
        });
        Assert.Equal(HttpStatusCode.OK, status);
        // 3 + 6 + 1.5 = 10.5 USD
        Assert.Equal(10_500_000_000L, body!["totalNanos"]!.GetValue<long>());
        Assert.True(body["priced"]!.GetValue<bool>());
        Assert.False(string.IsNullOrEmpty(body["description"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Billing_simulator_uses_provider_specific_price()
    {
        await using var host = await TestHost.StartAsync();
        var siliconflow = await AddProviderAsync(host, "SiliconFlow", "siliconflow", 2m);
        var unknown = await AddProviderAsync(host, "Some proxy", "no-such-key", 2m);
        var table = await host.Db.Models.GetPriceTableAsync("deepseek-v4-flash");
        var sfInput = table["siliconflow"].Base["input"]!.Value;

        var usage = new { tokens = new Dictionary<string, long> { ["input"] = 1_000_000 } };
        // Fixed off-peak timestamp: this test isolates provider-price resolution, not the wall clock.
        var requestTimeUtc = new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
        var (_, sf) = await host.SendAsync(HttpMethod.Post, "/api/billing/simulate",
            new { modelId = "deepseek-v4-flash", providerId = siliconflow, usage, requestTimeUtc });
        Assert.Equal("provider_price", sf!["pricingSource"]!.GetValue<string>());
        Assert.Equal("siliconflow", sf["priceKey"]!.GetValue<string>());
        Assert.Equal(Money.ToNanos(sfInput * 2m), sf["totalNanos"]!.GetValue<long>());

        var (_, other) = await host.SendAsync(HttpMethod.Post, "/api/billing/simulate",
            new { modelId = "deepseek-v4-flash", providerId = unknown, usage, requestTimeUtc });
        Assert.Equal("system_default", other!["pricingSource"]!.GetValue<string>());
        Assert.Equal(Money.ToNanos(0.15m * 2m), other["totalNanos"]!.GetValue<long>());
    }

    [Fact]
    public async Task Requests_and_stats_round_trip()
    {
        await using var host = await TestHost.StartAsync();
        var id = Ulid.NewUlid();
        await host.Db.Requests.InsertBatchAsync([new RequestRecord
        {
            Id = id,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ClientKind = "codex",
            InboundProtocol = "openai-responses",
            RequestedModel = "gpt-5",
            UpstreamModel = "gpt-5-chat-latest",
            ResponseModel = "gpt-5-chat-2026-05-01",
            SystemModelId = "gpt-5",
            Status = RequestStatus.Success,
            HttpStatus = 200,
            TtftMs = 120,
            OutputTps = 42.56,
            TotalInputTokens = 1000,
            TotalOutputTokens = 50,
            CacheReadTokens = 600,
            CacheWriteTokens = 200,
            ReasoningTokens = 20,
            CostNanoUsd = 2_000_000,
            UsageSource = "reported",
            BillingTraceJson = "[]",
            UsageItems = [new RequestUsageItem { RequestId = id, TokenType = "input", Tokens = 1000, UnitPrice = "1.25", CostNanoUsd = 1_250_000 }],
        }]);

        var page = await host.GetJsonAsync("/api/requests?client=codex");
        Assert.Equal(1, page["total"]!.GetValue<long>());
        var item = page["items"]![0]!;
        Assert.Equal(120, item["ttftMs"]!.GetValue<long>());
        Assert.Equal(42.56, item["outputTps"]!.GetValue<double>());
        Assert.Equal(600, item["cacheReadTokens"]!.GetValue<long>());
        Assert.Equal(200, item["cacheWriteTokens"]!.GetValue<long>());
        Assert.Equal(20, item["reasoningTokens"]!.GetValue<long>());
        Assert.Equal("gpt-5-chat-2026-05-01", item["responseModel"]!.GetValue<string>());
        var detail = await host.GetJsonAsync($"/api/requests/{id}");
        Assert.Equal(42.56, detail["outputTps"]!.GetValue<double>());
        Assert.Equal("input", detail["usageItems"]![0]!["tokenType"]!.GetValue<string>());
        Assert.Equal(600, detail["cacheReadTokens"]!.GetValue<long>());
        Assert.Equal(200, detail["cacheWriteTokens"]!.GetValue<long>());
        Assert.Equal(20, detail["reasoningTokens"]!.GetValue<long>());
        Assert.Equal("gpt-5-chat-2026-05-01", detail["responseModel"]!.GetValue<string>());

        // Searching by a fragment of the returned model ID finds the request.
        var byResponseModel = await host.GetJsonAsync("/api/requests?model=chat-2026-05-01");
        Assert.Equal(1, byResponseModel["total"]!.GetValue<long>());

        var summary = await host.GetJsonAsync("/api/stats/summary?range=today");
        Assert.Equal(1, summary["requests"]!.GetValue<long>());
        Assert.Equal(0.002m, summary["costUsd"]!.GetValue<decimal>());
        Assert.Equal(600, summary["cacheReadTokens"]!.GetValue<long>());
        Assert.Equal(200, summary["cacheWriteTokens"]!.GetValue<long>());
        Assert.Equal(20, summary["reasoningTokens"]!.GetValue<long>());
        var top = (await host.GetJsonAsync("/api/stats/top-models?range=7d")).AsArray();
        Assert.Equal("gpt-5", top[0]!["model"]!.GetValue<string>());
        Assert.Equal(1000, top[0]!["inputTokens"]!.GetValue<long>());
        Assert.Equal(600, top[0]!["cacheReadTokens"]!.GetValue<long>());

        // The overview's client switcher narrows every stats call to one client kind.
        Assert.Equal(1, (await host.GetJsonAsync("/api/stats/summary?range=today&client=codex"))["requests"]!.GetValue<long>());
        Assert.Equal(0, (await host.GetJsonAsync("/api/stats/summary?range=today&client=claude-code"))["requests"]!.GetValue<long>());
        Assert.Single((await host.GetJsonAsync("/api/stats/timeseries?range=today&groupBy=hour&by=provider&client=codex&tzOffset=480")).AsArray());
        Assert.Empty((await host.GetJsonAsync("/api/stats/timeseries?range=today&groupBy=hour&by=provider&client=claude-code")).AsArray());
        Assert.Empty((await host.GetJsonAsync("/api/stats/top-models?range=7d&client=claude-code")).AsArray());

        // The activity heatmap counts today's requests for the same filters (one row per active day, none for empty days).
        var heatmap = (await host.GetJsonAsync("/api/stats/activity-heatmap?client=codex&tzOffset=0")).AsArray();
        var today = Assert.Single(heatmap)!;
        Assert.Equal(1, today["requests"]!.GetValue<long>());
        Assert.Equal(DateTimeOffset.Now.ToString("yyyy-MM-dd"), today["day"]!.GetValue<string>());
        Assert.Empty((await host.GetJsonAsync("/api/stats/activity-heatmap?client=claude-code")).AsArray());
        Assert.Single((await host.GetJsonAsync("/api/stats/activity-heatmap?days=730&client=codex")).AsArray());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(null)]
    public async Task Request_list_and_detail_preserve_zero_or_missing_tps(double? outputTps)
    {
        await using var host = await TestHost.StartAsync();
        var id = Ulid.NewUlid();
        await host.Db.Requests.InsertBatchAsync([new RequestRecord
        {
            Id = id,
            StartedAtUtc = DateTimeOffset.UtcNow,
            InboundProtocol = "openai-chat",
            RequestedModel = "gpt-5",
            TtftMs = 120,
            OutputTps = outputTps,
        }]);

        var page = await host.GetJsonAsync("/api/requests");
        var item = Assert.Single(page["items"]!.AsArray())!;
        var detail = await host.GetJsonAsync($"/api/requests/{id}");
        Assert.Equal(120, item["ttftMs"]!.GetValue<long>());
        foreach (var row in new[] { item, detail })
        {
            if (outputTps is { } value) Assert.Equal(value, row["outputTps"]!.GetValue<double>());
            else Assert.Null(row["outputTps"]);
        }
    }

    [Fact]
    public async Task Request_list_and_detail_expose_reasoning_metadata()
    {
        await using var host = await TestHost.StartAsync();
        var effortOnly = Ulid.NewUlid();
        var budgetOnly = Ulid.NewUlid();
        var unrecorded = Ulid.NewUlid();
        await host.Db.Requests.InsertBatchAsync(
        [
            new RequestRecord
            {
                Id = effortOnly,
                StartedAtUtc = DateTimeOffset.UtcNow,
                InboundProtocol = "openai-responses",
                RequestedModel = "gpt-5",
                Status = RequestStatus.Success,
                ReasoningEffort = "xhigh",
            },
            new RequestRecord
            {
                Id = budgetOnly,
                StartedAtUtc = DateTimeOffset.UtcNow,
                InboundProtocol = "anthropic",
                RequestedModel = "claude-sonnet-4-6",
                Status = RequestStatus.Success,
                ReasoningMode = "enabled",
                ReasoningBudgetTokens = 8192,
            },
            new RequestRecord
            {
                Id = unrecorded,
                StartedAtUtc = DateTimeOffset.UtcNow,
                InboundProtocol = "openai-chat",
                RequestedModel = "gpt-5",
                Status = RequestStatus.Success,
            },
        ]);

        var page = await host.GetJsonAsync("/api/requests");
        var items = page["items"]!.AsArray();
        Assert.Equal(3, items.Count);
        var effortItem = items.Single(i => i!["id"]!.GetValue<string>() == effortOnly)!;
        Assert.Equal("xhigh", effortItem["reasoningEffort"]!.GetValue<string>());
        Assert.Null(effortItem["reasoningMode"]);
        Assert.Null(effortItem["reasoningBudgetTokens"]);

        var detail = await host.GetJsonAsync($"/api/requests/{budgetOnly}");
        Assert.Equal("enabled", detail["reasoningMode"]!.GetValue<string>());
        Assert.Equal(8192, detail["reasoningBudgetTokens"]!.GetValue<long>());
        Assert.Null(detail["reasoningEffort"]);

        // Rows written before the fields (or without an explicit setting) expose null, never a guess.
        var plainDetail = await host.GetJsonAsync($"/api/requests/{unrecorded}");
        Assert.Null(plainDetail["reasoningEffort"]);
        Assert.Null(plainDetail["reasoningMode"]);
        Assert.Null(plainDetail["reasoningBudgetTokens"]);
    }

    [Fact]
    public async Task Settings_patch_persists_and_ignores_read_only_fields()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Patch, "/api/settings",
            new { debugBodies = true, locale = "en", port = 1, effortBudgets = new { low = 10, medium = 20, high = 30 } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["debugBodies"]!.GetValue<bool>());
        Assert.Equal(17321, body["port"]!.GetValue<int>());
        Assert.Equal(20, body["effortBudgets"]!["medium"]!.GetValue<int>());
        Assert.Equal("http://127.0.0.1:17321", body["gatewayBaseUrl"]!.GetValue<string>());

        var stored = await host.Db.Settings.GetAsync<AppSettings>(AppSettings.StorageKey);
        Assert.True(stored!.DebugBodies);
        Assert.Equal("en", stored.Locale);
    }

    [Fact]
    public async Task Settings_proxy_keeps_credentials_out_of_the_url_and_the_response()
    {
        await using var host = await TestHost.StartAsync();
        try
        {
            var (status, body) = await host.SendAsync(HttpMethod.Patch, "/api/settings",
                new { proxyMode = "custom", proxyUrl = "http://me:s3cret@proxy.example.com:3128/", proxyBypass = " corp.example.com " });
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("custom", body!["proxyMode"]!.GetValue<string>());
            Assert.Equal("http://proxy.example.com:3128", body["proxyUrl"]!.GetValue<string>());
            Assert.Equal("me", body["proxyUsername"]!.GetValue<string>());
            Assert.True(body["hasProxyPassword"]!.GetValue<bool>());
            Assert.Equal("corp.example.com", body["proxyBypass"]!.GetValue<string>());
            Assert.DoesNotContain("s3cret", body.ToJsonString());

            var stored = await host.Db.Settings.GetAsync<AppSettings>(AppSettings.StorageKey);
            Assert.NotNull(stored!.ProxyPasswordProtected);
            Assert.DoesNotContain("s3cret", stored.ProxyPasswordProtected);

            (status, _) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { proxyUrl = "ftp://proxy.example.com" });
            Assert.Equal(HttpStatusCode.BadRequest, status);
            (status, _) = await host.SendAsync(HttpMethod.Patch, "/api/settings", new { proxyUrl = (string?)null });
            Assert.Equal(HttpStatusCode.BadRequest, status);
            stored = await host.Db.Settings.GetAsync<AppSettings>(AppSettings.StorageKey);
            Assert.Equal(("custom", "http://proxy.example.com:3128"), (stored!.ProxyMode, stored.ProxyUrl));
        }
        finally
        {
            // The proxy is process-wide: put it back so other tests in this assembly stay direct.
            await host.SendAsync(HttpMethod.Patch, "/api/settings", new { proxyMode = "system" });
        }
    }

    [Fact]
    public async Task Request_detail_exposes_the_privacy_report()
    {
        await using var host = await TestHost.StartAsync();
        var id = Ulid.NewUlid();
        await host.Db.Requests.InsertBatchAsync([new RequestRecord
        {
            Id = id,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ClientKind = "claude-code",
            InboundProtocol = "anthropic",
            RequestedModel = "claude-sonnet-4-6",
            Status = RequestStatus.Blocked,
            PrivacyJson = """{"dry_run":false,"blocked":true,"redactions":0,"hits":[{"rule_id":"builtin.aws-access-key","category":"aws_key","action":"block","count":1}]}""",
        }]);

        var detail = await host.GetJsonAsync($"/api/requests/{id}");
        Assert.True(detail["privacy"]!["blocked"]!.GetValue<bool>());
        Assert.Equal("aws_key", detail["privacy"]!["hits"]![0]!["category"]!.GetValue<string>());
        Assert.Equal("block", detail["privacy"]!["hits"]![0]!["action"]!.GetValue<string>());
        // Storage JSON is snake_case; the API always exposes camelCase.
        Assert.False(detail["privacy"]!["dryRun"]!.GetValue<bool>());
        // The upstream response declared no model: responseModel stays null in the API.
        Assert.Null(detail["responseModel"]);

        // Blocked requests are filterable like any other status.
        var page = await host.GetJsonAsync("/api/requests?status=blocked");
        Assert.Equal(1, page["total"]!.GetValue<long>());

        // The privacy event log lists the same outcome and aggregates it.
        var events = await host.GetJsonAsync("/api/privacy/events?blocked=true");
        Assert.Equal(1, events["total"]!.GetValue<long>());
        var hit = events["items"]![0]!;
        Assert.Equal(id, hit["id"]!.GetValue<string>());
        Assert.True(hit["blocked"]!.GetValue<bool>());
        Assert.Equal("aws_key", hit["hits"]![0]!["category"]!.GetValue<string>());

        var byAction = await host.GetJsonAsync("/api/privacy/events?action=redact");
        Assert.Equal(0, byAction["total"]!.GetValue<long>());

        var stats = await host.GetJsonAsync("/api/privacy/events/stats?range=all");
        Assert.Equal(1, stats["events"]!.GetValue<long>());
        Assert.Equal(1, stats["blocked"]!.GetValue<long>());
        Assert.Equal("aws_key", stats["categories"]![0]!["category"]!.GetValue<string>());
    }

    [Fact]
    public async Task Bindings_pin_and_clear_subscription_accounts()
    {
        await using var host = await TestHost.StartAsync();
        var provider = new Provider
        {
            Id = Ulid.NewUlid(),
            Name = "Claude 订阅",
            AuthScheme = AuthSchemes.OAuthSubscription,
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.Anthropic, BaseUrl = "https://example.invalid" }],
            PreferredUpstreamProtocols = [ApiProtocol.Anthropic],
        };
        await host.Db.Providers.InsertAsync(provider);
        await host.Db.Accounts.InsertAsync(new ProviderAccount { Id = "acc-1", ProviderId = provider.Id, DisplayName = "me@example.com", Status = AccountStatus.Active });
        await host.Db.Accounts.InsertAsync(new ProviderAccount { Id = "acc-2", ProviderId = provider.Id, DisplayName = "other@example.com", Status = AccountStatus.Active });

        // Pin an account.
        var (status, body) = await host.SendAsync(HttpMethod.Put, "/api/clients/claude-code/binding",
            new { providerId = provider.Id, accountId = "acc-1" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("acc-1", body!["accountId"]!.GetValue<string>());

        // An account of another provider is rejected.
        var foreign = new Provider { Id = Ulid.NewUlid(), Name = "Other", Endpoints = [], AuthScheme = AuthSchemes.OAuthSubscription };
        await host.Db.Providers.InsertAsync(foreign);
        await host.Db.Accounts.InsertAsync(new ProviderAccount { Id = "acc-foreign", ProviderId = foreign.Id, DisplayName = "f@x.com", Status = AccountStatus.Active });
        (status, body) = await host.SendAsync(HttpMethod.Put, "/api/clients/claude-code/binding",
            new { providerId = provider.Id, accountId = "acc-foreign" });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        // Omitting accountId keeps the pin; "" clears it.
        (status, body) = await host.SendAsync(HttpMethod.Put, "/api/clients/claude-code/binding",
            new { providerId = provider.Id });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("acc-1", body!["accountId"]!.GetValue<string>());

        (status, body) = await host.SendAsync(HttpMethod.Put, "/api/clients/claude-code/binding",
            new { providerId = provider.Id, accountId = "" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["accountId"] is null);
    }

    private static async Task<string> AddProviderAsync(TestHost host, string name, string priceKey, decimal multiplier)
    {
        var p = new Provider
        {
            Id = Ulid.NewUlid(),
            Name = name,
            Category = "aggregator",
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://example.invalid/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat],
            AuthScheme = AuthSchemes.Bearer,
            PriceKey = priceKey,
            PriceMultiplier = multiplier,
        };
        await host.Db.Providers.InsertAsync(p);
        return p.Id;
    }
}
