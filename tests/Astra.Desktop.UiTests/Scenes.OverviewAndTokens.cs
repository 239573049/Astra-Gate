using System.Net;
using System.Text;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Pages.Overview;
using Astra.Desktop.Pages.Tokens;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Astra.Desktop.Views;
using Avalonia.VisualTree;

namespace Astra.Desktop.UiTests;

public static partial class Scenes
{
    /// <summary>
    /// WP1 scenes (plan avalonia-page-inventory §5.4): the overview in its states and ranges, the token list with its sheets and
    /// dialogs, and a token's detail. Names are the web capture's where it has one (overview__data, tokens__data, tokens__empty);
    /// the dialogs open after the page settled (Settled runs the hook, then waits for the overlay). Light / dark come from the harness.
    /// </summary>
    static partial void AddOverviewAndTokens(Dictionary<string, Func<Scene>> d)
    {
        // ----- overview -----
        d["overview__data"] = () => Page(AppRoute.Overview, Fixtures());
        d["overview__data-7d"] = () => Run(AppRoute.Overview, Fixtures(), after: (_, p) => ((OverviewPage)p).SetRange("7d"));
        d["overview__data-30d"] = () => Run(AppRoute.Overview, Fixtures(), after: (_, p) => ((OverviewPage)p).SetRange("30d"));
        d["overview__data-scrolled"] = () => Scrolled(Page(AppRoute.Overview, Fixtures()));
        d["overview__data-mid"] = () => Scrolled(Page(AppRoute.Overview, Fixtures()), 470);
        d["overview__providers-tab"] = () => Scrolled(Run(AppRoute.Overview, Fixtures(), after: (v, _) => SelectTab(v, 1)), 470);
        d["overview__models-tab"] = () => Scrolled(Run(AppRoute.Overview, Fixtures(), after: (v, _) => SelectTab(v, 2)), 470);
        d["overview__empty"] = () => Page(AppRoute.Overview, Fixtures().Override("/api/stats/summary", ZeroSummary));
        d["overview__client-empty"] = () => Run(AppRoute.Overview, Fixtures().Override("/api/stats/summary", ZeroSummary), after: (_, p) => ((OverviewPage)p).SetClient("claude-code"));
        d["overview__error"] = () => Page(AppRoute.Overview, Fixtures().Override("/api/stats/summary", """{"error":"boom"}""", 500));
        d["overview__loading"] = () => Loading(AppRoute.Overview, "/api/stats/");
        // a recent-requests row opens the request's detail over the dashboard (web RequestDetailSheet in Breakdown)
        d["overview__request-detail"] = () => Run(AppRoute.Overview, Fixtures(), after: (_, p) => ((OverviewPage)p).OpenRequest("req_0050"));

        // ----- tokens -----
        d["tokens__data"] = () => Page(AppRoute.Tokens, Fixtures());
        d["tokens__empty"] = () => Page(AppRoute.Tokens, Fixtures().Override("/api/tokens", "[]"));
        d["tokens__loading"] = () => Loading(AppRoute.Tokens, "/api/tokens");
        d["tokens__error"] = () => Page(AppRoute.Tokens, Fixtures().Override("/api/tokens", """{"error":"数据库不可用"}""", 500));
        d["tokens__data-open"] = () => Run(AppRoute.Tokens, Fixtures(), before: (_, p) =>
        {
            ((TokensPage)p).SetOpen("tok_ci", true);
            ((TokensPage)p).SetOpen("tok_pool", true);
        });
        d["tokens__create-sheet"] = () => Run(AppRoute.Tokens, Fixtures(), after: (_, p) => ((TokensPage)p).BeginCreate());
        d["tokens__edit-sheet"] = () => Run(AppRoute.Tokens, Fixtures(), after: (_, p) => ((TokensPage)p).BeginEdit(TokenOf(p, "tok_ci")));
        d["tokens__pool-editor"] = () => Run(AppRoute.Tokens, Fixtures(), after: (_, p) => ((TokensPage)p).BeginEdit(TokenOf(p, "tok_pool")));
        d["tokens__delete-confirm"] = () => Run(AppRoute.Tokens, Fixtures(), after: (v, p) => _ = ((TokensPage)p).Actions.DeleteAsync(TokenOf(p, "tok_ci")));
        d["tokens__reset-confirm"] = () => Run(AppRoute.Tokens, Fixtures(), after: (v, p) => _ = ((TokensPage)p).Actions.ResetAsync(TokenOf(p, "tok_default")));
        d["tokens__copied"] = () => Run(AppRoute.Tokens, Fixtures(), wrap: WithReveal,
            after: (v, p) => _ = ((TokensPage)p).Actions.CopyKeyAsync("tok_default", _ => Task.FromResult(true)));

        // ----- token detail -----
        d["token-detail__data"] = () => Page(AppRoute.TokenDetail, Fixtures(), param: "tok_default");
        d["token-detail__pool"] = () => Page(AppRoute.TokenDetail, Fixtures(), param: "tok_pool");
        d["token-detail__empty"] = () => Page(AppRoute.TokenDetail, Fixtures().Override("/api/stats/summary", ZeroSummary), param: "tok_ci");
        d["token-detail__404"] = () => Page(AppRoute.TokenDetail, Fixtures().Override("/api/tokens/tok_gone", """{"error":"令牌不存在"}""", 404), param: "tok_gone");
        d["token-detail__data-scrolled"] = () => Scrolled(Page(AppRoute.TokenDetail, Fixtures(), param: "tok_default"));
        // opened from the list (Push): the shell's back button, no second one in the page header
        d["token-detail__pushed"] = () => Run(AppRoute.Tokens, Fixtures(), after: (_, p) => ((TokensPage)p).OpenDetail("tok_default"));
    }

