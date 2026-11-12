using Astra.Core.Plugins;
using Astra.Data;
using Astra.Data.Repositories;

namespace Astra.Data.Tests;

/// <summary>A v2 plugin's own data (migration 0024): storage, secrets, grants, activity, dismissed successors.</summary>
public class PluginDataRepositoryTests
{
    private static async Task<PluginDataRepository> RepoAsync()
    {
        var paths = TestDb.NewPaths(out _);
        var factory = new SqliteConnectionFactory(paths);
        await new MigrationRunner(factory, paths).MigrateAsync();
        return new PluginDataRepository(factory);
    }

    [Fact]
    public async Task Storage_lists_by_prefix_with_a_cursor_and_counts_its_size()
    {
        var repo = await RepoAsync();
        await repo.SetAsync("acme.a", "rule:1", "{\"w\":\"x\"}");
        await repo.SetAsync("acme.a", "rule:2", "[1,2]");
        await repo.SetAsync("acme.a", "other", "true");
        await repo.SetAsync("acme.b", "rule:1", "1");
        await repo.SetAsync("acme.a", "rule:2", "[1,2,3]");

        Assert.Equal("[1,2,3]", await repo.GetAsync("acme.a", "rule:2"));
        var page = await repo.ListAsync("acme.a", "rule:", 1, null);
        Assert.Equal("rule:1", Assert.Single(page).Key);
        Assert.Equal("rule:2", Assert.Single(await repo.ListAsync("acme.a", "rule:", 10, "rule:1")).Key);
        Assert.Equal("rule:1".Length + 9 + "rule:2".Length + 7 + "other".Length + 4, await repo.SizeAsync("acme.a"));
        Assert.True(await repo.DeleteAsync("acme.a", "other"));
        Assert.Null(await repo.GetAsync("acme.a", "other"));
    }

    [Fact]
    public async Task Secrets_grants_activity_and_dismissals_round_trip()
    {
        var repo = await RepoAsync();
        await repo.SetSecretAsync("acme.a", "token", "protected!");
        Assert.Equal("protected!", await repo.GetSecretAsync("acme.a", "token"));
        Assert.Equal(["token"], await repo.SecretNamesAsync("acme.a"));
        await repo.ClearAsync("acme.a");
        Assert.Null(await repo.GetSecretAsync("acme.a", "token"));

        Assert.Empty(await repo.GrantsAsync("acme.a"));
        await repo.SetGrantsAsync("acme.a", ["ui", "net:https://x.example"]);
        Assert.Equal(["ui", "net:https://x.example"], await repo.GrantsAsync("acme.a"));

        for (var i = 0; i < 3; i++)
            await repo.AddActivityAsync(new PluginActivity("acme.a", DateTimeOffset.UtcNow, "http.fetch", "https://x.example", "200"));
        Assert.Equal(3, (await repo.ActivityAsync("acme.a", 10)).Count);

        await repo.SetDismissedSuccessorAsync("@x/mw", "2.0.0");
        Assert.Equal("2.0.0", await repo.DismissedSuccessorAsync("@x/mw"));
        await repo.SetDismissedSuccessorAsync("@x/mw", null);
        Assert.Null(await repo.DismissedSuccessorAsync("@x/mw"));
    }
}
