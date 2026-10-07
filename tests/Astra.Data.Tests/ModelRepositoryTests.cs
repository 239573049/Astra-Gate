using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Models;
using Astra.Data.Tests;

namespace Astra.Data;

public class ModelRepositoryTests
{
    [Fact]
    public async Task Model_Crud_Search_And_Round_Trip()
    {
        var db = await TestDb.InitializeAsync();
        var model = TestSamples.SimpleModel("test-model-1");
        await db.Models.InsertAsync(model);

        var fetched = await db.Models.GetAsync("test-model-1");
        Assert.NotNull(fetched);
        Assert.Equal("Test Model", fetched!.DisplayName);
        Assert.Equal("tester", fetched.Vendor);
        Assert.Equal("test", fetched.Family);
        Assert.Equal(["tm1", "test-one"], fetched.Aliases);
        Assert.Equal(128000, fetched.ContextWindow);
        Assert.Equal(16384, fetched.MaxOutputTokens);
        Assert.True(fetched.Capabilities.Vision);
        Assert.True(fetched.Capabilities.Tools);
        Assert.Null(fetched.Capabilities.Audio);
        Assert.Equal("user", fetched.Source);
        Assert.True(fetched.Enabled);
        Assert.NotEqual(default, fetched.CreatedAt);
        Assert.NotEqual(default, fetched.UpdatedAt);

        // list + search across id, alias, vendor
        Assert.Contains(await db.Models.ListAsync(), m => m.Id == "test-model-1");
        Assert.Contains(await db.Models.ListAsync("test-one"), m => m.Id == "test-model-1");
        Assert.Contains(await db.Models.ListAsync("TESTER"), m => m.Id == "test-model-1");
        Assert.DoesNotContain(await db.Models.ListAsync("nope"), m => m.Id == "test-model-1");

        // full update
        fetched.DisplayName = "Renamed";
        fetched.Enabled = false;
        fetched.Aliases = [];
        fetched.ContextWindow = 200000;
        await db.Models.UpdateAsync(fetched);
        var updated = await db.Models.GetAsync("test-model-1");
        Assert.Equal("Renamed", updated!.DisplayName);
        Assert.False(updated.Enabled);
        Assert.Empty(updated.Aliases);
        Assert.Equal(200000, updated.ContextWindow);

        // delete
        Assert.True(await db.Models.DeleteAsync("test-model-1"));
        Assert.Null(await db.Models.GetAsync("test-model-1"));
        Assert.False(await db.Models.DeleteAsync("test-model-1"));
    }

    [Fact]
    public async Task Price_Table_Round_Trip_With_Time_Windows_And_Tiers()
    {
        var db = await TestDb.InitializeAsync();
        await db.Models.InsertAsync(TestSamples.SimpleModel("m1"));

        var schedule = TestSamples.RichPricing();
        await db.Models.UpsertPriceAsync(new ModelPrice
        {
            ModelId = "m1",
            PriceKey = "siliconflow",
            UpstreamModelId = "vendor/m1-pro",
            Pricing = schedule,
            Source = "user",
        });

        var table = await db.Models.GetPriceTableAsync("m1");
        Assert.Single(table);
        Assert.Equal(Json.Serialize(schedule), Json.Serialize(table["siliconflow"]));

        var price = (await db.Models.GetPriceAsync("m1", "siliconflow"))!;
        Assert.True(price.Id > 0);
        Assert.Equal("vendor/m1-pro", price.UpstreamModelId);
        Assert.False(price.UserModified);
        Assert.NotEqual(default, price.UpdatedAt);
        Assert.Equal("00:30", price.Pricing.TimeWindows![0].Start);
        Assert.Equal("Asia/Shanghai", price.Pricing.TimeWindows[0].Timezone);
        Assert.Equal(200000, price.Pricing.ContextTiers![0].ThresholdInputTokens);
        Assert.Equal("priority", price.Pricing.ServiceTiers!.Keys.Last());

        // Upsert replaces the same row (id unchanged).
        price.Pricing.Base["output"] = 99m;
        await db.Models.UpsertPriceAsync(price);
        var again = (await db.Models.GetPriceAsync("m1", "siliconflow"))!;
        Assert.Equal(99m, again.Pricing.Base["output"]);
        Assert.Equal(price.Id, again.Id);
        Assert.Single(await db.Models.ListPricesAsync("m1"));
    }

    [Fact]
    public async Task Upstream_Id_Index_And_Matcher_Candidates()
    {
        var db = await TestDb.InitializeAsync();
        var model = TestSamples.SimpleModel("m1");
        await db.Models.InsertAsync(model);
        await db.Models.UpsertPriceAsync(new ModelPrice
        {
            ModelId = "m1",
            PriceKey = "openrouter",
            UpstreamModelId = "tester/m1:free",
            Pricing = TestSamples.SimplePricing(),
            Source = "user",
        });
        await db.Models.UpsertPriceAsync(new ModelPrice
        {
            ModelId = "m1",
            PriceKey = "siliconflow",
            UpstreamModelId = null, // must be excluded from the index
            Pricing = TestSamples.SimplePricing(),
            Source = "user",
        });

        var index = await db.Models.GetUpstreamIdIndexAsync("openrouter");
        Assert.Equal("m1", index["tester/m1:free"]);
        Assert.Empty(await db.Models.GetUpstreamIdIndexAsync("unknown-key"));

        // Disabled models are not matcher candidates.
        var candidates = await db.Models.GetMatcherCandidatesAsync();
        var candidate = Assert.Single(candidates);
        Assert.Equal("m1", candidate.SystemModelId);
        Assert.Contains("test-one", candidate.Aliases);

        model.Enabled = false;
        await db.Models.UpdateAsync(model);
        Assert.Empty(await db.Models.GetMatcherCandidatesAsync());
    }

    [Fact]
    public async Task Deleting_Model_Cascades_Prices()
    {
        var db = await TestDb.InitializeAsync();
        await db.Models.InsertAsync(TestSamples.SimpleModel("m1"));
        await db.Models.UpsertPriceAsync(new ModelPrice
        {
            ModelId = "m1",
            PriceKey = "deepseek",
            Pricing = TestSamples.SimplePricing(),
            Source = "user",
        });

        await db.Models.DeleteAsync("m1");
        Assert.Empty(await db.Models.ListPricesAsync("m1"));
        Assert.Empty(await db.Models.GetPriceTableAsync("m1"));
    }
}
