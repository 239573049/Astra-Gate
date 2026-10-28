using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Tests.Shell;

public class MenuCommandTests
{
    private sealed class Harness
    {
        public RecordingNavigator Nav { get; } = new();
        public int Toggles, Refreshes;
        public List<string> PageCommands { get; } = [];
        public bool PageTakes = true;
        public string? Route;

        public MenuCommandRouter Router => new(Nav, () => Toggles++, () => Refreshes++, c => { PageCommands.Add(c); return PageTakes; }, () => Route);
    }

    [Theory]
    [InlineData("nav:overview", "Overview")]
    [InlineData("nav:smartRouting", "SmartRouting")]
    [InlineData("nav:settings", "Settings")]
    [InlineData("nav:traces", "Traces")]
    public void Nav_commands_show_the_page(string command, string route)
    {
        var h = new Harness();
        Assert.True(h.Router.Handle(command));
        Assert.Equal((route, (string?)null), h.Nav.Current);
    }

    [Fact]
    public void Every_nav_target_of_the_bridge_has_a_route()
    {
        string[] targets = ["overview", "requests", "traces", "clients", "tokens", "providers", "models", "plugins", "privacy", "smartRouting", "settings"];
        Assert.Equal(targets.Order(), MenuCommandRouter.NavRoutes.Keys.Order());
    }

    [Fact]
    public void Unknown_nav_target_is_not_handled()
    {
        var h = new Harness();
        Assert.False(h.Router.Handle("nav:nowhere"));
        Assert.Null(h.Nav.Current);
    }

    [Fact]
    public void Shell_commands_toggle_refresh_and_open_the_tray_settings()
    {
        var h = new Harness();
        Assert.True(h.Router.Handle(MenuCommands.ToggleSidebar));
        Assert.True(h.Router.Handle(MenuCommands.Refresh));
        Assert.True(h.Router.Handle(MenuCommands.SettingsTray));
        Assert.Equal((1, 1), (h.Toggles, h.Refreshes));
        Assert.Equal(("Settings", "tray"), h.Nav.Current);
        Assert.Empty(h.PageCommands);
    }

    [Fact]
    public void Find_goes_to_the_current_page_and_reports_whether_it_took_it()
    {
        var h = new Harness { PageTakes = false };
        Assert.False(h.Router.Handle(MenuCommands.Find));
        Assert.Equal([MenuCommands.Find], h.PageCommands);
    }

    [Fact]
    public void New_provider_opens_providers_with_the_flow_unless_already_there()
    {
        var h = new Harness { Route = "Tokens" };
        Assert.True(h.Router.Handle(MenuCommands.NewProvider));
        Assert.Equal(("Providers", MenuCommandRouter.NewProviderParam), h.Nav.Current);
        Assert.Empty(h.PageCommands);

        h.Route = "Providers";
        Assert.True(h.Router.Handle(MenuCommands.NewProvider));
        Assert.Equal([MenuCommands.NewProvider], h.PageCommands);
        Assert.Single(h.Nav.Calls);
    }

    [Theory]
    [InlineData("Digit1", "nav:overview")]
    [InlineData("Digit3", "nav:clients")]
    [InlineData("Digit6", "nav:settings")]
    [InlineData("KeyF", "find")]
    [InlineData("KeyR", "refresh")]
    [InlineData("KeyN", "new-provider")]
    [InlineData("Comma", "nav:settings")]
    public void Ctrl_chords_map_like_the_web_desktop_shortcuts(string code, string command)
        => Assert.Equal(command, ShortcutMap.CommandFor(code, ctrl: true, alt: false, shift: false, meta: false));

    [Fact]
    public void Ctrl_alt_s_toggles_the_sidebar_and_other_chords_are_ignored()
    {
        Assert.Equal(MenuCommands.ToggleSidebar, ShortcutMap.CommandFor("KeyS", true, true, false, false));
        Assert.Null(ShortcutMap.CommandFor("KeyS", true, false, false, false));
        Assert.Null(ShortcutMap.CommandFor("KeyF", false, false, false, false));   // plain F types
        Assert.Null(ShortcutMap.CommandFor("KeyF", true, false, true, false));     // Ctrl+Shift+F
        Assert.Null(ShortcutMap.CommandFor("KeyF", true, false, false, true));     // meta is macOS's menu
        Assert.Null(ShortcutMap.CommandFor("Digit7", true, false, false, false));
        Assert.Null(ShortcutMap.CommandFor("Digit1", true, true, false, false));
    }

    [Fact]
    public void Menu_labels_are_chinese_for_zh_and_english_otherwise()
    {
        Assert.Equal("设置…", MenuLabels.For("zh").Settings);
        Assert.Equal("概览", MenuLabels.For("zh-CN").Nav["overview"]);
        Assert.Equal("Reload Data", MenuLabels.For("en").Refresh);
        Assert.Equal("File", MenuLabels.For(null).File);
        Assert.All(ShortcutMap.NavOrder, t => Assert.True(MenuLabels.For("en").Nav.ContainsKey(t)));
    }
}
