using Astra.Core.Plugins;
using Astra.Data.Tests;

namespace Astra.Data;

public class PluginRepositoryTests
{
    private static PluginEntry Entry(string spec, string? options = null) =>
        new() { Spec = spec, Name = spec.Split('@')[0], Source = PluginSources.Npm, OptionsJson = options };

    [Fact]
    public async Task Added_plugins_keep_their_order_and_fields()
    {
        var db = await TestDb.InitializeAsync();
        await db.Plugins.AddAsync(Entry("a@latest", """{"x":1}"""));
        await db.Plugins.AddAsync(Entry("b@latest"));
        await db.Plugins.AddAsync(Entry("c@latest"));

        var list = await db.Plugins.ListAsync();
        Assert.Equal(["a@latest", "b@latest", "c@latest"], list.Select(p => p.Spec));
        Assert.Equal("""{"x":1}""", list[0].OptionsJson);
        Assert.True(list[0].Enabled);
        Assert.Equal(PluginSources.Npm, list[0].Source);
        Assert.True(list[0].AddedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Adding_the_same_spec_updates_it_in_place()
    {
        var db = await TestDb.InitializeAsync();
        await db.Plugins.AddAsync(Entry("a@latest"));
        await db.Plugins.AddAsync(Entry("b@latest"));
        await db.Plugins.AddAsync(new PluginEntry { Spec = "a@latest", Name = "a", InstalledVersion = "1.2.0" });

        var list = await db.Plugins.ListAsync();
        Assert.Equal(["a@latest", "b@latest"], list.Select(p => p.Spec));
        Assert.Equal("1.2.0", list[0].InstalledVersion);
    }

    [Fact]
    public async Task Setters_change_one_field()
    {
        var db = await TestDb.InitializeAsync();
        await db.Plugins.AddAsync(Entry("a@latest"));

        Assert.True(await db.Plugins.SetEnabledAsync("a@latest", false));
        Assert.True(await db.Plugins.SetOptionsAsync("a@latest", """{"words":["x"]}"""));
        Assert.True(await db.Plugins.SetInstalledVersionAsync("a@latest", "0.1.1"));
        Assert.False(await db.Plugins.SetEnabledAsync("missing", true));

        var a = await db.Plugins.GetAsync("a@latest");
        Assert.NotNull(a);
        Assert.False(a.Enabled);
        Assert.Equal("""{"words":["x"]}""", a.OptionsJson);
        Assert.Equal("0.1.1", a.InstalledVersion);
    }

    [Fact]
    public async Task Reorder_puts_the_listed_first_and_keeps_the_rest()
    {
        var db = await TestDb.InitializeAsync();
        foreach (var s in new[] { "a", "b", "c", "d" }) await db.Plugins.AddAsync(Entry(s));

        await db.Plugins.ReorderAsync(["c", "missing", "a"]);

        Assert.Equal(["c", "a", "b", "d"], (await db.Plugins.ListAsync()).Select(p => p.Spec));
    }

    [Fact]
    public async Task Preferences_follow_a_replaced_spec_and_go_with_a_removed_one()
    {
        var db = await TestDb.InitializeAsync();
        await db.Plugins.AddAsync(Entry("first"));
        await db.Plugins.AddAsync(Entry("zcode@1.0.0"));
        await db.Plugins.AddAsync(Entry("last"));
        await db.Plugins.SetPreferenceAsync("zcode", "zcode@1.0.0");

        await db.Plugins.ReplaceAsync("zcode@1.0.0", new PluginEntry { Spec = "zcode@1.1.0", Name = "zcode" });

        Assert.Equal(["first", "zcode@1.1.0", "last"], (await db.Plugins.ListAsync()).Select(p => p.Spec));
        Assert.Equal("zcode@1.1.0", (await db.Plugins.ListPreferencesAsync())["zcode"]);

        Assert.True(await db.Plugins.RemoveAsync("zcode@1.1.0"));
        Assert.Empty(await db.Plugins.ListPreferencesAsync());
    }
}
