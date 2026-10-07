using Dapper;
using Astra.Core;
using Astra.Core.Models;
using Astra.Core.Seed;
using Astra.Data.Tests;

namespace Astra.Data;

public class SeedImportTests
{
    [Fact]
    public async Task First_Import_Inserts_All_Seed_Models_And_Prices()
    {
        var db = await TestDb.InitializeAsync();
        var seed = SeedCatalog.Current;
        Assert.True(seed.Models.Count > 0); // sanity: the embedded catalog is non-empty

        var result = await db.Seed.ImportAsync();

        Assert.False(result.Skipped);
        Assert.Equal(seed.Models.Count, result.InsertedModels);
        Assert.Equal(0, result.UpdatedModels);
        Assert.Equal(0, result.SkippedModels);
        Assert.Equal(seed.Models.Sum(m => m.ProviderPricing.Count), result.UpsertedPrices);
        Assert.Equal(seed.SeedVersion, await db.Settings.GetAsync<int?>(SeedImporter.SeedVersionKey));
        Assert.Equal(seed.Models.Count, (await db.Models.ListAsync()).Count);
    }

    [Fact]
    public async Task Reimport_Without_Changes_Does_No_Work()
    {
        var db = await TestDb.InitializeAsync();
        await db.Seed.ImportAsync();

        var again = await db.Seed.ImportAsync();

        Assert.True(again.Skipped);
        Assert.Equal(0, again.InsertedModels);
        Assert.Equal(0, again.UpdatedModels);
        Assert.Equal(0, again.UpsertedPrices);
    }

    [Fact]
    public async Task Force_Reimport_Preserves_User_Modified_Field_And_Restores_Others()
    {
        var db = await TestDb.InitializeAsync();
        await db.Seed.ImportAsync();
        var target = SeedCatalog.Current.Models[0];

        // Simulate drift + a user-modified field: display_name is protected, pricing must be restored.
        await using (var conn = await db.Factory.OpenAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE models SET display_name = @name, pricing_json = @pricing, user_modified_fields_json = '[\"display_name\"]' WHERE id = @id",
                new { id = target.Id, name = "My Custom Name", pricing = "{\"base\":{\"input\":42}}" });
        }

        var result = await db.Seed.ImportAsync(force: true);

        Assert.False(result.Skipped);
        Assert.Equal(0, result.InsertedModels);
        Assert.Equal(SeedCatalog.Current.Models.Count, result.UpdatedModels);

        var model = (await db.Models.GetAsync(target.Id))!;
        Assert.Equal("My Custom Name", model.DisplayName); // preserved
        Assert.Contains("display_name", model.UserModifiedFields);
        Assert.Equal("seed", model.Source);
        Assert.NotEqual(42m, model.Pricing!.Base["input"]); // restored from seed
        if (target.Pricing is not null)
            Assert.Equal(target.Pricing.Base["input"], model.Pricing.Base["input"]);
    }

    [Fact]
    public async Task Force_Reimport_Preserves_User_Modified_Model_Price()
    {
        var db = await TestDb.InitializeAsync();
        await db.Seed.ImportAsync();
        var target = SeedCatalog.Current.Models.First(m => m.ProviderPricing.Count > 0);
        var key = target.ProviderPricing.Keys.First();

        await using (var conn = await db.Factory.OpenAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE model_prices SET user_modified = 1, pricing_json = '{\"base\":{\"input\":999}}' WHERE model_id = @id AND price_key = @key",
                new { id = target.Id, key });
        }

        var result = await db.Seed.ImportAsync(force: true);
        Assert.Equal(1, result.SkippedPrices);

        var price = (await db.Models.GetPriceAsync(target.Id, key))!;
        Assert.True(price.UserModified);
        Assert.Equal(999m, price.Pricing.Base["input"]);

        // A non-modified price of another key/row does get refreshed (still source=seed).
        var other = SeedCatalog.Current.Models.First(m =>
            m.Id != target.Id && m.ProviderPricing.Count > 0);
        var refreshed = await db.Models.GetPriceAsync(other.Id, other.ProviderPricing.Keys.First());
        Assert.NotNull(refreshed);
        Assert.Equal("seed", refreshed!.Source);
    }

    [Fact]
    public async Task Force_Reimport_Never_Touches_User_Models()
    {
        var db = await TestDb.InitializeAsync();
        await db.Seed.ImportAsync();
        var target = SeedCatalog.Current.Models[0];

        await using (var conn = await db.Factory.OpenAsync())
        {
            await conn.ExecuteAsync(
                "UPDATE models SET source = 'user', display_name = 'Mine' WHERE id = @id", new { id = target.Id });
        }

        var result = await db.Seed.ImportAsync(force: true);

        Assert.Equal(1, result.SkippedModels);
        var model = (await db.Models.GetAsync(target.Id))!;
        Assert.Equal("user", model.Source);
        Assert.Equal("Mine", model.DisplayName);
    }
}