    private const string ZeroSummary = """{"range":"today","costUsd":0,"requests":0,"successRate":0,"inputTokens":0,"outputTokens":0,"cacheReadTokens":0,"cacheWriteTokens":0,"reasoningTokens":0,"avgTtftMs":null}""";

    private static Astra.Api.Contracts.TokenDto TokenOf(PageViewModel page, string id)
        => ((TokensPage)page).Tokens.Data!.First(t => t.Id == id);

    /// <summary>Picks a breakdown tab of the overview (the tabs are Arc's; found by walking the tree).</summary>
    private static void SelectTab(ShellView view, int index)
    {
        var tabs = view.GetVisualDescendants().OfType<TKCode.Arc.Tabs>().FirstOrDefault();
        if (tabs is not null) tabs.SelectedIndex = index;
    }

    /// <summary>
    /// A page scene with hooks: <paramref name="before"/> runs right after the page is created (before any data arrived),
    /// <paramref name="after"/> once it settled (then the scene waits for what it opened), real toasts and confirmations
    /// (<c>ArcFeedback</c> over the scene's own view) and an optional wrapper around the fixture handler.
    /// </summary>
    private static Scene Run(AppRoute route, FixtureSet fixtures, Action<ShellView?, PageViewModel>? before = null,
        Action<ShellView, PageViewModel>? after = null, string? param = null, Func<HttpMessageHandler, HttpMessageHandler>? wrap = null)
    {
        ShellView? view = null;
        var handler = fixtures.Handler();
        var api = FixtureApi(wrap is null ? handler : wrap(handler));
        var feedback = new ArcFeedback(() => view);
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, feedback: feedback, clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        var shell = new ShellViewModel(DesktopPrefs.InMemory(), connection, context, AppModules.All);
        shell.Navigator.Navigate(route, param);
        before?.Invoke(null, shell.Navigator.Current);
        view = new ShellView { DataContext = shell };
        return new Scene(view, async () =>
        {
            await shell.Context.Queries.WhenIdleAsync();
            await shell.Navigator.Current.WhenSettledAsync();
            if (after is null) return;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => after(view, shell.Navigator.Current));
            await Task.Delay(600);
            await shell.Context.Queries.WhenIdleAsync();
            await shell.Navigator.Current.WhenSettledAsync();
            await Task.Delay(400);
        });
    }

    /// <summary>The fixtures plus an answer for the reveal endpoint (POST /api/tokens/{id}/reveal).</summary>
    private static HttpMessageHandler WithReveal(HttpMessageHandler inner) => new RevealHandler(inner);

    private sealed class RevealHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/reveal", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"token":"sk-astra-demo-secret"}""", Encoding.UTF8, "application/json") });
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// The scene scrolled once it settled: the window is a fixed 1440x900, and a long page (the overview's breakdown tables) would
    /// otherwise never be captured below the fold. <paramref name="offset"/> null = to the end.
    /// </summary>
    private static Scene Scrolled(Scene scene, double? offset = null) => scene with
    {
        Settled = async () =>
        {
            if (scene.Settled is not null) await scene.Settled();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var scroller = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(scene.View)
                    .OfType<Avalonia.Controls.ScrollViewer>().OrderByDescending(v => v.Extent.Height).FirstOrDefault();
                if (scroller is null) return;
                scroller.Offset = new Avalonia.Vector(0, offset ?? scroller.Extent.Height);
            });
            await Task.Delay(300);
        },
    };

    /// <summary>A page whose requests below <paramref name="hangPrefix"/> never answer: the loading state, captured after a short wait.</summary>
    private static Scene Loading(AppRoute route, string hangPrefix, string? param = null)
    {
        var fixtures = Fixtures();
        var api = FixtureApi(new HangingHandler(fixtures.Handler(), hangPrefix));
        var context = PageContext.Create(api, AvaloniaUiDispatcher.Instance, clock: new FixedClock(fixtures.Now));
        var connection = new ConnectionViewModel(null, api, context.Queries);
        connection.AssumeConnected("http://127.0.0.1:17321");
        var shell = new ShellViewModel(DesktopPrefs.InMemory(), connection, context, AppModules.All);
        shell.Navigator.Navigate(route, param);
        return new Scene(new ShellView { DataContext = shell }, () => Task.Delay(400));
    }

    /// <summary>Forwards everything, except that paths starting with the prefix wait forever (until cancelled).</summary>
    private sealed class HangingHandler(HttpMessageHandler inner, string prefix) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) await Task.Delay(Timeout.Infinite, cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
