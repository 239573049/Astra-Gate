using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Core.Tray;

/// <summary>What the service looks like right now (one probe).</summary>
public sealed record TrayProbe(bool Running, int? Port, bool ApiVersionMismatch);

/// <summary>The service side of the tray (the parts of desktop/src/main.ts that call <c>service.ts</c> and the admin API).</summary>
public interface ITrayService
{
    /// <summary>Probes the service.</summary>
    Task<TrayProbe> ProbeAsync();

    /// <summary>Starts (or adopts) the service; throws with a user-facing message on failure.</summary>
    Task StartAsync();

    /// <summary>Stops the service and keeps it stopped until started again.</summary>
    Task StopAsync();

    /// <summary>Stops and starts the service.</summary>
    Task RestartAsync();

    /// <summary>The clients (empty when unreachable).</summary>
    Task<IReadOnlyList<TrayClient>> ClientsAsync();

    /// <summary>The providers (empty when unreachable).</summary>
    Task<IReadOnlyList<TrayProvider>> ProvidersAsync();

    /// <summary>Points a client at a provider; throws on failure.</summary>
    Task SwitchProviderAsync(string clientKind, string providerId);
}

/// <summary>The OS side of the tray: icon, menu, panel window, main window and the odd system action.</summary>
public interface ITrayShell
{
    /// <summary>The icon's menu (rebuilt only when the state changed).</summary>
    void SetMenu(IReadOnlyList<TrayMenuItem> menu);

    /// <summary>The icon's hover text.</summary>
    void SetTooltip(string tooltip);

    /// <summary>Text next to the icon (macOS; ignored elsewhere).</summary>
    void SetTitle(string title);

    /// <summary>Shows the native menu at the icon (macOS right click, the panel's "more" button).</summary>
    void PopUpMenu();

    /// <summary>Opens the panel at the icon (null = at the cursor).</summary>
    void ShowPanel();

    /// <summary>Hides the panel.</summary>
    void HidePanel();

    /// <summary>The panel's content height in DIPs (it is already validated).</summary>
    void SetPanelHeight(double height);

    /// <summary>Shows (creating if needed) the main window, optionally on a page ("nav:&lt;target&gt;" or "settings:tray").</summary>
    void ShowMainWindow(string? command);

    /// <summary>Copies text.</summary>
    void Copy(string text);

    /// <summary>Opens a URL in the browser.</summary>
    void OpenUrl(string url);

    /// <summary>Opens the logs folder.</summary>
    void OpenLogs();

    /// <summary>Turns launch-at-login on or off.</summary>
    void SetOpenAtLogin(bool enabled);

    /// <summary>Runs an interactive update check (dialog with the outcome).</summary>
    Task CheckUpdatesAsync();

    /// <summary>Shows an error dialog.</summary>
    void ShowError(string message, string detail);

    /// <summary>Quits the app (stopping the service if the app started it).</summary>
    void Quit();
}

/// <summary>
/// The tray's state machine and <see cref="ITrayHost"/> (desktop/src/main.ts: <c>refreshTray</c>, <c>runServiceAction</c>, <c>trayCallbacks</c>,
/// <c>runTrayPanelCommand</c>, the <c>astra:tray-*</c> IPC handlers; desktop/src/tray.ts: tooltip / title). It probes the service every
/// 10 s (the platform layer calls <see cref="RefreshAsync"/>), rebuilds the menu only when the state changed, runs one start / stop / restart
/// at a time with the activity shown meanwhile, stores the prefs in the desktop prefs file in Electron's shape, and turns panel buttons and
/// menu items into actions. Everything OS-specific goes through <see cref="ITrayShell"/>, so this is tested without a window.
/// </summary>
public sealed class TrayController : ITrayHost
{
    private readonly ITrayPrefsFile _file;
    private readonly ITrayService _service;
    private readonly ITrayShell _shell;
    private readonly Func<bool?> _openAtLogin;
    private readonly Func<bool> _updateAvailable;
    private readonly string _platform;
    private readonly string? _version;
    private readonly object _gate = new();
    private TrayState _state = TrayState.Initial;
    private string? _menuKey;
    private string? _panelKey;
    private string _baseTooltip = "Astra";
    private TrayTitleText _title = TrayTitleText.None;
    private bool _panelVisible;

    /// <param name="file">The desktop prefs file.</param>
    /// <param name="service">Service probing and control.</param>
    /// <param name="shell">Icon, menu, windows.</param>
    /// <param name="openAtLogin">The login item's state; null where unsupported.</param>
    /// <param name="updateAvailable">Whether the last update check found something.</param>
    /// <param name="platform">"darwin" | "win32" | "linux".</param>
    /// <param name="version">The app version for the menu header.</param>
    public TrayController(ITrayPrefsFile file, ITrayService service, ITrayShell shell, Func<bool?> openAtLogin, Func<bool> updateAvailable, string platform, string? version)
    {
        _file = file;
        _service = service;
        _shell = shell;
        _openAtLogin = openAtLogin;
        _updateAvailable = updateAvailable;
        _platform = platform;
        _version = version;
    }

