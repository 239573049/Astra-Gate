using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Astra.Desktop.Components.Settings;

/// <summary>
/// Settings › General › Start at login (web <c>StartupSettings</c>). The service row adds or removes the login entry <c>astra autostart</c>
/// manages — a LaunchAgent, a systemd user unit or a Run key value, written by the server — so the gateway comes up at login with no
/// window; only the computer running Astra may change it. The app row (the tray menu's "Launch at Login") is the desktop's own login
/// item: opened at login in the tray, it brings the service up too.
/// </summary>
public sealed class StartupGroupView : LiveView
{
    private readonly SettingsApi _api;
    private SettingsGroup _group = null!;
    private SettingsRow _appRow = null!;
    private ControlledSwitch? _appSwitch;
    private Control? _appSpinner;
    private SettingsRow _serviceRow = null!;
    private ControlledSwitch? _serviceSwitch;
    private Control? _serviceSpinner;
    private LoginItemState? _login;
    private bool _busy;

    /// <summary>Creates the group.</summary>
    public StartupGroupView(PageContext context, SettingsApi api) : base(context)
    {
        _api = api;
        Watch(api.Autostart);
    }

    /// <inheritdoc />
    protected override void Build()
    {
        _group = RowBuilder.Group(Loc.T("settings.startup"), null);
        _appRow = RowBuilder.Row(Loc.T("settings.startup.app"), Loc.T("settings.startup.appHint"));
        _appSwitch = new ControlledSwitch { IsEnabled = false };
        Avalonia.Automation.AutomationProperties.SetName(_appSwitch, Loc.T("settings.startup.app"));
        _appSwitch.ToggleRequested += on => _ = ChangeAppAsync(on);
        _appSpinner = Ui.Spinner();
        _appRow.Trailing.Add(_appSpinner);

        _serviceRow = RowBuilder.Row(Loc.T("settings.startup.service"));
        _serviceSwitch = new ControlledSwitch();
        Avalonia.Automation.AutomationProperties.SetName(_serviceSwitch, Loc.T("settings.startup.service"));
        _serviceSwitch.ToggleRequested += on => _ = ChangeServiceAsync(on);
        _serviceSpinner = Ui.Spinner();
        _group.Rows.Add(_appRow);
        _group.Rows.Add(_serviceRow);
        Content = _group;
    }

    /// <inheritdoc />
    protected override void Refresh()
    {
        // App row: the login item state; the switch replaces the spinner once the host answered.
        if (_login is { } login)
        {
            _appRow.Detail = StartupLogic.AppDetail(login).Text;
            _appRow.Trailing.Clear();
            _appRow.Trailing.Add(_appSwitch!);
            _appSwitch!.SetValue(login.Enabled);
            _appSwitch.IsEnabled = login.Supported && !_busy;
        }

        var s = _api.Autostart.Data;
        _serviceRow.Trailing.Clear();
        if (s is null)
        {
            _serviceRow.Detail = _api.Autostart.IsError ? UiHost.ErrorText(_api.Autostart.Error!) : null;
            if (!_api.Autostart.IsError) _serviceRow.Trailing.Add(_serviceSpinner!);
        }
        else
        {
            _serviceRow.Detail = StartupLogic.ServiceDetail(s).Text;
            if (s.Supported)
            {
                _serviceSwitch!.SetValue(s.Enabled);
                _serviceSwitch.IsEnabled = s.CanChange && !_busy;
                _serviceRow.Trailing.Add(_serviceSwitch);
            }
        }

        var macFooter = StartupLogic.MacFooter(s, hasLoginItem: _login is not null, isMac: Context.Host.Platform == "darwin");
        _group.Footer = macFooter ? Loc.T("settings.startup.macFooter") : null;
    }

    /// <inheritdoc />
    protected override void OnShown()
    {
        Context.Host.LoginItemChanged += OnLoginItemChanged;
        _ = LoadLoginAsync();
    }

    /// <inheritdoc />
    protected override void OnHidden() => Context.Host.LoginItemChanged -= OnLoginItemChanged;

    private void OnLoginItemChanged(LoginItemState state) => Dispatcher.UIThread.Post(() =>
    {
        _login = state;
        RequestRefresh();
    });

    private async Task LoadLoginAsync()
    {
        var state = await Context.Host.GetLoginItemAsync();
        Dispatcher.UIThread.Post(() =>
        {
            _login = state;
            RequestRefresh();
        });
    }

    private async Task ChangeAppAsync(bool enabled)
    {
        _busy = true;
        Refresh();
        try
        {
            var state = await Context.Host.SetLoginItemAsync(enabled);
            _login = state;
            if (!StartupLogic.AppChangeSucceeded(state, enabled)) Host.Error(Loc.T("settings.startup.failed"));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Host.Error(UiHost.ErrorText(e));
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    private async Task ChangeServiceAsync(bool enabled)
    {
        _busy = true;
        Refresh();
        var saved = await Host.TryAsync(() => _api.PutAutostartAsync(enabled));
        _busy = false;
        if (saved is { Enabled: true }) Host.Success(Loc.T("settings.startup.saved"));
        Refresh();
    }
}
