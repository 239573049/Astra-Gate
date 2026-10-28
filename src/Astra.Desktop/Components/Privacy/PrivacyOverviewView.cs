using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Privacy;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Privacy;

/// <summary>
/// The privacy overview (web <c>PrivacyOverview</c>): guard statistics for a range, hits by category, and the policy switches below.
/// The range's query is swapped when the range changes (like the hook's key), the others stay cached.
/// </summary>
public sealed class PrivacyOverviewView : LiveView
{
    private readonly PrivacyApi _api;
    private string _range = PrivacyStatsLogic.DefaultRange;
    private Query<PrivacyEventStatsDto>? _stats;
    private IDisposable? _statsToken;
    private ControlledSegmented _rangeSwitch = null!;
    private ContentControl _metrics = null!, _table = null!;

    /// <summary>Creates the view.</summary>
    public PrivacyOverviewView(PageContext context, PrivacyApi api) : base(context)
    {
        _api = api;
    }

    /// <inheritdoc />
    protected override void Build()
    {
        _rangeSwitch = new ControlledSegmented(PrivacyStatsLogic.Ranges.Select(r => (r, Loc.T("range." + r))), _range);
        Avalonia.Automation.AutomationProperties.SetName(_rangeSwitch, Loc.T("overview.range"));
        _rangeSwitch.Chose += r => { _range = r; Swap(); };
        _metrics = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _table = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var policy = new PrivacyPolicyGroupView(Context, _api);
        var column = new StackPanel { Spacing = 16 };
        column.Children.Add(Section(Loc.T("privacy.stats"), _rangeSwitch, _metrics));
        column.Children.Add(Section(Loc.T("privacy.stats.byCategory"), null, _table));
        column.Children.Add(new Border { MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Stretch, Child = policy });
        Content = column;
    }

    private static Control Section(string title, Control? action, Control body)
    {
        var stack = new StackPanel();
        stack.Children.Add(new SectionTitle(title, action));
        stack.Children.Add(body);
        return stack;
    }

    /// <inheritdoc />
    protected override void OnShown() => Swap();

    /// <inheritdoc />
    protected override void OnHidden() => Unwatch();

    private void Unwatch()
    {
        if (_stats is not null) _stats.PropertyChanged -= OnStats;
        _statsToken?.Dispose();
        _statsToken = null;
        _stats = null;
    }

    private void Swap()
    {
        Unwatch();
        _stats = _api.Stats(_range);
        _stats.PropertyChanged += OnStats;
        _statsToken = _stats.Activate();
        RequestRefresh();
    }

    private void OnStats(object? sender, PropertyChangedEventArgs e) => RequestRefresh();

    /// <inheritdoc />
    protected override void Refresh()
    {
        _rangeSwitch.SetValue(_range);
        var q = _stats;
        var s = q?.Data;
        if (q is null || s is null)
        {
            _metrics.Content = q is { IsError: true } ? new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = UiHost.ErrorText(q.Error!) } : new LoadingState(3);
            _table.Content = null;
            return;
        }
        var context = Loc.T("range." + _range);
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        for (var i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        (string Key, long Value)[] cards = [("privacy.stats.events", s.Events), ("privacy.stats.blocked", s.Blocked), ("privacy.stats.redactions", s.Redactions), ("privacy.stats.dryRun", s.DryRun)];
        for (var i = 0; i < cards.Length; i++)
        {
            var card = new MetricCard { Label = Loc.T(cards[i].Key), Value = cards[i].Value, Context = context };
            Grid.SetColumn(card, i);
            grid.Children.Add(card);
        }
        // web: a failed refetch shows the error alert above the last stats.
        _metrics.Content = q.IsError
            ? new StackPanel { Spacing = 12, Children = { new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = UiHost.ErrorText(q.Error!) }, grid } }
            : grid;
        _table.Content = s.Categories.Count == 0 ? Ui.Card(EmptyHint()) : CategoryTable(s.Categories);
    }

    private static Control EmptyHint()
    {
        var icon = new LucideIcon { Kind = LucideIconKind.ShieldCheck, Size = 24, HorizontalAlignment = HorizontalAlignment.Center };
        icon.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.TextMuted);
        var title = KitStyle.Text(Loc.T("privacy.logs.empty"), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var detail = Ui.Note(Loc.T("privacy.logs.empty.detail"), ArcDesignTokenKind.TextSecondary);
        detail.HorizontalAlignment = HorizontalAlignment.Center;
        detail.TextAlignment = Avalonia.Media.TextAlignment.Center;
        return new StackPanel { Spacing = 8, Margin = new Thickness(16, 32), Children = { icon, title, detail } };
    }

    private static Control CategoryTable(IReadOnlyList<PrivacyCategoryStatDto> rows)
    {
        var table = new SortableDataTable
        {
            Caption = Loc.T("privacy.stats.byCategory"),
            ItemsSource = PrivacyStatsLogic.ByHits(rows).ToList(),
            RowKeySelector = r => ((PrivacyCategoryStatDto)r).Category,
            SortKey = "hits",
            SortDirection = DataTableSortDirection.Descending,
        };
        table.Columns.Add(new DataTableColumn
        {
            Header = Loc.T("privacy.stats.col.category"), Key = "category",
            ValueSelector = r => PrivacyMeta.CategoryLabel(((PrivacyCategoryStatDto)r).Category),
            SortValueSelector = r => ((PrivacyCategoryStatDto)r).Category,
        });
        table.Columns.Add(Numeric("privacy.stats.col.requests", "requests", 110, r => r.Requests));
        table.Columns.Add(Numeric("privacy.stats.col.hits", "hits", 96, r => r.Hits));
        table.Columns.Add(Numeric("privacy.stats.col.warn", "warnHits", 84, r => r.WarnHits));
        table.Columns.Add(Numeric("privacy.stats.col.block", "blockHits", 84, r => r.BlockHits));
        table.Columns.Add(Numeric("privacy.stats.col.redact", "redactHits", 84, r => r.RedactHits));
        return table;
    }

    private static DataTableColumn Numeric(string key, string id, double width, Func<PrivacyCategoryStatDto, long> value) => new()
    {
        Header = Loc.T(key), Key = id, IsNumeric = true, Width = new GridLength(width),
        ValueSelector = r => value((PrivacyCategoryStatDto)r),
        Formatter = v => SettingsFormat.Count(v is long l ? l : null),
    };
}