    /// <summary>The last probed state.</summary>
    public TrayState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    /// <summary>The prefs as stored (defaults when absent or malformed).</summary>
    public TrayPrefs Prefs => TrayPrefsJson.Parse(_file.Get(TrayPrefsJson.TrayKey));

    /// <summary>Whether the panel is on screen.</summary>
    public bool PanelVisible => _panelVisible;

    /// <summary>Menu labels; follows the app's language (Electron used the OS locale).</summary>
    public Func<TrayMenuLabels> Labels { get; set; } = () => TrayMenuLabels.For(I18n.Loc.Language);

    /// <summary>The native menu labels of the "Go To" submenu.</summary>
    public Func<MenuLabels> NavLabels { get; set; } = () => MenuLabels.For(I18n.Loc.Language);

    /// <inheritdoc />
    public event Action<TrayPrefs>? PrefsChanged;

    /// <inheritdoc />
    public event Action<TrayPanelState>? StateChanged;

    /// <inheritdoc />
    public event Action<bool>? VisibilityChanged;

    /// <summary>The menu-bar title changed (the text next to the icon, and the tooltip line).</summary>
    public event Action<TrayTitleText>? TitleChanged;

    /// <inheritdoc />
    public Task<TrayPrefs> GetPrefsAsync() => Task.FromResult(Prefs);

    /// <inheritdoc />
    public Task<TrayPrefs> SetPrefsAsync(TrayPrefs prefs)
    {
        // parseTrayPrefs on the way in: whatever arrives is stored in the canonical shape.
        var next = TrayPrefsJson.Parse(TrayPrefsJson.ToJson(prefs));
        _file.Set(TrayPrefsJson.TrayKey, TrayPrefsJson.ToJson(next));
        PrefsChanged?.Invoke(next);
        return Task.FromResult(next);
    }

    /// <inheritdoc />
    public Task<TrayPanelState> GetStateAsync() => Task.FromResult(State.Panel);

    /// <summary>Whether the one-time "still running in the tray" hint was shown (<c>closeHintShown</c>); marks it shown.</summary>
    public bool TakeCloseHint()
    {
        if (_file.Get(TrayPrefsJson.CloseHintKey) is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<bool>(out var shown) && shown) return false;
        _file.Set(TrayPrefsJson.CloseHintKey, true);
        return true;
    }

    /// <summary>Re-probes the service and rebuilds the menu (<c>refreshTray</c>). <paramref name="force"/> rebuilds even when nothing changed.</summary>
    public async Task RefreshAsync(bool force = false)
    {
        TrayProbe probe;
        try
        {
            probe = await _service.ProbeAsync();
        }
        catch (Exception)
        {
            probe = new TrayProbe(false, null, false);
        }
        IReadOnlyList<TrayClient> clients = [];
        IReadOnlyList<TrayProvider> providers = [];
        if (probe.Running && probe.Port is not null)
        {
            var c = Safe(_service.ClientsAsync);
            var p = Safe(_service.ProvidersAsync);
            clients = await c;
            providers = await p;
        }
        lock (_gate)
        {
            // The activity belongs to RunServiceAction: a poll finishing mid-transition must not clear it.
            _state = new TrayState(probe.Running, probe.Port, probe.Running && probe.ApiVersionMismatch, clients, providers,
                _updateAvailable() ? "yes" : null, _state.Activity, _openAtLogin());
        }
        Publish(force);
    }

