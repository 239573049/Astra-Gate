namespace Astra.Desktop.Core.Shell;

/// <summary>Window theme preference (desktop/src/shared/chrome.ts <c>ThemeSource</c>).</summary>
public enum ThemeSource { System, Light, Dark }

/// <summary>Result of starting / restarting the service (bridge <c>startService</c> / <c>restartService</c>).</summary>
public sealed record HostResult(bool Ok, string? Error = null);

/// <summary>Launch at login as Settings and the tray show it (desktop/src/shared/loginItem.ts <c>LoginItemState</c>).</summary>
/// <param name="Supported">False in unpackaged dev runs: a login item needs the stable executable of an installed app.</param>
/// <param name="Enabled">Whether the entry is on.</param>
public sealed record LoginItemState(bool Supported, bool Enabled);

/// <summary>What <see cref="IDesktopHost.CheckForUpdateAsync"/> found (desktop/src/update.ts <c>UpdateSummary</c>).</summary>
/// <param name="Message">Human-readable result for dialogs.</param>
/// <param name="Action">What the user can do right now.</param>
public sealed record UpdateSummary(string Message, UpdateAction Action);

/// <summary>What an update can do now. Electron's names; Avalonia tracks keep them (<c>DesktopDmg</c> = self-update of the .app).</summary>
public enum UpdateAction { None, Server, DesktopDmg, DesktopNpm, DesktopDownload }

/// <summary>What an in-app apply did (<c>UiApplyOutcome</c>); <c>Downloading</c> means a newer app is still being fetched, which is not "up to date".</summary>
public enum UpdateApplyOutcome { Server, Desktop, Download, Downloading, None }

/// <summary>One context-menu entry (desktop/src/shared/chrome.ts <c>ContextMenuItem</c>); <see cref="Separator"/> ignores the rest.</summary>
public sealed record ContextMenuItem(string? Id = null, string? Label = null, bool Enabled = true, bool Checked = false, bool Checkbox = false, bool Separator = false);

/// <summary>What the menu bar shows next to the tray icon (desktop/src/shared/prefs.ts).</summary>
public enum TrayTitleMode { None, Requests, Tokens, Cost, Quota, Alerts }

/// <summary>Tray panel blocks, in display order.</summary>
public enum TraySection { Overview, Cost, Chart, TopModel, CompareYesterday, CacheHitRate, Quota, Clients }

/// <summary>Tray panel configuration, edited on Settings › Tray panel.</summary>
public sealed record TrayPrefs(TrayTitleMode Title, IReadOnlyDictionary<TraySection, bool> Sections)
{
    /// <summary>The defaults of desktop/src/shared/prefs.ts <c>DEFAULT_TRAY_PREFS</c>.</summary>
    public static TrayPrefs Default { get; } = new(TrayTitleMode.Requests, new Dictionary<TraySection, bool>
    {
        [TraySection.Overview] = true,
        [TraySection.Cost] = true,
        [TraySection.Chart] = true,
        [TraySection.TopModel] = true,
        [TraySection.CompareYesterday] = false,
        [TraySection.CacheHitRate] = false,
        [TraySection.Quota] = true,
        [TraySection.Clients] = false,
    });
}

/// <summary>What the tray panel shows about the service (desktop/src/shared/trayPanel.ts <c>TrayPanelState</c>).</summary>
public sealed record TrayPanelState(bool Running, int? Port, TrayActivity? Activity, bool ApiVersionMismatch, string? UpdateAvailable);

/// <summary>What the service is doing right now.</summary>
public enum TrayActivity { Starting, Stopping, Restarting }

/// <summary>Commands the tray panel sends to the shell (<c>TrayPanelCommand</c>).</summary>
public enum TrayCommand { Open, OpenSettings, Start, Stop, Restart, Quit, Hide, Menu, CheckUpdates }

/// <summary>Menu / shortcut commands the shell sends to the page host (web shell/commands.ts <c>MenuCommand</c>). Navigation is "nav:&lt;target&gt;".</summary>
public static class MenuCommands
{
    /// <summary>Focus the page's search box.</summary>
    public const string Find = "find";

    /// <summary>Reload data.</summary>
    public const string Refresh = "refresh";

    /// <summary>Open the new-provider flow.</summary>
    public const string NewProvider = "new-provider";

