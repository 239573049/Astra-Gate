using Astra.Core.Routing;
using Astra.Data.Tests;

namespace Astra.Data;

public class RoutingGroupRepositoryTests
{
    private static RoutingGroup Make(string slug, string mode = GroupModes.Smart)
    {
        var now = DateTimeOffset.UtcNow;
        var g = new RoutingGroup { Id = "id-" + slug, Slug = slug, Name = slug.ToUpperInvariant(), Mode = mode, CreatedAt = now, UpdatedAt = now };
        g.SetMembers([new GroupMember { Provider = "p1", Model = "vendor/model", Weight = 3 }, new GroupMember { Group = "other" }]);
        return g;
    }

    [Fact]
    public async Task A_Group_Is_Saved_Read_Back_Changed_And_Deleted()
    {
        var db = await TestDb.InitializeAsync();
        await db.RoutingGroups.InsertAsync(Make("opus", GroupModes.Pace));

        var read = (await db.RoutingGroups.GetBySlugAsync("opus"))!;
        Assert.Equal((GroupModes.Pace, Stays.Auto, true, true), (read.Mode, read.Stays, read.Enabled, read.Listed));
        Assert.Equal(["p1/vendor/model", "group/other"], read.Members().Select(m => m.Key));
        Assert.Equal(3, read.Members()[0].Weight);

        read.Mode = GroupModes.Weight;
        read.Enabled = false;
        read.Listed = false;
        read.Stays = Stays.Strict;
        await db.RoutingGroups.UpdateAsync(read);
        var changed = (await db.RoutingGroups.GetAsync("id-opus"))!;
        Assert.Equal((GroupModes.Weight, Stays.Strict, false, false), (changed.Mode, changed.Stays, changed.Enabled, changed.Listed));

        Assert.True(await db.RoutingGroups.DeleteAsync("id-opus"));
        Assert.False(await db.RoutingGroups.DeleteAsync("id-opus"));
        Assert.Null(await db.RoutingGroups.GetBySlugAsync("opus"));
    }

    [Fact]
    public async Task The_Slug_Is_Unique()
    {
        var db = await TestDb.InitializeAsync();
        await db.RoutingGroups.InsertAsync(Make("a"));
        var twin = Make("a");
        twin.Id = "other";
        await Assert.ThrowsAnyAsync<Exception>(() => db.RoutingGroups.InsertAsync(twin));
    }

    [Fact]
    public async Task The_Cached_List_Sees_A_Write_At_Once_And_Is_Ordered_By_Name()
    {
        var db = await TestDb.InitializeAsync();
        Assert.Empty(await db.RoutingGroups.ListCachedAsync());
        await db.RoutingGroups.InsertAsync(Make("zed"));
        await db.RoutingGroups.InsertAsync(Make("alpha"));
        Assert.Equal(["alpha", "zed"], (await db.RoutingGroups.ListCachedAsync()).Select(g => g.Slug));
        Assert.Equal("alpha", (await db.RoutingGroups.FindCachedAsync("alpha"))!.Slug);
        Assert.Null(await db.RoutingGroups.FindCachedAsync("nope"));
        await db.RoutingGroups.DeleteAsync("id-alpha");
        Assert.Equal(["zed"], (await db.RoutingGroups.ListCachedAsync()).Select(g => g.Slug));
    }
}
