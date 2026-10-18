using System.Net;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Core.Tokens;
using Astra.Gateway.Pipeline;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

public class ProviderUsageApiTests
{
    [Fact]
    public async Task Usage_Is_Per_Account_And_Today_Follows_The_Configured_Time_Zone()
    {
        await using var host = await TestHost.StartAsync();
        await host.SendAsync(HttpMethod.Patch, "/api/settings", new { usageTimeZone = "Asia/Shanghai" });
        await host.Db.Providers.InsertAsync(new Provider { Id = "p", Name = "Subscription", AuthScheme = AuthSchemes.OAuthSubscription });
        foreach (var id in new[] { "oauth", "key", "unused" })
            await host.Db.Accounts.InsertAsync(new ProviderAccount
            {
                Id = id, ProviderId = "p", DisplayName = id, Kind = id == "key" ? AccountKinds.ApiKey : AccountKinds.OAuth,
                AccessTokenEnc = "not-returned",
            });
        var zone = UsagePeriods.Zone("Asia/Shanghai");
        var day = UsagePeriods.LocalDay(DateTimeOffset.UtcNow, zone);
        var start = UsagePeriods.StartOf(day, zone);
        await host.Db.Requests.InsertBatchAsync([
            new() { Id = "previous", ProviderId = "p", AccountId = "oauth", StartedAtUtc = start.AddMilliseconds(-1), InboundProtocol = "openai-chat", TotalInputTokens = 100, CostNanoUsd = 1000 },
            new() { Id = "today", ProviderId = "p", AccountId = "oauth", StartedAtUtc = start, InboundProtocol = "openai-chat", TotalInputTokens = 200, TotalOutputTokens = 50, CacheReadTokens = 100, ReasoningTokens = 20, CostNanoUsd = 2000 },
            new() { Id = "key", ProviderId = "p", AccountId = "key", StartedAtUtc = start, InboundProtocol = "openai-chat", TotalInputTokens = 300, TotalOutputTokens = 60, CostNanoUsd = 3000 },
        ]);
        var body = await host.GetJsonAsync("/api/providers/p/usage");
        Assert.Equal(UsagePeriods.DayKey(day), body["day"]!.GetValue<string>());
        Assert.Equal(zone.Id, body["timeZone"]!.GetValue<string>());
        var accounts = body["accounts"]!.AsArray().ToDictionary(a => a!["accountId"]!.GetValue<string>());
        Assert.Equal(3, accounts.Count);
        Assert.Equal(2, accounts["oauth"]!["total"]!["requests"]!.GetValue<long>());
        Assert.Equal(350, accounts["oauth"]!["total"]!["tokens"]!.GetValue<long>());
        Assert.Equal(3000, accounts["oauth"]!["total"]!["costNanoUsd"]!.GetValue<long>());
        Assert.Equal(1, accounts["oauth"]!["today"]!["requests"]!.GetValue<long>());
        Assert.Equal(250, accounts["oauth"]!["today"]!["tokens"]!.GetValue<long>());
        Assert.Equal(360, accounts["key"]!["total"]!["tokens"]!.GetValue<long>());
        Assert.Equal(0, accounts["unused"]!["total"]!["costNanoUsd"]!.GetValue<long>());
        Assert.DoesNotContain("not-returned", body.ToJsonString());
        Assert.Equal(HttpStatusCode.NotFound, (await host.SendAsync(HttpMethod.Get, "/api/providers/missing/usage")).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gateway_Usage_Uses_The_Linked_System_Models_Pricing_And_Saved_Bill(bool subscription)
    {
        const string response = """{"id":"chat-1","object":"chat.completion","model":"upstream-alias","choices":[{"index":0,"message":{"role":"assistant","content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1000,"completion_tokens":100,"prompt_tokens_details":{"cached_tokens":600},"completion_tokens_details":{"reasoning_tokens":40}}}""";
        await using var gw = await GatewayFixture.StartAsync(ClientKinds.OpenCode, ApiProtocol.OpenAIChat,
            _ => FakeUpstream.Json(response), authScheme: subscription ? AuthSchemes.OAuthSubscription : AuthSchemes.Bearer);
        gw.Provider.PriceKey = ""; // use the system model's default pricing
        gw.Provider.Settings = JsonNode.Parse("""{"subscription":{"client_policy":"any"}}""")!.AsObject();
        await gw.Host.Db.Providers.UpdateAsync(gw.Provider);
        var model = new SystemModel
        {
            Id = "usage-system-model", DisplayName = "Usage", Vendor = "test",
            Pricing = new PricingSchedule { Base = new PriceSet { [TokenTypes.Input] = 2m, [TokenTypes.CacheRead] = 0.25m, [TokenTypes.Output] = 8m } },
        };
        await gw.Host.Db.Models.InsertAsync(model);
        await gw.Host.Db.Providers.InsertModelAsync(new ProviderModel { ProviderId = gw.Provider.Id, ModelId = "upstream-alias", SystemModelId = model.Id });
        if (subscription)
            await gw.Host.Db.Accounts.InsertAsync(new ProviderAccount
            {
                Id = "usage-account", ProviderId = gw.Provider.Id, DisplayName = "Subscription",
                AccessTokenEnc = gw.Host.App.Services.GetRequiredService<ISecretProtector>().Protect("account-secret"),
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(2),
            });

        var record = await gw.RecordOfAsync(await gw.PostAsync("/v1/chat/completions",
            """{"model":"upstream-alias","messages":[{"role":"user","content":"hi"}]}"""));
        Assert.Equal(RequestStatus.Success, record.Status);
        Assert.Equal("system_default", record.PricingSource);
        Assert.Equal(1_750_000, record.CostNanoUsd); // 400×$2/M + 600×$0.25/M + 100×$8/M
        var usage = Assert.Single((await gw.Host.GetJsonAsync($"/api/providers/{gw.Provider.Id}/usage"))["accounts"]!.AsArray())!;
        Assert.Equal(subscription ? "usage-account" : null, usage["accountId"]?.GetValue<string>());
        Assert.Equal(1, usage["total"]!["requests"]!.GetValue<long>());
        Assert.Equal(1100, usage["total"]!["tokens"]!.GetValue<long>());
        Assert.Equal(record.CostNanoUsd, usage["total"]!["costNanoUsd"]!.GetValue<long>());

        // Editing catalog prices later cannot rewrite an account's past spend.
        model.Pricing.Base[TokenTypes.Input] = 99m;
        await gw.Host.Db.Models.UpdateAsync(model);
        var after = Assert.Single((await gw.Host.GetJsonAsync($"/api/providers/{gw.Provider.Id}/usage"))["accounts"]!.AsArray())!;
        Assert.Equal(record.CostNanoUsd, after["total"]!["costNanoUsd"]!.GetValue<long>());
    }
}
