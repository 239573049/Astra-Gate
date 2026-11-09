using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Providers;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Providers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Pages.Providers;

/// <summary>
/// The body of the Providers page: a toolbar (range, charts, cards / table), the overview, then the card grid or the table (max 1100 px, centered, like the web's <c>max-w-[1100px]</c>) with the loading / error /
/// empty branches, plus the two sheets it opens. The shell supplies the header, the scroller and the page padding.
/// </summary>
public sealed class ProvidersView : UserControl
{
    private readonly ProvidersViewModel _vm;
    private readonly Border _host = new() { MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly AddProviderSheet _add;
    private readonly ImportProvidersSheet _import;
    private string _signature = "";
    private INotifyPropertyChanged? _watchedRange;

    /// <summary>Builds the view for <paramref name="vm"/>.</summary>
    public ProvidersView(ProvidersViewModel vm)
    {
        _vm = vm;
        _add = new AddProviderSheet(vm);
        _import = new ImportProvidersSheet(vm);
        vm.AddSheet = _add;
        vm.ImportSheet = _import;
        Content = new Panel { Children = { _host, _add, _import } };
        vm.AddRequested += OpenAdd;
        vm.ImportRequested += OpenImport;
        Rebuild(force: true);
    }

    private void OpenAdd() => _add.Open();

    private void OpenImport() => _import.Open();

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm.ProviderList.PropertyChanged += OnChanged;
        _vm.Queries.Series.PropertyChanged += OnChanged;
        _vm.WeekStats.PropertyChanged += OnChanged;
        _vm.LayoutChanged += OnLayout;
        _watchedRange = _vm.RangedStats;
        _watchedRange.PropertyChanged += OnChanged;
        Loc.LanguageChanged += OnLanguage;
        Rebuild(force: true);
        if (_vm.OpenAddOnShow)
        {
            _vm.OpenAddOnShow = false;
            OpenAdd();
        }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _vm.ProviderList.PropertyChanged -= OnChanged;
        _vm.Queries.Series.PropertyChanged -= OnChanged;
        _vm.WeekStats.PropertyChanged -= OnChanged;
        _vm.LayoutChanged -= OnLayout;
        if (_watchedRange is not null) _watchedRange.PropertyChanged -= OnChanged;
        _watchedRange = null;
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() => Rebuild(force: true));

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Rebuild(force: false);

    // A range, view or charts change: follow the ranged query and redraw.
    private void OnLayout()
    {
        if (_watchedRange is not null && !ReferenceEquals(_watchedRange, _vm.RangedStats))
        {
            _watchedRange.PropertyChanged -= OnChanged;
            _watchedRange = _vm.RangedStats;
            _watchedRange.PropertyChanged += OnChanged;
        }
        Rebuild(force: true);
    }

