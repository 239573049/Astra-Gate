using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Theme;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using CoreThemeSource = Astra.Desktop.Core.Shell.ThemeSource;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// Settings › General (web <c>GeneralSettings</c>): appearance, start at login, the gateway, outbound proxy, plugin market, quota refresh,
/// usage time zone, logging, effort budgets and About with the update rows. The structure is drawn once the settings arrive and then only
/// updated in place, so a number being edited is not lost to a refresh (see <see cref="LiveView"/>).
/// </summary>
public sealed class GeneralSettingsView : LiveView
{
    private readonly SettingsPageViewModel _page;
    private readonly SettingsApi _api;
    private readonly List<Action<SettingsDto>> _updaters = [];

    private StackPanel _root = null!;
    private ControlledSegmented _theme = null!;
    private ControlledSelect _accent = null!;
    private SettingsRow _accentRow = null!;
    private ControlledSegmented _language = null!;
    private ContentControl _settingsHost = null!;
    private bool _settingsBuilt;
    private SettingsDto? _lastSettings;

    // gateway
    private SettingsGroup _gatewayGroup = null!;
    private SettingsRow _urlRow = null!, _remoteRow = null!, _dataRow = null!, _requireRow = null!, _idleRow = null!, _compressionRow = null!;
    private ListenRows _listen = null!;
    private ProxyGroup _proxy = null!;
    private MarketGroup _market = null!;
    private SettingsRow _retentionRow = null!;
    private ControlledSwitch _retentionSwitch = null!;
    private DeferredNumberField? _retentionNumber;

    // about
    private SettingsGroup _aboutGroup = null!;
    private SettingsRow _serverRow = null!, _desktopRow = null!, _autoCheckRow = null!, _previewRow = null!, _checkRow = null!, _logsRow = null!, _logoutRow = null!;
    private ControlledSwitch _autoCheck = null!, _preview = null!;
    private ArcButton _checkButton = null!, _applyButton = null!;
    private bool _checking, _applying, _savingChannel, _hostChecked;
    private string? _checkError;

    /// <summary>Creates the view.</summary>
    public GeneralSettingsView(SettingsPageViewModel page) : base(page.Context!)
    {
        _page = page;
        _api = page.Api;
        Watch(_api.Settings);
        Watch(_api.Version);
        Watch(_api.Auth);
        Watch(_api.Listen);
        Watch(_api.Tunnel);
        Watch(_api.Update);
    }

    /// <summary>The listen rows (scenes open the password sheet through them).</summary>
    internal ListenRows Listen => _listen;

    /// <inheritdoc />
    protected override void Build()
    {
        _updaters.Clear();
        _settingsBuilt = false;
        _lastSettings = null;
        _retentionNumber = null;
        _listen = new ListenRows(Host, _api);
        _listen.RowsChanged += RequestRefresh;
        _root = new StackPanel { MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Stretch };
        _root.Children.Add(BuildAppearance());
        _root.Children.Add(new StartupGroupView(Context, _api));
        _settingsHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _root.Children.Add(_settingsHost);
        _root.Children.Add(BuildAbout());
        Content = _root;
    }

    // ---------------------------------------------------------------- appearance

    private Control BuildAppearance()
    {
        var a = _page.Appearance;
        _theme = new ControlledSegmented(
            [("system", Loc.T("settings.theme.system")), ("light", Loc.T("settings.theme.light")), ("dark", Loc.T("settings.theme.dark"))],
            ThemeName(a.Theme));
        Avalonia.Automation.AutomationProperties.SetName(_theme, Loc.T("settings.theme"));
        _theme.Chose += v => _ = ChangeThemeAsync(v);

        var options = new List<(string, string)> { ("system", Loc.T("settings.accent.system")) };
        options.AddRange(ThemeService.Presets.Select(p => (p.Hex, Loc.T("settings.accent." + p.Id))));
        _accent = new ControlledSelect(options, a.AccentChoice, 160);
        Avalonia.Automation.AutomationProperties.SetName(_accent, Loc.T("settings.accent"));
        _accent.Chose += v => _ = ChangeAccentAsync(v);
        _accentRow = RowBuilder.Row(Loc.T("settings.accent"), null, _accent);

        _language = new ControlledSegmented([("zh", "中文"), ("en", "English")], a.Language);
        Avalonia.Automation.AutomationProperties.SetName(_language, Loc.T("settings.language"));
        _language.Chose += v => _ = ChangeLanguageAsync(v);

        return RowBuilder.Group(Loc.T("settings.appearance"), null,
            RowBuilder.Row(Loc.T("settings.theme"), null, _theme),
            _accentRow,
            RowBuilder.Row(Loc.T("settings.language"), Loc.T("settings.languageHint"), _language));
    }

