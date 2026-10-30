using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Tray;
using Astra.Desktop.Core;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Astra.Desktop.Core.Update;
using Astra.Desktop.Shell;
using Astra.Desktop.Theme;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Astra.Desktop.Platform.Tray;

/// <summary>The tray prefs inside <c>desktop-prefs.json</c>: top-level keys the shell's <see cref="DesktopPrefs"/> keeps verbatim.</summary>
internal sealed class DesktopPrefsFile(DesktopPrefs prefs) : ITrayPrefsFile
{
    public JsonNode? Get(string key) => prefs.GetExtra(key);

    public void Set(string key, JsonNode? value) => prefs.SetExtra(key, value);
}

/// <summary>The service side of the tray over the app's server supervisor (<see cref="ConnectionViewModel"/>) and the admin API.</summary>
internal sealed class ConnectionTrayService(ServiceManager? manager, ConnectionViewModel connection, ApiClient api) : ITrayService
{
    public async Task<TrayProbe> ProbeAsync()
    {
        if (manager is null)
            return new TrayProbe(connection.IsConnected, connection.ApiBase is { } b && Uri.TryCreate(b, UriKind.Absolute, out var u) ? u.Port : null, connection.ApiVersionMismatch);
        var probe = await manager.ProbeAsync(TimeSpan.FromSeconds(2));
        return new TrayProbe(probe.Running, probe.Port, probe.Running && manager.ComputeMismatch(probe.ApiVersion));
    }

    public async Task StartAsync() => Check(await connection.StartNowAsync());

    public async Task StopAsync() => Check(await connection.StopAsync());

    public async Task RestartAsync() => Check(await connection.RestartAsync());

    private static void Check(HostResult r)
    {
        if (!r.Ok) throw new InvalidOperationException(r.Error ?? "failed");
    }

    public async Task<IReadOnlyList<TrayClient>> ClientsAsync()
        => (await api.GetAsync<List<ClientInfoDto>>("/api/clients") ?? []).Select(c => new TrayClient(c.Kind, c.Name, c.Enabled, c.ProviderId)).ToList();

    public async Task<IReadOnlyList<TrayProvider>> ProvidersAsync()
        => (await api.GetAsync<List<ProviderDto>>("/api/providers") ?? []).Select(p => new TrayProvider(p.Id, p.Name, p.Enabled)).ToList();

    public async Task SwitchProviderAsync(string clientKind, string providerId)
        => await api.PutAsync<BindingInput, ClientInfoDto>("/api/clients/" + clientKind + "/binding", new BindingInput(providerId, null));
}

/// <summary>
/// The tray as the app runs it (desktop/src/main.ts's tray half): the OS icon and its menu, the popup panel, the menu-bar title, the 10 s
/// service probe, close-to-tray for the main window and menu-bar-only mode on macOS. It implements <see cref="ITrayShell"/> for the
/// <see cref="TrayController"/>, which holds the state and decisions and is the <see cref="ITrayHost"/> the pages see.
/// </summary>
internal sealed class TrayRuntime : ITrayShell, IDisposable
{
    private static readonly TimeSpan ProbeEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UpdateCheckEvery = TimeSpan.FromHours(12);
    // In the tray: give memory back shortly after the window or the panel went (its render resources torn down first), then now and again,
    // since the tray's own polling keeps allocating (MemoryRelief).
    private static readonly TimeSpan ReliefDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReliefEvery = TimeSpan.FromMinutes(5);
    // Closed this long: restart into the tray to drop what rendering left (TrayRelaunch; ~250 → ~80 MB). Short, because the app mostly
    // lives in the tray and a hidden successor shows its window at once; long enough for a close-and-reopen to keep the page as it was and
    // for the Windows notice card (NoticeWindow.Lifetime) to be gone. Retried this often while something holds it.
    private static readonly TimeSpan RelaunchAfter = TimeSpan.FromSeconds(15);

    private readonly Application _app;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly AppServices _services;
    private readonly ShellViewModel _shell;
    private readonly Func<Window> _createMain;
    private Window? _main;
    private readonly TrayDataService _data;
    private readonly TrayPanelView _view = new();
    private readonly TrayPopupWindow _popup;
    private readonly TrayPanelPresenter _presenter;
    private readonly DispatcherTimer _probe;
    private readonly DispatcherTimer _updates;
    private readonly DispatcherTimer _relief;
    private readonly DispatcherTimer _relaunch;
    private ITrayIcon? _icon;
    private IReadOnlyList<TrayMenuItem> _menu = [];
    private bool _updateAvailable;
    private bool _quitting;

