using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Pricing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using TKCode.Arc;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Pricing;

/// <summary>
/// Adding a pricing rule is always a dialog, so cancelling never changes the editor's value (web <c>PricingRuleDialog</c>,
/// web/src/components/PricingRuleDialog.tsx). Declare it once in the host's view (it renders nothing in place), call
/// <see cref="Show"/>, and handle <see cref="RuleAdded"/> with <see cref="PricingRuleDraft.Apply"/>. Each <see cref="Show"/> starts from the
/// defaults again (the web resets its state whenever the kind changes). The Add button is enabled only while the draft is valid.
/// </summary>
public sealed class PricingRuleDialog : UserControl
{
    private readonly WideSheet _sheet = new() { SheetWidth = 540 };
    private PricingRuleDraft? _draft;
    private ArcButton? _add;
    private Input? _name;
    private Input? _zone;

    /// <summary>Raised when the person confirms a valid rule; the dialog closes itself afterwards.</summary>
    public event EventHandler<PricingRuleDraft>? RuleAdded;

    /// <summary>Raised when the dialog was dismissed without adding (Cancel, Escape, the scrim).</summary>
    public event EventHandler? Cancelled;

    /// <summary>An empty, closed dialog.</summary>
    public PricingRuleDialog()
    {
        Content = _sheet;
        _sheet.Closed += (_, _) =>
        {
            RestoreOuterScrim();
            if (_draft is not null)
            {
                _draft = null;
                Cancelled?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>Whether the dialog is open.</summary>
    public bool IsOpen => _sheet.IsOpen;

    /// <summary>The draft being edited while the dialog is open (for tests and the visual harness).</summary>
    public PricingRuleDraft? Draft => _draft;

    /// <summary>Opens the dialog for a new rule of <paramref name="kind"/>; <paramref name="serviceNames"/> are the service tiers that exist (a new name may not repeat one).</summary>
    public void Show(PricingRuleKind kind, IEnumerable<string>? serviceNames = null)
    {
        var draft = new PricingRuleDraft(kind, serviceNames);
        _draft = draft;
        _sheet.Title = kind switch
        {
            PricingRuleKind.Context => Loc.T("pricing.addTier"),
            PricingRuleKind.Time => Loc.T("pricing.addWindow"),
            _ => Loc.T("pricing.addServiceTier"),
        };
        _sheet.Description = kind switch
        {
            PricingRuleKind.Context => Loc.T("pricing.contextTiers.hint"),
            PricingRuleKind.Time => Loc.T("pricing.timeWindows.hint"),
            _ => Loc.T("pricing.serviceTiers.hint"),
        };
        _sheet.Content = BuildBody(draft);
        var cancel = PricingUi.Button(Loc.T("common.cancel"), click: Cancel);
        _add = PricingUi.Button(Loc.T("common.add"), PricingUi.Look.Primary, click: () => Confirm());
        _sheet.Footer = [cancel, _add];
        Refresh();
        StackScrims();
        _sheet.IsOpen = true;
    }

    private ModalOverlay? _outer;
    private bool _outerHadScrim;
    private IBrush? _outerScrim;

    /// <summary>
    /// Opened over another dialog (the price editor), the web stacks its overlay on the page only: the page darkens by a second scrim
    /// while the editor stays bright, and this dialog sits on top. Arc instead lays one scrim over everything, the editor included. So
    /// this dialog's own scrim is clear and the editor's scrim darkens like two stacked web scrims: the web's
    /// page reads at ~0.56 of its brightness under one scrim and ~0.31 under two (measured on the captures), so black at 0.70.
    /// </summary>
    private void StackScrims()
    {
        _outer = this.GetLogicalAncestors().OfType<ModalOverlay>().FirstOrDefault(o => !ReferenceEquals(o, _sheet));
        if (_outer is null) return;
        _outerHadScrim = _outer.IsSet(ModalOverlay.ScrimBrushProperty);
        _outerScrim = _outer.ScrimBrush;
        _outer.ScrimBrush = new SolidColorBrush(Color.FromArgb(179, 0, 0, 0));
        _sheet.ScrimBrush = Brushes.Transparent;
    }

    private void RestoreOuterScrim()
    {
        if (_outer is null) return;
        if (_outerHadScrim) _outer.ScrimBrush = _outerScrim;
        else _outer.ClearValue(ModalOverlay.ScrimBrushProperty);
        _outer = null;
    }

    /// <summary>Closes the dialog without adding (the Cancel button).</summary>
    public void Cancel()
    {
        _sheet.IsOpen = false;
    }

    /// <summary>Adds the rule and closes (the Add button); does nothing while the draft is invalid. Returns whether a rule was added.</summary>
    public bool Confirm()
    {
        if (_draft is not { IsValid: true } draft) return false;
        _draft = null; // a close after a confirmed add is not a cancel
        _sheet.IsOpen = false;
        RuleAdded?.Invoke(this, draft);
        return true;
    }

    private void Refresh()
    {
        if (_draft is null) return;
        if (_add is not null) _add.IsEnabled = _draft.IsValid;
        if (_name is not null) _name.ErrorMessage = _draft.NameTaken ? Loc.T("pricing.ruleNameTaken") : null;
        if (_zone is not null) _zone.ErrorMessage = _draft.TimezoneValid ? null : Loc.T("pricing.invalidTimezone");
    }

    private Control BuildBody(PricingRuleDraft draft)
    {
        var body = new StackPanel { Spacing = 16 };
        _name = PricingUi.TextBox(draft.Name, v => { draft.Name = v; Refresh(); }, name: Loc.T("pricing.name"), visibleLabel: true);
        _name.Width = double.NaN;
        _name.HorizontalAlignment = HorizontalAlignment.Stretch;
        body.Children.Add(_name);
        _zone = null;

        switch (draft.Kind)
        {
            case PricingRuleKind.Context:
                body.Children.Add(Stretch(PricingUi.Number(draft.Threshold, v => { draft.Threshold = v; Refresh(); }, name: Loc.T("pricing.threshold"), integer: true, minimum: 1, visibleLabel: true)));
                body.Children.Add(PricingUi.Columns(2,
                    Stretch(PricingUi.Choice(
                        [new SelectOption("whole", Loc.T("pricing.mode.whole")), new SelectOption("progressive", Loc.T("pricing.mode.progressive"))],
                        draft.Mode, v => draft.Mode = v, name: Loc.T("pricing.tierMode"))),
                    Stretch(PricingUi.Choice(
                        [new SelectOption("highest", Loc.T("pricing.output.highest")), new SelectOption("base", Loc.T("pricing.output.base"))],
                        draft.OutputPolicy, v => draft.OutputPolicy = v, name: Loc.T("pricing.outputPolicy")))));
                body.Children.Add(PricingUi.Columns(2, PricingRuleDraft.ContextPriceTypes.Select(type =>
                    Stretch(PricingUi.Number(null, v => { draft.Prices[type] = v; Refresh(); }, name: type, minimum: 0, visibleLabel: true))).ToArray()));
                break;

            case PricingRuleKind.Time:
                _zone = PricingUi.TextBox(draft.Timezone, v => { draft.Timezone = v; Refresh(); }, name: Loc.T("pricing.timezone"), mono: true, visibleLabel: true);
                _zone.Width = double.NaN;
                body.Children.Add(_zone);
                // web: <input type="time"> only holds HH:mm; the typed fields say when they do not (Add stays off via TimeValid)
                Input? start = null, end = null;
                start = PricingUi.TextBox(draft.Start, v => { draft.Start = v; start!.IsInvalid = !PricingRuleDraft.IsClockTime(v); Refresh(); }, name: Loc.T("pricing.start"), mono: true, visibleLabel: true);
                end = PricingUi.TextBox(draft.End, v => { draft.End = v; end!.IsInvalid = !PricingRuleDraft.IsClockTime(v); Refresh(); }, name: Loc.T("pricing.end"), mono: true, visibleLabel: true);
                body.Children.Add(PricingUi.Columns(3,
                    Stretch(start),
                    Stretch(end),
                    Stretch(PricingUi.Number(draft.Multiplier, v => { draft.Multiplier = v; Refresh(); }, name: Loc.T("pricing.multiplier"), minimum: 0, visibleLabel: true))));
                body.Children.Add(DayChips(draft));
                body.Children.Add(Stretch(PricingUi.TextBox(draft.Excluded, v => { draft.Excluded = v; Refresh(); }, name: Loc.T("pricing.excludeDates"), mono: true,
                    placeholder: "2026-10-01, 2026-10-02", visibleLabel: true)));
                break;

            default:
                body.Children.Add(Stretch(PricingUi.Number(draft.Multiplier, v => { draft.Multiplier = v; Refresh(); }, name: Loc.T("pricing.multiplier"), minimum: 0, visibleLabel: true)));
                break;
        }
        return body;
    }

    private static T Stretch<T>(T control) where T : Control
    {
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        control.Width = double.NaN;
        return control;
    }

    /// <summary>The weekday chips of a time window (Mon..Sun, one letter in Chinese like the web's dialog: 一..日 / Mon..Sun).</summary>
    private static Control DayChips(PricingRuleDraft draft)
    {
        var names = Loc.Language == "zh" ? new[] { "一", "二", "三", "四", "五", "六", "日" } : new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };
        return DayChipGroup.Create(Loc.T("pricing.days"), names, draft.Days, days =>
        {
            draft.Days.Clear();
            foreach (var d in days) draft.Days.Add(d);
        });
    }
}

/// <summary>The seven weekday chips shared by the rule dialog and the editor's time window rows (web <c>ChipGroup</c> over 1..7).</summary>
internal static class DayChipGroup
{
    /// <summary>A multi-select chip group over ISO weekdays; <paramref name="changed"/> receives the chosen days (ascending).</summary>
    public static ChipGroup Create(string label, IReadOnlyList<string> names, IEnumerable<int> selected, Action<IReadOnlyList<int>> changed, bool enabled = true)
    {
        var items = Enumerable.Range(1, 7).ToList();
        var group = new ChipGroup
        {
            Label = label,
            IsMultiple = true,
            ItemsSource = items,
            TextSelector = item => item is int d ? names[d - 1] : null,
            ValueSelector = item => item,
            IsEnabled = enabled,
        };
        var wanted = selected.ToHashSet();
        foreach (var day in items.Where(wanted.Contains)) group.SelectedItems?.Add(day);
        group.SelectionChanged += (_, _) => changed((group.SelectedItems?.OfType<int>() ?? []).Order().ToList());
        return group;
    }
}
