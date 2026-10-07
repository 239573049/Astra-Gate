using Dapper;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Models;
using Astra.Core.Seed;

namespace Astra.Data.Tests;

internal static class TestSamples
{
    public static SystemModel SimpleModel(string id, string displayName = "Test Model", string vendor = "tester") => new()
    {
        Id = id,
        DisplayName = displayName,
        Vendor = vendor,
        Family = "test",
        Aliases = ["tm1", "test-one"],
        ContextWindow = 128000,
        MaxOutputTokens = 16384,
        Capabilities = new ModelCapabilities { Vision = true, Tools = true },
        Pricing = SimplePricing(),
        Source = "user",
        Enabled = true,
    };

    public static PricingSchedule SimplePricing() => new()
    {
        Currency = "USD",
        Unit = "per_1m_tokens",
        Base = new PriceSet
        {
            Tokens = new Dictionary<string, decimal?> { ["input"] = 3m, ["output"] = 15m },
        },
    };

    /// <summary>Pricing covering service tiers, context tiers and a cross-midnight time window.</summary>
    public static PricingSchedule RichPricing() => new()
    {
        Currency = "USD",
        Unit = "per_1m_tokens",
        Base = new PriceSet
        {
            Tokens = new Dictionary<string, decimal?>
            {
                ["input"] = 3m,
                ["output"] = 15m,
                ["cache_read"] = 0.3m,
                ["cache_write_5m"] = 3.75m,
                ["cache_write_1h"] = 6m,
                ["reasoning"] = null,
            },
            PerCall = new Dictionary<string, decimal> { ["web_search_call"] = 0.01m },
        },
        ServiceTiers = new Dictionary<string, ServiceTierRule>
        {
            ["flex"] = new ServiceTierRule { Multiplier = 0.5m },
            ["priority"] = new ServiceTierRule
            {
                Prices = new PriceSet
                {
                    Tokens = new Dictionary<string, decimal?> { ["input"] = 5m, ["output"] = 25m },
                },
            },
        },
        ContextTiers =
        [
            new ContextTier
            {
                Name = ">200K",
                ThresholdInputTokens = 200000,
                Mode = "whole",
                OutputPolicy = "highest",
                Prices = new PriceSet
                {
                    Tokens = new Dictionary<string, decimal?> { ["input"] = 6m, ["output"] = 22.5m, ["cache_read"] = 0.6m },
                },
            },
        ],
        TimeWindows =
        [
            new TimeWindowRule
            {
                Name = "off-peak",
                Timezone = "Asia/Shanghai",
                Start = "00:30",
                End = "08:30",
                Multiplier = 0.5m,
            },
        ],
        PerRequest = 0m,
        Notes = "round-trip sample",
    };

    /// <summary>Inserts two providers (p1 "Alpha", p2 "Beta") into a fresh migrated database.</summary>
    public static async Task<AstraDatabase> DbWithProvidersAsync()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Alpha", Category = "cn", PriceKey = "alpha" });
        await db.Providers.InsertAsync(new Provider { Id = "p2", Name = "Beta", Category = "cn", PriceKey = "beta" });
        return db;
    }
}