    private static string ThemeName(CoreThemeSource s) => s switch { CoreThemeSource.Light => "light", CoreThemeSource.Dark => "dark", _ => "system" };

    private void RefreshAppearance()
    {
        var a = _page.Appearance;
        _theme.SetValue(ThemeName(a.Theme));
        _accent.SetValue(a.AccentChoice);
        _language.SetValue(a.Language);
        _accentRow.Detail = a.AccentChoice == MemoryAppearance.SystemAccentChoice
            ? Loc.T(a.SystemAccent is not null ? "settings.accent.followingSystem" : "settings.accent.systemUnavailable")
            : null;
    }

    private async Task ChangeThemeAsync(string name)
    {
        var source = name switch { "light" => CoreThemeSource.Light, "dark" => CoreThemeSource.Dark, _ => CoreThemeSource.System };
        await Host.TryAsync(() => _page.Appearance.SetThemeAsync(source));
        RefreshAppearance();
    }

    private async Task ChangeAccentAsync(string choice)
    {
        await Host.TryAsync(() => _page.Appearance.SetAccentAsync(choice));
        RefreshAppearance();
    }

    private async Task ChangeLanguageAsync(string language)
    {
        _page.Appearance.SetLanguage(language);
        // The language of stored billing descriptions follows too.
        await Host.TryAsync(() => _api.PatchSettingsAsync(JsonPatch.Of(("locale", language))));
    }

    // ---------------------------------------------------------------- settings groups

    private async Task PatchAsync(System.Text.Json.Nodes.JsonObject patch) => await Host.TryAsync(() => _api.PatchSettingsAsync(patch));

    private DeferredNumberField Number(string label, Func<SettingsDto, decimal> value, Func<decimal, System.Text.Json.Nodes.JsonObject> patch, string? suffix, decimal step = 1,
        Func<decimal, Task>? commit = null)
    {
        var field = new DeferredNumberField(label, value(_lastSettings!), v => _ = commit is null ? PatchAsync(patch(v)) : commit(v), suffix, step);
        _updaters.Add(s => field.SetSaved(value(s)));
        return field;
    }

    private ControlledSwitch Switch(string label, Func<SettingsDto, bool> value, Func<bool, Task> toggle)
    {
        var sw = new ControlledSwitch { IsChecked = value(_lastSettings!) };
        Avalonia.Automation.AutomationProperties.SetName(sw, label);
        sw.ToggleRequested += on => _ = toggle(on);
        _updaters.Add(s => sw.SetValue(value(s)));
        return sw;
    }

