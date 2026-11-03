using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Pages.Settings;

/// <summary>Settings › About: server and desktop versions, the update rows (auto check, preview channel, check / apply), logs and sign-out.</summary>
public sealed class AboutSettingsView : LiveView
{
    private readonly SettingsApi _api;
    private SettingsGroup _aboutGroup = null!;
    private SettingsRow _serverRow = null!, _desktopRow = null!, _autoCheckRow = null!, _previewRow = null!, _checkRow = null!, _logsRow = null!, _logoutRow = null!;
    private ControlledSwitch _autoCheck = null!, _preview = null!;
    private ArcButton _checkButton = null!, _applyButton = null!;
    private bool _checking, _applying, _savingChannel, _hostChecked;
    private string? _checkError;

    /// <summary>Creates the view.</summary>
    public AboutSettingsView(SettingsPageViewModel page) : base(page.Context!)
    {
        _api = page.Api;
        Watch(_api.Settings);
        Watch(_api.Version);
        Watch(_api.Auth);
        Watch(_api.Update);
    }

    /// <inheritdoc />
    protected override void Build()
    {
        _serverRow = RowBuilder.Row(Loc.T("settings.serverVersion"));
        _desktopRow = RowBuilder.Row(Loc.T("settings.desktopVersion"), UpdateLogic.DesktopVersionDetail(Context.Host.Version, null));

        _autoCheck = new ControlledSwitch();
        Avalonia.Automation.AutomationProperties.SetName(_autoCheck, Loc.T("settings.autoCheck"));
        _autoCheck.ToggleRequested += on => _ = Host.TryAsync(() => _api.PatchSettingsAsync(JsonPatch.Of(("updateAutoCheck", on))));
        _autoCheckRow = RowBuilder.Row(Loc.T("settings.autoCheck"), null, _autoCheck);

        _preview = new ControlledSwitch();
        Avalonia.Automation.AutomationProperties.SetName(_preview, Loc.T("settings.previewUpdates"));
        _preview.ToggleRequested += on => _ = ChangeChannelAsync(on);
        _previewRow = RowBuilder.Hinted(Loc.T("settings.previewUpdates"), Loc.T("settings.previewUpdatesHint"), _preview);

        _checkButton = Ui.Button(null, () => _ = RunCheckAsync(), ButtonVariant.Ghost, LucideIconKind.RefreshCw);
        _applyButton = Ui.Button(null, () => _ = RunApplyAsync(), ButtonVariant.Ghost, LucideIconKind.Download, tip: Loc.T("settings.updateApply"));
        _checkRow = RowBuilder.Row(Loc.T("settings.checkUpdates"), null, _checkButton, _applyButton);

        _logsRow = RowBuilder.Row(Loc.T("settings.logs"), null, Icon(LucideIconKind.FolderOpen));
        _logsRow.Invoked += (_, _) => _ = Context.Host.RevealLogsAsync();
        _logoutRow = RowBuilder.Row(Loc.T("settings.logout"), null, Icon(LucideIconKind.LogOut));
        _logoutRow.Invoked += (_, _) => _ = Host.TryAsync(() => _api.LogoutAsync());

        _aboutGroup = RowBuilder.Group(Loc.T("settings.about"), null);
        _ = HostCheckOnceAsync();
        Content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch, Children = { _aboutGroup } };
    }

    private static Control Icon(LucideIconKind kind)
    {
        var icon = new LucideIcon { Kind = kind, Size = 16, VerticalAlignment = VerticalAlignment.Center };
        icon.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        return icon;
    }

    /// <inheritdoc />
    protected override void Refresh()
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
        Refresh();
        var saved = await Host.TryAsync(() => _api.PatchSettingsAsync(JsonPatch.Of(("updateChannel", UpdateLogic.ChannelFor(preview)))));
        _savingChannel = false;
        Refresh();
        // Flips as soon as the setting is saved; the check for the new channel runs behind it (the check row shows its spinner).
        if (saved is not null) await RunCheckAsync();
    }

    private async Task RunCheckAsync()
    {
        _checking = true;
        _checkError = null;
        Refresh();
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
            Refresh();
        }
    }

    /// <summary>The host's check, once per view, when no surface has run one yet (the tray's background check normally has).</summary>
    private async Task HostCheckOnceAsync()
    {
        if (_hostChecked || Context.Host.LastUpdateCheck is not null) return;
        _hostChecked = true;
        await Context.Host.CheckForUpdateAsync();
        RequestRefresh();
    }

    private async Task RunApplyAsync()
    {
        if (_applying) return;
        _applying = true;
        Refresh();
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
            Refresh();
        }
    }
}
