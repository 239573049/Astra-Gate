using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Shell;
using Astra.Desktop.Core.Warmup;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using ArcButton = TKCode.Arc.Button;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Components.Settings;

/// <summary>
/// Settings › General › Window warm-up (web <c>WarmupSettings</c>, linked update 36): the global switch, when (daily times and weekdays, or
/// right after a window resets), which families, and one row for each Claude / ChatGPT account with its own mode and "warm up now". The
/// controls show what the server saved; a click only asks the server.
/// </summary>
public sealed class WarmupGroupView : LiveView
{
    private readonly WarmupApi _api;
    private ControlledSwitch _enabled = null!;
    private ControlledSegmented _mode = null!;
    private SettingsRow _timesRow = null!;
    private SettingsRow _daysRow = null!;
    private Input _times = null!;
    private StackPanel _days = null!;
    private ControlledSwitch _claude = null!;
    private ControlledSwitch _codex = null!;
    private StackPanel _accounts = null!;
    private SettingsGroup _group = null!;
    private bool _timesDirty;

    /// <summary>Creates the group over the app context.</summary>
    public WarmupGroupView(PageContext context) : base(context)
    {
        _api = new WarmupApi(context.Api, context.Queries);
        Watch(_api.Settings);
        Watch(_api.Accounts);
    }

    /// <inheritdoc />
    protected override void Build()
    {
        _enabled = new ControlledSwitch();
        Avalonia.Automation.AutomationProperties.SetName(_enabled, Loc.T("warmup.enabled"));
        _enabled.ToggleRequested += on => _ = SaveAsync(new WarmupSettingsPatch(on, null, null, null, null, null));

        _mode = new ControlledSegmented([("daily", Loc.T("warmup.mode.daily")), ("auto", Loc.T("warmup.mode.auto"))], "daily");
        Avalonia.Automation.AutomationProperties.SetName(_mode, Loc.T("warmup.mode"));
        _mode.Chose += v => _ = SaveAsync(new WarmupSettingsPatch(null, v, null, null, null, null));

        _times = new Input { Width = 200, PlaceholderText = "06:00, 11:05", Size = ControlSize.Small };
        Avalonia.Automation.AutomationProperties.SetName(_times, Loc.T("warmup.times"));
        _times.TextChanged += (_, _) => _timesDirty = true;
        var saveTimes = Ui.Button(Loc.T("common.save"), () => _ = SaveTimesAsync());
        _timesRow = RowBuilder.Row(Loc.T("warmup.times"), null, _times, saveTimes);

        _days = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _daysRow = RowBuilder.Row(Loc.T("warmup.weekdays"), null, _days);

        _claude = new ControlledSwitch();
        Avalonia.Automation.AutomationProperties.SetName(_claude, Loc.T("warmup.claude"));
        _claude.ToggleRequested += on => _ = SaveAsync(new WarmupSettingsPatch(null, null, null, null, on, null));
        _codex = new ControlledSwitch();
        Avalonia.Automation.AutomationProperties.SetName(_codex, Loc.T("warmup.codex"));
        _codex.ToggleRequested += on => _ = SaveAsync(new WarmupSettingsPatch(null, null, null, null, null, on));

        _group = RowBuilder.Group(Loc.T("warmup.title"), Loc.T("warmup.hint"),
            RowBuilder.Row(Loc.T("warmup.enabled"), null, _enabled),
            RowBuilder.Row(Loc.T("warmup.mode"), null, _mode),
            _timesRow,
            _daysRow,
            RowBuilder.Row(Loc.T("warmup.claude"), null, _claude),
            RowBuilder.Row(Loc.T("warmup.codex"), null, _codex));

        _accounts = new StackPanel();
        Content = new StackPanel { Children = { _group, _accounts } };
    }

    /// <inheritdoc />
    protected override void Refresh()
    {
        if (_api.Settings.Data is not { } w) return;
        _enabled.SetValue(w.Enabled);
        _mode.SetValue(w.Mode == "auto" ? "auto" : "daily");
        _claude.SetValue(w.Claude);
        _codex.SetValue(w.Codex);
        var daily = w.Mode != "auto";
        _timesRow.IsVisible = daily;
        _daysRow.IsVisible = daily;
        _timesRow.Detail = Loc.T("warmup.zone", ("zone", w.TimeZone));
        // never overwrite what the user is typing: the field follows the server only while untouched
        if (!_timesDirty || !_times.IsFocused) _times.Text = string.Join(", ", w.Times);
        _timesDirty = false;
        _days.Children.Clear();
        foreach (var day in WarmupLogic.Days)
        {
            var picked = WarmupLogic.IsPicked(w.Weekdays, day);
            var d = day;
            _days.Children.Add(Ui.Button(Loc.T("warmup.day." + day), () => _ = SaveAsync(new WarmupSettingsPatch(null, null, null, WarmupLogic.Toggle(w.Weekdays, d), null, null)),
                picked ? ButtonVariant.Secondary : ButtonVariant.Ghost));
        }
        RefreshAccounts();
    }

    private void RefreshAccounts()
    {
        _accounts.Children.Clear();
        var list = _api.Accounts.Data ?? [];
        var group = new SettingsGroup { Title = Loc.T("warmup.accounts") };
        if (list.Count == 0) group.Rows.Add(new TextBlock { Text = Loc.T("warmup.accounts.empty"), Margin = new Avalonia.Thickness(16, 12) });
        foreach (var a in list)
        {
            var account = a;
            var mode = new ControlledSelect(
                [("inherit", Loc.T("warmup.mode.inherit")), ("daily", Loc.T("warmup.mode.daily")), ("auto", Loc.T("warmup.mode.auto")), ("off", Loc.T("warmup.mode.off"))],
                a.Mode, 190);
            Avalonia.Automation.AutomationProperties.SetName(mode, Loc.T("warmup.mode"));
            mode.Chose += v => _ = Host.TryAsync(() => _api.PatchAccountAsync(account.AccountId, new WarmupAccountPatch(v, null, null)));
            var now = Ui.Button(Loc.T("warmup.runNow"), () => _ = WarmNowAsync(account));
            group.Rows.Add(RowBuilder.Row($"{a.ProviderName} · {a.AccountName}", WarmupLogic.Detail(a, DateTimeOffset.UtcNow), mode, now));
        }
        _accounts.Children.Add(group);
    }

    private async Task SaveAsync(WarmupSettingsPatch patch)
    {
        if (await Host.TryAsync(() => _api.UpdateAsync(patch)) is not null) Context.Feedback.Success(Loc.T("warmup.saved"));
    }

    private async Task SaveTimesAsync()
    {
        if (WarmupLogic.ParseTimes(_times.Text ?? "") is not { } times)
        {
            Context.Feedback.Error(Loc.T("warmup.timesInvalid"));
            return;
        }
        _timesDirty = false;
        await SaveAsync(new WarmupSettingsPatch(null, null, times, null, null, null));
    }

    private async Task WarmNowAsync(WarmupAccountDto account)
    {
        var run = await Host.TryAsync(() => _api.WarmNowAsync(account.AccountId));
        if (run is null) return;
        var text = Loc.T("warmup.ran", ("name", account.AccountName), ("result", Loc.T("warmup.result." + run.Result))) + (string.IsNullOrEmpty(run.Message) ? "" : " — " + run.Message);
        if (run.Result == "ok") Context.Feedback.Success(text);
        else Context.Feedback.Error(text);
    }
}
