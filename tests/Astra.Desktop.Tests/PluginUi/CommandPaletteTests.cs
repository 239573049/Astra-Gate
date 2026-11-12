using Astra.Api.Contracts;
using Astra.Desktop.Core.Plugins.V2;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Tests.PluginUi;

public class CommandPaletteTests
{
    private static PluginV2ContributionsDto Contributions() => new(
        [new PluginV2PageDto("acme.x", "home", "Acme home", null, "tools", [])],
        [],
        [new PluginV2MenuDto("acme.x", "command.palette", "sync", "Sync now", null)],
        [new PluginV2CommandDto("acme.x", "sync", "Sync now", null, "mod+shift+s"), new PluginV2CommandDto("acme.x", "clear", "Clear", null, "alt+c"),
         new PluginV2CommandDto("acme.x", "hidden", "Hidden", null, null)],
        [], [], []);

    [Fact]
    public void Lists_pages_plugin_pages_palette_commands_and_bound_commands_like_the_web()
    {
        var items = CommandPalette.Items(Contributions(), [("Tokens", "Tokens")]);
        Assert.Equal(new PaletteTarget("Tokens", null, null, null), items.Single(i => i.Id == "nav:Tokens").Target);
        Assert.Equal(new PaletteTarget("PluginPage", "acme.x/home", null, null), items.Single(i => i.Id == "page:acme.x/home").Target);
        Assert.Equal("mod+shift+s", items.Single(i => i.Id == "cmd:acme.x:sync").Keybinding);
        Assert.Contains(items, i => i.Id == "cmd:acme.x:clear");
        Assert.DoesNotContain(items, i => i.Id == "cmd:acme.x:hidden");
        Assert.Equal(["cmd:acme.x:sync"], CommandPalette.Filter(items, "sync ACME").Select(i => i.Id));
    }

    [Fact]
    public void Keybindings_match_per_platform_and_need_a_modifier()
    {
        var chord = CommandPalette.Parse("mod+shift+g")!;
        Assert.True(CommandPalette.Matches(chord, "KeyG", ctrl: false, alt: false, shift: true, meta: true, mac: true));
        Assert.False(CommandPalette.Matches(chord, "KeyG", ctrl: true, alt: false, shift: true, meta: false, mac: true));
        Assert.True(CommandPalette.Matches(chord, "KeyG", ctrl: true, alt: false, shift: true, meta: false, mac: false));
        Assert.Null(CommandPalette.Parse("g"));
        Assert.Null(CommandPalette.Parse("hyper+g"));
        Assert.Equal("⌘⇧G", CommandPalette.Label("mod+shift+g", mac: true));
        Assert.Equal("Ctrl+Shift+G", CommandPalette.Label("mod+shift+g", mac: false));
        Assert.Equal("clear", CommandPalette.CommandFor(Contributions(), "KeyC", false, true, false, false, mac: false)?.Id);
    }

    [Fact]
    public void Ctrl_K_opens_the_palette() => Assert.Equal(MenuCommands.Palette, ShortcutMap.CommandFor("KeyK", true, false, false, false));
}
