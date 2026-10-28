using Astra.Core;
using Astra.Desktop.Components.Tray;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Astra.Desktop.Platform;
using Astra.Desktop.Platform.Tray;
using Astra.Desktop.Shell;
using AtomUI;
using AtomUI.Desktop.Controls;
using Avalonia;
using TKCode.Arc;
using TKCode.Arc.Fonts;

namespace Astra.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--smoke")) return Smoke();
        // One app per data directory (Electron's requestSingleInstanceLock): a second launch shows the running app's window and exits.
        // Restarting into the tray: the predecessor still holds the lock (and its tray icon) for a moment.
        TrayRelaunch.WaitForPredecessor(args);
        var root = new AstraPaths().Root;
        var instance = SingleInstance.TryClaim(root);
        if (instance is null)
        {
            SingleInstance.SignalRunning(root, TimeSpan.FromSeconds(3));
            return 0;
        }
        Instance = instance;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e)
        {
            // Electron: main().catch → dialog.showErrorBox + app.exit(1). Without this a start-up failure is a silent exit.
            FatalErrorBox.Show(e);
            return 1;
        }
        finally
        {
            instance.Dispose();
        }
    }

    /// <summary>This process's claim on the data directory; <c>App</c> starts answering later launches once the window exists.</summary>
    public static SingleInstance? Instance { get; private set; }

    /// <summary>
    /// Native AOT smoke test (CI runs it on the published binary): bring up Avalonia and the Arc theme and load the
    /// window's XAML and every page without showing it. A publish that skipped the XAML compile, or trimmed something away, throws
    /// here instead of on the user's first launch (plan avalonia-client §11).
    /// </summary>
    private static int Smoke()
    {
        try
        {
            BuildAvaloniaApp().SetupWithoutStarting();
            // The shell never connects here (no manager): a smoke run must not start or stop anybody's server.
            AppServices.Current.InitializeRuntime(null);
            var shell = AppServices.Current.CreateShell();
            var window = new MainWindow(shell);
            // The tray panel and its window (they are not a route): every state the panel draws builds, and the Settings preview seam is set.
            TrayPreviewControl.Install();
            var panel = new TrayPanelView();
            foreach (var state in new[] { new TrayPanelState(true, 1, null, true, "yes"), new TrayPanelState(false, null, null, false, null), new TrayPanelState(false, null, TrayActivity.Starting, false, null) })
                panel.Update(TrayPrefs.Default, state, TrayData.Empty, DateTimeOffset.UtcNow, TimeSpan.Zero);
            _ = new TrayPopupWindow(panel);
            // Every route's page: its title resolves and its view builds (the placeholder until the real page registers).
            foreach (var info in RouteInfo.Routes)
            {
                shell.Navigator.Navigate(info.Route, info.WebPath.Contains(":id") ? "smoke" : null);
                var page = shell.Navigator.Current;
                if (string.IsNullOrEmpty(page.Title) || page.Title == RouteInfo.Of(info.Route).TitleKey)
                    throw new InvalidOperationException($"route {info.Route} has no title");
                _ = shell.Navigator.CreateView(page);
            }
            Console.WriteLine("smoke ok");
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"smoke failed: {e}");
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .WithArcFonts()
        .UseAtomUI(builder =>
        {
            builder.UseDesktopControls();
            builder.UseArc();
            builder.UseArcAdvancedCharts();
            builder.UseArcBlocks();
            // Last: the first window must already have the user's light / dark choice and accent (no flash).
            AppServices.Current.Theme.Configure(builder);
        });
}
