using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Components.Settings;

/// <summary>
/// Settings › Gateway › Listen (web <c>ListenRows</c>): listen on this machine only (127.0.0.1) or on the LAN (0.0.0.0). The address goes to
/// config.json and applies at the next start, so a pending change offers a restart of the service. LAN access needs an admin password:
/// without one, choosing it asks for the password first; only this machine may change it.
/// </summary>
public sealed class ListenRows : IRowSet
{
    private readonly UiHost _host;
    private readonly SettingsApi _api;
    private readonly SettingsRow _main = new();
    private readonly Dictionary<string, SettingsRow> _urlRows = [];
    private readonly PasswordRows _password;
    private ControlledSegmented? _scope;
    private ArcButton? _restart;
    private List<Control> _rows = [];
    private bool _restarting;
    private string _signature = "";

    /// <summary>Creates the row set.</summary>
    public ListenRows(UiHost host, SettingsApi api)
    {
        _host = host;
        _api = api;
        _password = new PasswordRows(async pw => await _host.TryAsync(async () =>
        {
            await _api.SetAdminPasswordAsync(pw);
            _host.Success(Loc.T("settings.remote.passwordSaved"));
        }));
        _password.RowsChanged += () => RowsChanged?.Invoke();
        _rows = [_main];
    }

    /// <inheritdoc />
    public IReadOnlyList<Control> Rows => _rows;

    /// <inheritdoc />
    public event Action? RowsChanged;

    /// <summary>Takes the current queries' state; builds, updates or replaces rows.</summary>
    public void Update()
    {
        _main.Label = Loc.T("settings.listen");
        var s = _api.Listen.Data;
        if (s is null)
        {
            _main.Detail = _api.Listen.IsError ? UiHost.ErrorText(_api.Listen.Error!) : null;
            _main.Trailing.Clear();
            _scope = null;
            _restart = null;
            _signature = "";
            if (_api.Listen.IsLoading || !_api.Listen.IsError) _main.Trailing.Add(Ui.Spinner());
            Replace([_main]);
            return;
        }

        var fromOtherDevice = _api.Auth.Data?.Required == true;
        _main.Detail = ListenLogic.DetailText(s, fromOtherDevice, isDesktop: true);

        var showControls = ListenLogic.ControlsVisible(s, fromOtherDevice);
        var showRestart = showControls && ListenLogic.RestartVisible(s, isDesktop: true);
        var signature = $"{showControls}|{showRestart}";
        if (signature != _signature)
        {
            _signature = signature;
            _main.Trailing.Clear();
            _scope = null;
            _restart = null;
            if (showRestart)
            {
                _restart = Ui.Button(Loc.T("settings.listen.restart"), () => _ = RestartAsync(), loading: _restarting);
                _main.Trailing.Add(_restart);
            }
            if (showControls)
            {
                var scope = ListenLogic.ScopeOf(s.ConfiguredHost);
                _scope = new ControlledSegmented(
                    [("local", Loc.T("settings.listen.local")), ("lan", Loc.T("settings.listen.lan"))], scope == ListenScope.Local ? "local" : "lan");
                Avalonia.Automation.AutomationProperties.SetName(_scope, Loc.T("settings.listen"));
                _scope.Chose += v => _ = ChooseAsync(v == "lan" ? ListenScope.Lan : ListenScope.Local);
                _main.Trailing.Add(_scope);
            }
        }
        _scope?.SetValue(ListenLogic.ScopeOf(s.ConfiguredHost) == ListenScope.Local ? "local" : "lan");

        var rows = new List<Control> { _main };
        foreach (var url in s.NetworkUrls)
        {
            if (!_urlRows.TryGetValue(url, out var row))
            {
                row = RowBuilder.Row(Loc.T("settings.listen.networkUrl"), $"{url}/v1",
                    Ui.IconButton(LucideIconKind.Copy, Loc.T("common.copy"), () => _ = _host.CopyAsync($"{url}/v1")));
                _urlRows[url] = row;
            }
            rows.Add(row);
        }
        if (ListenLogic.PasswordRowVisible(s))
        {
            _password.Update(s.PasswordSet, hideChange: fromOtherDevice);
            rows.AddRange(_password.Rows);
        }
        Replace(rows);
    }

    private void Replace(List<Control> rows)
    {
        if (_rows.Count == rows.Count && _rows.Zip(rows).All(p => ReferenceEquals(p.First, p.Second))) return;
        _rows = rows;
        RowsChanged?.Invoke();
    }

    private async Task ChooseAsync(ListenScope next)
    {
        if (_api.Listen.Data is not { } s) return;
        switch (ListenLogic.Choose(s, next))
        {
            case ListenLogic.Step.None:
                return;
            case ListenLogic.Step.AskPassword:
                Reset(s);
                await ShowPasswordSheetAsync();
                return;
            case ListenLogic.Step.ConfirmLan:
                if (!await _host.ConfirmAsync(Loc.T("settings.listen.lanConfirm"), Loc.T("settings.listen.lanDetail"), Loc.T("settings.listen.lanAction")))
                {
                    Reset(s);
                    return;
                }
                break;
        }
        await SaveAsync(next);
    }

