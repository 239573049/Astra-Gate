using Astra.Core.Models;
using Astra.Gateway.Pipeline;

namespace Astra.Gateway.Tests;

public sealed class EffectiveModelResolverTests
{
    [Fact]
    public async Task Billing_reads_the_current_catalog_after_capability_discovery()
    {
        var store = await RoutingStore.StartAsync("configured");
        await store.Db.Models.InsertAsync(new SystemModel { Id = "catalog-model", DisplayName = "Before", ContextWindow = 1000 });
        var configured = (await store.Db.Providers.GetModelAsync(store.Provider.Id, "configured"))!;
        configured.SystemModelId = "catalog-model";
        await store.Db.Providers.UpdateModelAsync(configured);
        var resolver = new EffectiveModelResolver(store.Db);
        var identity = await resolver.ResolveProviderModelAsync(store.Provider, "configured");

        // Discovery can await the network. Do not cache an effective model built before that await.
        var system = (await store.Db.Models.GetAsync("catalog-model"))!;
        system.ContextWindow = 2000;
        await store.Db.Models.UpdateAsync(system);
        Assert.Equal(2000, (await resolver.ResolveAsync(store.Provider, identity)).ContextWindow);
    }

    [Fact]
    public async Task Unlisted_models_still_auto_link_and_a_new_provider_row_replaces_the_fallback()
    {
        var store = await RoutingStore.StartAsync();
        await store.Db.Models.InsertAsync(new SystemModel { Id = "catalog-model", Aliases = ["unlisted"], ContextWindow = 1000 });
        var resolver = new EffectiveModelResolver(store.Db);
        var (_, effective) = await resolver.ResolveByModelIdAsync(store.Provider, "unlisted");
        Assert.Equal("catalog-model", effective.SystemModelId);
        Assert.Equal(1000, effective.ContextWindow);
        await store.Db.Providers.InsertModelAsync(new ProviderModel
        {
            ProviderId = store.Provider.Id, ModelId = "unlisted", SystemModelId = "catalog-model",
            Overrides = new ModelOverrides { ContextWindow = 3000 },
        });
        Assert.Equal(3000, (await resolver.ResolveByModelIdAsync(store.Provider, "unlisted")).Effective.ContextWindow);
        Assert.Null((await resolver.ResolveByModelIdAsync(store.Provider, "unknown")).Effective.SystemModelId);
    }
}
