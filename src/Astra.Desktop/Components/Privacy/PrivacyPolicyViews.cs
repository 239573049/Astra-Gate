using Astra.Api.Contracts;
using Astra.Core.Privacy;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Privacy;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Components.Privacy;

/// <summary>
/// Saves the privacy policy the way the web does: every change sends the complete new policy (<c>PUT /api/privacy</c>), built from the
/// current one, and a failure becomes an error toast. Shared by the policy switches, the category rules and the custom-rule list.
/// </summary>
public sealed class PrivacyPolicyEditor(UiHost host, PrivacyApi api)
{
    /// <summary>The current settings, or null before the policy loaded.</summary>
    public PrivacySettings? Current => api.Policy.Data?.Settings;

    /// <summary>Applies <paramref name="change"/> to a copy of the current policy and saves it; false when nothing was saved.</summary>
    public async Task<bool> EditAsync(Action<PrivacySettings> change)
    {
        if (Current is not { } s) return false;
        var next = PrivacyPolicyLogic.With(s, change);
        return await host.TryAsync(() => api.UpdateAsync(next)) is not null;
    }

    /// <summary>Saves a policy built by a logic function.</summary>
    public async Task<bool> SaveAsync(PrivacySettings next) => await host.TryAsync(() => api.UpdateAsync(next)) is not null;
}

/// <summary>Badges of the privacy pages.</summary>
public static class PrivacyBadges
{
    /// <summary>The action's badge (block red, redact orange, otherwise neutral; web <c>PrivacyActionBadge</c>).</summary>
    public static Badge Action(string action) => Ui.Badge(PrivacyMeta.ActionLabel(action), PrivacyMeta.ActionTone(action));
}

/// <summary>
/// The guard's policy group (web <c>PrivacyPolicyGroup</c>, plan §6.7): the guard, "detect only", response restoring, sample recording and
/// the default action. Every change saves the whole policy.
/// </summary>
public sealed class PrivacyPolicyGroupView : LiveView
{
    private readonly PrivacyApi _api;
    private readonly PrivacyPolicyEditor _editor;
    private SettingsGroup _group = null!;
    private ControlledSwitch _enabled = null!, _dryRun = null!, _restore = null!, _samples = null!, _fullSamples = null!;
    private ControlledSelect _default = null!;
    private SettingsRow _enabledRow = null!, _dryRow = null!, _restoreRow = null!, _samplesRow = null!, _fullSamplesRow = null!, _defaultRow = null!;

    /// <summary>Creates the group.</summary>
    public PrivacyPolicyGroupView(PageContext context, PrivacyApi api) : base(context)
    {
        _api = api;
        _editor = new PrivacyPolicyEditor(Host, api);
        Watch(api.Policy);
    }

    private ControlledSwitch Switch(string label, Func<PrivacySettings, Action<bool>> apply)
    {
        var sw = new ControlledSwitch();
        Avalonia.Automation.AutomationProperties.SetName(sw, label);
        sw.ToggleRequested += on =>
        {
            if (_editor.Current is not { } s) return;
            _ = _editor.EditAsync(n => apply(n)(on));
        };
        return sw;
    }

    /// <inheritdoc />
    protected override void Build()
    {
        _enabled = Switch(Loc.T("settings.privacy.enabled"), n => v => n.Enabled = v);
        _dryRun = Switch(Loc.T("settings.privacy.dryRun"), n => v => n.DryRun = v);
        _restore = Switch(Loc.T("settings.privacy.restore"), n => v => n.RestoreResponses = v);
        _samples = Switch(Loc.T("settings.privacy.recordSamples"), n => v => n.RecordSamples = v);
        _fullSamples = Switch(Loc.T("settings.privacy.recordFullSamples"), n => v => n.RecordFullSamples = v);
        _default = new ControlledSelect(PrivacyPolicyLogic.SelectableActions(true).Select(a => (a, PrivacyMeta.ActionLabel(a))), "warn", 144);
        Avalonia.Automation.AutomationProperties.SetName(_default, Loc.T("settings.privacy.defaultAction"));
        _default.Chose += a => _ = _editor.EditAsync(n => n.DefaultAction = a);
        _enabledRow = RowBuilder.Row(Loc.T("settings.privacy.enabled"), Loc.T("settings.privacy.enabledHint"), _enabled);
        _dryRow = RowBuilder.Row(Loc.T("settings.privacy.dryRun"), Loc.T("settings.privacy.dryRunHint"), _dryRun);
        _restoreRow = RowBuilder.Row(Loc.T("settings.privacy.restore"), Loc.T("settings.privacy.restoreHint"), _restore);
        _samplesRow = RowBuilder.Row(Loc.T("settings.privacy.recordSamples"), Loc.T("settings.privacy.recordSamplesHint"), _samples);
        _fullSamplesRow = RowBuilder.Row(Loc.T("settings.privacy.recordFullSamples"), Loc.T("settings.privacy.recordFullSamplesHint"), _fullSamples);
        _defaultRow = RowBuilder.Row(Loc.T("settings.privacy.defaultAction"), null, _default);
        _group = RowBuilder.Group(Loc.T("settings.privacy"), Loc.T("settings.privacy.footer"));
        Content = _group;
    }

