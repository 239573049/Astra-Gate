using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Clients;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using Tone = Astra.Desktop.Components.Kit.Tone;

namespace Astra.Desktop.Components.Clients;

/// <summary>
/// The Claude Code panel for the Astra launcher's direct mode (web/src/components/ClaudeDirectPanel.tsx): the mode selector, the
/// subscription accounts the launcher may use, the terminal command that starts one, and the opt-in statistics of the selected account.
/// The accounts are the ones already signed in under the Claude subscription provider — nothing is created or signed in here.
/// <see cref="GatewayEnabled"/> is the client's own take-over switch: while it is on, direct cannot take effect (the warning says so).
/// The panel observes its queries while it is on screen and redraws from them (it has no text input a redraw could disturb).
/// </summary>
public sealed class ClaudeDirectPanel : UserControl
{
    private readonly ClientsApi _api;
    private readonly IFeedback _feedback;
    private readonly INavigator _navigator;
    private readonly Func<string, Task<bool>> _copy;
    private readonly TimeSpan _zone;
    private readonly Query<ClaudeDirectStateDto> _state;
    private readonly StackPanel _root = new();
    private Query<List<ClaudeDirectRequestDto>>? _requests;
    private IDisposable? _stateToken;
    private IDisposable? _requestsToken;
    private bool _busy;
    private string? _copied;
    private bool _gatewayEnabled;

    /// <summary>A panel over <paramref name="api"/>; <paramref name="copy"/> puts text on the clipboard (false when it could not).</summary>
    public ClaudeDirectPanel(ClientsApi api, IFeedback feedback, INavigator navigator, Func<string, Task<bool>> copy, TimeSpan zone, bool gatewayEnabled)
    {
        _api = api;
        _feedback = feedback;
        _navigator = navigator;
        _copy = copy;
        _zone = zone;
        _gatewayEnabled = gatewayEnabled;
        _state = api.ClaudeDirect();
        Content = _root;
        Rebuild();
    }

    /// <summary>The client's gateway take-over is on (the warning, and direct cannot take effect).</summary>
    public bool GatewayEnabled
    {
        get => _gatewayEnabled;
        set
        {
            if (_gatewayEnabled == value) return;
            _gatewayEnabled = value;
            Schedule();
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _state.PropertyChanged += OnChanged;
        _stateToken = _state.Activate();
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _state.PropertyChanged -= OnChanged;
        _stateToken?.Dispose();
        _stateToken = null;
        WatchRequests(null);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Schedule();

    private bool _scheduled;

    // A query reports several properties per answer and handlers fire inside controls this rebuild removes: redraw once, afterwards.
    private void Schedule()
    {
        if (_scheduled) return;
        _scheduled = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _scheduled = false;
            Rebuild();
        });
    }

    private void WatchRequests(Query<List<ClaudeDirectRequestDto>>? query)
    {
        if (ReferenceEquals(query, _requests)) return;
        if (_requests is not null) _requests.PropertyChanged -= OnChanged;
        _requestsToken?.Dispose();
        _requestsToken = null;
        _requests = query;
        if (query is null || VisualRoot is null) return;
        query.PropertyChanged += OnChanged;
        _requestsToken = query.Activate();
    }

    private ClaudeDirectStateDto? Data => _state.HasData ? _state.Data : null;

    private async Task ChooseAsync(ClaudeDirectSelectInput body)
    {
        _busy = true;
        Schedule();
        try
        {
            await _api.SelectClaudeDirectAsync(body);
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            _feedback.Error(e.Message);
        }
        finally
        {
            _busy = false;
            Schedule();
        }
    }

    private void SwitchMode(string next)
    {
        var (body, needAccount) = ClaudeDirectLogic.Switch(Data, next, _busy);
        if (needAccount)
        {
            _feedback.Error(Loc.T("clients.claudeDirect.needAccountToast"));
            Schedule();
            return;
        }
        if (body is null)
        {
            Schedule();
            return;
        }
        _ = ChooseAsync(body);
    }

    private void ChooseAccount(string id)
    {
        if (_busy || id == ClaudeDirectLogic.SelectedId(Data)) return;
        _ = ChooseAsync(new ClaudeDirectSelectInput(ClaudeDirectLogic.Mode(Data), id, null));
    }

    private void SetConsent(bool on)
    {
        if (ClaudeDirectLogic.Selected(Data) is not { } selected || _busy) return;
        _ = ChooseAsync(new ClaudeDirectSelectInput(ClaudeDirectLogic.Mode(Data), selected.Id, on));
    }

