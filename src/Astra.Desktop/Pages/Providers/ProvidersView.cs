using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Providers;
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
/// The body of the Providers page: a card grid (max 1100 px, centered, like the web's <c>max-w-[1100px]</c>) with the loading / error /
/// empty branches, plus the two sheets it opens. The shell supplies the header, the scroller and the page padding.
/// </summary>
public sealed class ProvidersView : UserControl
{
    private readonly ProvidersViewModel _vm;
    private readonly Border _host = new() { MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly AddProviderSheet _add;
    private readonly ImportProvidersSheet _import;
    private string _signature = "";

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
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() => Rebuild(force: true));

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Rebuild(force: false);

    private void Rebuild(bool force)
    {
        var list = _vm.ProviderList;
        var series = _vm.Queries.Series.Data;
        // The polled series and refetches often return the same data: keep the cards (and their sparklines) as they are then.
        var signature = $"{list.Status}|{list.HasData}|{list.Data?.ToJsonString()}|{string.Join(';', series?.Select(p => $"{p.Bucket}{p.Key}{p.Requests}{p.CostUsd}") ?? [])}";
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
            else stack.Children.Add(Cards(items, series));
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

    private Control Cards(IReadOnlyList<ProviderDto> items, List<TimeseriesPointDto>? series)
    {
        var days = ProviderFormat.LastDays(_vm.Now, ProviderUsageBuckets.Days);
        var usage = ProviderUsageBuckets.Build(series, days);
        var services = new ProviderCardServices(_vm.Queries, _vm.Feedback, _vm.Open, _vm.Test);
        var grid = new AutoFillGrid { MinColumnWidth = 260, Gap = 12 };
        foreach (var p in items)
        {
            usage.TryGetValue(p.Name, out var u);
            grid.Children.Add(new ProviderCardView(ProviderCardModel.From(p, u), days, u?.CostUsd ?? 0, services));
        }
        return grid;
    }
}