    private void Rebuild(bool force)
    {
        var list = _vm.ProviderList;
        var series = _vm.Queries.Series.Data;
        // The polled series and refetches often return the same data: keep the cards (and their sparklines) as they are then.
        var signature = $"{list.Status}|{list.HasData}|{list.Data?.ToJsonString()}|{string.Join(';', series?.Select(p => $"{p.Bucket}{p.Key}{p.Requests}{p.CostUsd}") ?? [])}"
            + $"|{Stats(_vm.WeekStats.Data)}|{_vm.Range}|{Stats(_vm.RangedStats.Data)}|{_vm.View}|{_vm.ChartsOpen}";
        if (!force && signature == _signature) return;
        _signature = signature;

        var stack = new StackPanel { Spacing = 12 };
        if (!list.HasData && !list.IsError) stack.Children.Add(new LoadingState(4));
        if (list.IsError)
            stack.Children.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = list.Error?.Message });
        if (list.HasData)
        {
            var items = _vm.Items;
            if (items.Count == 0) stack.Children.Add(EmptyCard());
            else
            {
                var week = (_vm.WeekStats.Data ?? []).ToDictionary(x => x.ProviderId, StringComparer.Ordinal);
                stack.Children.Add(Toolbar());
                stack.Children.Add(ProviderOverviewView.Build(
                    ProviderOverviewData.Build(items, _vm.RangedStats.Data ?? [], week, Loc.T("providers.overview.others")), _vm.Range, _vm.ChartsOpen));
                stack.Children.Add(List(items, series, week));
            }
        }
        _host.Child = stack;
    }

    private Control EmptyCard()
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        actions.Children.Add(ProviderUi.Button(Loc.T("providers.add"), LucideIconKind.Plus, ButtonVariant.Primary, onClick: _vm.RequestAdd));
        actions.Children.Add(ProviderUi.Button(Loc.T("providers.import"), LucideIconKind.Download, onClick: _vm.RequestImport));
        var empty = new EmptyStateView(Loc.T("providers.empty"), Loc.T("providers.emptyDetail"), new LucideIcon { Kind = LucideIconKind.Server, Size = 24 }, actions);
        // web .card: surface fill, 0.5px separator edge, panel radius
        var card = new Border { BorderThickness = new Thickness(0.5), ClipToBounds = true, Child = empty };
        card.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        return card;
    }

    private static string Stats(List<ProviderStatDto>? stats)
        => string.Join(';', stats?.Select(x => $"{x.ProviderId}{x.Requests}{x.SuccessRequests}{x.CostUsd}{x.AvgTtftMs}{x.InputTokens}{x.OutputTokens}{x.FailoversOut}") ?? []);

    // web: the row above the overview — range on the left; the charts switch and the cards / table choice on the right.
    private Control Toolbar()
    {
        var range = new ControlledSegmented(ProvidersViewModel.Ranges.Select(r => (r, Loc.T("range." + r))), _vm.Range);
        Avalonia.Automation.AutomationProperties.SetName(range, Loc.T("providers.overview.range"));
        range.Chose += v => _vm.SetRange(v);
        var charts = ProviderUi.Button(
            Loc.T(_vm.ChartsOpen ? "providers.overview.hideCharts" : "providers.overview.showCharts"),
            _vm.ChartsOpen ? LucideIconKind.ChevronUp : LucideIconKind.ChartColumn, ButtonVariant.Ghost, ControlSize.Small,
            () => _vm.SetChartsOpen(!_vm.ChartsOpen));
        var view = new ControlledSegmented(
            [(ProvidersPrefs.GridView, Loc.T("providers.view.grid")), (ProvidersPrefs.TableView, Loc.T("providers.view.table"))], _vm.View);
        Avalonia.Automation.AutomationProperties.SetName(view, Loc.T("providers.view.label"));
        view.Chose += v => _vm.SetView(v);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { charts, view } };
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        bar.Children.Add(range);
        Grid.SetColumn(right, 2);
        bar.Children.Add(right);
        return bar;
    }

    private Control List(IReadOnlyList<ProviderDto> items, List<TimeseriesPointDto>? series, Dictionary<string, ProviderStatDto> week)
    {
        var days = ProviderFormat.LastDays(_vm.Now, ProviderUsageBuckets.Days);
        var labels = days.Select(d => d[5..]).ToList();
        var usage = ProviderUsageBuckets.Build(series, days);
        // One scale for every card and row, so bar heights compare across providers.
        var max = usage.Values.SelectMany(u => u.Requests).DefaultIfEmpty(0).Max();
        var services = new ProviderCardServices(_vm.Queries, _vm.Feedback, _vm.Open, _vm.Test);
        if (_vm.View == ProvidersPrefs.TableView)
        {
            var rows = items.Select(p => ProviderTableRow.From(p, week.GetValueOrDefault(p.Id),
                usage.TryGetValue(p.Name, out var u) ? u.Requests : new int[days.Count])).ToList();
            return ProvidersTableView.Build(rows, max, labels, services);
        }
        // Wider than the web's 260: there the name row left a card about 60 px for the provider's name ("Zhi…").
        var grid = new AutoFillGrid { MinColumnWidth = 300, Gap = 12 };
        foreach (var p in items)
        {
            usage.TryGetValue(p.Name, out var u);
            grid.Children.Add(new ProviderCardView(ProviderCardModel.From(p, u), week.GetValueOrDefault(p.Id), labels, max, services));
        }
        return grid;
    }
}
