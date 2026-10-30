using Astra.Core;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Update;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Avalonia.Controls;

namespace Astra.Desktop;

/// <summary>
/// The composition root. Two stages, because the theme must be configured while Avalonia is being built (the first window has its final
/// colors, plan avalonia-client §3 D5) but the rest needs Avalonia running:
/// <list type="number">
/// <item>at construction (before Avalonia): the data dir, <see cref="Prefs"/> and <see cref="Theme"/>;</item>
/// <item><see cref="InitializeRuntime"/> (from <c>App</c> / the smoke test): the one <see cref="ApiClient"/> (its base URL follows the
/// server's port, <see cref="ConnectionViewModel"/> calls <c>SetPort</c>), the one <see cref="QueryClient"/> over the UI thread, the shared stores,
/// <see cref="Feedback"/> (<see cref="ArcFeedback"/> over the main window), <see cref="Host"/> and the <see cref="Connection"/>.</item>
/// </list>
/// Pages never read this class: they get a <see cref="PageContext"/>.
/// </summary>
public sealed class AppServices
{
    private static AppServices? _current;
    private UpdateCoordinator? _updates;

    private AppServices()
    {
        Paths = new AstraPaths();
        Prefs = DesktopPrefs.Load(Paths);
        if (Prefs.Language is { } language) Loc.Language = language;
        Theme = new ThemeService(Prefs);
    }

    /// <summary>The process-wide instance.</summary>
    public static AppServices Current => _current ??= new AppServices();

    public AstraPaths Paths { get; }
    public DesktopPrefs Prefs { get; }
    public ThemeService Theme { get; }

    /// <summary>
    /// This process is restarting itself into the tray (<see cref="Platform.Tray.TrayRelaunch"/>): the server it spawned is handed to the
    /// successor, so exiting must not stop it.
    /// </summary>
    public bool HandingOver { get; set; }

    /// <summary>An update check or apply is running in this process, or a staged app update waits for it to exit.</summary>
    public bool UpdateBusy => _updates?.IsBusy == true || Host?.UpdateStaged == true;

    /// <summary>The main window, once it exists (toasts and dialogs are shown in it).</summary>
    public Window? MainWindow { get; set; }

    /// <summary>Starts, probes and stops the server; null in a run that must never touch one (the smoke test).</summary>
    public ServiceManager? Manager { get; private set; }

    /// <summary>The one admin-API client.</summary>
    public ApiClient Api { get; private set; } = null!;

    /// <summary>The shared query cache, delivering results on the UI thread.</summary>
    public QueryClient Queries { get; private set; } = null!;

    /// <summary>Toasts and confirm dialogs in the main window.</summary>
    public IFeedback Feedback { get; private set; } = null!;

    /// <summary>The bridge the pages use for OS-level things.</summary>
    public AvaloniaDesktopHost Host { get; private set; } = null!;

    /// <summary>The server connection (sidebar status, banners, port following).</summary>
    public ConnectionViewModel Connection { get; private set; } = null!;

    /// <summary>What every page is built with (its navigator is replaced by the shell's).</summary>
    public PageContext Context { get; private set; } = null!;

    /// <summary>Creates the runtime pieces; call once, after Avalonia is initialized.</summary>
    /// <param name="manager">The server supervisor, or null for a run that connects to nothing.</param>
    public void InitializeRuntime(ServiceManager? manager)
    {
        if (Api is not null) return;
        Manager = manager;
        // Until the supervisor reports the real port, the configured one (a drifted port is followed by SetPort).
        var port = manager?.ReadRuntime()?.Port ?? manager?.ReadConfig().Port ?? 1;
        Api = new ApiClient($"http://127.0.0.1:{port}");
        Queries = new QueryClient(AvaloniaUiDispatcher.Instance);
        Feedback = new ArcFeedback(() => MainWindow);
        var login = new LoginItemService(new LoginItemEnvironment(
            OperatingSystem.IsMacOS() ? LoginOs.MacOS : OperatingSystem.IsWindows() ? LoginOs.Windows : LoginOs.Linux,
            Packaged: !ServiceHost.IsDevRun,
            ExecPath: Environment.ProcessPath ?? "",
            Home: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Env: Environment.GetEnvironmentVariable,
            RunKey: OperatingSystem.IsWindows() ? new WindowsRunKey() : null));
        login.RefreshLinuxEntry();
        Host = new AvaloniaDesktopHost(Theme, Paths, login, () => _updates ??= CreateUpdates());
        Connection = new ConnectionViewModel(manager, Api, Queries);
        Host.Connection = Connection;
        Context = PageContext.Create(Api, AvaloniaUiDispatcher.Instance, Feedback, Host, queries: Queries);
    }

    /// <summary>The shell view model over the app's real services.</summary>
    public ShellViewModel CreateShell() => new(Prefs, Connection, Context, AppModules.All);

    private UpdateCoordinator? CreateUpdates()
    {
        if (Manager is null) return null;
        var http = new HttpClient();
        // A packaged app updates as a whole (its own server comes with it); the CLI's install and dev runs update the server.
        var track = SelfUpdate.Track(Paths);
        SelfUpdate.Log(Paths, $"update track: {track} (app {ServiceHost.AppVersion})");
        return new UpdateCoordinator(new UpdateCoordinatorOptions
        {
            Paths = Paths,
            Manager = Manager,
            Http = http,
            AppVersion = ServiceHost.AppVersion,
            ApiBase = () => Connection.ApiBase,
            Track = track,
            Desktop = SelfUpdate.Create(track, Paths, http),
            Log = line => SelfUpdate.Log(Paths, line),
        });
    }
}
