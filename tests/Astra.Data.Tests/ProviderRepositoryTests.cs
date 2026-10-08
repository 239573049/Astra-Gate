using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Models;
using Astra.Data.Tests;

namespace Astra.Data;

public class ProviderRepositoryTests
{
    [Fact]
    public async Task Provider_Round_Trip_Keeps_All_Columns()
    {
        var db = await TestDb.InitializeAsync();
        var provider = new Provider
        {
            Id = "p1",
            TemplateId = "deepseek",
            PriceKey = "deepseek",
            TemplateVersion = 3,
            TemplateSnapshotJson = "{\"id\":\"deepseek\"}",
            Name = "DeepSeek",
            Icon = "deepseek",
            Category = "cn",
            Endpoints =
            [
                new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://api.deepseek.com/v1" },
                new ProviderEndpoint { Protocol = ApiProtocol.Anthropic, BaseUrl = "https://api.deepseek.com/anthropic", FullUrl = true },
            ],
            PreferredUpstreamProtocols = [ApiProtocol.OpenAIChat, ApiProtocol.Anthropic],
            AuthScheme = AuthSchemes.Bearer,
            ApiKeyEnc = "protected::key",
            ExtraHeaders = new Dictionary<string, string> { ["HTTP-Referer"] = "https://astra.dev" },
            PriceMultiplier = 1.25m,
            AdapterId = "deepseek",
            Settings = new JsonObject { ["auto_cache_control"] = true },
            Enabled = true,
            SortOrder = 2,
            Notes = "primary",
            Website = "https://platform.deepseek.com",
        };

        await db.Providers.InsertAsync(provider);
        var got = (await db.Providers.GetAsync("p1"))!;

        Assert.Equal("deepseek", got.TemplateId);
        Assert.Equal("deepseek", got.PriceKey);
        Assert.Equal(3, got.TemplateVersion);
        Assert.Equal("{\"id\":\"deepseek\"}", got.TemplateSnapshotJson);
        Assert.Equal("DeepSeek", got.Name);
        Assert.Equal("cn", got.Category);
        Assert.Equal(2, got.Endpoints.Count);
        Assert.Equal(ApiProtocol.OpenAIChat, got.Endpoints[0].Protocol);
        Assert.Equal("https://api.deepseek.com/v1", got.Endpoints[0].BaseUrl);
        Assert.False(got.Endpoints[0].FullUrl);
        Assert.True(got.Endpoints[1].FullUrl);
        Assert.Equal([ApiProtocol.OpenAIChat, ApiProtocol.Anthropic], got.PreferredUpstreamProtocols);
        Assert.Equal(AuthSchemes.Bearer, got.AuthScheme);
        Assert.Equal("protected::key", got.ApiKeyEnc);
        Assert.Equal("https://astra.dev", got.ExtraHeaders["HTTP-Referer"]);
        Assert.Equal(1.25m, got.PriceMultiplier);
        Assert.Equal("deepseek", got.AdapterId);
        Assert.True(got.SettingFlag("auto_cache_control", defaultValue: false));
        Assert.True(got.Enabled);
        Assert.Equal(2, got.SortOrder);
        Assert.Equal("primary", got.Notes);
        Assert.Equal("https://platform.deepseek.com", got.Website);
        Assert.NotEqual(default, got.CreatedAt);
        Assert.NotEqual(default, got.UpdatedAt);

        // Update.
        got.Name = "DeepSeek Intl";
        got.PriceMultiplier = 0.8m;
        got.Enabled = false;
        got.PriceKey = "deepseek-intl";
        await db.Providers.UpdateAsync(got);
        var updated = (await db.Providers.GetAsync("p1"))!;
        Assert.Equal("DeepSeek Intl", updated.Name);
        Assert.Equal(0.8m, updated.PriceMultiplier);
        Assert.False(updated.Enabled);
        Assert.Equal("deepseek-intl", updated.PriceKey);
        Assert.True(await db.Providers.DeleteAsync("p1"));
        Assert.Null(await db.Providers.GetAsync("p1"));
    }

    [Fact]
    public async Task Provider_Models_Crud_Reorder_And_Cascade()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Alpha" });

