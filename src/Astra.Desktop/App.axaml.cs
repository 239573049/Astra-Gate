using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Components.Tray;
using Astra.Desktop.Platform;
using Astra.Desktop.Platform.Tray;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Astra.Desktop.Theme;

namespace Astra.Desktop;

public class App : Application
{
    private readonly CancellationTokenSource _stop = new();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = AppServices.Current;
            var manager = ServiceHost.Create(services.Paths);
            var args = desktop.Args ?? [];
            // Restarted into the tray by the previous run (TrayRelaunch): its server is ours, and it showed this page.
            manager.InheritedServerPid = TrayRelaunch.OwnedServer(args);
            services.InitializeRuntime(manager);
            var shell = services.CreateShell();
            if (TrayRelaunch.Route(args) is { } page) shell.Navigator.Navigate(page.Route, page.Param);
            // Built when it is first shown, not at start: a tray-only process that never shows it stays smaller (AOT Astra.app on macOS,
            // measured 2026-10-30: 55–61 → 50–51 MB — the native window, the shell's views and a 1024 px Dock icon render).
            MainWindow CreateWindow()
            {
                var window = new MainWindow(shell);
                services.MainWindow = window;
                AppMenu.Install(this, window, services.Host);
                if (OperatingSystem.IsMacOS()) MacApp.SetDockIcon(() => DockIcon.Render(Components.Kit.AppAssets.Logo));
                return window;
            }
            // A tray app: the last window closing (to the tray) must not end the process; Quit does (plan avalonia-client WP10).
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            // Launched by the login item: start in the tray / menu bar with no window (Electron: no window and no Dock icon).
            var hidden = LoginItemService.LaunchedAtLogin(desktop.Args ?? []);
            TrayPreviewControl.Install();
            var tray = new TrayRuntime(this, desktop, services, shell, CreateWindow);
            if (!hidden) desktop.MainWindow = tray.MainWindow;
            tray.Start();
            if (hidden) tray.StartHidden();
            // A second launch (Dock, Start menu, launcher) brings this window forward (Electron's 'second-instance').
            Program.Instance?.Listen(() => Dispatcher.UIThread.Post(() => tray.ShowMainWindow(null)));
            // The remote-access acknowledgement survives restarts, like the web's localStorage flag.
            RemoteAckStore.Shared.Bind(
                () => services.Prefs.GetExtra(DesktopPrefs.RemoteAckKey) is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var ack) && ack,
                () => services.Prefs.SetExtra(DesktopPrefs.RemoteAckKey, true));

            // The OS accent is only readable now; the theme was configured from the last run's value, so this is usually a no-op.
            _ = services.Theme.AttachAsync();
            // Connect once the window is up: a slow or failing server start shows in the banner instead of delaying the window.
            // Without a window (hidden start) at once: the tray needs the server.
            // Once: a window hidden to the tray and shown again raises Opened again, and two supervisors would race.
            var connecting = false;
            void Connect()
            {
                if (connecting) return;
                connecting = true;
                _ = shell.Connection.RunAsync(_stop.Token);
            }
            if (hidden) Connect();
            else tray.MainWindow.Opened += (_, _) => Connect();
            desktop.Exit += (_, _) =>
            {
                tray.Dispose();
                _stop.Cancel();
                // Restarting into the tray: the successor adopts the server, so it keeps running.
                if (services.HandingOver) return;
                // Off the UI thread: the manager awaits without ConfigureAwait(false), which would deadlock while Exit blocks it.
                Task.Run(manager.StopIfOursAsync).GetAwaiter().GetResult();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