    /// <inheritdoc />
    protected override void Refresh()
    {
        if (_api.Policy.Data?.Settings is not { } s)
        {
            Control note = _api.Policy.IsError
                ? Ui.GroupLine(Ui.ErrorText(UiHost.ErrorText(_api.Policy.Error!)), 12, 12)
                : new Border { Padding = new Thickness(16, 16), Child = Ui.Spinner(), HorizontalAlignment = HorizontalAlignment.Center };
            RowAssembly.Sync(_group, [note]);
            return;
        }
        _enabled.SetValue(s.Enabled);
        _dryRun.SetValue(s.DryRun);
        _dryRun.IsEnabled = s.Enabled;
        _restore.SetValue(s.RestoreResponses);
        _restore.IsEnabled = s.Enabled;
        _samples.SetValue(s.RecordSamples);
        _samples.IsEnabled = s.Enabled;
        _fullSamples.SetValue(s.RecordFullSamples);
        _fullSamples.IsEnabled = s.Enabled && s.RecordSamples;
        _default.SetValue(s.DefaultAction);
        _default.IsEnabled = s.Enabled;
        RowAssembly.Sync(_group, [_enabledRow, _dryRow, _restoreRow, _samplesRow, _fullSamplesRow, _defaultRow]);
    }
}

/// <summary>
/// The detection-rules tab (web <c>PrivacyRulesPanel</c>): the action of each built-in category and the custom rules (switch, delete, add
/// from a template or by hand).
/// </summary>
public sealed class PrivacyRulesView : LiveView
{
    private readonly PrivacyApi _api;
    private readonly PrivacyPolicyEditor _editor;
    private StackPanel _stack = null!;

    /// <summary>Creates the view.</summary>
    public PrivacyRulesView(PageContext context, PrivacyApi api) : base(context)
    {
        _api = api;
        _editor = new PrivacyPolicyEditor(Host, api);
        Watch(api.Policy);
    }

    /// <inheritdoc />
    protected override void Build()
    {
        _stack = new StackPanel { Spacing = 16, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Stretch };
        Content = _stack;
    }

    /// <inheritdoc />
    protected override void Refresh()
    {
        _stack.Children.Clear();
        var policy = _api.Policy.Data;
        if (policy is null)
        {
            if (!_api.Policy.IsError) _stack.Children.Add(new Border { Padding = new Thickness(0, 16), Child = Ui.Spinner(), HorizontalAlignment = HorizontalAlignment.Center });
            return;
        }
        var s = policy.Settings;
        _stack.Children.Add(CategoriesGroup(policy, s));
        _stack.Children.Add(CustomRulesGroup(s));
    }

    private Control CategoriesGroup(PrivacyPolicyDto policy, PrivacySettings s)
    {
        var group = RowBuilder.Group(Loc.T("settings.privacy.categories"), null);
        group.Margin = new Thickness(0);
        foreach (var category in PrivacyPolicyLogic.Categories(policy.Rules))
        {
            var cat = category;
            var current = PrivacyPolicyLogic.ActionOf(s, cat);
            var row = RowBuilder.Row(PrivacyMeta.CategoryLabel(cat), PrivacyPolicyLogic.Description(policy.Rules, cat));
            if (current == PrivacyActions.Off) row.Trailing.Add(Ui.Badge(PrivacyMeta.ActionLabel("off"), "neutral"));
            else
            {
                var select = new ControlledSelect(PrivacyPolicyLogic.SelectableActions(false).Select(a => (a, PrivacyMeta.ActionLabel(a))), current, 144) { IsEnabled = s.Enabled };
                Avalonia.Automation.AutomationProperties.SetName(select, PrivacyMeta.CategoryLabel(cat));
                select.Chose += a => _ = _editor.EditAsync(n => n.CategoryActions[cat] = a);
                row.Trailing.Add(select);
            }
            if (PrivacyPolicyLogic.IsOff(s, cat))
                row.Trailing.Add(Ui.Button(Loc.T("settings.privacy.enableCategory"), () => _ = _editor.EditAsync(n => n.CategoryActions[cat] = PrivacyPolicyLogic.EnabledAction(n)),
                    ButtonVariant.Ghost, enabled: s.Enabled));
            group.Rows.Add(row);
        }
        return group;
    }

