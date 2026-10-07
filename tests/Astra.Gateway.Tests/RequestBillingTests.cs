using Astra.Core.Billing;
using Astra.Core.Requests;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

public class RequestBillingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cache_And_Reasoning_Counters_Are_Recorded_Even_When_Pricing_Is_Missing(bool priced)
    {
        var usage = new NormalizedUsage();
        usage.Add(TokenTypes.Input, 10_000);
        usage.Add(TokenTypes.CacheRead, 90_000);
        usage.Add(TokenTypes.CacheWrite5m, 15_000);
        usage.Add(TokenTypes.CacheWrite1h, 5_000);
        usage.Add(TokenTypes.Output, 2_000);
        usage.Add(TokenTypes.Reasoning, 1_000);
        usage.Calls[TokenTypes.WebSearchCall] = 1;
        var pricing = priced
            ? new ResolvedPricing(new PricingSchedule
            {
                Base = new PriceSet
                {
                    [TokenTypes.Input] = 3m,
                    [TokenTypes.CacheRead] = 0.3m,
                    [TokenTypes.CacheWrite5m] = 3.75m,
                    [TokenTypes.CacheWrite1h] = 6m,
                    [TokenTypes.Output] = 15m,
                },
            }, PricingSource.ProviderPrice, "anthropic", 1m)
            : ResolvedPricing.None;
        var record = new RequestRecord
        {
            Id = "test-cache-usage",
            StartedAtUtc = DateTimeOffset.UtcNow,
            RequestedModel = "claude-sonnet-4-6",
            UsageSource = "reported",
        };

        RequestBilling.Apply(record, usage, pricing, "en");

        Assert.Equal(120_000, record.TotalInputTokens);
        Assert.Equal(3_000, record.TotalOutputTokens);
        Assert.Equal(90_000, record.CacheReadTokens);
        Assert.Equal(20_000, record.CacheWriteTokens);
        Assert.Equal(1_000, record.ReasoningTokens);
        Assert.Equal(15_000, record.UsageItems.Single(i => i.TokenType == TokenTypes.CacheWrite5m).Tokens);
        Assert.Equal(5_000, record.UsageItems.Single(i => i.TokenType == TokenTypes.CacheWrite1h).Tokens);
        Assert.True(record.UsageItems.Single(i => i.TokenType == TokenTypes.WebSearchCall).IsPerCall);
        Assert.Equal(priced ? "provider_price" : "none", record.PricingSource);
        Assert.Equal(priced ? "anthropic" : null, record.PriceKey);
        Assert.Equal(priced, record.CostNanoUsd > 0);
    }

    [Fact]
    public void Missing_Usage_Is_Not_Left_With_Stale_Counters()
    {
        var record = new RequestRecord { UsageSource = "missing", CacheReadTokens = 100, CacheWriteTokens = 50, ReasoningTokens = 25 };

        RequestBilling.Apply(record, null, ResolvedPricing.None, "en");

        Assert.Equal(0, record.TotalInputTokens);
        Assert.Equal(0, record.TotalOutputTokens);
        Assert.Equal(0, record.CacheReadTokens);
        Assert.Equal(0, record.CacheWriteTokens);
        Assert.Equal(0, record.ReasoningTokens);
        Assert.Empty(record.UsageItems);
    }

    [Fact]
    public void Subscription_Requests_Are_Labelled_As_Equivalent_Cost()
    {
        var usage = new NormalizedUsage();
        usage.Add(TokenTypes.Input, 1_000);
        var record = new RequestRecord
        {
            Id = "test-subscription",
            StartedAtUtc = DateTimeOffset.UtcNow,
            RequestedModel = "claude-sonnet-4-6",
            UsageSource = "reported",
        };

        RequestBilling.Apply(record, usage, ResolvedPricing.None, "zh", subscriptionEquivalent: true);

        Assert.StartsWith("订阅额度，等效成本仅供参考｜", record.BillingDescription);
        Assert.Equal("none", record.PricingSource);

        // Regular requests keep the plain description.
        var plain = new RequestRecord { Id = "test-plain", StartedAtUtc = DateTimeOffset.UtcNow, UsageSource = "reported" };
        RequestBilling.Apply(plain, null, ResolvedPricing.None, "zh");
        Assert.DoesNotContain("订阅额度", plain.BillingDescription);
    }
}