    private static async Task<IReadOnlyList<T>> Safe<T>(Func<Task<IReadOnlyList<T>>> load)
    {
        try
        {
            return await load();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Rebuilds the menu / tooltip when the state changed and tells the panel (<c>tray.update</c> + <c>pushTrayPanelState</c>).</summary>
    private void Publish(bool force = false)
    {
        TrayState state;
        lock (_gate) state = _state;
        var key = state.Key();
        if (force || key != _menuKey)
        {
            _menuKey = key;
            var labels = Labels();
            _baseTooltip = TrayMenuModel.Tooltip(state, labels);
            _shell.SetTooltip(Tooltip());
            _shell.SetMenu(TrayMenuModel.Build(state, labels, _platform, _version, NavLabels()));
        }
        var panel = state.Panel;
        var panelKey = $"{panel.Running}|{panel.Port}|{panel.Activity}|{panel.ApiVersionMismatch}|{panel.UpdateAvailable}";
        if (panelKey == _panelKey) return;
        _panelKey = panelKey;
        StateChanged?.Invoke(panel);
    }

    /// <summary>The language changed: the menu's labels with it.</summary>
    public void Relabel() => Publish(force: true);

    private string Tooltip() => _title.Detail.Length > 0 ? $"{_baseTooltip} · {_title.Detail}" : _baseTooltip;

    /// <summary>Runs one start / stop / restart, showing progress meanwhile (<c>runServiceAction</c>); ignored while another runs.</summary>
    public async Task RunServiceActionAsync(TrayActivity activity)
    {
        lock (_gate)
        {
            if (_state.Activity is not null) return;
            _state = _state with { Activity = activity };
        }
        Publish();
        try
        {
            await (activity switch
            {
                TrayActivity.Starting => _service.StartAsync(),
                TrayActivity.Stopping => _service.StopAsync(),
                _ => _service.RestartAsync(),
            });
        }
        catch (Exception e)
        {
            _shell.ShowError(activity == TrayActivity.Stopping ? "Failed to stop the Astra service" : "Failed to start the Astra service", e.Message);
        }
        finally
        {
            lock (_gate) _state = _state with { Activity = null };
        }
        await RefreshAsync();
    }

    /// <inheritdoc />
    public async Task CommandAsync(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Open:
                _shell.HidePanel();
                _shell.ShowMainWindow(null);
                return;
            case TrayCommand.OpenSettings:
                _shell.HidePanel();
                _shell.ShowMainWindow(MenuCommands.SettingsTray);
                return;
            case TrayCommand.Start:
                await RunServiceActionAsync(TrayActivity.Starting);
                return;
            case TrayCommand.Stop:
                await RunServiceActionAsync(TrayActivity.Stopping);
                return;
            case TrayCommand.Restart:
                await RunServiceActionAsync(TrayActivity.Restarting);
                return;
            case TrayCommand.CheckUpdates:
                _shell.HidePanel();
                await CheckUpdatesAsync();
                return;
            case TrayCommand.Menu:
                _shell.HidePanel();
                _shell.PopUpMenu();
                return;
            case TrayCommand.Hide:
                _shell.HidePanel();
                return;
            case TrayCommand.Quit:
                _shell.Quit();
                return;
        }
    }

    /// <summary>A menu item was chosen (<c>trayCallbacks</c>).</summary>
    public async Task OnMenuAsync(TrayMenuAction action)
    {
        var state = State;
        switch (action.Verb)
        {
            case TrayMenuVerb.OpenWindow:
                _shell.ShowMainWindow(null);
                break;
            case TrayMenuVerb.ShowPanel:
                _shell.ShowPanel();
                break;
            case TrayMenuVerb.Navigate when action.Arg is { } target:
                _shell.ShowMainWindow(MenuCommands.Nav(target));
                break;
            case TrayMenuVerb.Start:
                await RunServiceActionAsync(TrayActivity.Starting);
                break;
            case TrayMenuVerb.Stop:
                await RunServiceActionAsync(TrayActivity.Stopping);
                break;
            case TrayMenuVerb.Restart:
                await RunServiceActionAsync(TrayActivity.Restarting);
                break;
            case TrayMenuVerb.CopyApiAddress when state.Port is { } port:
                _shell.Copy(ApiBase(port));
                break;
            case TrayMenuVerb.OpenInBrowser when state.Port is { } port:
                _shell.OpenUrl(ApiBase(port) + "/");
                break;
            case TrayMenuVerb.OpenLogs:
                _shell.OpenLogs();
                break;
            case TrayMenuVerb.SwitchProvider when action.Arg is { } kind && action.Arg2 is { } providerId:
                if (state.Port is null) break;
                try
                {
                    await _service.SwitchProviderAsync(kind, providerId);
                }
                catch (Exception e)
                {
                    _shell.ShowError($"Failed to switch provider for {kind}", e.Message);
                }
                // Forced: on failure the native radio item has already moved to the clicked provider.
                await RefreshAsync(force: true);
                break;
            case TrayMenuVerb.ToggleOpenAtLogin:
                _shell.SetOpenAtLogin(action.Flag);
                await RefreshAsync(force: true);
                break;
            case TrayMenuVerb.CheckUpdates:
                await CheckUpdatesAsync();
                break;
            case TrayMenuVerb.Quit:
                _shell.Quit();
                break;
        }
    }

    private async Task CheckUpdatesAsync()
    {
        try
        {
            await _shell.CheckUpdatesAsync();
        }
        finally
        {
            await RefreshAsync();
        }
    }

    /// <inheritdoc />
    public void SetTitle(string title, string detail)
    {
        var next = TrayTitle.Sanitize(title, detail);
        if (next == _title) return;
        _title = next;
        _shell.SetTitle(next.Title);
        // Tooltip only: rebuilding the menu for a title tick would churn it.
        _shell.SetTooltip(Tooltip());
        TitleChanged?.Invoke(next);
    }

    /// <inheritdoc />
    public void Resize(double height)
    {
        if (TrayPanelLayout.ContentHeight(height) is { } h) _shell.SetPanelHeight(h);
    }

    /// <summary>The platform layer reports the panel being shown / hidden.</summary>
    public void OnPanelVisibility(bool visible)
    {
        if (_panelVisible == visible) return;
        _panelVisible = visible;
        VisibilityChanged?.Invoke(visible);
    }

    private static string ApiBase(int port) => $"http://127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
