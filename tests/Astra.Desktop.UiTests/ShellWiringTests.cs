using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Pages.Tokens;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Astra.Desktop.Views;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;

namespace Astra.Desktop.UiTests;

public class ShellWiringTests
{
    private sealed class ProbePage(PageContext ctx, AppRoute route, string? param) : PageViewModel(route, param, ctx)
    {
        public int Activations, Deactivations;
        public List<string> Commands { get; } = [];
        protected override void OnActivated() => Activations++;
        protected override void OnDeactivated() => Deactivations++;
        public override bool OnMenuCommand(string command) { Commands.Add(command); return true; }
    }

    private sealed class ProbeModule(Dictionary<string, ProbePage> made) : IPageModule
    {
        public void Register(IPageRegistrar pages)
        {
            pages.Add(AppRoute.Providers, (c, p) => Make(c, AppRoute.Providers, p), vm => new TextBlock { Text = "providers" });
            pages.Add(AppRoute.ProviderDetail, (c, p) => Make(c, AppRoute.ProviderDetail, p), vm => new TextBlock { Text = "detail" });
        }

        private ProbePage Make(PageContext c, AppRoute r, string? p) => made[$"{r}:{p}"] = new ProbePage(c, r, p);
    }

    private static PageContext Context(FixtureSet? fx = null)
    {
        fx ??= Scenes.Fixtures();
        return PageContext.Create(new ApiClient("http://fixture", fx.Handler()), clock: new FixedClock(fx.Now));
    }

    private static ShellViewModel Shell(IReadOnlyList<IPageModule> modules, PageContext? ctx = null, bool collapsed = false)
    {
        ctx ??= Context();
        var prefs = DesktopPrefs.InMemory();
        prefs.SidebarCollapsed = collapsed;
        return new ShellViewModel(prefs, new ConnectionViewModel(null, ctx.Api, ctx.Queries), ctx, modules);
    }

    [Fact]
    public void A_route_registered_twice_names_both_modules()
    {
        var made = new Dictionary<string, ProbePage>();
        var e = Assert.Throws<InvalidOperationException>(() => new PageRegistry([new ProbeModule(made), new ProbeModule(made)]));
        Assert.Contains("Providers", e.Message);
        Assert.Contains(nameof(ProbeModule), e.Message);
    }

    [Fact]
    public void Routes_without_a_module_stay_placeholders_and_app_modules_register_cleanly()
    {
        // every route has its page module now; the placeholder is still what a shell without modules shows
        var registry = new PageRegistry(AppModules.All);
        Assert.All(RouteInfo.Routes, info => Assert.True(registry.IsRegistered(info.Route), $"{info.Route} has no module"));
        var shell = Shell([]);
        Assert.IsType<PlaceholderPageViewModel>(shell.Navigator.Current);
    }

    [Fact]
    public void Navigator_is_an_INavigator_by_route_name_and_activates_pages_as_they_are_shown_and_covered()
    {
        var made = new Dictionary<string, ProbePage>();
        var shell = Shell([new ProbeModule(made)]);
        INavigator nav = shell.Navigator;

        nav.Navigate("Providers");
        var list = made["Providers:"];
        Assert.Equal((1, 0), (list.Activations, list.Deactivations));

        nav.Push("ProviderDetail", "p1");
        var detail = made["ProviderDetail:p1"];
        Assert.Equal((1, 1), (list.Activations, list.Deactivations));   // covered, kept below
        Assert.Equal((1, 0), (detail.Activations, detail.Deactivations));
        Assert.True(nav.CanGoBack);

        nav.GoBack();
        Assert.Equal((2, 1), (list.Activations, list.Deactivations));   // shown again
        Assert.Equal(1, detail.Deactivations);
        Assert.Same(list, shell.Navigator.Current);

        nav.Navigate("Settings");
        Assert.Equal(2, list.Deactivations);
        Assert.Throws<ArgumentException>(() => nav.Navigate("Nowhere"));
    }