    private Control CustomRulesGroup(PrivacySettings s)
    {
        var group = RowBuilder.Group(Loc.T("settings.privacy.customRules"), null);
        group.Margin = new Thickness(0);
        if (s.CustomRules.Count == 0) group.Rows.Add(Ui.GroupLine(Ui.Note(Loc.T("settings.privacy.noCustomRules"), ArcDesignTokenKind.TextSecondary), 16, 16));
        foreach (var rule in s.CustomRules)
        {
            var r = rule;
            var pattern = KitStyle.Text(r.Pattern, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
            var row = RowBuilder.Row(r.Name, pattern);
            var enabled = new ControlledSwitch { IsChecked = r.Enabled, IsEnabled = s.Enabled };
            Avalonia.Automation.AutomationProperties.SetName(enabled, r.Name);
            enabled.ToggleRequested += on => _ = _editor.EditAsync(n => { foreach (var x in n.CustomRules.Where(x => x.Id == r.Id)) x.Enabled = on; });
            row.Trailing.Add(enabled);
            row.Trailing.Add(Ui.IconButton(LucideIconKind.Trash2, $"{Loc.T("common.delete")} {r.Name}", () => _ = RemoveAsync(r)));
            group.Rows.Add(row);
        }
        group.Rows.Add(Ui.GroupLine(Ui.Button(Loc.T("settings.privacy.addRule"), () => _ = OpenAddRule(), icon: LucideIconKind.Plus, enabled: s.Enabled), 8, 8));
        return group;
    }

    private async Task RemoveAsync(PrivacyCustomRule rule)
    {
        if (!await Host.ConfirmAsync(Loc.T("settings.privacy.deleteRuleConfirm", ("name", rule.Name)), null, Loc.T("common.delete"), true)) return;
        await _editor.EditAsync(n => n.CustomRules.RemoveAll(x => x.Id == rule.Id));
    }

    /// <summary>Opens the add-rule dialog.</summary>
    public Task OpenAddRule() => new AddRuleSheet(Host, Context.Clock, async rule => await _editor.EditAsync(n => n.CustomRules.Add(rule))).ShowAsync();
}

/// <summary>Add a custom detection rule (web <c>AddRuleSheet</c>): pick a template to start from, then name it and edit the pattern; the pattern is checked before it is sent (the server validates again).</summary>
public sealed class AddRuleSheet
{
    private readonly UiHost _host;
    private readonly Core.Shell.IClock _clock;
    private readonly Func<PrivacyCustomRule, Task<bool>> _add;
    private readonly Sheet _sheet;
    private readonly Input _name;
    private readonly Textarea _pattern;
    private readonly ControlledSelect _templates;
    private readonly TKCode.Arc.Button _create;