    public TrayRuntime(Application app, IClassicDesktopStyleApplicationLifetime desktop, AppServices services, ShellViewModel shell, Func<Window> createMain)
    {
        _app = app;
        _desktop = desktop;
        _services = services;
        _shell = shell;
        _createMain = createMain;
        var host = services.Host;
        Controller = new TrayController(new DesktopPrefsFile(services.Prefs), new ConnectionTrayService(services.Manager, services.Connection, services.Api), this,
            () => host.GetLoginItemAsync().GetAwaiter().GetResult() is { Supported: true } s ? s.Enabled : null,
            () => _updateAvailable, host.Platform, host.Version);
        _data = new TrayDataService(services.Api, services.Queries, SystemClock.Instance);
        _popup = new TrayPopupWindow(_view);
        _popup.VisibilityChanged += Controller.OnPanelVisibility;
        // The panel's garbage goes back like the window's (RelieveMemory waits while the window is on screen).
        _popup.VisibilityChanged += visible =>
        {
            if (!visible) DispatcherTimer.RunOnce(RelieveMemory, ReliefDelay);
        };
        _view.ContentHeightChanged += Controller.Resize;
        var feedback = new ArcFeedback(() => _popup);
        _presenter = TrayPanelPresenter.ForPopup(_view, _data, Controller, SystemClock.Instance, feedback,
            items => _popup.HoldOpenAsync(() => ContextMenus.ShowAsync(items, _view)), InterceptMenu);

        // The menu-bar title follows the data and the service state (TrayTitleSource): always computed here, panel open or not.
        _data.Changed += UpdateTitle;
        Controller.StateChanged += _ => Dispatcher.UIThread.Post(UpdateTitle);
        Controller.PrefsChanged += _ => Dispatcher.UIThread.Post(UpdateTitle);
        Loc.LanguageChanged += OnLanguage;

        _probe = new DispatcherTimer(ProbeEvery, DispatcherPriority.Background, (_, _) => _ = Controller.RefreshAsync());
        _updates = new DispatcherTimer(UpdateCheckEvery, DispatcherPriority.Background, (_, _) => _ = CheckQuietlyAsync());
        _relief = new DispatcherTimer(ReliefEvery, DispatcherPriority.Background, (_, _) => RelieveMemory());
        // Not the (interval, priority, callback) constructor: that one starts the timer, and a tray-only start would restart every tick.
        _relaunch = new DispatcherTimer(DispatcherPriority.Background) { Interval = RelaunchAfter };
        _relaunch.Tick += (_, _) => TryRelaunch();
    }

    /// <summary>The state machine and the pages' <see cref="ITrayHost"/>.</summary>
    public TrayController Controller { get; }

    /// <summary>The main window, built on first use: a tray-only start builds none until it is shown.</summary>
    public Window MainWindow
    {
        get
        {
            if (_main is not null) return _main;
            _main = _createMain();
            // Close-to-tray: closing the window only hides it; the app keeps running in the tray / menu bar until Quit.
            _main.Closing += (_, e) =>
            {
                if (_quitting || e.CloseReason == WindowCloseReason.ApplicationShutdown) return;
                e.Cancel = true;
                HideMainWindow();
            };
            return _main;
        }
    }