    /// <summary>Collapse / expand the sidebar.</summary>
    public const string ToggleSidebar = "toggle-sidebar";

    /// <summary>Settings › Tray panel.</summary>
    public const string SettingsTray = "settings:tray";

    /// <summary>"nav:" + target (overview, requests, traces, clients, tokens, providers, models, plugins, privacy, smartRouting, settings).</summary>
    public static string Nav(string target) => "nav:" + target;
}

/// <summary>The tray part of <see cref="IDesktopHost"/> (bridge <c>tray</c>).</summary>
public interface ITrayHost
{
    /// <summary>Current prefs.</summary>
    Task<TrayPrefs> GetPrefsAsync();

    /// <summary>Saves prefs; resolves with what was stored.</summary>
    Task<TrayPrefs> SetPrefsAsync(TrayPrefs prefs);

    /// <summary>Prefs changed (from any window).</summary>
    event Action<TrayPrefs>? PrefsChanged;

    /// <summary>Service state for the panel.</summary>
    Task<TrayPanelState> GetStateAsync();

    /// <summary>Service state changed.</summary>
    event Action<TrayPanelState>? StateChanged;

    /// <summary>A panel button was pressed.</summary>
    Task CommandAsync(TrayCommand command);

    /// <summary>Panel only: menu-bar title (macOS) and tooltip detail line.</summary>
    void SetTitle(string title, string detail);

    /// <summary>Panel only: content height in DIPs.</summary>
    void Resize(double height);

    /// <summary>The panel was shown or hidden.</summary>
    event Action<bool>? VisibilityChanged;
}

/// <summary>
/// Everything <c>window.__ASTRA__</c> offers pages (desktop/src/bridge.d.ts, web/src/shell/bridge.ts), as an interface so
/// pages stay testable. The Avalonia app provides the real implementation (plan avalonia-client §6); <see cref="NullDesktopHost"/>
/// is the headless one. Events may fire on any thread; view models marshal through the UI dispatcher.
/// </summary>
public interface IDesktopHost
{
    /// <summary>"darwin" | "win32" | "linux" (Node's names, which the web code compares against).</summary>
    string Platform { get; }

    /// <summary>The app's version.</summary>
    string Version { get; }

    /// <summary>The running server's API major differs from the one this app expects (EXPECTED_API_MAJOR).</summary>
    bool ApiVersionMismatch { get; }

    /// <summary>System accent color "#rrggbb", or null when unavailable.</summary>
    Task<string?> GetAccentColorAsync();

    /// <summary>The system accent color changed.</summary>
    event Action<string?>? AccentColorChanged;

    /// <summary>Applies the window theme preference.</summary>
    Task<bool> SetThemeSourceAsync(ThemeSource source);

    /// <summary>Opens a file or folder with the OS (bridge <c>openPath</c>).</summary>
    Task<bool> OpenPathAsync(string path);

    /// <summary>Opens the logs folder.</summary>
    Task<bool> RevealLogsAsync();

    /// <summary>Starts the service.</summary>
    Task<HostResult> StartServiceAsync();

    /// <summary>Restarts the service.</summary>
    Task<HostResult> RestartServiceAsync();

    /// <summary>Launch-at-login state.</summary>
    Task<LoginItemState> GetLoginItemAsync();

    /// <summary>Turns launch-at-login on or off; resolves with the state afterwards (unchanged when the system refused).</summary>
    Task<LoginItemState> SetLoginItemAsync(bool enabled);

    /// <summary>The login item changed.</summary>
    event Action<LoginItemState>? LoginItemChanged;

    /// <summary>Checks for an update (server and/or app, per install track).</summary>
    Task<UpdateSummary> CheckForUpdateAsync();

    /// <summary>Applies the pending update.</summary>
    Task<UpdateApplyOutcome> ApplyUpdateAsync();

    /// <summary>Shows a native context menu; resolves with the chosen id, or null when dismissed.</summary>
    Task<string?> ShowContextMenuAsync(IReadOnlyList<ContextMenuItem> items);

    /// <summary>A menu / shortcut command arrived (<see cref="MenuCommands"/>).</summary>
    event Action<string>? MenuCommandReceived;

    /// <summary>The tray surface.</summary>
    ITrayHost Tray { get; }
}
