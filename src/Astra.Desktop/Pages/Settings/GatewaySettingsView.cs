using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Avalonia.Controls;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// Settings › Gateway: the addresses (gateway URL, the tunnel's public URL, listen scope and LAN addresses, admin password, data folder),
/// then how requests are handled (access key, stream idle timeout, upstream compression, keeping the system awake).
/// </summary>
public sealed class GatewaySettingsView : SettingsSectionView
{
    private SettingsGroup _addresses = null!;
    private SettingsRow _urlRow = null!, _remoteRow = null!, _dataRow = null!, _requireRow = null!;
    private ListenRows _listen = null!;
    private TextBlock _keyOffNote = null!;

    /// <summary>Creates the view.</summary>
    public GatewaySettingsView(SettingsPageViewModel page) : base(page)
    {
        Watch(Api.Auth);
        Watch(Api.Listen);
        Watch(Api.Tunnel);
    }

    /// <summary>The listen rows (scenes open the password sheet through them).</summary>
    internal ListenRows Listen => _listen;

    /// <inheritdoc />
    protected override void BuildFrame(StackPanel root, Control settingsHost)
    {
        _listen = new ListenRows(Host, Api);
        _listen.RowsChanged += RequestRefresh;
        root.Children.Add(settingsHost);
    }

    /// <inheritdoc />
    protected override void BuildGroups(SettingsDto s, StackPanel stack)
    {
        _urlRow = RowBuilder.Row(Loc.T("settings.gatewayUrl"), null,
            Ui.IconButton(LucideIconKind.Copy, Loc.T("common.copy"), () => _ = Host.CopyAsync($"{Last!.GatewayBaseUrl}/v1")));
        _remoteRow = RowBuilder.Row(Loc.T("settings.remote.publicUrl"), null, Ui.IconButton(LucideIconKind.Copy, Loc.T("common.copy"), () =>
        {
            if (GeneralLogic.RemoteUrl(Api.Tunnel.Data) is { } url) _ = Host.CopyAsync(url);
        }));
        _dataRow = RowBuilder.Row(Loc.T("settings.dataDir"), null,
            Ui.IconButton(LucideIconKind.FolderOpen, Loc.T("settings.openFolder"), () => _ = Context.Host.OpenPathAsync(Last!.DataDir)));
        _addresses = RowBuilder.Group(Loc.T("settings.gateway"), null).WithHint(Loc.T("settings.gatewayFooter"));
        stack.Children.Add(_addresses);

        _keyOffNote = Ui.Note(Loc.T("settings.requireKeyOffNote"), ArcDesignTokenKind.Warning, 11);
        _requireRow = RowBuilder.Hinted(Loc.T("settings.requireKey"), Loc.T("settings.requireKeyHint"),
            Switch(Loc.T("settings.requireKey"), x => x.RequireGatewayKey, async on =>
            {
                if (!on && !await Host.ConfirmAsync(Loc.T("settings.requireKeyOffConfirm"), Loc.T("settings.requireKeyOffDetail"), Loc.T("settings.requireKeyOff"), true)) return;
                await PatchAsync(JsonPatch.Of(("requireGatewayKey", on)));
            }));
        var idle = RowBuilder.Hinted(Loc.T("settings.streamIdle"), Loc.T("settings.streamIdleHint"),
            Number(Loc.T("settings.streamIdle"), x => x.StreamIdleTimeoutSec, v => JsonPatch.Of(("streamIdleTimeoutSec", (int?)v)), " s"));
        var compression = new ControlledSelect(GeneralLogic.Compressions.Select(c => (c, GeneralLogic.CompressionLabel(c))), s.RequestCompression, 160);
        Avalonia.Automation.AutomationProperties.SetName(compression, Loc.T("settings.requestCompression"));
        compression.Chose += v => _ = PatchAsync(JsonPatch.Of(("requestCompression", v)));
        OnSettings(x => compression.SetValue(x.RequestCompression));
        // Keeping the system awake while requests are in flight (the web shows it as its own group after the gateway).
        var awake = RowBuilder.Hinted(Loc.T("settings.preventSleep"), Loc.T("settings.preventSleepHint"),
            Switch(Loc.T("settings.preventSleep"), x => x.PreventSleep, on => PatchAsync(JsonPatch.Of(("preventSleep", on)))));
        // Clients bound to several providers fall back along their bindings (issue #28).
        var failover = RowBuilder.Hinted(Loc.T("settings.clientFailover"), Loc.T("settings.clientFailoverHint"),
            Switch(Loc.T("settings.clientFailover"), x => x.ClientProviderFailover, on => PatchAsync(JsonPatch.Of(("clientProviderFailover", on)))));
        stack.Children.Add(RowBuilder.Group(null, null,
            _requireRow,
            idle,
            RowBuilder.Hinted(Loc.T("settings.requestCompression"), Loc.T("settings.requestCompressionHint"), compression),
            awake,
            failover));
    }

    /// <inheritdoc />
    protected override void Refreshed(SettingsDto s)
    {
        _urlRow.Detail = $"{s.GatewayBaseUrl}/v1";
        _dataRow.Detail = s.DataDir;
        var remote = GeneralLogic.RemoteUrl(Api.Tunnel.Data);
        _remoteRow.Detail = remote;
        // The access key is a security switch: when it is off the row says so on a line of its own instead of only in the tooltip.
        _requireRow.Detail = s.RequireGatewayKey ? null : _keyOffNote;
        _listen.Update();
        RowAssembly.Sync(_addresses, RowAssembly.Flatten(_urlRow, remote is null ? null : _remoteRow, _listen, _dataRow));
    }
}