    /// <summary>Creates the icon and starts polling. Call once, on the UI thread (the main window may not exist yet).</summary>
    public void Start()
    {
        if (OperatingSystem.IsMacOS())
        {
            var mac = new MacTrayIcon(Activate);
            mac.Create();
            _icon = mac;
        }
        else _icon = new AvaloniaTrayIcon(_app, Activate);
        _services.Host.TrayHost = Controller;
        _services.Host.ContextMenu = items => ContextMenus.ShowAsync(items, ActiveOwner());

        // macOS: re-opening the app (Finder, Launchpad, Spotlight) while it lives in the menu bar shows the window (Electron's 'activate').
        if (_app.TryGetFeature<IActivatableLifetime>() is { } activatable)
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) ShowMainWindow(null);
            };
        // A shutdown started elsewhere (⌘Q in the app menu, the OS logging out) is a quit too.
        _desktop.ShutdownRequested += (_, _) => _quitting = true;

        _ = Controller.RefreshAsync();
        _probe.Start();
        _updates.Start();
        // Electron checks 3 s after launch (update.ts startBackgroundCheck), early enough for the reminder to follow the window.
        DispatcherTimer.RunOnce(() => _ = CheckQuietlyAsync(), TimeSpan.FromSeconds(3));
        _services.Host.UpdateApplied += () => Dispatcher.UIThread.Post(() =>
        {
            _updateAvailable = false;
            _ = Controller.RefreshAsync();
        });
        Controller.StateChanged += s => Dispatcher.UIThread.Post(() => _ = MaybeMismatchDialogAsync(s));
    }

    /// <summary>Started at login (<c>--hidden</c>): no window and, on macOS, no Dock icon — the app lives in the menu bar.</summary>
    public void StartHidden()
    {
        if (OperatingSystem.IsMacOS()) MacApp.SetDockVisible(false);
        // Later than after a close: start-up (theme, first page, connecting) leaves the most garbage behind.
        EnterTray(TimeSpan.FromSeconds(20));
    }

    /// <summary>The window went to the tray: its page stops polling and the memory it used goes back to the OS.</summary>
    private void EnterTray(TimeSpan delay)
    {
        _shell.Navigator.SetSuspended(true);
        DispatcherTimer.RunOnce(RelieveMemory, delay);
        _relief.Start();
    }

    private void LeaveTray()
    {
        _relaunch.Stop();
        _relief.Stop();
        _shell.Navigator.SetSuspended(false);
    }

    /// <summary>
    /// Hands over to a fresh tray-only process once the window has been closed for <see cref="RelaunchAfter"/> (the <c>_relaunch</c> timer,
    /// running from the close until the window is shown again) — unless that would cost something: a page holds unsaved edits, an update
    /// is running, or any window is on screen (the main window, the panel, a message box or notice opened from the tray). Retried on every
    /// tick until it goes.
    /// </summary>
    private void TryRelaunch()
    {
        if (_quitting || _services.UpdateBusy || _desktop.Windows.Any(w => w.IsVisible)) return;
        if (_shell.Navigator.HoldsUnsavedWork) return;
        if (!TrayRelaunch.TryStart(TrayRelaunch.Args(Environment.ProcessId, _shell.Navigator.Current, _services.Manager?.OwnedServerPid))) return;
        _services.HandingOver = true;
        Quit();
    }

    private void RelieveMemory()
    {
        // Only while nothing is on screen: a collection is a short pause, and the panel or the window would show it.
        if (_main?.IsVisible == true || _popup.IsVisible) return;
        MemoryRelief.Release();
    }

    private void Activate(ScreenRect? anchor) => Dispatcher.UIThread.Post(() => _popup.Toggle(anchor));

    private void OnLanguage() => Dispatcher.UIThread.Post(() =>
    {
        Controller.Relabel();
        UpdateTitle();
    });

    private void UpdateTitle()
    {
        var title = _data.Title(Controller.State.Running);
        Controller.SetTitle(title.Title, title.Detail);
    }

    /// <summary>
    /// The background check (update.ts <c>startBackgroundCheck</c> + <c>maybeRemind</c>): honours the server's "check automatically" setting,
    /// asks the server to re-read the feed first (<c>refreshUpdateCheck</c>: its own cached status can be hours old), then reminds.
    /// </summary>
    private async Task CheckQuietlyAsync()
    {
        try
        {
            UpdateStatusDto? status = null;
            if (_services.Connection.IsConnected)
            {
                try
                {
                    status = await _services.Api.GetAsync<UpdateStatusDto>("/api/update/status");
                    if (status is { AutoCheck: false }) return;
                    status = await _services.Api.PostAsync<UpdateStatusDto>("/api/update/check") ?? status;
                }
                catch (ApiException)
                {
                    // An older server or a failed feed read: the checker below falls back to the feed itself.
                }
            }
            var summary = await _services.Host.CheckForUpdateAsync();
            _updateAvailable = summary.Action != UpdateAction.None;
            await Controller.RefreshAsync();
            if (_updateAvailable && status?.Available is { Length: > 0 } version) await MaybeRemindAsync(version, status.Notes);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or ApiException)
        {
            // Offline or no feed: keep the last answer.
        }
    }

    private bool _reminding;

    /// <summary>
    /// Tells the user about a pending update unless the circuit breaker says otherwise (<see cref="UpdateReminder"/>). Recorded BEFORE it is
    /// shown, so a crash or force-quit mid-dialog still counts. A visible window gets a dialog; a tray-only app a quiet notification.
    /// </summary>
    private async Task MaybeRemindAsync(string version, string? notes)
    {
        if (_reminding) return;
        var prefs = _services.Prefs;
        var now = DateTimeOffset.UtcNow;
        if (UpdateReminder.Decide(prefs.GetExtra(UpdateReminder.Key), version, now) is not null) return;
        prefs.SetExtra(UpdateReminder.Key, UpdateReminder.Record(prefs.GetExtra(UpdateReminder.Key), version, now));
        var t = UpdateDialogLabels.For(Loc.Language);
        if (_main?.IsVisible != true)
        {
            // The in-app notice (where it is used) opens the interactive check on click, like Electron's notification.
            DesktopNotifier.Show(t.Message(version), t.NotificationBody, () => _ = InteractiveCheckAsync());
            return;
        }
        _reminding = true;
        try
        {
            var choice = await MessageBox.ShowAsync(MessageBoxKind.Info, t.Message(version), UpdateDialogLabels.Notes(notes), [t.Update, t.Later, t.Skip], 0, 1);
            if (choice == 2)
            {
                prefs.SetExtra(UpdateReminder.Key, UpdateReminder.Skip(prefs.GetExtra(UpdateReminder.Key), version));
                return;
            }
            if (choice != 0) return;
            await ApplyWithDialogsAsync(t, announce: true);
        }
        finally
        {
            _reminding = false;
        }
    }

    private async Task ApplyWithDialogsAsync(UpdateDialogLabels t, bool announce)
    {
        try
        {
            var outcome = await _services.Host.ApplyUpdateAsync();
            if (announce && outcome == UpdateApplyOutcome.Server) await MessageBox.ShowAsync(MessageBoxKind.Info, t.Updated, null, [t.Ok]);
        }
        catch (Exception e) when (e is UpdateException or HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            await MessageBox.ShowAsync(MessageBoxKind.Error, t.Failed, e.Message, [t.Ok]);
        }
    }

    /// <summary>The tray's "Check for Updates…" (update.ts <c>checkInteractive</c>): the result in a box, with the action it allows.</summary>
    private async Task InteractiveCheckAsync()
    {
        var t = UpdateDialogLabels.For(Loc.Language);
        var summary = await _services.Host.CheckForUpdateAsync();
        _updateAvailable = summary.Action != UpdateAction.None;
        _ = Controller.RefreshAsync();
        var buttons = new List<string>();
        string? action = summary.Action switch
        {
            UpdateAction.Server or UpdateAction.DesktopNpm => t.UpdateServer,
            UpdateAction.DesktopDownload => t.Download,
            _ => null,
        };
        if (action is not null) buttons.Add(action);
        buttons.Add(t.Ok);
        var choice = await MessageBox.ShowAsync(MessageBoxKind.Info, t.CheckTitle, summary.Message, buttons, 0, buttons.Count - 1);
        if (action is null || choice != 0) return;
        if (summary.Action == UpdateAction.DesktopDownload) OpenUrl(DownloadPageUrl);
        else await ApplyWithDialogsAsync(t, announce: false);
    }

    /// <summary>update.ts <c>DOWNLOAD_PAGE_URL</c>.</summary>
    private const string DownloadPageUrl = "https://astra-gate.si/download";

    private bool _mismatchShown;

    /// <summary>The one-time API-mismatch warning (main.ts <c>showMismatchDialog</c>), beside the banner and the tray line.</summary>
    private async Task MaybeMismatchDialogAsync(TrayPanelState state)
    {
        if (_mismatchShown || !state.ApiVersionMismatch) return;
        _mismatchShown = true;
        string? apiVersion = null;
        if (_services.Manager is { } manager)
        {
            try
            {
                apiVersion = (await manager.ProbeAsync(TimeSpan.FromSeconds(2))).ApiVersion;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
            }
        }
        var t = UpdateDialogLabels.For(Loc.Language);
        await MessageBox.ShowAsync(MessageBoxKind.Warning, t.MismatchTitle, t.MismatchDetail(apiVersion, Core.ReleaseVersion.ExpectedApiMajor), [t.Ok]);
    }

    /// <summary>Windows / Linux cannot pop the tray icon's menu: the panel's "more" button shows it as a popup menu on the panel.</summary>
    private bool InterceptMenu(TrayCommand command)
    {
        if (command != TrayCommand.Menu || OperatingSystem.IsMacOS()) return false;
        _ = _popup.HoldOpenAsync(async () =>
        {
            await ContextMenus.ShowTrayMenuAsync(_menu, _view, a => _ = Controller.OnMenuAsync(a));
            return true;
        });
        return true;
    }

    private Control? ActiveOwner() => _popup.IsVisible || _main is null ? _view : _main.Content as Control ?? _main;

    private TopLevel AnyTopLevel() => _popup.IsVisible || _main is null ? _popup : _main;

    // ---------- ITrayShell ----------

    public void SetMenu(IReadOnlyList<TrayMenuItem> menu)
    {
        _menu = menu;
        _icon?.SetMenu(menu, a => Dispatcher.UIThread.Post(() => _ = Controller.OnMenuAsync(a)));
    }

    public void SetTooltip(string tooltip) => _icon?.SetToolTip(tooltip);

    public void SetTitle(string title) => _icon?.SetTitle(title);

    public void PopUpMenu() => _icon?.PopUpMenu();

    public void ShowPanel() => _popup.ShowPanel(_icon?.Anchor());

    public void HidePanel() => _popup.HidePanel();

    public void SetPanelHeight(double height) => _popup.SetContentHeight(height);

    public void ShowMainWindow(string? command)
    {
        // Built first (it sets the Dock icon), then the Dock entry appears.
        var main = MainWindow;
        if (OperatingSystem.IsMacOS()) MacApp.SetDockVisible(true);
        if (_desktop.MainWindow is null) _desktop.MainWindow = main;
        LeaveTray();
        main.Show();
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Activate();
        if (OperatingSystem.IsMacOS()) MacApp.Activate();
        if (command is not null) _services.Host.RaiseMenuCommand(command);
    }

    private void HideMainWindow()
    {
        var main = MainWindow;
        // macOS: hiding a full-screen window leaves an empty black Space behind; leave full screen first and hide once it has left
        // (Electron: once('leave-full-screen', hideToTray)). The exit animation takes a moment, so wait for the state change.
        if (main.WindowState == WindowState.FullScreen)
        {
            void Left(object? s, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property != Window.WindowStateProperty || main.WindowState == WindowState.FullScreen) return;
                main.PropertyChanged -= Left;
                // One more beat on macOS: the Space switch animation still runs when the state flips.
                DispatcherTimer.RunOnce(HideMainWindow, TimeSpan.FromMilliseconds(OperatingSystem.IsMacOS() ? 700 : 0));
            }
            main.PropertyChanged += Left;
            main.WindowState = WindowState.Normal;
            return;
        }
        main.Hide();
        EnterTray(ReliefDelay);
        // Restarted, so a second close counts from now.
        _relaunch.Stop();
        _relaunch.Start();
        if (OperatingSystem.IsMacOS()) MacApp.SetDockVisible(false);
        // The first close explains where the app went (Electron's showCloseHintOnce): the window and its Dock / taskbar entry vanish.
        if (Controller.TakeCloseHint())
        {
            var labels = Controller.Labels();
            DesktopNotifier.Show(labels.ClosedToTrayTitle, labels.ClosedToTray(_services.Host.Platform));
        }
    }

    public void Copy(string text) => _ = AnyTopLevel().Clipboard?.SetTextAsync(text);

    public void OpenUrl(string url) => _ = AnyTopLevel().Launcher.LaunchUriAsync(new Uri(url));

    public void OpenLogs() => _ = _services.Host.RevealLogsAsync();

    public void SetOpenAtLogin(bool enabled) => _ = _services.Host.SetLoginItemAsync(enabled);

    public Task CheckUpdatesAsync() => InteractiveCheckAsync();

    // Electron: dialog.showErrorBox / showMessageBox — a box of its own, without forcing the main window open.
    public void ShowError(string message, string detail)
        => Dispatcher.UIThread.Post(() => _ = MessageBox.ShowAsync(MessageBoxKind.Error, message, detail, [UpdateDialogLabels.For(Loc.Language).Ok]));

    public void Quit()
    {
        _quitting = true;
        _popup.HidePanel();
        _desktop.Shutdown();
    }

    public void Dispose()
    {
        _probe.Stop();
        _updates.Stop();
        _relief.Stop();
        _relaunch.Stop();
        Loc.LanguageChanged -= OnLanguage;
        _presenter.Dispose();
        _data.Dispose();
        _icon?.Dispose();
    }
}
