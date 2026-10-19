using Dapper;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Core.Requests;
using Astra.Core.Tokens;
using Astra.Data.Tests;

namespace Astra.Data;

/// <summary>
/// The cached reads the gateway uses on every request (RowCache): a committed write is visible to the very next read,
/// and what a read hands out is the caller's own copy.
/// </summary>
public class RowCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_token_change_is_visible_to_the_next_lookup()
    {
        var db = await TestDb.InitializeAsync();
        await db.Tokens.InsertAsync(new TokenRecord { Id = "t1", Name = "Team", KeyPrefix = "sk-astra-abc", KeyHash = "h", CreatedAt = Now, UpdatedAt = Now });
        var first = Assert.Single(await db.Tokens.FindByPrefixAsync("sk-astra-abc"));
        Assert.True(first.Enabled);

        first.Name = "changed by the caller";
        Assert.Equal("Team", Assert.Single(await db.Tokens.FindByPrefixAsync("sk-astra-abc")).Name);

        var stored = (await db.Tokens.GetAsync("t1"))!;
        stored.Enabled = false;
        Assert.True(await db.Tokens.UpdateAsync(stored));
        Assert.False(Assert.Single(await db.Tokens.FindByPrefixAsync("sk-astra-abc")).Enabled);

        Assert.True(await db.Tokens.DeleteAsync("t1"));
        Assert.Empty(await db.Tokens.FindByPrefixAsync("sk-astra-abc"));
    }

    [Fact]
    public async Task A_provider_and_its_model_change_are_visible_to_the_next_read()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Alpha", Category = "custom", AuthScheme = AuthSchemes.Bearer, Enabled = true });
        await db.Providers.InsertModelAsync(new ProviderModel { ProviderId = "p1", ModelId = "m", Enabled = true });

        var provider = (await db.Providers.GetAsync("p1"))!;
        var model = (await db.Providers.GetModelAsync("p1", "m"))!;
        provider.Name = "changed by the caller";
        Assert.Equal("Alpha", (await db.Providers.GetAsync("p1"))!.Name);

        provider.Name = "Beta";
        provider.Enabled = false;
        Assert.True(await db.Providers.UpdateAsync(provider));
        var reread = (await db.Providers.GetAsync("p1"))!;
        Assert.Equal("Beta", reread.Name);
        Assert.False(reread.Enabled);

        Assert.True(Assert.Single(await db.Providers.ListModelsAsync("p1")).Enabled);
        model.Enabled = false;
        Assert.True(await db.Providers.UpdateModelAsync(model));
        Assert.False((await db.Providers.GetModelAsync("p1", "m"))!.Enabled);
        Assert.False(Assert.Single(await db.Providers.ListModelsAsync("p1")).Enabled);

        Assert.True(await db.Providers.DeleteAsync("p1"));
        Assert.Null(await db.Providers.GetAsync("p1"));
        Assert.Null(await db.Providers.GetModelAsync("p1", "m"));
    }

    [Fact]
    public async Task Model_prices_and_link_candidates_follow_writes()
    {
        var db = await TestDb.InitializeAsync();
        await db.Models.InsertAsync(TestSamples.SimpleModel("m1"));
        Assert.Empty(await db.Models.GetPriceTableAsync("m1"));
        Assert.Empty(await db.Models.GetUpstreamIdIndexAsync("vendor"));
        Assert.Contains(await db.Models.GetMatcherCandidatesAsync(), c => c.SystemModelId == "m1");

        await db.Models.UpsertPriceAsync(new ModelPrice
        {
            ModelId = "m1", PriceKey = "vendor", UpstreamModelId = "vendor/m1", Pricing = TestSamples.SimplePricing(), Source = "user",
        });
        Assert.Single(await db.Models.GetPriceTableAsync("m1"));
        Assert.Equal("m1", (await db.Models.GetUpstreamIdIndexAsync("vendor"))["vendor/m1"]);

        await db.Models.InsertAsync(TestSamples.SimpleModel("m2"));
        Assert.Contains(await db.Models.GetMatcherCandidatesAsync(), c => c.SystemModelId == "m2");

        var m1 = (await db.Models.GetAsync("m1"))!;
        m1.DisplayName = "Renamed";
        Assert.True(await db.Models.UpdateAsync(m1));
        Assert.Equal("Renamed", (await db.Models.GetAsync("m1"))!.DisplayName);
    }

    [Fact]
    public async Task A_client_and_its_bindings_follow_writes_and_are_copies()
    {
        var db = await TestDb.InitializeAsync();
        foreach (var id in new[] { "p1", "p2", "p3" })
            await db.Providers.InsertAsync(new Provider { Id = id, Name = id, Category = "custom", AuthScheme = AuthSchemes.Bearer, Enabled = true });
        await db.Clients.UpsertAsync(new ClientRecord { Kind = ClientKinds.Codex, Enabled = true, TokenId = "default" });
        await db.Clients.ReplaceBindingsAsync(ClientKinds.Codex, [new ClientBinding { ProviderId = "p1" }]);

        var client = (await db.Clients.GetAsync(ClientKinds.Codex))!;
        var bindings = await db.Clients.ListBindingsAsync(ClientKinds.Codex);
        client.Enabled = false;
        bindings[0].ProviderId = "changed by the caller";
        Assert.True((await db.Clients.GetAsync(ClientKinds.Codex))!.Enabled);
        Assert.Equal("p1", Assert.Single(await db.Clients.ListBindingsAsync(ClientKinds.Codex)).ProviderId);

        await db.Clients.UpsertAsync(new ClientRecord { Kind = ClientKinds.Codex, Enabled = false, TokenId = "default" });
        await db.Clients.ReplaceBindingsAsync(ClientKinds.Codex, [new ClientBinding { ProviderId = "p2" }, new ClientBinding { ProviderId = "p3" }]);
        Assert.False((await db.Clients.GetAsync(ClientKinds.Codex))!.Enabled);
        Assert.Equal(["p2", "p3"], (await db.Clients.ListBindingsAsync(ClientKinds.Codex)).Select(b => b.ProviderId));
    }

    [Fact]
    public async Task A_second_database_instance_over_the_same_file_invalidates_the_first()
    {
        // restore-all builds its own AstraDatabase over the file a running host serves.
        var paths = TestDb.NewPaths(out _);
        var host = await AstraDatabase.InitializeAsync(paths, importSeed: false);
        var offline = await AstraDatabase.InitializeAsync(paths, importSeed: false);
        await host.Clients.UpsertAsync(new ClientRecord { Kind = ClientKinds.Codex, Enabled = true });
        Assert.True((await host.Clients.GetAsync(ClientKinds.Codex))!.Enabled);

        await offline.Clients.UpsertAsync(new ClientRecord { Kind = ClientKinds.Codex, Enabled = false });
        Assert.False((await host.Clients.GetAsync(ClientKinds.Codex))!.Enabled);
    }

    [Fact]
    public async Task Model_match_indexes_follow_catalog_writes_without_mutating_old_snapshots()
    {
        var db = await TestDb.InitializeAsync();
        var empty = await db.Models.GetMatcherIndexAsync();
        Assert.Null(empty.Match("alias"));
        var model = TestSamples.SimpleModel("m1");
        model.Aliases = ["alias"];
        await db.Models.InsertAsync(model);
        var populated = await db.Models.GetMatcherIndexAsync();
        Assert.Equal("m1", populated.Match("ALIAS"));
        Assert.Null(empty.Match("alias"));
        model.Aliases = ["new-alias"];
        await db.Models.UpdateAsync(model);
        Assert.Null((await db.Models.GetMatcherIndexAsync()).Match("alias"));
        Assert.Equal("m1", (await db.Models.GetMatcherIndexAsync()).Match("new-alias"));
        Assert.Equal("m1", populated.Match("alias"));
        model.Enabled = false;
        await db.Models.UpdateAsync(model);
        Assert.Null((await db.Models.GetMatcherIndexAsync()).Match("new-alias"));
    }

    [Fact]
    public async Task A_cached_missing_provider_model_is_invalidated_by_insert_and_delete()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Alpha" });
        Assert.Null(await db.Providers.GetModelAsync("p1", "m1"));
        await db.Providers.InsertModelAsync(new ProviderModel { ProviderId = "p1", ModelId = "m1" });
        Assert.NotNull(await db.Providers.GetModelAsync("p1", "m1"));
        await db.Providers.DeleteAsync("p1");
        Assert.Null(await db.Providers.GetModelAsync("p1", "m1"));
    }

    [Fact]
    public async Task Another_process_write_is_seen_once_the_entry_expires()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Alpha", Category = "custom", AuthScheme = AuthSchemes.Bearer, Enabled = true });
        Assert.Equal("Alpha", (await db.Providers.GetAsync("p1"))!.Name);
        Assert.Null(await db.Providers.GetModelAsync("p1", "later"));
        await db.Models.InsertAsync(TestSamples.SimpleModel("m1"));
        Assert.Null((await db.Models.GetMatcherIndexAsync()).Match("external-alias"));
        Assert.Empty(await db.Models.GetUpstreamIdIndexAsync("vendor"));
        Assert.Null(await db.Providers.GetModelAsync("p1", "later"));

        // A connection no factory of this process opened stands in for another process.
        await using (var other = new Microsoft.Data.Sqlite.SqliteConnection(db.Factory.ConnectionString))
        {
            await other.OpenAsync();
            await other.ExecuteAsync("UPDATE providers SET name = 'Elsewhere' WHERE id = 'p1'");
            await other.ExecuteAsync("INSERT INTO provider_models(provider_id, model_id) VALUES ('p1', 'later')");
            await other.ExecuteAsync("UPDATE models SET aliases_json = '[\"external-alias\"]' WHERE id = 'm1'");
            await other.ExecuteAsync("""
                INSERT INTO model_prices(model_id, price_key, upstream_model_id, pricing_json, source, updated_at)
                VALUES ('m1', 'vendor', 'external-id', '{}', 'user', '2026-10-19T00:00:00.000Z')
                """);
        }
        Assert.Null(await db.Providers.GetModelAsync("p1", "later"));
        Assert.Null((await db.Models.GetMatcherIndexAsync()).Match("external-alias"));
        await Task.Delay(TimeSpan.FromMilliseconds(2100)); // RowCache.MaxAge (2 s) and a little
        Assert.Equal("Elsewhere", (await db.Providers.GetAsync("p1"))!.Name);
        Assert.NotNull(await db.Providers.GetModelAsync("p1", "later"));
        Assert.Equal("m1", (await db.Models.GetMatcherIndexAsync()).Match("external-alias"));
        Assert.Equal("m1", (await db.Models.GetUpstreamIdIndexAsync("vendor"))["EXTERNAL-ID"]);
    }

    [Fact]
    public async Task Any_write_through_the_factory_invalidates_but_the_request_log_does_not()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Alpha", Category = "custom", AuthScheme = AuthSchemes.Bearer, Enabled = true });
        Assert.Equal("Alpha", (await db.Providers.GetAsync("p1"))!.Name);

        // A write no repository method knows about still invalidates: tracking is per connection, not per method.
        var before = db.Factory.Generation;
        await using (var conn = await db.Factory.OpenAsync()) await conn.ExecuteAsync("UPDATE providers SET name = 'Gamma' WHERE id = 'p1'");
        Assert.True(db.Factory.Generation > before);
        Assert.Equal("Gamma", (await db.Providers.GetAsync("p1"))!.Name);

        // Reads do not advance the generation.
        before = db.Factory.Generation;
        await db.Providers.ListAsync();
        Assert.Equal(before, db.Factory.Generation);

        await db.Requests.InsertBatchAsync([new RequestRecord
        {
            Id = "01TEST0000000000000000000Z", StartedAtUtc = Now, ProviderId = "p1", ProviderName = "Gamma",
            InboundProtocol = "openai-chat", Status = RequestStatus.Success, HttpStatus = 200, TokenId = "default",
        }]);
        Assert.Equal(before, db.Factory.Generation);
    }
}