    private void Reset(ListenDto s) => _scope?.SetValue(ListenLogic.ScopeOf(s.ConfiguredHost) == ListenScope.Local ? "local" : "lan");

    private async Task SaveAsync(ListenScope next)
    {
        var saved = await _host.TryAsync(() => _api.PutListenAsync(ListenLogic.HostFor(next)));
        if (saved is null)
        {
            if (_api.Listen.Data is { } s) Reset(s);
            return;
        }
        if (ListenLogic.SavedNeedsRestartToast(saved)) _host.Success(Loc.T("settings.listen.saved"));
    }

    private async Task RestartAsync()
    {
        _restarting = true;
        if (_restart is not null) _restart.IsLoading = true;
        var result = await _host.Context.Host.RestartServiceAsync();
        _restarting = false;
        if (_restart is not null) _restart.IsLoading = false;
        if (!result.Ok)
        {
            _host.Error(result.Error ?? Loc.T("common.error"));
            return;
        }
        _host.Success(Loc.T("settings.listen.restarted"));
        _host.Context.Queries.Invalidate();
    }

    /// <summary>Opens the "set an admin password first" sheet (asked before LAN access when none exists yet).</summary>
    public Task ShowPasswordSheetAsync() => new ListenPasswordSheet(_host, () => SaveAsync(ListenScope.Lan), _api).ShowAsync();
}

/// <summary>Asked before LAN access when no admin password exists yet: sets it (twice, at least 8 characters), then switches (web <c>PasswordSheet</c>).</summary>
public sealed class ListenPasswordSheet
{
    private readonly UiHost _host;
    private readonly Func<Task> _saved;
    private readonly SettingsApi _api;
    private readonly Sheet _sheet;
    private readonly ArcButton _confirm;
    private readonly TextBlock _mismatch;
    private readonly Input _first, _again;
    private string _password = "";
    private string _repeat = "";

    /// <summary>Builds the sheet; <paramref name="saved"/> runs after the password was stored (the switch to LAN).</summary>
    public ListenPasswordSheet(UiHost host, Func<Task> saved, SettingsApi api)
    {
        _host = host;
        _saved = saved;
        _api = api;

        var first = Ui.Secret(Loc.T("settings.remote.passwordNew"), "", t => { _password = t; Validate(); }, double.NaN, Loc.T("settings.remote.passwordNew"));
        first.HorizontalAlignment = HorizontalAlignment.Stretch;
        first.Width = double.NaN;
        var again = Ui.Secret(Loc.T("settings.listen.passwordRepeat"), "", t => { _repeat = t; Validate(); }, double.NaN, Loc.T("settings.listen.passwordRepeat"));
        again.HorizontalAlignment = HorizontalAlignment.Stretch;
        again.Width = double.NaN;
        _again = again;
        // web: a <form> — Enter in either field submits; the first field has autoFocus.
        foreach (var field in new[] { first, again })
            field.KeyDown += (_, e) =>
            {
                if (e.Key != Avalonia.Input.Key.Enter) return;
                e.Handled = true;
                _ = SubmitAsync();
            };
        _first = first;
        _mismatch = Ui.Note(Loc.T("settings.listen.passwordMismatch"), ArcDesignTokenKind.Danger);
        _mismatch.IsVisible = false;
        var body = Ui.Stack(12, first, again, _mismatch, Ui.Note(Loc.T("settings.listen.lanDetail"), ArcDesignTokenKind.TextSecondary));

        var cancel = Ui.Button(Loc.T("common.cancel"), () => _sheet!.Close(), ButtonVariant.Secondary, size: ControlSize.Medium);
        _confirm = Ui.Button(Loc.T("settings.listen.passwordAction"), () => _ = SubmitAsync(), ButtonVariant.Primary, enabled: false, size: ControlSize.Medium);
        _sheet = new Sheet
        {
            SheetWidth = 420,
            Title = Loc.T("settings.listen.passwordTitle"),
            Description = Loc.T("settings.listen.passwordDetail"),
            Content = body,
            Footer = [cancel, _confirm],
        };
    }

    /// <summary>The sheet itself (scenes open it directly).</summary>
    public Sheet Sheet => _sheet;

    /// <summary>Shows it; completes when it closes.</summary>
    public Task ShowAsync()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _first.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        return _host.ShowAsync(_sheet);
    }

    private void Validate()
    {
        _confirm.IsEnabled = ListenLogic.PasswordValid(_password, _repeat);
        var mismatch = ListenLogic.PasswordMismatch(_password, _repeat);
        _mismatch.IsVisible = mismatch;
        // web: invalid={mismatch} on the repeat field (red border), the message stays the line below.
        _again.IsInvalid = mismatch;
    }

    private async Task SubmitAsync()
    {
        if (!ListenLogic.PasswordValid(_password, _repeat) || _confirm.IsLoading) return;
        _confirm.IsLoading = true;
        var ok = await _host.TryAsync(() => _api.SetAdminPasswordAsync(_password));
        _confirm.IsLoading = false;
        if (!ok) return;
        _host.Success(Loc.T("settings.remote.passwordSaved"));
        _sheet.Close();
        await _saved();
    }
}