        var overrides = new ModelOverrides
        {
            DisplayName = "Renamed upstream",
            ContextWindow = 64000,
            Pricing = TestSamples.RichPricing(),
        };
        var a = new ProviderModel { ProviderId = "p1", ModelId = "alpha-a", SystemModelId = null, SortOrder = 0 };
        var b = new ProviderModel { ProviderId = "p1", ModelId = "alpha-b", SystemModelId = "m1", Overrides = overrides, SortOrder = 1 };
        var c = new ProviderModel { ProviderId = "p1", ModelId = "alpha-c", Enabled = false, SortOrder = 2 };
        await db.Providers.InsertModelAsync(a);
        await db.Providers.InsertModelAsync(b);
        await db.Providers.InsertModelAsync(c);
        Assert.All([a, b, c], m => Assert.True(m.Id > 0));

        var fetched = (await db.Providers.GetModelAsync("p1", "alpha-b"))!;
        Assert.Equal("m1", fetched.SystemModelId);
        Assert.Equal("Renamed upstream", fetched.Overrides.DisplayName);
        Assert.Equal(64000, fetched.Overrides.ContextWindow);
        Assert.Equal("per_1m_tokens", fetched.Overrides.Pricing!.Unit);
        Assert.True(fetched.Overrides.Pricing.TimeWindows![0].Multiplier == 0.5m);

        var byId = (await db.Providers.GetModelByIdAsync(c.Id))!;
        Assert.False(byId.Enabled);

        // Reorder: c, a, b.
        await db.Providers.ReorderModelsAsync("p1", [c.Id, a.Id, b.Id]);
        var ordered = await db.Providers.ListModelsAsync("p1");
        Assert.Equal(["alpha-c", "alpha-a", "alpha-b"], ordered.Select(m => m.ModelId).ToList());

        // Update.
        a.SystemModelId = "m2";
        a.Enabled = false;
        Assert.True(await db.Providers.UpdateModelAsync(a));
        var updatedA = (await db.Providers.GetModelAsync("p1", "alpha-a"))!;
        Assert.Equal("m2", updatedA.SystemModelId);
        Assert.False(updatedA.Enabled);

        // Delete one, then cascade via provider delete.
        Assert.True(await db.Providers.DeleteModelAsync(c.Id));
        Assert.Null(await db.Providers.GetModelAsync("p1", "alpha-c"));
        Assert.True(await db.Providers.DeleteAsync("p1"));
        Assert.Empty(await db.Providers.ListModelsAsync("p1"));
    }

    [Fact]
    public async Task Quota_Snapshot_Is_Written_Alone_And_Survives_Full_Updates()
    {
        var db = await TestDb.InitializeAsync();
        var stamp = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var provider = new Provider
        {
            Id = "pq",
            Name = "Quota",
            Endpoints = [new ProviderEndpoint { Protocol = ApiProtocol.OpenAIChat, BaseUrl = "https://api.example.com/v1" }],
            CreatedAt = stamp,
            UpdatedAt = stamp,
        };
        await db.Providers.InsertAsync(provider);
        Assert.Null((await db.Providers.GetAsync("pq"))!.Quota);

        var checkedAt = new DateTimeOffset(2026, 10, 8, 9, 30, 0, 123, TimeSpan.Zero);
        var snapshot = new JsonObject { ["fetchedAtUtc"] = "2026-10-08T09:30:00Z", ["plans"] = new JsonArray(new JsonObject { ["remaining"] = 12.5 }) };
        Assert.True(await db.Providers.UpdateQuotaAsync("pq", snapshot, checkedAt));
        Assert.False(await db.Providers.UpdateQuotaAsync("missing", snapshot, checkedAt));

        var got = (await db.Providers.GetAsync("pq"))!;
        Assert.Equal(checkedAt, got.QuotaCheckedAtUtc);
        Assert.Equal(12.5, got.Quota!["plans"]![0]!["remaining"]!.GetValue<double>());
        // The background refresh must not look like a user edit.
        Assert.Equal(stamp, got.UpdatedAt);

        // A full-row update (settings edit) leaves the snapshot alone.
        got.Name = "Renamed";
        got.Quota = null;
        await db.Providers.UpdateAsync(got);
        var after = (await db.Providers.GetAsync("pq"))!;
        Assert.Equal("Renamed", after.Name);
        Assert.NotNull(after.Quota);
        Assert.Equal(checkedAt, after.QuotaCheckedAtUtc);
    }
}