    private void BuildSettingsGroups(SettingsDto s)
    {
        _lastSettings = s;
        _updaters.Clear();
        var stack = new StackPanel();

        // gateway
        _urlRow = RowBuilder.Row(Loc.T("settings.gatewayUrl"), null);
        var copyUrl = Ui.IconButton(LucideIconKind.Copy, Loc.T("common.copy"), () => _ = Host.CopyAsync($"{_lastSettings!.GatewayBaseUrl}/v1"));
        _urlRow.Trailing.Add(copyUrl);
        _remoteRow = RowBuilder.Row(Loc.T("settings.remote.publicUrl"), null);
        _remoteRow.Trailing.Add(Ui.IconButton(LucideIconKind.Copy, Loc.T("common.copy"), () =>
        {
            if (GeneralLogic.RemoteUrl(_api.Tunnel.Data) is { } url) _ = Host.CopyAsync(url);
        }));
        _dataRow = RowBuilder.Row(Loc.T("settings.dataDir"), null);
        _dataRow.Trailing.Add(Ui.IconButton(LucideIconKind.FolderOpen, Loc.T("settings.openFolder"), () => _ = Context.Host.OpenPathAsync(_lastSettings!.DataDir)));
        _requireRow = RowBuilder.Row(Loc.T("settings.requireKey"), Loc.T("settings.requireKeyHint"),
            Switch(Loc.T("settings.requireKey"), x => x.RequireGatewayKey, async on =>
            {
                if (!on && !await Host.ConfirmAsync(Loc.T("settings.requireKeyOffConfirm"), Loc.T("settings.requireKeyOffDetail"), Loc.T("settings.requireKeyOff"), true)) return;
                await PatchAsync(JsonPatch.Of(("requireGatewayKey", on)));
            }));
        _idleRow = RowBuilder.Row(Loc.T("settings.streamIdle"), Loc.T("settings.streamIdleHint"),
            Number(Loc.T("settings.streamIdle"), x => x.StreamIdleTimeoutSec, v => JsonPatch.Of(("streamIdleTimeoutSec", (int?)v)), " s"));
        var compression = new ControlledSelect(GeneralLogic.Compressions.Select(c => (c, GeneralLogic.CompressionLabel(c))), s.RequestCompression, 160);
        Avalonia.Automation.AutomationProperties.SetName(compression, Loc.T("settings.requestCompression"));
        compression.Chose += v => _ = PatchAsync(JsonPatch.Of(("requestCompression", v)));
        _updaters.Add(x => compression.SetValue(x.RequestCompression));
        _compressionRow = RowBuilder.Row(Loc.T("settings.requestCompression"), Loc.T("settings.requestCompressionHint"), compression);
        _gatewayGroup = RowBuilder.Group(Loc.T("settings.gateway"), Loc.T("settings.gatewayFooter"));
        stack.Children.Add(_gatewayGroup);

        // keeping the system awake while requests are in flight (web SettingsPage: its own group after the gateway, the hint as footer)
        stack.Children.Add(RowBuilder.Group(null, Loc.T("settings.preventSleepHint"),
            RowBuilder.Row(Loc.T("settings.preventSleep"), null,
                Switch(Loc.T("settings.preventSleep"), x => x.PreventSleep, on => PatchAsync(JsonPatch.Of(("preventSleep", on)))))));

        _proxy = new ProxyGroup(Host, _api, s);
        stack.Children.Add(_proxy.Group);
        _market = new MarketGroup(Host, _api, s);
        stack.Children.Add(_market.Group);

        // quota
        stack.Children.Add(RowBuilder.Group(Loc.T("settings.quota"), Loc.T("settings.quota.autoIntervalHint"),
            RowBuilder.Row(Loc.T("settings.quota.autoInterval"), null,
                Number(Loc.T("settings.quota.autoInterval"), x => x.QuotaAutoIntervalMinutes, v => JsonPatch.Of(("quotaAutoIntervalMinutes", (int?)v)), $" {Loc.T("settings.minutes")}",
                    commit: async v =>
                    {
                        // Providers show their effective interval: refresh them after the global one changes.
                        if (await Host.TryAsync(() => _api.PatchSettingsAsync(JsonPatch.Of(("quotaAutoIntervalMinutes", (int?)v)))) is not null) _api.RefreshProviders();
                    })),
            RowBuilder.Row(Loc.T("settings.quota.subscriptionInterval"), Loc.T("settings.quota.subscriptionIntervalHint"),
                Number(Loc.T("settings.quota.subscriptionInterval"), x => x.SubscriptionQuotaIntervalMinutes, v => JsonPatch.Of(("subscriptionQuotaIntervalMinutes", (int?)v)),
                    $" {Loc.T("settings.minutes")}"))));

        // usage time zone
        var zones = new ControlledSelect([("", Loc.T("settings.usageTimeZone.system", ("zone", s.EffectiveUsageTimeZone))), .. GeneralLogic.ZoneOptions(s).Select(z => (z, z))], s.UsageTimeZone ?? "", 224);
        Avalonia.Automation.AutomationProperties.SetName(zones, Loc.T("settings.usageTimeZone"));
        zones.Chose += v => _ = PatchAsync(JsonPatch.Of(("usageTimeZone", v == "" ? null : v)));
        _updaters.Add(x => zones.SetValue(x.UsageTimeZone ?? ""));
        stack.Children.Add(RowBuilder.Group(null, Loc.T("settings.usageTimeZone.hint"), RowBuilder.Row(Loc.T("settings.usageTimeZone"), null, zones)));

        // logging
        _retentionSwitch = Switch(Loc.T("settings.requestRetention"), x => x.RequestRetentionDays is not null, on => PatchAsync(JsonPatch.Of(("requestRetentionDays", GeneralLogic.RetentionFor(on)))));
        _retentionRow = RowBuilder.Row(Loc.T("settings.requestRetention"), null);
        stack.Children.Add(RowBuilder.Group(Loc.T("settings.logging"), Loc.T("settings.debugBodiesHint"),
            RowBuilder.Row(Loc.T("settings.debugBodies"), null,
                Switch(Loc.T("settings.debugBodies"), x => x.DebugBodies, on => PatchAsync(JsonPatch.Of(("debugBodies", on))))),
            RowBuilder.Row(Loc.T("settings.bodyRetention"), null,
                Number(Loc.T("settings.bodyRetention"), x => x.BodyRetentionDays, v => JsonPatch.Of(("bodyRetentionDays", (int?)v)), $" {Loc.T("settings.days")}")),
            _retentionRow));

        // conversation content in traces (web SettingsPage: its own group after logging, the hint as footer)
        stack.Children.Add(RowBuilder.Group(null, Loc.T("settings.traceContentHint"),
            RowBuilder.Row(Loc.T("settings.traceContent"), null,
                Switch(Loc.T("settings.traceContent"), x => x.TraceContent, on => PatchAsync(JsonPatch.Of(("traceContent", on)))))));

        // effort budgets
        var effort = RowBuilder.Group(Loc.T("settings.effort"), Loc.T("settings.effortHint"));
        foreach (var level in GeneralLogic.Efforts)
        {
            var l = level;
            effort.Rows.Add(RowBuilder.Row(Loc.T("settings.effort." + l), null,
                Number(Loc.T("settings.effort." + l), x => GeneralLogic.Budget(x.EffortBudgets, l), v => GeneralLogic.EffortPatch(_lastSettings!.EffortBudgets, l, (int?)v), " tokens", 1024)));
        }
        stack.Children.Add(effort);

        _settingsHost.Content = stack;
        _settingsBuilt = true;
    }

