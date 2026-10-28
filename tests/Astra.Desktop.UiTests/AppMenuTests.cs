using Astra.Desktop.Core.Shell;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Controls.Presenters;
using Avalonia.LogicalTree;
using Avalonia.Headless.XUnit;
using Avalonia.Input;

namespace Astra.Desktop.UiTests;

public class AppMenuTests
{
    [Theory]
    [InlineData(Key.D1, "Digit1")]
    [InlineData(Key.D6, "Digit6")]
    [InlineData(Key.F, "KeyF")]
    [InlineData(Key.OemComma, "Comma")]
    [InlineData(Key.S, "KeyS")]
    public void Keys_map_to_the_dom_codes_the_shortcut_table_uses(Key key, string code) => Assert.Equal(code, AppMenu.CodeFor(key));

    [Fact]
    public void Other_keys_are_not_shortcuts()
    {
        Assert.Null(AppMenu.CodeFor(Key.D7));
        Assert.Null(AppMenu.CodeFor(Key.A));
    }

    [AvaloniaFact]
    public void The_macos_menu_has_the_electron_accelerators_and_sends_the_same_commands()
    {
        var sent = new List<string>();
        var menu = AppMenu.Build(MenuLabels.For("en"), sent.Add, () => sent.Add("close"), () => sent.Add("logs"));
        Assert.Equal(["File", "View", "Help"], menu.Items.OfType<NativeMenuItem>().Select(i => i.Header));

        var view = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "View").Menu!;
        var items = view.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToList();
        Assert.Equal(["Overview", "Requests", "Clients", "Providers", "Models", "Settings", "Find", "Reload Data", "Toggle Sidebar"], items.Select(i => i.Header));
        Assert.Equal(new KeyGesture(Key.D1, KeyModifiers.Meta), items[0].Gesture);
        Assert.Equal(new KeyGesture(Key.D6, KeyModifiers.Meta), items[5].Gesture);
        Assert.Equal(new KeyGesture(Key.S, KeyModifiers.Meta | KeyModifiers.Alt), items[^1].Gesture);

        foreach (var item in items) ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
        Assert.Equal(
            ["nav:overview", "nav:requests", "nav:clients", "nav:providers", "nav:models", "nav:settings", "find", "refresh", "toggle-sidebar"],
            sent);

