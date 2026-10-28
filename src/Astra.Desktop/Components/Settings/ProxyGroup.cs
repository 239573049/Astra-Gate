using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Avalonia.Controls;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Components.Settings;

/// <summary>
/// Outbound proxy for everything the server fetches — upstreams, logins, quota, updates, client installs (web <c>ProxySettings</c>). System
/// and direct apply at once; custom needs a URL first, so its fields save together with an explicit button. The fields keep what is typed
/// across refreshes and only take the server's values when those changed.
/// </summary>
public sealed class ProxyGroup
{
    private readonly UiHost _host;
    private readonly SettingsApi _api;
    private readonly ProxyForm _form;
    private SettingsDto _saved;
    private readonly SettingsGroup _group;
    private readonly SettingsRow _modeRow;
    private readonly ControlledSegmented _mode;
    private readonly SettingsRow _urlRow, _userRow, _passwordRow, _bypassRow;
    private readonly Input _url, _user, _bypass;
    private readonly Input _password;
    private readonly Control _saveBar;
    private readonly TKCode.Arc.Button _save;
    private bool _saving;
    private bool _syncing;

    /// <summary>Creates the group from the current settings.</summary>
    public ProxyGroup(UiHost host, SettingsApi api, SettingsDto settings)
    {
        _host = host;
        _api = api;
        _saved = settings;
        _form = new ProxyForm(settings);

        _group = RowBuilder.Group(Loc.T("settings.proxy"), Loc.T("settings.proxy.footer"));
        _mode = new ControlledSegmented(ProxyForm.Modes.Select(m => (m, Loc.T("settings.proxy.mode." + m))), _form.Mode);
        Avalonia.Automation.AutomationProperties.SetName(_mode, Loc.T("settings.proxy.mode"));
        _mode.Chose += m => _ = ChangeModeAsync(m);
        _modeRow = RowBuilder.Row(Loc.T("settings.proxy.mode"), null, _mode);

        _url = Ui.Field(Loc.T("settings.proxy.url"), _form.Url, t => Typed(() => _form.Url = t), 256, mono: true, placeholder: "http://127.0.0.1:7890");
        _urlRow = RowBuilder.Row(Loc.T("settings.proxy.url"), Loc.T("settings.proxy.urlHint"), _url);
        _user = Ui.Field(Loc.T("settings.proxy.username"), _form.Username, t => Typed(() => _form.Username = t));
        _userRow = RowBuilder.Row(Loc.T("settings.proxy.username"), Loc.T("settings.proxy.optional"), _user);
        _password = Ui.Secret(Loc.T("settings.proxy.password"), "", t => Typed(() => _form.Password = t));
        _passwordRow = RowBuilder.Row(Loc.T("settings.proxy.password"), null, _password);
        _bypass = Ui.Field(Loc.T("settings.proxy.bypass"), _form.Bypass, t => Typed(() => _form.Bypass = t), 256, mono: true, placeholder: "example.com, *.lan");
        _bypassRow = RowBuilder.Row(Loc.T("settings.proxy.bypass"), Loc.T("settings.proxy.bypassHint"), _bypass);
        _save = Ui.Button(Loc.T("common.save"), () => _ = SaveAsync());
        _saveBar = Ui.ButtonBar([_save]);
        Update(settings, settings);
    }

    /// <summary>The group.</summary>
    public SettingsGroup Group => _group;

    private void Typed(Action set)
    {
        if (_syncing) return;
        set();
        UpdateState();
    }

    /// <summary>Takes new settings: fields the server changed are re-read, typing survives otherwise.</summary>
    public void Update(SettingsDto previous, SettingsDto s)
    {
        _syncing = true;
        try
        {
            _form.Sync(_saved, s);
            _saved = s;
            _mode.SetValue(_form.Mode);
            if (_url.Text != _form.Url) _url.Text = _form.Url;
            if (_user.Text != _form.Username) _user.Text = _form.Username;
            if ((_password.Text ?? "") != _form.Password) _password.Text = _form.Password;
            if (_bypass.Text != _form.Bypass) _bypass.Text = _form.Bypass;
            _password.PlaceholderText = s.HasProxyPassword ? "••••••••" : null;
            _passwordRow.Detail = Loc.T(s.HasProxyPassword ? "settings.proxy.passwordSaved" : "settings.proxy.optional");
        }
        finally
        {
            _syncing = false;
        }
        UpdateState();
    }

    private void UpdateState()
    {
        _modeRow.Detail = Loc.T($"settings.proxy.mode.{_form.Mode}.hint");
        _save.IsEnabled = _form.CanSave(_saved, _saving);
        var rows = new List<Control> { _modeRow };
        if (_form.Mode == "custom") rows.AddRange([_urlRow, _userRow, _passwordRow, _bypassRow, _saveBar]);
        RowAssembly.Sync(_group, rows);
    }

