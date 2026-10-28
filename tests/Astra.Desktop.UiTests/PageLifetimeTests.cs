using Astra.Desktop.Shell;
using Avalonia.Headless.XUnit;

namespace Astra.Desktop.UiTests;

/// <summary>
/// A page the navigator dropped must be collectable. Regression: pages hung handlers on the shared queries (which live as long as the
/// query client) and never took them off, so every visit kept its view model and view alive — about 19 MB per tour of the app — and their
/// handlers kept running on every refetch.
/// <para>
/// One dropped instance of a page may outlive it: a <c>Transitions</c> object an Arc control theme shares through a style setter keeps the
/// <c>CollectionChanged</c> handlers of controls that used it (Avalonia / TKCode.Arc, not this app; seen 2026-10-27 with a heap dump), and
/// through their visual tree an old Requests or Traces page. That stays at one per page type; a leak grows with every visit.
/// </para>
/// </summary>
public class PageLifetimeTests
{
    private static readonly (AppRoute Route, string? Param)[] Tour =
    [
        (AppRoute.Overview, null), (AppRoute.Requests, null), (AppRoute.Traces, null), (AppRoute.Tokens, null),
        (AppRoute.TokenDetail, "tok_default"), (AppRoute.Clients, null), (AppRoute.Providers, null),
        (AppRoute.ProviderDetail, "prov_openai"), (AppRoute.Models, null), (AppRoute.ModelDetail, "gpt-4.1"),
        (AppRoute.Plugins, null), (AppRoute.Privacy, null), (AppRoute.SmartRouting, null), (AppRoute.Settings, null),
        (AppRoute.Settings, "notifications"), (AppRoute.Settings, "remote"), (AppRoute.Settings, "tray"),
    ];

    [AvaloniaFact]
    public void Pages_left_behind_are_collected()
    {
        var shell = Scenes.BuildShell(Scenes.Fixtures());
        var window = new MainWindow(shell) { Width = 1280, Height = 800 };
        try
        {
            window.Show();
            SceneCapture.Pump(5);
            var dropped = new List<(string Name, WeakReference Page)>();
            for (var tour = 0; tour < 3; tour++) dropped.AddRange(Visit(shell));
            shell.Navigator.Navigate(AppRoute.Overview);
            SceneCapture.Pump(10);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }
            var kept = dropped.Where(p => p.Page.IsAlive).GroupBy(p => p.Name).Where(g => g.Count() > 1).Select(g => $"{g.Key} ×{g.Count()}").ToList();
            Assert.True(kept.Count == 0, $"pages kept alive after leaving them: {string.Join(", ", kept)}");
        }
        finally
        {
            window.Close();
        }
    }

    private static List<(string Name, WeakReference Page)> Visit(ShellViewModel shell)
    {
        var pages = new List<(string, WeakReference)>();
        foreach (var (route, param) in Tour)
        {
            shell.Navigator.Navigate(route, param);
            SceneCapture.Await(shell.Context.Queries.WhenIdleAsync(TimeSpan.FromSeconds(3)), TimeSpan.FromSeconds(10));
            SceneCapture.Pump(3);
            pages.Add((param is null ? route.ToString() : $"{route}/{param}", new WeakReference(shell.Navigator.Current)));
        }
        return pages;
    }
}
