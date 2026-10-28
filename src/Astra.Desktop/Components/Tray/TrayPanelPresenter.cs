using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Tray;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Threading;

namespace Astra.Desktop.Components.Tray;

/// <summary>
/// Drives a <see cref="TrayPanelView"/> (web pages/TrayPanelPage.tsx for the popup, the preview half of components/tray/TraySettings.tsx):
/// feeds it the tray host's prefs and service state and the <see cref="TrayDataService"/>'s data, redraws on every change and every 30 s
/// (relative times), and turns its buttons into tray commands. The popup flavor follows the panel's visibility: each opening refreshes the
/// numbers and starts on the overview, and only a visible panel polls everything. Runs on the UI thread.
/// </summary>
public sealed class TrayPanelPresenter : IDisposable
{
    private readonly TrayPanelView _view;
    private readonly TrayDataService _data;
    private readonly ITrayHost? _tray;
    private readonly IClock _clock;
    private readonly bool _popup;
    private readonly DispatcherTimer _tick;
    private TrayPrefs _prefs;
    private TrayPanelState _state;
    private bool _visible;
    private bool _renderQueued;
    private bool _disposed;

    /// <summary>The popup: prefs, state and visibility come from <paramref name="tray"/>; commands go back to it.</summary>
    public static TrayPanelPresenter ForPopup(TrayPanelView view, TrayDataService data, ITrayHost tray, IClock clock, IFeedback feedback,
        Func<IReadOnlyList<ContextMenuItem>, Task<string?>> contextMenu, Func<TrayCommand, bool>? intercept = null)
    {
        var p = new TrayPanelPresenter(view, data, tray, clock, popup: true, TrayPrefs.Default, new TrayPanelState(false, null, null, false, null));
        view.Command = c =>
        {
            if (intercept?.Invoke(c) == true) return;
            _ = tray.CommandAsync(c);
        };
        view.RefreshRequested = data.Refresh;
        view.CopyAddress = () => _ = p.CopyAsync(feedback);
        view.SwitchClient = c => _ = p.SwitchAsync(c, feedback, contextMenu);
        view.TabChanged = _ => { };
        _ = p.LoadAsync();
        return p;
    }

    /// <summary>The Settings preview: fixed prefs and state (the tab rebuilds the preview when they change), always "on screen", inert buttons.</summary>
    public static TrayPanelPresenter ForPreview(TrayPanelView view, TrayDataService data, IClock clock, TrayPrefs prefs, TrayPanelState state)
    {
        var p = new TrayPanelPresenter(view, data, null, clock, popup: false, prefs, state);
        view.RefreshRequested = () => data.Refresh();
        p.Configure();
        p.Render();
        return p;
    }

    private TrayPanelPresenter(TrayPanelView view, TrayDataService data, ITrayHost? tray, IClock clock, bool popup, TrayPrefs prefs, TrayPanelState state)
    {
        _view = view;
        _data = data;
        _tray = tray;
        _clock = clock;
        _popup = popup;
        _prefs = prefs;
        _state = state;
        _data.Changed += QueueRender;
        Loc.LanguageChanged += QueueRender;
        if (_tray is not null)
        {
            _tray.PrefsChanged += OnPrefs;
            _tray.StateChanged += OnState;
            _tray.VisibilityChanged += OnVisibility;
        }
        // "Updated 3 minutes ago" and reset times stay current while the panel is open (useTick).
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => QueueRender());
        _tick.Start();
    }

    private async Task LoadAsync()
    {
        var prefs = await _tray!.GetPrefsAsync();
        var state = await _tray.GetStateAsync();
        Dispatcher.UIThread.Post(() =>
        {
            _prefs = prefs;
            _state = state;
            Configure();
            Render();
        });
    }

    private void OnPrefs(TrayPrefs prefs) => Dispatcher.UIThread.Post(() =>
    {
        _prefs = prefs;
        Configure();
        QueueRender();
    });

    private void OnState(TrayPanelState state) => Dispatcher.UIThread.Post(() =>
    {
        var cameUp = state.Running && !_state.Running;
        _state = state;
        Configure();
        // The service came (back) up: refetch right away instead of waiting for the next poll.
        if (cameUp) _data.Refresh();
        QueueRender();
    });

    private void OnVisibility(bool visible) => Dispatcher.UIThread.Post(() =>
    {
        _visible = visible;
        if (visible)
        {
            // Every opening starts on the overview with fresh numbers.
            _view.Tab = TrayTab.Overview;
            _data.Refresh();
        }
        Configure();
        QueueRender();
    });

    private void Configure()
    {
        if (_disposed) return;
        // The popup service also feeds the menu-bar title, so it keeps the title's queries polling while hidden (background).
        _data.Configure(new TrayDataOptions(_prefs, _state.Running, Background: _popup, Live: !_popup || _visible));
    }

    private void QueueRender()
    {
        if (_renderQueued || _disposed) return;
        _renderQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _renderQueued = false;
            if (!_disposed) Render();
        });
    }

    private void Render() => _view.Update(_prefs, _state, _data.Snapshot(), _clock.Now, LocalTime.ZoneOf(_clock));

    private async Task CopyAsync(IFeedback feedback)
    {
        if (TrayPanelLogic.CopyText(_data.Snapshot().Settings) is not { } text) return;
        if (TopLevel.GetTopLevel(_view)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(text);
        feedback.Success(Loc.T("common.copied"));
    }

    private async Task SwitchAsync(ClientInfoDto client, IFeedback feedback, Func<IReadOnlyList<ContextMenuItem>, Task<string?>> contextMenu)
    {
        var providers = _data.Snapshot().Providers;
        if (providers.Count == 0) return;
        var picked = await contextMenu(TrayPanelLogic.SwitchMenu(providers, client, name => Loc.T("tray.disabledProvider", ("name", name))));
        if (picked is null || picked == client.ProviderId) return;
        try
        {
            await _data.SetBindingAsync(client.Kind, picked);
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            feedback.Error(e.Message);
        }
    }

    /// <summary>Stops listening and polling (the data service is the caller's).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tick.Stop();
        _data.Changed -= QueueRender;
        Loc.LanguageChanged -= QueueRender;
        if (_tray is not null)
        {
            _tray.PrefsChanged -= OnPrefs;
            _tray.StateChanged -= OnState;
            _tray.VisibilityChanged -= OnVisibility;
        }
    }
}
