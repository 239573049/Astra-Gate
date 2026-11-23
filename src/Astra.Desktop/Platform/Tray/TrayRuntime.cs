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

    /// <summary>
    /// The clients as the menu needs them, from <c>/api/clients/summary</c> (database reads only; this runs every 10 s). A
    /// server older than that endpoint answers 404, and then the full list stands in.
    /// </summary>
    public async Task<IReadOnlyList<TrayClient>> ClientsAsync()
    {
        try
        {
            return (await api.GetAsync<List<ClientSummaryDto>>("/api/clients/summary") ?? []).Select(c => new TrayClient(c.Kind, c.Name, c.Enabled, c.ProviderId)).ToList();
        }
        catch (ApiException e) when (e.Status == 404)
        {
            return (await api.GetAsync<List<ClientInfoDto>>("/api/clients") ?? []).Select(c => new TrayClient(c.Kind, c.Name, c.Enabled, c.ProviderId)).ToList();
        }
    }

    public async Task<IReadOnlyList<TrayProvider>> ProvidersAsync()
        => (await api.GetAsync<List<ProviderDto>>("/api/providers") ?? []).Select(p => new TrayProvider(p.Id, p.Name, p.Enabled)).ToList();

    public async Task SwitchProviderAsync(string clientKind, string providerId)
        => await api.PutAsync<BindingInput, ClientInfoDto>("/api/clients/" + clientKind + "/binding", new BindingInput(providerId, null));

    /// <summary>The profiles from <c>/api/profiles/summary</c> (the stored names only, no drift check); a server without profiles answers 404 = none.</summary>
    public async Task<IReadOnlyList<TrayProfile>> ProfilesAsync()
    {
        try
        {
            return (await api.GetAsync<List<ProfileSummaryDto>>("/api/profiles/summary") ?? []).Select(p => new TrayProfile(p.Id, p.Name, p.Active)).ToList();
        }
        catch (ApiException e) when (e.Status == 404)
        {
            return [];
        }
    }

    public async Task ApplyProfileAsync(string profileId)
        => await api.PostAsync<ProfilePlanDto>("/api/profiles/" + ApiClient.Enc(profileId) + "/apply");
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
    // The bottom-right update card (reminder, check result, progress) and what it is about.
    private readonly UpdateNotice _notice;
    private UpdateSummary? _offer;
    private bool _progressInBackground;
    private bool _retryChecks;

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
        _notice = new UpdateNotice(() => _main is { IsVisible: true } main ? main : null);
        _notice.Acted += OnNoticeAction;
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
        // Windows: an empty right-click menu (issue #18) opens the panel instead, which has Open and Quit.
        else _icon = new AvaloniaTrayIcon(_app, Activate, ShowPanel);
        // A menu from the first moment (Open, Quit, … for the state before the first probe): the probe takes seconds while the server
        // starts, and on Windows a right click while the icon has no menu opens nothing at all (Avalonia returns before it makes a window).
        Controller.Relabel();
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
        // Whoever started an update (the card, Settings, the tray), its card shows how it goes.
        _services.Host.UpdateProgressChanged += p => Dispatcher.UIThread.Post(() => OnUpdateProgress(p));
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
            // The host's answer names the version per install track (a packaged app's own); the server's status is the fallback.
            if (_updateAvailable && (summary.Version ?? status?.Available) is { Length: > 0 } version)
                MaybeRemind(summary with { Version = version, Notes = summary.Notes ?? status?.Notes });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or ApiException)
        {
            // Offline or no feed: keep the last answer.
        }
    }

    private static UpdateDialogLabels UpdateLabels() => UpdateDialogLabels.For(Loc.Language);

    /// <summary>
    /// Tells the user about a pending update unless the circuit breaker says otherwise (<see cref="UpdateReminder"/>): the update card in the
    /// bottom-right corner — of the main window when it is on screen, else of the screen. Recorded BEFORE it is shown, so a crash or force-quit
    /// while it is up still counts. Not while another card (a check, a running update) is up or an update runs.
    /// </summary>
    private void MaybeRemind(UpdateSummary offer)
    {
        if (_notice.IsShown || _services.UpdateBusy || offer.Version is not { } version) return;
        var prefs = _services.Prefs;
        var now = DateTimeOffset.UtcNow;
        if (UpdateReminder.Decide(prefs.GetExtra(UpdateReminder.Key), version, now) is not null) return;
        prefs.SetExtra(UpdateReminder.Key, UpdateReminder.Record(prefs.GetExtra(UpdateReminder.Key), version, now));
        _offer = offer;
        _notice.Show(UpdateCards.Available(UpdateLabels(), offer, reminder: true, _services.Host.Version));
    }

    /// <summary>The tray's "Check for Updates…" (update.ts <c>checkInteractive</c>): the card says it is checking, then what it found.</summary>
    private async Task InteractiveCheckAsync()
    {
        var t = UpdateLabels();
        // An update is already running: bring its card back instead of a check that would only say "already running".
        if (_services.UpdateBusy && _services.Host.CurrentUpdateProgress is { IsFinished: false } running)
        {
            _progressInBackground = false;
            _notice.Show(UpdateCards.Progress(t, running));
            return;
        }
        _notice.Show(UpdateCards.Checking(t));
        var summary = await _services.Host.CheckForUpdateAsync();
        _updateAvailable = summary.Action != UpdateAction.None;
        _ = Controller.RefreshAsync();
        // Closed while it checked, or replaced (an update started from Settings): leave it.
        if (_notice.Model?.Kind != UpdateCardKind.Checking) return;
        _retryChecks = summary.Error is not null;
        if (summary.Action != UpdateAction.None) _offer = summary;
        _notice.Show(UpdateCards.CheckResult(t, summary, _services.Host.Version));
    }

    /// <summary>A step of a running update: its card, unless the user sent it to the background (it comes back when the update ends).</summary>
    private void OnUpdateProgress(UpdateProgress p)
    {
        if (p.IsFinished) _progressInBackground = false;
        else if (_progressInBackground) return;
        if (p.Stage == UpdateStage.Failed) _retryChecks = false;
        if (p.Stage is UpdateStage.Done or UpdateStage.Relaunching) _offer = null;
        _notice.Show(UpdateCards.Progress(UpdateLabels(), p));
    }

    private void OnNoticeAction(UpdateCardAction action)
    {
        switch (action)
        {
            case UpdateCardAction.Update:
                _ = ApplyFromCardAsync();
                break;
            case UpdateCardAction.Download:
                _notice.Hide();
                OpenUrl(UpdateFeed.DownloadPageUrl);
                break;
            case UpdateCardAction.Skip:
                if (_offer?.Version is { } version)
                    _services.Prefs.SetExtra(UpdateReminder.Key, UpdateReminder.Skip(_services.Prefs.GetExtra(UpdateReminder.Key), version));
                _notice.Hide();
                break;
            case UpdateCardAction.Background:
                _progressInBackground = true;
                _notice.Hide();
                break;
            case UpdateCardAction.Retry:
                if (_retryChecks) _ = InteractiveCheckAsync();
                else _ = ApplyFromCardAsync();
                break;
            default:
                _notice.Hide();
                break;
        }
    }

    /// <summary>
    /// "Update now" / "Retry" on the card: the card turns into the progress at once, then follows the coordinator's steps
    /// (<see cref="OnUpdateProgress"/>), which also say how it ended — failures included.
    /// </summary>
    private async Task ApplyFromCardAsync()
    {
        var t = UpdateLabels();
        _progressInBackground = false;
        _notice.Show(UpdateCards.Progress(t, new UpdateProgress(UpdateStage.Preparing, _offer?.Version)));
        try
        {
            var outcome = await _services.Host.ApplyUpdateAsync();
            // Server / Desktop: the coordinator's "updated" / "restarting" card is up. Download: the host opened the download page.
            if (outcome == UpdateApplyOutcome.Download) _notice.Hide();
            else if (outcome == UpdateApplyOutcome.None) _notice.Show(UpdateCards.CheckResult(t, new UpdateSummary(t.UpToDate, UpdateAction.None)));
        }
        catch (Exception e) when (e is UpdateException or HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            // A refusal before the update started ("already running") has no progress of its own: say so unless a running update's card is up.
            if (_notice.Model is not { Kind: UpdateCardKind.Failed } && _services.Host.CurrentUpdateProgress is not { IsFinished: false })
                _notice.Show(UpdateCards.Progress(t, new UpdateProgress(UpdateStage.Failed, _offer?.Version, Error: e.Message)));
        }
    }

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
        // A card in the screen's corner moves into the window once it is laid out.
        Dispatcher.UIThread.Post(_notice.Rehost, DispatcherPriority.Loaded);
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
        // A running update's card goes on in the screen's corner.
        _notice.Rehost();
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