    private async Task ChangeModeAsync(string mode)
    {
        var body = _form.ChangeMode(mode, _saved);
        UpdateState();
        if (body is null) return;
        if (await _host.TryAsync(() => _api.PatchSettingsAsync(body)) is null) _form.Mode = _saved.ProxyMode;
        _mode.SetValue(_form.Mode);
        UpdateState();
    }

    private async Task SaveAsync()
    {
        _saving = true;
        _save.IsLoading = true;
        UpdateState();
        var saved = await _host.TryAsync(() => _api.PatchSettingsAsync(_form.SaveBody()));
        _saving = false;
        _save.IsLoading = false;
        if (saved is not null) _host.Success(Loc.T("settings.proxy.saved"));
        UpdateState();
    }
}

/// <summary>
/// Where the plugin market comes from: the Astra site's registry unless set otherwise (an address), or kept to the local copies (empty).
/// Saved with the button; the switch and the reset apply at once (web <c>PluginMarketSettings</c>).
/// </summary>
public sealed class MarketGroup
{
    private readonly UiHost _host;
    private readonly SettingsApi _api;
    private readonly MarketForm _form;
    private SettingsDto _saved;
    private readonly SettingsGroup _group;
    private readonly ControlledSwitch _offline = new();
    private readonly SettingsRow _offlineRow, _urlRow;
    private readonly Input _url;
    private readonly Control _bar;
    private readonly TKCode.Arc.Button _save;
    private readonly TKCode.Arc.Button _reset;
    private bool _busy;
    private bool _syncing;

    /// <summary>Creates the group from the current settings.</summary>
    public MarketGroup(UiHost host, SettingsApi api, SettingsDto settings)
    {
        _host = host;
        _api = api;
        _saved = settings;
        _form = new MarketForm(settings);
        _group = RowBuilder.Group(Loc.T("settings.pluginMarket"), Loc.T("settings.pluginMarket.footer"));
        Avalonia.Automation.AutomationProperties.SetName(_offline, Loc.T("settings.pluginMarket.offline"));
        _offline.ToggleRequested += on => _ = WriteAsync(MarketForm.OfflineBody(on), saved: false);
        _offlineRow = RowBuilder.Row(Loc.T("settings.pluginMarket.offline"), Loc.T("settings.pluginMarket.offlineHint"), _offline);
        _url = Ui.Field(Loc.T("settings.pluginMarket.url"), _form.Url, t => { if (!_syncing) { _form.Url = t; UpdateState(); } }, 320, mono: true, placeholder: settings.PluginMarketDefaultUrl);
        _urlRow = RowBuilder.Row(Loc.T("settings.pluginMarket.url"), Loc.T("settings.pluginMarket.urlHint"), _url);
        _reset = Ui.Button(Loc.T("settings.pluginMarket.reset"), () => _ = WriteAsync(MarketForm.ResetBody(), saved: true));
        _save = Ui.Button(Loc.T("common.save"), () => _ = WriteAsync(_form.SaveBody(), saved: true));
        _bar = Ui.ButtonBar([_reset, _save]);
        Update(settings, settings);
    }

    /// <summary>The group.</summary>
    public SettingsGroup Group => _group;

    /// <summary>Takes new settings.</summary>
    public void Update(SettingsDto previous, SettingsDto s)
    {
        _syncing = true;
        try
        {
            _form.Sync(_saved, s);
            _saved = s;
            if (_url.Text != _form.Url) _url.Text = _form.Url;
            _url.PlaceholderText = s.PluginMarketDefaultUrl;
        }
        finally
        {
            _syncing = false;
        }
        UpdateState();
    }

    private void UpdateState()
    {
        _offline.SetValue(MarketForm.Offline(_saved));
        _save.IsEnabled = _form.CanSave(_saved, _busy);
        _reset.IsVisible = MarketForm.CanReset(_saved);
        _reset.IsEnabled = !_busy;
        var rows = new List<Control> { _offlineRow };
        if (!MarketForm.Offline(_saved)) rows.AddRange([_urlRow, _bar]);
        RowAssembly.Sync(_group, rows);
    }

    private async Task WriteAsync(System.Text.Json.Nodes.JsonObject body, bool saved)
    {
        _busy = true;
        UpdateState();
        var result = await _host.TryAsync(() => _api.PatchSettingsAsync(body));
        _busy = false;
        if (result is not null && saved) _host.Success(Loc.T("settings.pluginMarket.saved"));
        UpdateState();
    }
}
