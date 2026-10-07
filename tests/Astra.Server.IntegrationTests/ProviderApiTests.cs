using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Astra.Providers.Subscription;
using Astra.Providers.Templates;
using Astra.Server.Api;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

public class ProviderApiTests
{
    [Fact]
    public async Task Catalog_And_Price_Keys_Are_Available_Without_Configuring_Providers()
    {
        await using var host = await TestHost.StartAsync();
        var templates = (await host.GetJsonAsync("/api/provider-templates")).AsArray();
        Assert.Equal(24, templates.Count);
        var claudeSub = templates.Single(t => t!["id"]!.GetValue<string>() == "claude-subscription")!;
        Assert.Equal("oauth-subscription", claudeSub["authScheme"]!.GetValue<string>());
        Assert.False(claudeSub["requiresApiKey"]!.GetValue<bool>());
        var openai = templates.Single(t => t!["id"]!.GetValue<string>() == "openai")!;
        Assert.Contains(openai["models"]!.AsArray(), m => m!.GetValue<string>() == "gpt-5");
        var routin = templates.Single(t => t!["id"]!.GetValue<string>() == "routin")!;
        Assert.Equal(["openai-responses", "anthropic"],
            routin["endpoints"]!.AsArray().Select(e => e!["protocol"]!.GetValue<string>()).ToList());
        Assert.All(routin["endpoints"]!.AsArray(), e => Assert.Equal("https://api.routin.ai/plan/v1", e!["baseUrl"]!.GetValue<string>()));
        var ollama = templates.Single(t => t!["id"]!.GetValue<string>() == "ollama")!;
        Assert.False(ollama["requiresApiKey"]!.GetValue<bool>());
        var prices = (await host.GetJsonAsync("/api/price-keys")).AsArray();
        Assert.Contains(prices, p => p!["priceKey"]!.GetValue<string>() == "siliconflow-cn");
        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());
    }

    [Fact]
    public async Task Provider_Key_Is_Encrypted_And_Never_Returned_In_Plaintext()
    {
        await using var host = await TestHost.StartAsync();
        const string key = "sk-a-long-test-secret-key-1234";
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "openai", apiKey = key, models = new[] { "gpt-5" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["hasApiKey"]!.GetValue<bool>());
        Assert.Equal("sk-…1234", body["apiKeyMasked"]!.GetValue<string>());
        Assert.DoesNotContain(key, body.ToJsonString());
        var id = body["id"]!.GetValue<string>();
        var stored = await host.Db.Providers.GetAsync(id);
        Assert.NotEqual(key, stored!.ApiKeyEnc);
        Assert.NotNull(stored.TemplateSnapshotJson);
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { name = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(stored.ApiKeyEnc, (await host.Db.Providers.GetAsync(id))!.ApiKeyEnc);
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { apiKey = "" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body!["hasApiKey"]!.GetValue<bool>());
        Assert.Null((await host.Db.Providers.GetAsync(id))!.ApiKeyEnc);
    }

    [Fact]
    public async Task Provider_Model_Only_Needs_Id_And_Inherits_System_Fields_And_Provider_Price()
    {
        await using var host = await TestHost.StartAsync();
        var p = await Create(host, "siliconflow");
        var (status, added) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{p}/models", new { modelIds = new[] { "deepseek-ai/DeepSeek-V4-Flash-0731" } });
        Assert.Equal(HttpStatusCode.OK, status);
        var m = added!.AsArray().Single()!;
        Assert.Equal("deepseek-v4-flash", m["systemModelId"]!.GetValue<string>());
        Assert.Equal("inherited", m["origins"]!["contextWindow"]!.GetValue<string>());
        Assert.Equal("provider_price", m["pricingSource"]!.GetValue<string>());
        Assert.Equal("siliconflow", m["priceKey"]!.GetValue<string>());
        Assert.Equal(0.22m, m["effectivePricing"]!["base"]!["input"]!.GetValue<decimal>());
        Assert.Equal(1_000_000, m["effective"]!["contextWindow"]!.GetValue<long>());
        Assert.Equal("{}", m["overrides"]!.ToJsonString());
    }

    [Fact]
    public async Task Price_Override_Does_Not_Multiply_And_Null_Resets_To_Inherited()
    {
        await using var host = await TestHost.StartAsync();
        var p = await Create(host, "siliconflow");
        await host.SendAsync(HttpMethod.Patch, $"/api/providers/{p}", new { priceMultiplier = 2 });
        var (_, added) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{p}/models", new { modelIds = new[] { "deepseek-v4-flash" } });
        var pmId = added![0]!["id"]!.GetValue<long>();
        var (_, overridden) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{p}/models/{pmId}", new
        {
            overrides = new { pricing = new { currency = "USD", unit = "per_1m_tokens", @base = new { input = 7, output = 14 } } },
        });
        Assert.Equal("provider_override", overridden!["pricingSource"]!.GetValue<string>());
        Assert.Equal(1, overridden["multiplier"]!.GetValue<decimal>());
        var (status, inherited) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{p}/models/{pmId}", new JsonObject
        { ["overrides"] = new JsonObject { ["pricing"] = null } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("provider_price", inherited!["pricingSource"]!.GetValue<string>());
        Assert.Equal(2, inherited["multiplier"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task A_Model_From_Another_Provider_Cannot_Be_Edited_Or_Removed()
    {
        await using var host = await TestHost.StartAsync();
        var a = await Create(host);
        var b = await Create(host);
        var (_, added) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{a}/models", new { modelIds = new[] { "gpt-5" } });
        var pm = added![0]!["id"]!.GetValue<long>();
        var (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{b}/models/{pm}", new { enabled = false });
        Assert.Equal(HttpStatusCode.NotFound, status);
        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/providers/{b}/models/{pm}");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.True((await host.Db.Providers.GetModelByIdAsync(pm))!.Enabled);
    }

    [Fact]
    public async Task Bound_Provider_Deletion_Is_Blocked_And_Duplicate_Has_Independent_Models()
    {
        await using var host = await TestHost.StartAsync();
        var id = await Create(host);
        await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models", new { modelIds = new[] { "gpt-5" } });
        await host.SendAsync(HttpMethod.Put, "/api/clients/codex/binding", new { providerId = id });
        var (status, error) = await host.SendAsync(HttpMethod.Delete, $"/api/providers/{id}");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("codex", error!["details"]!["boundClients"]![0]!.GetValue<string>());
        (status, var copy) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/duplicate");
        Assert.Equal(HttpStatusCode.OK, status);
        var copyId = copy!["id"]!.GetValue<string>();
        Assert.NotEqual(id, copyId);
        Assert.Empty(copy["boundClients"]!.AsArray());
        var copiedModels = (await host.GetJsonAsync($"/api/providers/{copyId}/models")).AsArray();
        Assert.Equal("gpt-5", copiedModels.Single()!["modelId"]!.GetValue<string>());
        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/providers/{copyId}");
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.NotNull(await host.Db.Providers.GetAsync(id));
    }

    [Fact]
    public async Task Subscription_Template_Is_Single_Instance_And_Cannot_Be_Duplicated()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "grok-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = body!["id"]!.GetValue<string>();

        // a second provider from the same subscription template is refused with the existing instance named
        (status, var error) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "grok-subscription" });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(id, error!["details"]!["existingProviderId"]!.GetValue<string>());
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "claude-subscription", variantId = "" });
        Assert.NotEqual(HttpStatusCode.Conflict, status); // a different subscription family is fine

        // duplicating the subscription provider is refused too; a normal provider still duplicates
        (status, error) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/duplicate");
        Assert.Equal(HttpStatusCode.Conflict, status);
        var regular = await Create(host);
        (status, _) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{regular}/duplicate");
        Assert.Equal(HttpStatusCode.OK, status);

        // deleting the instance frees the template again
        (status, _) = await host.SendAsync(HttpMethod.Delete, $"/api/providers/{id}");
        Assert.Equal(HttpStatusCode.NoContent, status);
        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "grok-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("https://cli-chat-proxy.grok.com/v1", body!["endpoints"]![0]!["baseUrl"]!.GetValue<string>());
    }

    [Fact]
    public async Task Account_Quota_Is_Fetched_Persisted_And_Served_By_The_Accounts_Api()
    {
        await using var host = await TestHost.StartAsync(builder =>
            builder.Services.AddHttpClient(SubscriptionQuotaService.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => Task.FromResult(JsonResponse(
                    """{"five_hour":{"utilization":12.5,"resets_at":"2026-10-06T17:00:00Z"},"seven_day":{"utilization":43}}""")))));
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new { templateId = "claude-subscription" });
        Assert.Equal(HttpStatusCode.OK, status);
        var providerId = body!["id"]!.GetValue<string>();

        var protector = host.App.Services.GetRequiredService<ISecretProtector>();
        await host.Db.Accounts.InsertAsync(new ProviderAccount
        {
            Id = "acc-q",
            ProviderId = providerId,
            DisplayName = "me@example.com",
            AccessTokenEnc = protector.Protect("at-fresh"),
            RefreshTokenEnc = protector.Protect("rt-1"),
            ExpiresAtUtc = DateTimeOffset.UtcNow + TimeSpan.FromHours(1),
            Status = AccountStatus.Active,
        });

        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/provider-accounts/acc-q/quota", new { });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(12.5, body!["quota"]!["session"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal(43, body["quota"]!["weekly"]!["usedPercent"]!.GetValue<double>());
        Assert.Equal("2026-10-06T17:00:00Z", body["quota"]!["session"]!["resetsAtUtc"]!.GetValue<string>());

        // the snapshot is persisted: plain account listings serve it without touching upstream again
        var accounts = (await host.GetJsonAsync($"/api/providers/{providerId}/accounts")).AsArray();
        Assert.Equal(12.5, accounts.Single()!["quota"]!["session"]!["usedPercent"]!.GetValue<double>());
    }

    [Fact]
    public async Task Template_Variant_Preserves_Price_Key_And_Endpoint_On_Upgrade()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        { templateId = "siliconflow", variantId = "cn", apiKey = "sk-test-long-enough", models = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = body!["id"]!.GetValue<string>();
        Assert.Equal("siliconflow-cn", body["priceKey"]!.GetValue<string>());
        Assert.Equal("https://api.siliconflow.cn/v1", body["endpoints"]![0]!["baseUrl"]!.GetValue<string>());
        var catalog = host.App.Services.GetRequiredService<ProviderTemplateCatalog>();
        catalog.Get("siliconflow")!.Version++;
        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/template-update");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("siliconflow-cn", body!["priceKey"]!.GetValue<string>());
        Assert.Equal("https://api.siliconflow.cn/v1", body["endpoints"]![0]!["baseUrl"]!.GetValue<string>());
        Assert.Equal(2, body["templateVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Template_Update_Merges_Unmodified_Defaults_Without_Overwriting_User_Fields()
    {
        await using var host = await TestHost.StartAsync();
        var id = await Create(host, "openrouter");
        await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { name = "My provider", priceMultiplier = 1.5 });
        var catalog = host.App.Services.GetRequiredService<ProviderTemplateCatalog>();
        var template = catalog.Get("openrouter")!;
        template.Version = 3; // the embedded catalog already ships v2; only a newer version triggers the merge
        template.DefaultHeaders["X-Title"] = "Astra v2";
        template.DefaultHeaders["X-New"] = "default";
        var (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/template-update");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("My provider", body!["name"]!.GetValue<string>());
        Assert.Equal(1.5m, body["priceMultiplier"]!.GetValue<decimal>());
        Assert.Equal("Astra v2", body["extraHeaders"]!["X-Title"]!.GetValue<string>());
        Assert.Equal("default", body["extraHeaders"]!["X-New"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_Provider_Settings_Return_400_Without_Persisting_Partial_Changes()
    {
        await using var host = await TestHost.StartAsync();
        var id = await Create(host);
        var (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { name = "Changed", priceMultiplier = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Test", (await host.Db.Providers.GetAsync(id))!.Name);
        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { endpoints = new[] { new { protocol = "openai-chat", baseUrl = "file:///etc/passwd" } } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Remote_Model_Discovery_Uses_Upstream_Credentials_And_Auto_Links()
    {
        var requests = new List<string>();
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                requests.Add(request.RequestUri!.AbsolutePath);
                Assert.Equal("upstream-test-key", request.Headers.Authorization!.Parameter);
                Assert.False(request.Headers.Contains("X-Astra-Admin"));
                await Task.CompletedTask;
                return JsonResponse("{\"data\":[{\"id\":\"gpt-5\"},{\"id\":\"unknown-model\"}]}");
            })));
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        { name = "Mock", apiKey = "upstream-test-key", endpoints = new[] { new { protocol = "openai-chat", baseUrl = "https://example.invalid/v1" } } });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = body!["id"]!.GetValue<string>();
        var remote = (await host.GetJsonAsync($"/api/providers/{id}/remote-models")).AsArray();
        Assert.Equal("gpt-5", remote.Single(m => m!["id"]!.GetValue<string>() == "gpt-5")!["linkedSystemModelId"]!.GetValue<string>());
        Assert.Equal(["/v1/models"], requests);
    }

    [Fact]
    public async Task Connection_Test_Logs_Actual_Reported_Usage_And_Cost()
    {
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.EndsWith("/v1/chat/completions", request.RequestUri!.AbsolutePath);
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                Assert.Equal("gpt-5", body["model"]!.GetValue<string>());
                return JsonResponse("{\"model\":\"gpt-5-2026-05-01\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"OK\"}}],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":20,\"prompt_tokens_details\":{\"cached_tokens\":60},\"completion_tokens_details\":{\"reasoning_tokens\":10}}}");
            })));
        var id = await Create(host);
        await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models", new { modelIds = new[] { "gpt-5" } });
        var (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/test", new { modelId = "gpt-5" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(result!["ok"]!.GetValue<bool>());
        await host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var records = await host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery { ProviderId = id });
        var r = records.Items.Single();
        Assert.Equal("gpt-5", r.RequestedModel);
        Assert.Equal("gpt-5-2026-05-01", r.ResponseModel);
        Assert.Equal(100, r.TotalInputTokens);
        Assert.Equal(20, r.TotalOutputTokens);
        Assert.Equal(60, r.CacheReadTokens);
        Assert.Equal(10, r.ReasoningTokens);
        Assert.Equal("reported", r.UsageSource);
        Assert.True(r.CostNanoUsd > 0);
    }

    [Theory]
    [InlineData("openai-chat", "model")]
    [InlineData("openai-responses", "model")]
    [InlineData("anthropic", "model")]
    [InlineData("gemini", "modelVersion")]
    [InlineData("gemini", "model_version")]
    public async Task Connection_Test_Records_Only_The_Model_Id_Declared_By_The_Api(string protocol, string modelField)
    {
        foreach (var reported in new[] { true, false })
        {
            var upstreamBody = new JsonObject();
            if (reported) upstreamBody[modelField] = "actual-reported-model";
            await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => Task.FromResult(JsonResponse(upstreamBody.ToJsonString())))));
            var id = await CreateCustomAsync(host, protocol,
                protocol == "gemini" ? "https://example.invalid/v1beta" : "https://example.invalid/v1", "none");
            var (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/test", new { modelId = "requested-alias" });
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(result!["ok"]!.GetValue<bool>());
            await host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
            var records = await host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery { ProviderId = id });
            var record = Assert.Single(records.Items);
            Assert.Equal("requested-alias", record.RequestedModel);
            Assert.Equal("requested-alias", record.UpstreamModel);
            Assert.Equal(reported ? "actual-reported-model" : null, record.ResponseModel);
        }
    }

    [Fact]
    public async Task Malformed_Provider_Payloads_Return_400_Json_And_Never_Persist_Partially()
    {
        await using var host = await TestHost.StartAsync();

        // create with a duplicate protocol endpoint leaves nothing behind
        var (status, error) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "Broken",
            endpoints = new[]
            {
                new { protocol = "openai-chat", baseUrl = "https://a.example.invalid/v1" },
                new { protocol = "openai-chat", baseUrl = "https://b.example.invalid/v1" },
            },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());

        var id = await Create(host);

        // wrong JSON field types and explicit nulls are rejected as 400 JSON errors
        foreach (var bad in new JsonObject[]
        {
            new() { ["name"] = 123 },
            new() { ["name"] = "NewName", ["settings"] = "not-an-object" },
            new() { ["name"] = "NewName", ["endpoints"] = "not-an-array" },
            new() { ["endpoints"] = null },
            new() { ["extraHeaders"] = null },
            new() { ["priceMultiplier"] = "expensive" },
        })
        {
            (status, error) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", bad);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.NotNull(error!["error"]);
        }

        // reserved / malformed / case-insensitively duplicate extra headers are rejected,
        // together with the valid "name" field of the same request
        foreach (var headers in new[]
        {
            new JsonObject { ["Host"] = "evil.example" },
            new JsonObject { ["X-Astra-Admin"] = "1" },
            new JsonObject { ["Bad Header"] = "x" },
            new JsonObject { ["X-Dup"] = "a", ["x-dup"] = "b" },
            new JsonObject { ["X-Ok"] = "a\u0007b" },
        })
        {
            (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}",
                new JsonObject { ["extraHeaders"] = headers.DeepClone(), ["name"] = "NewName" });
            Assert.Equal(HttpStatusCode.BadRequest, status);
        }

        // apiKey must be a string; explicit null is refused (only "" clears it)
        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new JsonObject { ["apiKey"] = null });
        Assert.Equal(HttpStatusCode.BadRequest, status);

        // none of the failed writes persisted their valid fields either
        var stored = await host.Db.Providers.GetAsync(id);
        Assert.Equal("Test", stored!.Name);
        Assert.Empty(stored.ExtraHeaders);
    }

    [Fact]
    public async Task Short_Api_Keys_Are_Masked_As_Bullets_Only()
    {
        await using var host = await TestHost.StartAsync();
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "Short key",
            endpoints = new[] { new { protocol = "openai-chat", baseUrl = "https://example.invalid/v1" } },
            apiKey = "abc",
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body!["hasApiKey"]!.GetValue<bool>());
        Assert.Equal("••••", body["apiKeyMasked"]!.GetValue<string>());
        Assert.DoesNotContain("abc", body.ToJsonString());
        Assert.Equal("••••", (await host.GetJsonAsync("/api/providers")).AsArray().Single()!["apiKeyMasked"]!.GetValue<string>());
    }

    [Fact]
    public async Task Adding_Models_Trims_Dedupes_And_Returns_Only_New_Rows()
    {
        await using var host = await TestHost.StartAsync();
        var id = await Create(host);

        // invalid model id payloads are rejected before anything is written
        var (status, error) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models",
            new JsonObject { ["modelIds"] = "not-an-array" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        (status, error) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models", new { modelIds = new[] { "   " } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        Assert.Empty((await host.GetJsonAsync($"/api/providers/{id}/models")).AsArray());

        // same id and whitespace-only-padded ids collapse into one row each; only new rows are returned
        (status, var added) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models",
            new { modelIds = new[] { "gpt-5", " gpt-5 ", "gpt-5", " gpt-6" } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["gpt-5", "gpt-6"], added!.AsArray().Select(m => m!["modelId"]!.GetValue<string>()).ToList());

        // re-adding everything (still padded) adds nothing and returns []
        (status, added) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models",
            new { modelIds = new[] { "gpt-5", "gpt-6", " gpt-6 " } });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(added!.AsArray());
        Assert.Equal(2, (await host.GetJsonAsync($"/api/providers/{id}/models")).AsArray().Count);
    }

    [Fact]
    public async Task Provider_Order_Requires_Every_Id_Exactly_Once_And_Keeps_The_Old_Order_On_Errors()
    {
        await using var host = await TestHost.StartAsync();
        var a = await Create(host);
        var b = await Create(host);
        var c = await Create(host);
        Assert.Equal([a, b, c], (await host.GetJsonAsync("/api/providers")).AsArray().Select(p => p!["id"]!.GetValue<string>()).ToList());

        var (status, _) = await host.SendAsync(HttpMethod.Put, "/api/providers/order", new { ids = new[] { c, a, b } });
        Assert.Equal(HttpStatusCode.NoContent, status);
        Assert.Equal([c, a, b], (await host.GetJsonAsync("/api/providers")).AsArray().Select(p => p!["id"]!.GetValue<string>()).ToList());

        foreach (var bad in new[]
        {
            new[] { a, b, c, "unknown-id" }, // unknown id
            new[] { c, c, a },               // duplicate id
            new[] { a, b },                  // missing id
        })
        {
            (status, var error) = await host.SendAsync(HttpMethod.Put, "/api/providers/order", new { ids = bad });
            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.NotNull(error!["error"]);
            Assert.Equal([c, a, b], (await host.GetJsonAsync("/api/providers")).AsArray().Select(p => p!["id"]!.GetValue<string>()).ToList());
        }
    }

    [Fact]
    public async Task Provider_Creation_With_Duplicate_Model_Ids_Rolls_Back_The_Whole_Transaction()
    {
        await using var host = await TestHost.StartAsync(); // temp data dir, never the user's real one
        var provider = new Provider
        {
            Id = Ulid.NewUlid(),
            Name = "Tx",
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://example.invalid/v1" }],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat],
        };
        var models = new List<ProviderModel>
        {
            new() { ProviderId = provider.Id, ModelId = "same-model" },
            new() { ProviderId = provider.Id, ModelId = "same-model" },
        };

        await Assert.ThrowsAsync<SqliteException>(() => host.Db.Providers.InsertWithModelsAsync(provider, models));
        Assert.Null(await host.Db.Providers.GetAsync(provider.Id));
        Assert.Empty(await host.Db.Providers.ListModelsAsync(provider.Id));
    }

    [Fact]
    public async Task Connection_Test_Uses_Protocol_Specific_Paths_Bodies_And_Credentials()
    {
        var sent = new List<Sent>();
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                sent.Add(await RecordAsync(request));
                return JsonResponse(request.RequestUri!.AbsolutePath switch
                {
                    "/v1/responses" => """{"id":"resp_test","status":"completed","output":[]}""",
                    "/anthropic/v1/messages" => """{"id":"msg_test","role":"assistant","content":[{"type":"text","text":"OK"}]}""",
                    "/v1beta/models/gemini-test:generateContent" => """{"candidates":[{"content":{"parts":[{"text":"OK"}]}}]}""",
                    _ => """{"choices":[{"message":{"role":"assistant","content":"OK"}}]}""",
                });
            })));

        var chat = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "bearer", "sk-chat-key-0001");
        var responses = await CreateCustomAsync(host, "openai-responses", "https://example.invalid/v1", "bearer", "sk-resp-key-0002");
        var anthropic = await CreateCustomAsync(host, "anthropic", "https://example.invalid/anthropic", "x-api-key", "sk-ant-key-0003");
        var gemini = await CreateCustomAsync(host, "gemini", "https://example.invalid/v1beta", "x-goog-api-key", "gm-key-0004");
        var full = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/custom/endpoint?flag=1", "bearer", "sk-full-key-0005", fullUrl: true);

        var (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{chat}/test", new { modelId = "chat-model-x" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(result!["ok"]!.GetValue<bool>());
        var r = sent.Single(s => s.Path == "/v1/chat/completions");
        Assert.Equal("https://example.invalid/v1/chat/completions", r.Url);
        Assert.Equal("Bearer sk-chat-key-0001", r.Authorization);
        var body = JsonNode.Parse(r.Body!)!;
        Assert.Equal("chat-model-x", body["model"]!.GetValue<string>());
        Assert.Equal(16, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("user", body["messages"]![0]!["role"]!.GetValue<string>());

        (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{responses}/test", new { modelId = "resp-model-x" });
        Assert.True(result!["ok"]!.GetValue<bool>());
        r = sent.Single(s => s.Path == "/v1/responses");
        Assert.Equal("Bearer sk-resp-key-0002", r.Authorization);
        body = JsonNode.Parse(r.Body!)!;
        Assert.Equal("resp-model-x", body["model"]!.GetValue<string>());
        Assert.Equal("Say OK.", body["input"]!.GetValue<string>());
        Assert.False(body["store"]!.GetValue<bool>());
        Assert.Equal(16, body["max_output_tokens"]!.GetValue<int>());

        (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{anthropic}/test", new { modelId = "claude-model-x" });
        Assert.True(result!["ok"]!.GetValue<bool>());
        r = sent.Single(s => s.Path == "/anthropic/v1/messages");
        Assert.Equal("https://example.invalid/anthropic/v1/messages", r.Url);
        Assert.Equal("sk-ant-key-0003", r.XApiKey);
        Assert.Null(r.Authorization);
        Assert.Equal("2023-06-01", r.AnthropicVersion);
        body = JsonNode.Parse(r.Body!)!;
        Assert.Equal("claude-model-x", body["model"]!.GetValue<string>());
        Assert.Equal(16, body["max_tokens"]!.GetValue<int>());

        (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{gemini}/test", new { modelId = "gemini-test" });
        Assert.True(result!["ok"]!.GetValue<bool>());
        r = sent.Single(s => s.Path == "/v1beta/models/gemini-test:generateContent");
        Assert.Equal("gm-key-0004", r.XGoogApiKey);
        body = JsonNode.Parse(r.Body!)!;
        Assert.Equal("Say OK.", body["contents"]![0]!["parts"]![0]!["text"]!.GetValue<string>());
        Assert.Equal(16, body["generationConfig"]!["maxOutputTokens"]!.GetValue<int>());

        // fullUrl endpoints are requested verbatim; no path is appended
        (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{full}/test", new { modelId = "full-model-x" });
        Assert.True(result!["ok"]!.GetValue<bool>());
        r = sent.Single(s => s.Url == "https://example.invalid/custom/endpoint?flag=1");
        Assert.Equal("full-model-x", JsonNode.Parse(r.Body!)!["model"]!.GetValue<string>());
        Assert.Equal(5, sent.Count);
    }

    [Fact]
    public async Task Remote_Models_Follow_Gemini_Page_Tokens_Keep_The_Base_Query_And_Encode_The_Query_Key()
    {
        var sent = new List<Sent>();
        var page = 0;
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                sent.Add(await RecordAsync(request));
                page++;
                return JsonResponse(page == 1
                    ? """{"models":[{"name":"models/gemini-test","displayName":"Gemini Test"},{"name":"models/plain-2"}],"nextPageToken":"tok 2"}"""
                    : """{"models":[{"name":"models/gemini-2"}]}""");
            })));

        const string key = "gk+1/2=&q";
        var (status, created) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "Gemini",
            endpoints = new[] { new { protocol = "gemini", baseUrl = "https://example.invalid/v1beta?env=prod" } },
            authScheme = "query-key",
            apiKey = key,
            models = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.OK, status);
        var id = created!["id"]!.GetValue<string>();

        var remote = (await host.GetJsonAsync($"/api/providers/{id}/remote-models")).AsArray();
        Assert.Equal(["gemini-2", "gemini-test", "plain-2"], remote.Select(m => m!["id"]!.GetValue<string>()).ToList());
        Assert.Equal("Gemini Test", remote.Single(m => m!["id"]!.GetValue<string>() == "gemini-test")!["displayName"]!.GetValue<string>());
        Assert.All(remote, m => Assert.False(m!["id"]!.GetValue<string>().StartsWith("models/", StringComparison.Ordinal)));

        Assert.Equal(2, sent.Count);
        Assert.StartsWith("https://example.invalid/v1beta/models?", sent[0].Url);
        Assert.Contains("env=prod", sent[0].Query);              // base-URL query survives
        Assert.Contains("key=gk%2B1%2F2%3D%26q", sent[0].Query); // query-key auth is URI-encoded
        Assert.DoesNotContain("pageToken", sent[0].Query);
        Assert.Contains("pageToken=tok%202", sent[1].Query);
        Assert.Contains("env=prod", sent[1].Query);
        Assert.Contains("key=gk%2B1%2F2%3D%26q", sent[1].Query);
    }

    [Fact]
    public async Task Remote_Models_Paginate_Anthropic_With_After_Id()
    {
        var sent = new List<Sent>();
        var page = 0;
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                sent.Add(await RecordAsync(request));
                page++;
                return JsonResponse(page == 1
                    ? """{"data":[{"id":"claude-b"},{"id":"claude-a"}],"has_more":true,"last_id":"claude-b"}"""
                    : """{"data":[{"id":"claude-c"}],"has_more":false}""");
            })));

        var id = await CreateCustomAsync(host, "anthropic", "https://example.invalid/v1", "x-api-key", "sk-ant-remote-0006");
        var remote = (await host.GetJsonAsync($"/api/providers/{id}/remote-models")).AsArray();
        Assert.Equal(["claude-a", "claude-b", "claude-c"], remote.Select(m => m!["id"]!.GetValue<string>()).ToList());

        Assert.Equal(2, sent.Count);
        Assert.EndsWith("/v1/models", sent[0].Url);
        Assert.Equal("https://example.invalid/v1/models?after_id=claude-b", sent[1].Url);
        Assert.Equal("sk-ant-remote-0006", sent[1].XApiKey);
    }

    [Fact]
    public async Task Remote_Models_Non_Advancing_Pagination_Fails_With_502_Instead_Of_A_Partial_List()
    {
        var sent = new List<Sent>();
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                sent.Add(await RecordAsync(request));
                await Task.CompletedTask;
                return JsonResponse("""{"data":[{"id":"m1"}],"has_more":true,"last_id":"m1"}""");
            })));

        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", apiKey: "k");
        var (status, body) = await host.SendAsync(HttpMethod.Get, $"/api/providers/{id}/remote-models");
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("pagination", body!["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, sent.Count);
    }

    [Fact]
    public async Task Remote_Models_Upstream_Failures_Map_To_502_Json()
    {
        Func<HttpResponseMessage> respond = () => JsonResponse("{}");
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                await Task.CompletedTask;
                return respond();
            })));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", apiKey: "k");

        async Task Assert502Async(string fragment)
        {
            var (status, error) = await host.SendAsync(HttpMethod.Get, $"/api/providers/{id}/remote-models");
            Assert.Equal(HttpStatusCode.BadGateway, status);
            Assert.NotNull(error!["error"]);
            Assert.Contains(fragment, error["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        }

        respond = () => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent("{not json", Encoding.UTF8, "text/plain") };
        await Assert502Async("invalid model list");
        respond = () => JsonResponse("[]"); // a JSON array is not a model-list object
        await Assert502Async("invalid model list");
        respond = () => JsonResponse("""{"data":{"id":"wrong-shape"}}""");
        await Assert502Async("invalid model list");
        respond = () => JsonResponse("""{"data":[17]}"""); // rows must be objects
        await Assert502Async("invalid model list");
        respond = () => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        { Content = new StringContent("boom", Encoding.UTF8, "text/plain") };
        await Assert502Async("Upstream returned HTTP 500");
        respond = () => throw new HttpRequestException("connection refused");
        await Assert502Async("could not connect");
    }

    [Fact]
    public async Task Remote_Models_Timeout_Returns_504_Without_A_Real_Wait()
    {
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromMilliseconds(400))
            .ConfigurePrimaryHttpMessageHandler(() => new HangingHandler()));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", apiKey: "k");

        var (status, body) = await host.SendAsync(HttpMethod.Get, $"/api/providers/{id}/remote-models");
        Assert.Equal(HttpStatusCode.GatewayTimeout, status);
        Assert.Contains("timed out", body!["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Upstream_Error_Echoing_The_Api_Key_Is_Redacted_In_Response_And_Request_Log()
    {
        const string key = "sk-echo-test-key-1234";
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                await Task.CompletedTask;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent("{\"error\":{\"message\":\"Invalid API key provided: " + key + "\"}}", Encoding.UTF8, "application/json") };
            })));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "bearer", key);

        var (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/test", new { modelId = "gpt-5" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(result!["ok"]!.GetValue<bool>());
        Assert.Equal(401, result["httpStatus"]!.GetValue<int>());
        Assert.Contains("[redacted]", result["error"]!.GetValue<string>());
        Assert.DoesNotContain(key, result.ToJsonString());

        await host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var records = await host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery { ProviderId = id });
        var record = records.Items.Single();
        Assert.Equal("upstream_error", record.Status);
        Assert.Equal(401, record.HttpStatus);
        Assert.Contains("[redacted]", record.ErrorMessage);
        Assert.DoesNotContain(key, record.ErrorMessage);
    }

    [Fact]
    public async Task Remote_Models_Upstream_Error_Redacts_Echoed_Credentials_In_The_Json_Error()
    {
        const string bearerKey = "sk-remote-echo-key-5678";
        const string queryKey = "gq+secret=&1";
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                await Task.CompletedTask;
                var message = request.RequestUri!.AbsolutePath.Contains("v1beta", StringComparison.Ordinal)
                    ? "{\"error\":{\"message\":\"API key not valid: " + queryKey + "\"}}"
                    : "{\"error\":{\"message\":\"Invalid API key: " + bearerKey + "\"}}";
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                { Content = new StringContent(message, Encoding.UTF8, "application/json") };
            })));
        var openai = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "bearer", bearerKey);
        var gemini = await CreateCustomAsync(host, "gemini", "https://example.invalid/v1beta", "query-key", queryKey);

        foreach (var id in new[] { openai, gemini })
        {
            var (status, body) = await host.SendAsync(HttpMethod.Get, $"/api/providers/{id}/remote-models");
            Assert.Equal(HttpStatusCode.BadGateway, status);
            var text = body!.ToJsonString();
            Assert.Contains("[redacted]", text);
            Assert.DoesNotContain(bearerKey, text);
            Assert.DoesNotContain(queryKey, text);
            Assert.DoesNotContain(Uri.EscapeDataString(queryKey), text);
        }
    }

    [Fact]
    public async Task Connection_Test_Reports_Failure_When_Upstream_Returns_Invalid_Json()
    {
        await using var host = await TestHost.StartAsync(builder => builder.Services.AddHttpClient(ProviderProbe.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(async request =>
            {
                await Task.CompletedTask;
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("not json at all", Encoding.UTF8, "text/plain") };
            })));
        var id = await CreateCustomAsync(host, "openai-chat", "https://example.invalid/v1", "bearer", "k");

        var (status, result) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/test", new { modelId = "gpt-5" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(result!["ok"]!.GetValue<bool>());
        Assert.Equal(200, result["httpStatus"]!.GetValue<int>());
        Assert.Contains("invalid JSON", result["error"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);

        await host.App.Services.GetRequiredService<UsageWriter>().FlushAsync();
        var records = await host.Db.Requests.QueryAsync(new Astra.Data.Repositories.RequestQuery { ProviderId = id });
        var record = records.Items.Single();
        Assert.Equal("upstream_error", record.Status);
        Assert.Equal(200, record.HttpStatus);
        Assert.Contains("invalid JSON", record.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Template_Update_Keeps_User_Header_And_Setting_Customs_While_Untouched_Fields_Follow()
    {
        await using var host = await TestHost.StartAsync();
        var modified = await Create(host, "anthropic");
        var untouched = await Create(host, "anthropic");

        var (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{modified}", new
        {
            extraHeaders = new Dictionary<string, string> { ["anthropic-version"] = "2099-01-01" },
            settings = new JsonObject { ["auto_cache_control"] = "never" },
        });
        Assert.Equal(HttpStatusCode.OK, status);

        var catalog = host.App.Services.GetRequiredService<ProviderTemplateCatalog>();
        var template = catalog.Get("anthropic")!;
        template.Version = 2;
        template.DefaultHeaders = new Dictionary<string, string> { ["anthropic-version"] = "2024-10-22", ["x-new"] = "v2" };
        template.Settings = new JsonObject { ["auto_cache_control"] = false };

        (status, var a) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{modified}/template-update");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, a!["templateVersion"]!.GetValue<int>());
        Assert.Equal("2099-01-01", a["extraHeaders"]!["anthropic-version"]!.GetValue<string>()); // user value kept
        Assert.Equal("v2", a["extraHeaders"]!["x-new"]!.GetValue<string>());
        Assert.Equal("never", a["settings"]!["auto_cache_control"]!.GetValue<string>());          // user value kept

        (status, var b) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{untouched}/template-update");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, b!["templateVersion"]!.GetValue<int>());
        Assert.Equal("2024-10-22", b["extraHeaders"]!["anthropic-version"]!.GetValue<string>());  // followed the template
        Assert.Equal("v2", b["extraHeaders"]!["x-new"]!.GetValue<string>());
        Assert.False(b["settings"]!["auto_cache_control"]!.GetValue<bool>());                     // followed the template
    }

    [Fact]
    public async Task Template_Update_Appends_New_Default_Models_Without_Duplicates_Or_ReEnabling()
    {
        await using var host = await TestHost.StartAsync();
        var id = await Create(host, "custom-chat"); // template without default models
        var (_, added) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/models",
            new { modelIds = new[] { "user-kept", "gpt-5" } });
        Assert.Equal(2, added!.AsArray().Count);
        var gpt = added.AsArray().Single(m => m!["modelId"]!.GetValue<string>() == "gpt-5")!;
        var (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}/models/{gpt["id"]!.GetValue<long>()}", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, status);

        var catalog = host.App.Services.GetRequiredService<ProviderTemplateCatalog>();
        var template = catalog.Get("custom-chat")!;
        template.Version = 2;
        template.Models = ["gpt-5", "brand-new-default"];

        (status, var body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/template-update");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body!["templateVersion"]!.GetValue<int>());

        var models = (await host.GetJsonAsync($"/api/providers/{id}/models")).AsArray();
        Assert.Equal(["user-kept", "gpt-5", "brand-new-default"], models.Select(m => m!["modelId"]!.GetValue<string>()).ToList());
        Assert.False(models.Single(m => m!["modelId"]!.GetValue<string>() == "gpt-5")!["enabled"]!.GetValue<bool>());
        Assert.True(models.Single(m => m!["modelId"]!.GetValue<string>() == "brand-new-default")!["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Explicit_Empty_PriceKey_Uses_The_Official_System_Default_And_Survives_Every_Write()
    {
        await using var host = await TestHost.StartAsync();

        // an omitted or explicit-null price key keeps the historical meaning: inherit the template default
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        { templateId = "siliconflow", name = "Inherit", apiKey = "sk-test-long-enough", models = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("siliconflow", body!["priceKey"]!.GetValue<string>());
        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new JsonObject
        { ["templateId"] = "siliconflow", ["name"] = "Null key", ["apiKey"] = "sk-test-long-enough", ["priceKey"] = null });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("siliconflow", body!["priceKey"]!.GetValue<string>());

        // an explicit empty price key is a real value: only the system model's official default price
        (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new JsonObject
        {
            ["templateId"] = "siliconflow",
            ["name"] = "Official default",
            ["apiKey"] = "sk-test-long-enough",
            ["priceKey"] = "",
            ["models"] = new JsonArray { "deepseek-v4-flash" },
        });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", body!["priceKey"]!.GetValue<string>());
        var id = body["id"]!.GetValue<string>();
        Assert.Equal("", (await host.Db.Providers.GetAsync(id))!.PriceKey); // the DB keeps the empty string too
        var m = (await host.GetJsonAsync($"/api/providers/{id}/models")).AsArray().Single()!;
        Assert.Equal("deepseek-v4-flash", m["modelId"]!.GetValue<string>());
        Assert.Equal("system_default", m["pricingSource"]!.GetValue<string>());
        Assert.Null(m["priceKey"]);
        Assert.Equal(0.15m, m["effectivePricing"]!["base"]!["input"]!.GetValue<decimal>()); // official, not the SF 0.22

        var (simStatus, sim) = await host.SendAsync(HttpMethod.Post, "/api/billing/simulate", new
        {
            providerId = id,
            modelId = "deepseek-v4-flash",
            usage = new { tokens = new { input = 1_000_000 } },
            requestTimeUtc = "2026-10-04T12:00:00Z", // a Sunday: no Mon-Fri UTC peak window, so the off-peak base price
        });
        Assert.Equal(HttpStatusCode.OK, simStatus);
        Assert.Equal("system_default", sim!["pricingSource"]!.GetValue<string>());
        Assert.Equal(150_000_000L, sim["totalNanos"]!.GetValue<long>()); // 1M × $0.15 / 1M tokens

        // patching other fields, duplicating, and a template update all keep the explicit empty string
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new { name = "Renamed", priceMultiplier = 2 });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", body!["priceKey"]!.GetValue<string>());
        Assert.Equal("", (await host.Db.Providers.GetAsync(id))!.PriceKey);
        (status, var copy) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/duplicate");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", copy!["priceKey"]!.GetValue<string>());
        Assert.Equal("", (await host.Db.Providers.GetAsync(copy["id"]!.GetValue<string>()))!.PriceKey);

        // null restores the historical inherit; the provider price table takes over again
        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new JsonObject { ["priceKey"] = null });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body!["priceKey"]);
        Assert.Null((await host.Db.Providers.GetAsync(id))!.PriceKey);
        m = (await host.GetJsonAsync($"/api/providers/{id}/models")).AsArray().Single()!;
        Assert.Equal("provider_price", m["pricingSource"]!.GetValue<string>());
        Assert.Equal("siliconflow", m["priceKey"]!.GetValue<string>());
        Assert.Equal(0.22m, m["effectivePricing"]!["base"]!["input"]!.GetValue<decimal>());

        (status, body) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new JsonObject { ["priceKey"] = "" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", (await host.Db.Providers.GetAsync(id))!.PriceKey);
        m = (await host.GetJsonAsync($"/api/providers/{id}/models")).AsArray().Single()!;
        Assert.Equal("system_default", m["pricingSource"]!.GetValue<string>());
        Assert.Equal(0.15m, m["effectivePricing"]!["base"]!["input"]!.GetValue<decimal>());

        var catalog = host.App.Services.GetRequiredService<ProviderTemplateCatalog>();
        catalog.Get("siliconflow")!.Version++;
        (status, body) = await host.SendAsync(HttpMethod.Post, $"/api/providers/{id}/template-update");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("", body!["priceKey"]!.GetValue<string>()); // not reset to the template price
        Assert.Equal("", (await host.Db.Providers.GetAsync(id))!.PriceKey);
        Assert.Equal(2, body["templateVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task Provider_Input_Validation_Rejects_Null_Endpoint_Rows_Bad_Protocols_And_Control_Char_Api_Keys()
    {
        await using var host = await TestHost.StartAsync();

        // a null endpoint row is refused on create and leaves nothing behind
        var (status, error) = await host.SendAsync(HttpMethod.Post, "/api/providers", new JsonObject
        {
            ["name"] = "Broken",
            ["endpoints"] = new JsonArray { null },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());

        // a non-string protocol is refused too
        (status, error) = await host.SendAsync(HttpMethod.Post, "/api/providers", new JsonObject
        {
            ["name"] = "Broken",
            ["endpoints"] = new JsonArray { new JsonObject { ["protocol"] = 123, ["baseUrl"] = "https://example.invalid/v1" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());

        // an apiKey with an interior control character is refused, and the valid name of the same body is not saved
        (status, error) = await host.SendAsync(HttpMethod.Post, "/api/providers", new JsonObject
        {
            ["name"] = "Would Have Persisted",
            ["endpoints"] = new JsonArray { new JsonObject { ["protocol"] = "openai-chat", ["baseUrl"] = "https://example.invalid/v1" } },
            ["apiKey"] = "sk-a\u0007b",
        });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        Assert.Empty((await host.GetJsonAsync("/api/providers")).AsArray());

        var id = await Create(host);

        // the same control-character rule applies on PATCH and drops the valid "name" of the same request
        (status, error) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new JsonObject
        { ["name"] = "NewName", ["apiKey"] = "sk-a\u0007b" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.NotNull(error!["error"]);
        var stored = await host.Db.Providers.GetAsync(id);
        Assert.Equal("Test", stored!.Name);
        Assert.Null(stored.ApiKeyEnc);

        // a null endpoint row on PATCH is refused and the original configuration is left completely intact
        (status, _) = await host.SendAsync(HttpMethod.Patch, $"/api/providers/{id}", new JsonObject
        { ["name"] = "NewName", ["endpoints"] = new JsonArray { null } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        stored = await host.Db.Providers.GetAsync(id);
        Assert.Equal("Test", stored!.Name);
        var endpoint = Assert.Single(stored.Endpoints);
        Assert.Equal(ApiProtocol.OpenAIChat, endpoint.Protocol);
        Assert.Equal("https://example.invalid/v1", endpoint.BaseUrl);
    }

    private static async Task<string> Create(TestHost host, string? template = null)
    {
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            templateId = template, name = "Test", apiKey = template is null ? null : "sk-test-long-enough", models = Array.Empty<string>(),
            endpoints = template is null ? new[] { new { protocol = "openai-chat", baseUrl = "https://example.invalid/v1" } } : null,
        });
        Assert.Equal(HttpStatusCode.OK, status);
        return body!["id"]!.GetValue<string>();
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private static async Task<string> CreateCustomAsync(TestHost host, string protocol, string baseUrl,
        string? authScheme = null, string? apiKey = null, bool fullUrl = false)
    {
        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "Probe",
            endpoints = new[] { new { protocol, baseUrl, fullUrl } },
            authScheme,
            apiKey,
            models = Array.Empty<string>(),
        });
        Assert.Equal(HttpStatusCode.OK, status);
        return body!["id"]!.GetValue<string>();
    }

    /// <summary>The values of one upstream request, copied out before the message is disposed.</summary>
    private sealed record Sent(
        HttpMethod Method, string Url, string Path, string Query,
        string? Authorization, string? XApiKey, string? XGoogApiKey, string? AnthropicVersion, string? Body);

    private static async Task<Sent> RecordAsync(HttpRequestMessage request)
    {
        string? Header(string name) => request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
        return new Sent(request.Method, request.RequestUri!.AbsoluteUri, request.RequestUri.AbsolutePath, request.RequestUri.Query,
            Header("Authorization"), Header("x-api-key"), Header("x-goog-api-key"), Header("anthropic-version"),
            request.Content is null ? null : await request.Content.ReadAsStringAsync());
    }

    /// <summary>Hangs until the (short) named-client timeout cancels the request — no real upstream wait.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK); // never reached; the delay is always cancelled
        }
    }
}