    [Fact]
    public void Menu_commands_from_the_host_reach_the_shell_and_the_current_page()
    {
        var made = new Dictionary<string, ProbePage>();
        var host = new NullDesktopHost();
        var fx = Scenes.Fixtures();
        var ctx = PageContext.Create(new ApiClient("http://fixture", fx.Handler()), host: host);
        var shell = Shell([new ProbeModule(made)], ctx);

        host.RaiseMenuCommand("nav:providers");
        Assert.Equal(AppRoute.Providers, shell.Navigator.Current.Route);

        host.RaiseMenuCommand(MenuCommands.ToggleSidebar);
        Assert.True(shell.IsSidebarCollapsed);
        host.RaiseMenuCommand(MenuCommands.ToggleSidebar);
        Assert.False(shell.IsSidebarCollapsed);

        host.RaiseMenuCommand(MenuCommands.Find);
        Assert.Equal([MenuCommands.Find], made["Providers:"].Commands);

        host.RaiseMenuCommand(MenuCommands.NewProvider);   // already on Providers: the page gets it
        Assert.Contains(MenuCommands.NewProvider, made["Providers:"].Commands);
    }

    [AvaloniaFact]
    public async Task Tokens_page_loads_through_the_store_and_settles()
    {
        var fx = Scenes.Fixtures();
        var shell = Scenes.BuildShell(fx);
        shell.Navigator.Navigate(AppRoute.Tokens);
        var page = Assert.IsType<Astra.Desktop.Pages.Tokens.TokensPage>(shell.Navigator.Current);
        Assert.Equal("令牌", page.Title);

        var settled = page.WhenSettledAsync();
        SceneCapture.Await(Task.WhenAny(settled, Task.Delay(10000)), TimeSpan.FromSeconds(15));
        Assert.True(settled.IsCompleted);
        Assert.Equal(3, page.Tokens.Data!.Count);
        Assert.Empty(fx.Unmatched);
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public void Sidebar_address_line_shows_the_gateway_base_url_from_the_settings()
    {
        var fx = Scenes.Fixtures().Override("/api/settings", SettingsWith("https://gate.example.com/"));
        var shell = Scenes.BuildShell(fx);
        SceneCapture.Await(shell.Context.Queries.WhenIdleAsync(), TimeSpan.FromSeconds(15));
        Assert.Equal("gate.example.com", shell.Connection.Address);
        Assert.True(shell.Connection.ShowAddress);
    }

    [AvaloniaFact]
    public void Sidebar_address_line_falls_back_to_the_api_address_when_the_settings_fail()
    {
        var fx = Scenes.Fixtures().Override("/api/settings", "{\"error\":\"boom\"}", 500);
        var shell = Scenes.BuildShell(fx);
        SceneCapture.Await(shell.Context.Queries.WhenIdleAsync(TimeSpan.FromSeconds(8)), TimeSpan.FromSeconds(15));
        Assert.Equal("127.0.0.1:17321", shell.Connection.Address);
    }

    private static string SettingsWith(string gatewayBaseUrl)
    {
        var fixture = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ui", "default", "settings.json")))!.AsObject();
        fixture["gatewayBaseUrl"] = gatewayBaseUrl;
        return fixture.ToJsonString();
    }

    // ---- the recurring "sidebarCollapsed: true / resized window after the first launch" bug (plan avalonia-client §24) ----

    [AvaloniaFact]
    public void Building_a_collapsed_shell_never_leaks_into_the_process_prefs_or_the_next_shell()
    {
        var process = AppServices.Current.Prefs;
        process.SidebarCollapsed = false;

        var collapsed = Scenes.BuildShell(Scenes.Fixtures(), collapsed: true);
        Assert.True(collapsed.IsSidebarCollapsed);
        Assert.False(process.SidebarCollapsed);                       // the scene's choice stayed in its own detached prefs
        Assert.False(Scenes.BuildShell(Scenes.Fixtures()).IsSidebarCollapsed);
    }

    [AvaloniaTheory]
    [InlineData(1440, 900)]
    [InlineData(1100, 700)]
    [InlineData(960, 640)]
    public void Showing_and_resizing_the_main_window_never_collapses_the_sidebar_or_writes_the_flag(double width, double height)
    {
        var process = AppServices.Current.Prefs;
        process.SidebarCollapsed = false;
        var shell = Scenes.BuildShell(Scenes.Fixtures());
        var window = new MainWindow(shell);
        try
        {
            window.Show();
            SceneCapture.Pump(20);
            window.Width = width;
            window.Height = height;
            SceneCapture.Pump(60);
            Assert.False(shell.IsSidebarCollapsed);
            Assert.False(process.SidebarCollapsed);
            Assert.True(((ShellView)window.Content!).FindControl<GlassSidebar>("Nav")!.Width >= 224 - 0.5);
        }
        finally
        {
            window.Close();
        }
    }
}