    private async Task CopyAsync(string text)
    {
        if (!await _copy(text)) return;
        _copied = text;
        _feedback.Info(Loc.T("common.copied"));
        Schedule();
    }

    private void Rebuild()
    {
        _root.Children.Clear();
        var data = Data;
        var mode = ClaudeDirectLogic.Mode(data);
        var direct = mode == "direct";

        // ----- mode -----
        var modeGroup = new SettingsGroup { Title = Loc.T("clients.claudeDirect.modeTitle"), Footer = Loc.T("clients.claudeDirect.modeFooter") };
        var segmented = new SegmentedControl
        {
            AutoScrollToSelectedItem = false,
            ItemsSource = new List<SegmentedOption>
            {
                new("gateway", Loc.T("clients.claudeDirect.gateway")),
                new("direct", Loc.T("clients.claudeDirect.direct")),
            },
            SelectedValue = mode,
        };
        Avalonia.Automation.AutomationProperties.SetName(segmented, Loc.T("clients.claudeDirect.mode"));
        segmented.SelectionChanged += (_, _) =>
        {
            if (segmented.SelectedValue is string v && v != mode) SwitchMode(v);
        };
        var modeRow = new SettingsRow
        {
            Label = Loc.T("clients.claudeDirect.mode"),
            Detail = Loc.T(direct ? "clients.claudeDirect.directDetail" : "clients.claudeDirect.gatewayDetail"),
        };
        modeRow.Trailing.Add(segmented);
        modeGroup.Rows.Add(modeRow);
        var note = ClientUi.Wrap(Loc.T("clients.claudeDirect.modeNote"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, 11);
        note.Margin = new Thickness(16, 0, 16, 8);
        modeGroup.Rows.Add(note);
        _root.Children.Add(modeGroup);

        if (_state.IsLoading) _root.Children.Add(new LoadingState(3) { Margin = new Thickness(0, 0, 0, 20) });
        if (_state.IsError && !_state.HasData)
        {
            // The endpoint is loopback-only: a 403 means this console is not on the machine running astra-server.
            var alert = ClientUi.Alert(AlertTone.Danger, Loc.T("clients.claudeDirect.loadFailed"), _state.Error?.Message);
            alert.Margin = new Thickness(0, 0, 0, 20);
            _root.Children.Add(alert);
        }
        if (ClaudeDirectLogic.Warned(_gatewayEnabled, data))
        {
            var alert = ClientUi.Alert(AlertTone.Warning, Loc.T("clients.claudeDirect.gatewayOnTitle"), Loc.T("clients.claudeDirect.gatewayOnDetail"));
            alert.Margin = new Thickness(0, 0, 0, 20);
            _root.Children.Add(alert);
        }

        var selected = ClaudeDirectLogic.Selected(data);
        if (data is not null)
        {
            // ----- accounts -----
            var accounts = new SettingsGroup { Title = Loc.T("clients.claudeDirect.accountsTitle"), Footer = Loc.T("clients.claudeDirect.accountsFooter") };
            if (data.Accounts.Count == 0)
            {
                var go = ClientUi.Button(Loc.T("clients.claudeDirect.goProviders"), () => _navigator.Navigate("Providers"), ButtonVariant.Primary);
                go.HorizontalAlignment = HorizontalAlignment.Center;
                accounts.Rows.Add(new EmptyStateView(Loc.T("clients.claudeDirect.noAccount"), Loc.T("clients.claudeDirect.noAccountDetail"), action: go));
            }
            else
            {
                var options = data.Accounts.Select(a => (a.Id,
                    ClaudeDirectLogic.Label(a, Loc.T("clients.claudeDirect.accountDisabledSuffix", ("status", Loc.T("providers.subscription.status." + a.Status)))),
                    !ClaudeDirectLogic.Usable(a)));
                var row = new SettingsRow { Label = Loc.T("clients.claudeDirect.account") };
                row.Trailing.Add(ClientUi.Select(options, ClaudeDirectLogic.SelectedId(data), ChooseAccount, Loc.T("clients.claudeDirect.account"), maxWidth: 300));
                accounts.Rows.Add(row);
            }
            _root.Children.Add(accounts);
        }

        if (data is not null && direct && selected is not null)
        {
            // ----- launcher -----
            var launch = new SettingsGroup { Title = Loc.T("clients.claudeDirect.launchTitle"), Footer = Loc.T("clients.claudeDirect.launchNote") };
            var command = ClaudeDirectLogic.LaunchCommand;
            var label = new SelectableTextBlock { Text = command, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
            label.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily).Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
            var copied = _copied == command;
            var copy = ClientUi.Button(copied ? Loc.T("common.copied") : Loc.T("common.copy"), () => _ = CopyAsync(command),
                copied ? ButtonVariant.Primary : ButtonVariant.Secondary, small: true, icon: ClientUi.Icon(LucideIconKind.Copy, 12));
            ToolTip.SetTip(copy, Loc.T("common.copy"));
            var launchRow = new SettingsRow { Label = label };
            launchRow.Trailing.Add(copy);
            launch.Rows.Add(launchRow);
            _root.Children.Add(launch);

            // ----- statistics -----
            var stats = new SettingsGroup { Title = Loc.T("clients.claudeDirect.statsTitle"), Footer = Loc.T("clients.claudeDirect.statsFooter") };
            var consent = new Switch { IsChecked = selected.TelemetryEnabled, IsEnabled = !_busy };
            Avalonia.Automation.AutomationProperties.SetName(consent, Loc.T("clients.claudeDirect.statsConsent"));
            consent.IsCheckedChanged += (_, _) => SetConsent(consent.IsChecked == true);
            var consentRow = new SettingsRow { Label = Loc.T("clients.claudeDirect.statsConsent"), Detail = Loc.T("clients.claudeDirect.statsConsentDetail") };
            consentRow.Trailing.Add(consent);
            stats.Rows.Add(consentRow);
            var live = ClaudeDirectLogic.Live(data);
            WatchRequests(live ? _api.ClaudeDirectRequests(selected.Id, live: true) : null);
            if (live && _requests is not null)
                foreach (var row in StatsRows(_requests)) stats.Rows.Add(row);
            _root.Children.Add(stats);
        }
        else WatchRequests(null);
    }

    /// <summary>The selected account's reported requests: the totals of what is shown, then one row per request (web <c>ClaudeDirectStats</c>).</summary>
    private IEnumerable<Control> StatsRows(Query<List<ClaudeDirectRequestDto>> requests)
    {
        if (requests.IsLoading || (!requests.HasData && !requests.IsError))
        {
            yield return new LoadingState(3) { Margin = new Thickness(16) };
            yield break;
        }
        if (requests.IsError && !requests.HasData)
        {
            yield return new Border { Padding = new Thickness(16), Child = ClientUi.Alert(AlertTone.Danger, Loc.T("clients.claudeDirect.statsFailed"), requests.Error?.Message) };
            yield break;
        }
        var rows = requests.Data ?? [];
        if (rows.Count == 0)
        {
            yield return new EmptyStateView(Loc.T("clients.claudeDirect.statsEmpty"), Loc.T("clients.claudeDirect.statsEmptyDetail"), new LucideIcon { Kind = LucideIconKind.KeyRound, Size = 24 });
            yield break;
        }
        var totals = ClaudeDirectLogic.Totals(rows);
        yield return new SettingsRow
        {
            Label = Loc.T("clients.claudeDirect.statsTotals", ("count", StatsFormat.Tokens(totals.Requests))),
            Detail = Loc.T("clients.claudeDirect.statsInDetail",
                ("input", StatsFormat.Tokens(totals.TotalInputTokens)),
                ("cache", StatsFormat.Tokens(rows.Sum(r => r.CacheReadTokens))),
                ("output", StatsFormat.Tokens(totals.OutputTokens))),
        };
        var cost = new SettingsRow { Label = Loc.T("clients.claudeDirect.statsCost"), Detail = Loc.T("clients.claudeDirect.statsCostDetail") };
        cost.Trailing.Add(ClientUi.Text(ClaudeDirectLogic.CostText(rows), 13, ArcDesignTokenKind.Foreground));
        yield return cost;
        if (totals.Failed > 0)
        {
            var failed = new SettingsRow { Label = Loc.T("clients.claudeDirect.statsFailedCount", ("count", StatsFormat.Tokens(totals.Failed))) };
            failed.Trailing.Add(KitBadge.Create(Loc.T("clients.claudeDirect.statusError"), Tone.Red));
            yield return failed;
        }
        foreach (var r in rows)
        {
            var model = KitStyle.Text(r.Model, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground);
            var badge = r.Status == "error" ? KitBadge.Create(Loc.T("clients.claudeDirect.statusError"), Tone.Red) : KitBadge.Create(Loc.T("status.success"), Tone.Green);
            var row = new SettingsRow
            {
                Label = ClientUi.HRow(8, model, badge),
                Detail = ClaudeDirectLogic.RowDetail(r, _zone, Loc.Language, (k, a) => Loc.T(k, a)),
            };
            row.Trailing.Add(ClientUi.Text(StatsFormat.Nanos(r.EstimatedCostNanoUsd), 13, ArcDesignTokenKind.Foreground));
            yield return row;
        }
    }
}