    /// <summary>Builds the dialog; <paramref name="add"/> saves the new rule (true on success).</summary>
    public AddRuleSheet(UiHost host, Core.Shell.IClock clock, Func<PrivacyCustomRule, Task<bool>> add)
    {
        _host = host;
        _clock = clock;
        _add = add;
        _templates = new ControlledSelect(
            [("", Loc.T("settings.privacy.ruleTemplateNone")), .. PrivacyRuleTemplates.All.Select(t => (t.Id, Loc.T(t.NameKey)))], "", double.NaN)
        {
            Label = Loc.T("settings.privacy.ruleTemplates"),
            Description = Loc.T("settings.privacy.ruleTemplatesHint"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _templates.Chose += PickTemplate;
        _name = new Input { Label = Loc.T("settings.privacy.ruleName") };
        _name.TextChanged += (_, _) => Validate();
        _pattern = new Textarea { Label = Loc.T("settings.privacy.rulePattern"), Description = Loc.T("settings.privacy.rulePatternHint"), MinHeight = 80, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        _pattern.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        _pattern.TextChanged += (_, _) => Validate();

        var cancel = Ui.Button(Loc.T("common.cancel"), () => _sheet!.Close(), size: ControlSize.Medium);
        _create = Ui.Button(Loc.T("common.create"), () => _ = SubmitAsync(), ButtonVariant.Primary, enabled: false, size: ControlSize.Medium);
        _sheet = new Sheet
        {
            SheetWidth = 480,
            Title = Loc.T("settings.privacy.addRule"),
            Content = Ui.Stack(12, _templates, _name, _pattern),
            Footer = [cancel, _create],
        };
    }

    /// <summary>The dialog (scenes open it directly).</summary>
    public Sheet Sheet => _sheet;

    /// <summary>Shows it; completes when it closes.</summary>
    public Task ShowAsync()
    {
        // web: the name field has autoFocus.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _name.Focus(), Avalonia.Threading.DispatcherPriority.Background);
        return _host.ShowAsync(_sheet);
    }

    private void Validate() => _create.IsEnabled = (_name.Text ?? "").Trim() != "" && (_pattern.Text ?? "").Trim() != "";

    private void PickTemplate(string id)
    {
        var t = PrivacyRuleTemplates.Find(id);
        if (t is null) return;
        _name.Text = Loc.T(t.NameKey);
        _pattern.Text = t.Pattern;
        Validate();
    }

    private async Task SubmitAsync()
    {
        var rule = PrivacyPolicyLogic.NewRule(_name.Text ?? "", _pattern.Text ?? "", _clock.Now);
        if (rule is null) return;
        if (!PrivacyPolicyLogic.TryValidatePattern(rule.Pattern, out var error))
        {
            _host.Error($"{Loc.T("settings.privacy.invalidPattern")}: {error}");
            return;
        }
        _create.IsLoading = true;
        var ok = await _add(rule);
        _create.IsLoading = false;
        if (ok) _sheet.Close();
    }
}

/// <summary>The tester tab (web <c>DryRunTester</c>): runs a text through the current policy as if the guard were on, touching no real request, and lists what it would block or redact.</summary>
public sealed class DryRunTesterView : LiveView
{
    private readonly PrivacyApi _api;
    private SettingsGroup _group = null!;
    private Textarea _text = null!;
    private TKCode.Arc.Button _run = null!;
    private ContentControl _result = null!;
    private PrivacyDryRunResultDto? _last;
    private bool _running;
    private string _typed = "";

    /// <summary>Creates the view.</summary>
    public DryRunTesterView(PageContext context, PrivacyApi api) : base(context)
    {
        _api = api;
        Watch(api.Policy);
    }

    /// <inheritdoc />
    protected override void Build()
    {
        _text = new Textarea { Text = _typed, PlaceholderText = Loc.T("settings.privacy.testerPlaceholder"), MinHeight = 96, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Avalonia.Automation.AutomationProperties.SetName(_text, Loc.T("settings.privacy.tester"));
        _text.Token(TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        _text.TextChanged += (_, _) => { _typed = _text.Text ?? ""; UpdateRun(); };
        _run = Ui.Button(Loc.T("settings.privacy.runTest"), () => _ = RunAsync(), icon: LucideIconKind.ShieldCheck, size: ControlSize.Medium);
        _run.HorizontalAlignment = HorizontalAlignment.Left;
        _result = new ContentControl();
        var body = new StackPanel { Spacing = 12, Margin = new Thickness(16, 12) };
        body.Children.Add(_text);
        body.Children.Add(_run);
        body.Children.Add(_result);
        _group = RowBuilder.Group(Loc.T("settings.privacy.tester"), Loc.T("settings.privacy.testerHint"), body);
        Content = new Border { MaxWidth = 680, HorizontalAlignment = HorizontalAlignment.Stretch, Child = _group };
    }

    /// <inheritdoc />
    protected override void Refresh()
    {
        UpdateRun();
        RenderResult();
    }

    private bool GuardEnabled => _api.Policy.Data?.Settings.Enabled ?? false;

    private void UpdateRun()
    {
        _run.IsEnabled = GuardEnabled && _typed.Trim() != "";
        _run.IsLoading = _running;
    }

    private async Task RunAsync()
    {
        _running = true;
        // web: the mutation resets on a new run, so neither a pending run nor a failed one shows the previous result.
        _last = null;
        RenderResult();
        UpdateRun();
        var result = await Host.TryAsync(() => _api.DryRunAsync(_typed));
        _running = false;
        if (result is not null) _last = result;
        Refresh();
    }

    /// <summary>Shows a result without running the test (scenes and tests).</summary>
    public void ShowResult(PrivacyDryRunResultDto result)
    {
        _last = result;
        RenderResult();
    }

    private void RenderResult()
    {
        if (_last is not { } r)
        {
            _result.Content = null;
            return;
        }
        var badges = Ui.Inline(8);
        foreach (var (text, tone) in PrivacyEventRows.DryRunBadges(r)) badges.Children.Add(Ui.Badge(text.Text, tone));
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(badges);
        foreach (var h in r.Hits)
        {
            var line = new WrapPanel { ItemSpacing = 8, LineSpacing = 2, VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(PrivacyBadges.Action(h.Action));
            line.Children.Add(KitStyle.Text($"{PrivacyMeta.CategoryLabel(h.Category)} · {h.RuleId} × {h.Count}", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground));
            if (h.Samples.Count > 0)
            {
                var samples = KitStyle.Text(PrivacyEventRows.Samples(h), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
                samples.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
                samples.FontSize = 11;
                line.Children.Add(samples);
            }
            stack.Children.Add(line);
        }
        var box = new Border { BorderThickness = new Thickness(0.5), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8), Child = stack };
        box.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        _result.Content = box;
    }
}
