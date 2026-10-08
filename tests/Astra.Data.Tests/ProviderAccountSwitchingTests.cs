using Astra.Core.Models;
using Astra.Data.Tests;

namespace Astra.Data;

/// <summary>Switching state of subscription accounts (plan §5.4, migration 0008).</summary>
public class ProviderAccountSwitchingTests
{
    private static ProviderAccount Account(string id, string providerId = "p1") => new()
    {
        Id = id,
        ProviderId = providerId,
        DisplayName = id,
        Status = AccountStatus.Active,
    };

    private static async Task<AstraDatabase> NewDbAsync()
    {
        var db = await TestDb.InitializeAsync();
        await db.Providers.InsertAsync(new Provider { Id = "p1", Name = "Claude", AuthScheme = AuthSchemes.OAuthSubscription });
        await db.Providers.InsertAsync(new Provider { Id = "p2", Name = "Other", AuthScheme = AuthSchemes.OAuthSubscription });
        return db;
    }

    [Fact]
    public async Task New_Accounts_Append_To_The_Order_And_Start_Enabled()
    {
        var db = await NewDbAsync();
        await db.Accounts.InsertAsync(Account("a"));
        await db.Accounts.InsertAsync(Account("b"));
        await db.Accounts.InsertAsync(Account("x", "p2"));

        var list = await db.Accounts.ListAsync("p1");
        Assert.Equal(["a", "b"], list.Select(a => a.Id));
        Assert.Equal([0, 1], list.Select(a => a.SortOrder));
        Assert.All(list, a => Assert.True(a.Enabled));
        Assert.All(list, a => Assert.False(a.IsCurrent));
        Assert.Equal(0, (await db.Accounts.GetAsync("x"))!.SortOrder);
    }

    [Fact]
    public async Task Set_Current_Keeps_One_Current_Per_Provider_And_Clears_Its_Cooldown()
    {
        var db = await NewDbAsync();
        await db.Accounts.InsertAsync(Account("a"));
        await db.Accounts.InsertAsync(Account("b"));
        await db.Accounts.InsertAsync(Account("x", "p2"));
        await db.Accounts.SetCurrentAsync("p2", "x");
        await db.Accounts.SetCooldownAsync("b", DateTimeOffset.UtcNow.AddHours(1), "429");

        Assert.True(await db.Accounts.SetCurrentAsync("p1", "a"));
        Assert.True(await db.Accounts.SetCurrentAsync("p1", "b"));
        var list = await db.Accounts.ListAsync("p1");
        Assert.Equal(["b"], list.Where(a => a.IsCurrent).Select(a => a.Id));
        var b = list.Single(a => a.Id == "b");
        Assert.Null(b.CooldownUntilUtc);
        Assert.Null(b.LastError);
        Assert.True((await db.Accounts.GetAsync("x"))!.IsCurrent); // other providers untouched

        Assert.False(await db.Accounts.SetCurrentAsync("p1", "x")); // foreign account
        Assert.True(await db.Accounts.SetCurrentAsync("p1", null));
        Assert.DoesNotContain(await db.Accounts.ListAsync("p1"), a => a.IsCurrent);
    }

    [Fact]
    public async Task Reorder_Puts_Given_Ids_First_And_Keeps_The_Rest()
    {
        var db = await NewDbAsync();
        foreach (var id in new[] { "a", "b", "c", "d" }) await db.Accounts.InsertAsync(Account(id));

        await db.Accounts.ReorderAsync("p1", ["c", "a", "ghost"]);

        Assert.Equal(["c", "a", "b", "d"], (await db.Accounts.ListAsync("p1")).Select(a => a.Id));
    }

    [Fact]
    public async Task Enable_Cooldown_And_Name_Have_Their_Own_Writers_And_Survive_A_Full_Update()
    {
        var db = await NewDbAsync();
        await db.Accounts.InsertAsync(Account("a"));
        var until = new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero);
        await db.Accounts.SetEnabledAsync("a", false);
        await db.Accounts.SetCooldownAsync("a", until, "429 限流");
        await db.Accounts.SetDisplayNameAsync("a", "Work");
        await db.Accounts.SetCurrentAsync("p1", "a");
        await db.Accounts.SetCooldownAsync("a", until, "429 限流");

        // A login refresh / quota write (full UpdateAsync of a stale copy) must not reset the switching state.
        var stale = Account("a");
        stale.DisplayName = "Work";
        await db.Accounts.UpdateAsync(stale);

        var got = (await db.Accounts.GetAsync("a"))!;
        Assert.False(got.Enabled);
        Assert.True(got.IsCurrent);
        Assert.Equal(until, got.CooldownUntilUtc);
        Assert.Equal("429 限流", got.LastError);
        Assert.Equal("Work", got.DisplayName);

        await db.Accounts.SetCooldownAsync("a", null, null);
        Assert.Null((await db.Accounts.GetAsync("a"))!.CooldownUntilUtc);
    }
}