    // ---------------------------------------------------------------- about

    private Control BuildAbout()
    {
        _serverRow = RowBuilder.Row(Loc.T("settings.serverVersion"));
        _desktopRow = RowBuilder.Row(Loc.T("settings.desktopVersion"), UpdateLogic.DesktopVersionDetail(Context.Host.Version, null));

        _autoCheck = new ControlledSwitch();
        Avalonia.Automation.AutomationProperties.SetName(_autoCheck, Loc.T("settings.autoCheck"));
        _autoCheck.ToggleRequested += on => _ = PatchAsync(JsonPatch.Of(("updateAutoCheck", on)));
        _autoCheckRow = RowBuilder.Row(Loc.T("settings.autoCheck"), null, _autoCheck);

        _preview = new ControlledSwitch();
        Avalonia.Automation.AutomationProperties.SetName(_preview, Loc.T("settings.previewUpdates"));
        _preview.ToggleRequested += on => _ = ChangeChannelAsync(on);
        _previewRow = RowBuilder.Row(Loc.T("settings.previewUpdates"), Loc.T("settings.previewUpdatesHint"), _preview);

        _checkButton = Ui.Button(null, () => _ = RunCheckAsync(), ButtonVariant.Ghost, LucideIconKind.RefreshCw);
        _applyButton = Ui.Button(null, () => _ = RunApplyAsync(), ButtonVariant.Ghost, LucideIconKind.Download, tip: Loc.T("settings.updateApply"));
        _checkRow = RowBuilder.Row(Loc.T("settings.checkUpdates"), null, _checkButton, _applyButton);

        _logsRow = RowBuilder.Row(Loc.T("settings.logs"), null, Icon(LucideIconKind.FolderOpen));
        _logsRow.Invoked += (_, _) => _ = Context.Host.RevealLogsAsync();
        _logoutRow = RowBuilder.Row(Loc.T("settings.logout"), null, Icon(LucideIconKind.LogOut));
        _logoutRow.Invoked += (_, _) => _ = Host.TryAsync(() => _api.LogoutAsync());

        _aboutGroup = RowBuilder.Group(Loc.T("settings.about"), null);
        _ = HostCheckOnceAsync();
        return _aboutGroup;
    }

    private static Control Icon(LucideIconKind kind)
    {
        var icon = new LucideIcon { Kind = kind, Size = 16, VerticalAlignment = VerticalAlignment.Center };
        icon.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        return icon;
    }

    private void RefreshAbout()
    {
        var version = _api.Version.Data;
        _serverRow.Detail = version is null ? "—" : $"{version.Version} · API {version.ApiVersion}";
        _desktopRow.Detail = UpdateLogic.DesktopVersionDetail(Context.Host.Version, version?.Version);
        var settings = _api.Settings.Data;
        var status = _api.Update.Data;
        var host = Context.Host.LastUpdateCheck;
        _autoCheckRow.Detail = UpdateLogic.AutoCheckDetail(status)?.Text;
        _autoCheck.SetValue(settings?.UpdateAutoCheck ?? status?.AutoCheck ?? true);
        var channel = UpdateLogic.Channel(settings, status);
        _preview.SetValue(channel == "preview");
        _preview.IsEnabled = channel is not null && !_savingChannel;
        _checkRow.Detail = UpdateLogic.CheckDetail(status, _checkError, canApply: true, Loc.Language, host);
        _checkButton.IsEnabled = !_checking;
        _checkButton.IsLoading = _checking;
        _applyButton.IsVisible = UpdateLogic.AvailableVersion(status, host) is not null;
        _applyButton.IsEnabled = !_applying;
        _applyButton.IsLoading = _applying;

        var rows = new List<Control> { _serverRow, _desktopRow, _autoCheckRow, _previewRow, _checkRow, _logsRow };
        if (_api.Auth.Data?.Required == true) rows.Add(_logoutRow);
        RowAssembly.Sync(_aboutGroup, rows);
    }