        var file = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "File").Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToList();
        Assert.Equal(new KeyGesture(Key.N, KeyModifiers.Meta), file[0].Gesture);
        ((INativeMenuItemExporterEventsImplBridge)file[0]).RaiseClicked();
        Assert.Equal("new-provider", sent[^1]);
    }

    [AvaloniaFact]
    public void With_a_window_the_menu_adds_electrons_edit_window_and_full_screen_roles()
    {
        var did = new List<string>();
        var actions = new AppMenu.WindowMenuActions(() => did.Add("min"), () => did.Add("zoom"), () => did.Add("fs"), op => did.Add(op));
        var menu = AppMenu.Build(MenuLabels.For("en"), _ => { }, () => { }, () => { }, actions);
        Assert.Equal(["File", "Edit", "View", "Window", "Help"], menu.Items.OfType<NativeMenuItem>().Select(i => i.Header));

        var edit = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Edit").Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToList();
        Assert.Equal(["Undo", "Redo", "Cut", "Copy", "Paste", "Select All"], edit.Select(i => i.Header));
        Assert.Equal(new KeyGesture(Key.C, KeyModifiers.Meta), edit[3].Gesture);
        foreach (var item in edit) ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();

        var view = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "View").Menu!.Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToList();
        Assert.Equal("Toggle Full Screen", view[^1].Header);
        Assert.Equal(new KeyGesture(Key.F, KeyModifiers.Meta | KeyModifiers.Control), view[^1].Gesture);
        ((INativeMenuItemExporterEventsImplBridge)view[^1]).RaiseClicked();

        var window = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "Window").Menu!.Items.OfType<NativeMenuItem>().ToList();
        Assert.Equal(new KeyGesture(Key.M, KeyModifiers.Meta), window[0].Gesture);
        foreach (var item in window) ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();

        Assert.Equal(["undo", "redo", "cut", "copy", "paste", "selectAll", "fs", "min", "zoom"], did);
        Assert.Equal("编辑", AppMenu.Build(MenuLabels.For("zh"), _ => { }, () => { }, () => { }, actions).Items.OfType<NativeMenuItem>().ElementAt(1).Header);
    }

    [AvaloniaFact]
    public void A_language_change_relabels_the_exported_menu_in_place()
    {
        // Avalonia.Native exports a window menu once and throws on a second SetMenu with another instance ("The menu being updated
        // does not match"), so AppMenu keeps the first menu and moves the new labels onto its items.
        var sent = new List<string>();
        var actions = new AppMenu.WindowMenuActions(() => { }, () => { }, () => { }, _ => { });
        var menu = AppMenu.Build(MenuLabels.For("en"), sent.Add, () => { }, () => { }, actions);
        var before = Flatten(menu).ToList();

        AppMenu.Relabel(menu, AppMenu.Build(MenuLabels.For("zh"), _ => { }, () => { }, () => { }, actions));

        Assert.Equal(["文件", "编辑", "显示", "窗口", "帮助"], menu.Items.OfType<NativeMenuItem>().Select(i => i.Header));
        var view = menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == "显示").Menu!.Items.OfType<NativeMenuItem>().ToList();
        Assert.Equal("概览", view[0].Header);
        Assert.Equal("切换全屏幕", view[^1].Header);
        // Same item instances, same handlers: a relabelled item still sends its command.
        Assert.Equal(before, Flatten(menu));
        ((INativeMenuItemExporterEventsImplBridge)view[0]).RaiseClicked();
        Assert.Equal(["nav:overview"], sent);

        static IEnumerable<NativeMenuItemBase> Flatten(NativeMenu m) =>
            m.Items.SelectMany(i => i is NativeMenuItem { Menu: { } sub } ? Flatten(sub).Prepend(i) : [i]);
    }

    private sealed class HeaderPage(PageContext ctx) : PageViewModel(AppRoute.Tokens, null, ctx)
    {
        private string _title = "First";
        public override string Title => _title;
        public override string? Subtitle => "sub";
        public override bool HostScrolls => false;

        public void Setup() => SetHeaderActions(new Button { Content = "act" });
        public void Rename(string title) { _title = title; OnPropertyChanged(nameof(Title)); }
    }

    private sealed class HeaderModule(Action<HeaderPage> made) : IPageModule
    {
        public void Register(IPageRegistrar pages) => pages.Add(AppRoute.Tokens, (c, _) =>
        {
            var p = new HeaderPage(c);
            p.Setup();
            made(p);
            return p;
        }, _ => new TextBlock { Text = "body" });
    }

    [AvaloniaFact]
    public void The_shell_header_shows_the_pages_title_subtitle_and_actions_and_follows_changes()
    {
        HeaderPage? page = null;
        var fx = Scenes.Fixtures();
        var ctx = Astra.Desktop.Shell.PageContext.Create(new Astra.Desktop.Core.Api.ApiClient("http://fixture", fx.Handler()));
        var shell = new ShellViewModel(Astra.Desktop.Theme.DesktopPrefs.InMemory(), new ConnectionViewModel(null, ctx.Api, ctx.Queries), ctx, [new HeaderModule(p => page = p)]);
        shell.Navigator.Navigate(AppRoute.Tokens);
        var view = new ShellView { DataContext = shell };
        var window = new AtomUI.Desktop.Controls.Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        try
        {
            SceneCapture.Pump(30);
            var texts = view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("First", texts);
            Assert.Contains("sub", texts);
            Assert.IsType<Button>(view.FindControl<ContentPresenter>("HeaderActionsHost")!.Content);

            page!.Rename("Second");
            SceneCapture.Pump(10);
            Assert.Contains("Second", view.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text));

            // A page that scrolls itself gets no scroller from the shell, only the plain cell; a normal page goes back into the scroll area.
            Assert.True(view.FindControl<Border>("PageFixed")!.IsVisible);
            Assert.False(view.FindControl<TKCode.Arc.ScrollArea>("PageScroll")!.IsVisible);
            shell.Navigator.Navigate(AppRoute.Overview);
            SceneCapture.Pump(5);
            Assert.False(view.FindControl<Border>("PageFixed")!.IsVisible);
            Assert.True(view.FindControl<TKCode.Arc.ScrollArea>("PageScroll")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }
}
