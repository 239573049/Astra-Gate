using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Astra.Desktop.Views;
using Avalonia.Controls;

namespace Astra.Desktop.UiTests;

/// <summary>What a scene factory returns: the control that fills the window and, for a page that loads data, the signal that it has.</summary>
/// <param name="View">The window's content.</param>
/// <param name="Settled">Awaited (while the UI thread is pumped) before the frame is captured: every request the page started was answered (<see cref="IPageLoadState"/>). Null = nothing to wait for.</param>
public sealed record Scene(Control View, Func<Task>? Settled = null)
{
    /// <summary>A static control is a scene with nothing to wait for.</summary>
    public static implicit operator Scene(Control view) => new(view);
}

/// <summary>
/// Every scene the visual regression renders, by name. A name is <c>&lt;page&gt;__&lt;state&gt;</c> — the same as the web side's
/// <c>scripts/visual/capture-web.mjs</c> scene list, whose PNGs are <c>&lt;name&gt;__&lt;theme&gt;.png</c>. Pages add their scenes
/// here (one line each); the factory builds the scene that fills the 1440x900 window (no title bar), with the window already themed.
/// Factories run on the UI thread. Build a page scene with <see cref="Page"/>: the real shell over a fixture <c>ApiClient</c>, the clock
/// frozen at <c>FixtureSet.Now</c>, the sidebar connected, and the capture waiting for the page to settle.
/// </summary>
public static partial class Scenes
{
    public static readonly IReadOnlyDictionary<string, Func<Scene>> All = Build();

    private static Dictionary<string, Func<Scene>> Build()
    {
        var d = new Dictionary<string, Func<Scene>>(StringComparer.Ordinal)
        {
        // Shell: the sidebar, header and the page of the Tokens route (a stub until the real Tokens page lands), like the web's /tokens.
        ["shell__expanded"] = () => Page(AppRoute.Tokens, Fixtures(), collapsed: false),
        ["shell__collapsed"] = () => Page(AppRoute.Tokens, Fixtures(), collapsed: true),

        // The stub Tokens page (Pages/Tokens/TokensModule); the real page replaces it and keeps these names.
        ["tokens__data"] = () => Page(AppRoute.Tokens, Fixtures()),
        ["tokens__empty"] = () => Page(AppRoute.Tokens, Fixtures().Override("/api/tokens", "[]")),

        };
        // Each work package contributes its scenes from its own file (Scenes.<Area>.cs) by implementing its partial method, so
        // packages never edit the same lines, e.g.  static partial void AddOverview(Dictionary<string, Func<Scene>> d) { d["overview__data"] = () => Page(AppRoute.Overview, Fixtures()); }
        // Page states the web side captures (capture-web.mjs SCENES): overview__data, tokens__data, tokens__empty,
        // providers__data, providers__empty, models__data, requests__data, requests__empty, settings__data.
        AddOverviewAndTokens(d);
        AddProviders(d);
        AddProviderDetail(d);
        AddAccounts(d);
        AddModels(d);
        AddClients(d);
        AddRequests(d);
        AddTraces(d);
        AddPlugins(d);
        AddSettingsAndPrivacy(d);
        AddTray(d);
        AddShell(d);
        return d;
    }

    static partial void AddOverviewAndTokens(Dictionary<string, Func<Scene>> d);
    static partial void AddProviders(Dictionary<string, Func<Scene>> d);
    static partial void AddProviderDetail(Dictionary<string, Func<Scene>> d);
    static partial void AddAccounts(Dictionary<string, Func<Scene>> d);
    static partial void AddModels(Dictionary<string, Func<Scene>> d);
    static partial void AddClients(Dictionary<string, Func<Scene>> d);
    static partial void AddRequests(Dictionary<string, Func<Scene>> d);
    static partial void AddTraces(Dictionary<string, Func<Scene>> d);
    static partial void AddPlugins(Dictionary<string, Func<Scene>> d);
    static partial void AddSettingsAndPrivacy(Dictionary<string, Func<Scene>> d);
    static partial void AddTray(Dictionary<string, Func<Scene>> d);
    static partial void AddShell(Dictionary<string, Func<Scene>> d);

    /// <summary>
    /// The scene's admin-API client over <paramref name="handler"/> (the fixture handler, possibly wrapped by the scene's own
    /// interceptors). Every scene builds its client here so <see cref="VisualRecorder"/> can record what the page was served.
    /// </summary>
    public static ApiClient FixtureApi(HttpMessageHandler handler) => new("http://fixture", VisualRecorder.Wrap(handler));

    /// <summary>The default fixture set, fresh per scene (overrides and the unmatched list are per scene).</summary>
    public static FixtureSet Fixtures() => FixtureSet.Load();

    /// <summary>
    /// The app's shell showing <paramref name="route"/>, built over <paramref name="fixtures"/>: the page factories get a
    /// <see cref="PageContext"/> whose <c>ApiClient</c> answers from the fixture handler and whose clock is the fixtures' now; the sidebar
    /// is connected (its address line comes from the fixture's <c>/api/settings</c>); nothing touches a real server or the user's prefs.
    /// </summary>
    public static Scene Page(AppRoute route, FixtureSet fixtures, bool collapsed = false, string? param = null)
    {
        var shell = BuildShell(fixtures, collapsed);
        shell.Navigator.Navigate(route, param);
        return new Scene(new ShellView { DataContext = shell }, async () =>
        {
            await shell.Context.Queries.WhenIdleAsync();
            await shell.Navigator.Current.WhenSettledAsync();
        });
    }

    /// <summary>The shell view model alone (for tests that drive it).</summary>
    public static ShellViewModel BuildShell(FixtureSet fixtures, bool collapsed = false)
    {
        var api = FixtureApi(fixtures.Handler());
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        // Detached prefs: a scene's sidebar state must never leak into another scene or a prefs file.
        var prefs = DesktopPrefs.InMemory();
        prefs.SidebarCollapsed = collapsed;
        return new ShellViewModel(prefs, connection, context, AppModules.All);
    }
}