    private async Task ChangeChannelAsync(bool preview)
    {
        _savingChannel = true;
        RefreshAbout();
        var saved = await Host.TryAsync(() => _api.PatchSettingsAsync(JsonPatch.Of(("updateChannel", UpdateLogic.ChannelFor(preview)))));
        _savingChannel = false;
        RefreshAbout();
        // Flips as soon as the setting is saved; the check for the new channel runs behind it (the check row shows its spinner).
        if (saved is not null) await RunCheckAsync();
    }

    private async Task RunCheckAsync()
    {
        _checking = true;
        _checkError = null;
        RefreshAbout();
        try
        {
            var status = await _api.CheckUpdateAsync();
            // Then the host's own check: on a packaged app it is the app (with its server) that updates, not the server alone.
            await Context.Host.CheckForUpdateAsync();
            Host.Toast(UpdateLogic.CheckToast(status, Context.Host.LastUpdateCheck));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _checkError = UiHost.ErrorText(e);
            Host.Error(_checkError);
        }
        finally
        {
            _checking = false;
            RefreshAbout();
        }
    }

    /// <summary>The host's check, once per view, when no surface has run one yet (the tray's background check normally has).</summary>
    private async Task HostCheckOnceAsync()
    {
        if (_hostChecked || Context.Host.LastUpdateCheck is not null) return;
        _hostChecked = true;
        await Context.Host.CheckForUpdateAsync();
        RefreshAbout();
    }

    private async Task RunApplyAsync()
    {
        if (_applying) return;
        _applying = true;
        RefreshAbout();
        try
        {
            var outcome = await Context.Host.ApplyUpdateAsync();
            Host.Success(UpdateLogic.ApplyToast(outcome).Text);
            if (UpdateLogic.ApplyRefreshes(outcome))
            {
                Context.Queries.Invalidate("version");
                Context.Queries.Invalidate("update");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Host.Error(UiHost.ErrorText(e));
        }
        finally
        {
            _applying = false;
            RefreshAbout();
        }
    }

    // ---------------------------------------------------------------- refresh

    /// <inheritdoc />
    protected override void Refresh()
    {
        RefreshAppearance();
        RefreshAbout();
        var s = _api.Settings.Data;
        if (s is null)
        {
            if (!_settingsBuilt)
                _settingsHost.Content = _api.Settings.IsError
                    ? Ui.ErrorText(UiHost.ErrorText(_api.Settings.Error!))
                    : new Border { Padding = new Thickness(0, 32), Child = Ui.Spinner() };
            return;
        }
        if (!_settingsBuilt) BuildSettingsGroups(s);
        else _proxy.Update(_lastSettings!, s);
        if (_settingsBuilt) _market.Update(_lastSettings!, s);
        _lastSettings = s;
        foreach (var update in _updaters) update(s);

        _urlRow.Detail = $"{s.GatewayBaseUrl}/v1";
        _dataRow.Detail = s.DataDir;
        var remote = GeneralLogic.RemoteUrl(_api.Tunnel.Data);
        _remoteRow.Detail = remote;
        _listen.Update();
        _retentionRow.Detail = s.RequestRetentionDays is null ? Loc.T("settings.keepForever") : null;
        _retentionRow.Trailing.Clear();
        _retentionRow.Trailing.Add(_retentionSwitch);
        if (s.RequestRetentionDays is { } days)
        {
            _retentionNumber ??= new DeferredNumberField(Loc.T("settings.requestRetention"), days, v => _ = PatchAsync(JsonPatch.Of(("requestRetentionDays", (int?)v))), $" {Loc.T("settings.days")}");
            _retentionNumber.SetSaved(days);
            _retentionRow.Trailing.Add(_retentionNumber);
        }
        RowAssembly.Sync(_gatewayGroup, RowAssembly.Flatten(_urlRow, remote is null ? null : _remoteRow, _listen, _dataRow, _requireRow, _idleRow, _compressionRow));
    }
}
