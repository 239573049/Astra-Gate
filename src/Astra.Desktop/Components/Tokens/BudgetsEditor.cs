using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Tokens;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Tokens;

/// <summary>
/// The token's usage caps (web <c>BudgetsEditor</c>): one row per budget (metric, period, limit, remove) and an "add limit" button
/// that starts on the first free metric x period. An empty limit is dropped on save; two rows on the same metric and period are
/// flagged by the sheet (<see cref="BudgetRules.HasDuplicate"/>). The rules are <c>Astra.Desktop.Core.Tokens.BudgetRules</c>.
/// </summary>
public sealed class BudgetsEditor : UserControl
{
    private readonly Action<IReadOnlyList<DraftBudget>> _changed;
    private readonly StackPanel _root = new() { Spacing = 8 };
    private List<DraftBudget> _budgets;

    /// <summary>Builds the editor; <paramref name="changed"/> receives the new list after each edit.</summary>
    public BudgetsEditor(IReadOnlyList<DraftBudget> budgets, Action<IReadOnlyList<DraftBudget>> changed)
    {
        _budgets = budgets.ToList();
        _changed = changed;
        Content = _root;
        Rebuild();
    }

    /// <summary>The budgets as edited so far.</summary>
    public IReadOnlyList<DraftBudget> Budgets => _budgets;

    private void Edit(int index, Func<DraftBudget, DraftBudget> patch)
    {
        _budgets[index] = patch(_budgets[index]);
        _changed(_budgets.ToList());
    }

    private void Rebuild()
    {
        _root.Children.Clear();
        if (_budgets.Count == 0)
        {
            var none = KitStyle.Text(Loc.T("tokens.unlimited"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            none.FontSize = KitStyle.Type12;
            _root.Children.Add(none);
        }
        for (var i = 0; i < _budgets.Count; i++) _root.Children.Add(Row(i, _budgets[i]));
        if (BudgetRules.NextFree(_budgets) is { } free)
        {
            var add = new ArcButton
            {
                Variant = ButtonVariant.Secondary, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { new LucideIcon { Kind = LucideIconKind.Plus, Size = 14 }, new TextBlock { Text = Loc.T("tokens.addLimit") } },
                },
            };
            add.Click += (_, _) =>
            {
                _budgets.Add(free);
                _changed(_budgets.ToList());
                Rebuild();
            };
            _root.Children.Add(add);
        }
    }

    private Control Row(int index, DraftBudget budget)
    {
        var metric = Pick.Select(BudgetRules.Metrics.Select(m => (m, Loc.T(BudgetRules.MetricKey(m)))), budget.Metric,
            m => Edit(index, b => b with { Metric = m }), Loc.T("tokens.limitMetric"));
        var period = Pick.Select(BudgetRules.Periods.Select(p => (p, Loc.T(BudgetRules.PeriodKey(p)))), budget.Period,
            p => Edit(index, b => b with { Period = p }), Loc.T("tokens.limitPeriod"));
        // Fixed widths for the two selects, so the limit field keeps the rest of the row.
        metric.Width = 148;
        period.Width = 92;
        var limit = new NumberBox
        {
            Value = budget.Limit, Minimum = 0, AllowFraction = budget.Metric == "cost", Placeholder = Loc.T("tokens.limitValue"),
        };
        Avalonia.Automation.AutomationProperties.SetName(limit, Loc.T("tokens.limitValue"));
        limit.ValueEdited += v => Edit(index, b => b with { Limit = v });
        var remove = new ArcButton { Variant = ButtonVariant.Ghost, Size = ControlSize.Small, Content = new LucideIcon { Kind = LucideIconKind.Trash2, Size = 14 } };
        ToolTip.SetTip(remove, Loc.T("tokens.removeLimit"));
        Avalonia.Automation.AutomationProperties.SetName(remove, Loc.T("tokens.removeLimit"));
        remove.Click += (_, _) =>
        {
            _budgets.RemoveAt(index);
            _changed(_budgets.ToList());
            Rebuild();
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(period, 1);
        Grid.SetColumn(limit, 2);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(metric);
        grid.Children.Add(period);
        grid.Children.Add(limit);
        grid.Children.Add(remove);
        return grid;
    }
}
