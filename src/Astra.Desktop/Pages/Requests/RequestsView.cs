using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Requests;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Traces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Requests;

/// <summary>
/// The request log's body (web <c>RequestsView</c>): the filter bar, then the loading / error / empty / table branches, the pager and the detail
/// sheet. The shell draws the header (title, total, live rate, refresh) from <see cref="RequestsPageViewModel"/>.
/// </summary>
public sealed class RequestsView : UserControl
{
    private readonly RequestsPageViewModel _vm;
    private readonly RequestFilterBar _filters = new() { Margin = new Thickness(0, 0, 0, 12) };
    private readonly StackPanel _body = new();
    private readonly Panel _root = new();
    private RequestGrid? _table;
    private int _tableVersion = -1;
    private RequestDetailSheet? _sheet;
    private Border? _pager;
    private (int Page, int Pages, string Language) _pagerKey;
    private Window? _window;

    /// <summary>Builds the view for <paramref name="vm"/>.</summary>
    public RequestsView(RequestsPageViewModel vm)
    {
        _vm = vm;
        _filters.ModelChanged += vm.SetModel;
        _filters.FilterChanged += (id, values) => vm.SetFilter(id, values);
        _filters.Cleared += vm.ClearFilters;
        var column = new StackPanel();
        column.Children.Add(_filters);
        column.Children.Add(_body);
        _root.Children.Add(column);
        Content = _root;
        vm.StateChanged += OnState;
        vm.DetailChanged += OnDetail;
        vm.FocusSearchRequested += _filters.FocusSearch;
        Refresh();
        // A detail opened before the view existed (a link, a test) still shows.
        if (vm.Detail is not null) OnDetail();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.LanguageChanged += OnLanguage;
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowChanged;
            SyncVisibility();
        }
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Loc.LanguageChanged -= OnLanguage;
        if (_window is not null) _window.PropertyChanged -= OnWindowChanged;
        _window = null;
    }

    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty) SyncVisibility();
    }

    /// <summary>The web closes the live stream while its tab is hidden; the window being hidden or minimized is the desktop's equivalent.</summary>
    private void SyncVisibility()
    {
        if (_window is null) return;
        _vm.Visibility.Set(_window.IsVisible && _window.WindowState != WindowState.Minimized);
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        // Column headers and cell texts are built from the catalog: build the table again.
        _table = null;
        _tableVersion = -1;
        _filters.RefreshTexts();
        Refresh();
    });

    private void OnState() => Refresh();

    private void Refresh()
    {
        SyncFilters();
        BuildBody();
    }

    private void SyncFilters()
    {
        _filters.SetOptions(RequestFilterId.Client, (_vm.ClientsQuery.Data ?? []).Select(c => new RequestFilterOption(c.Kind, c.Name)));
        _filters.SetOptions(RequestFilterId.Token, (_vm.Context!.Tokens.Tokens.Data ?? []).Select(t => new RequestFilterOption(t.Id, t.Name)));
        _filters.SetOptions(RequestFilterId.Provider, (_vm.ProvidersQuery.Data ?? []).Select(p => new RequestFilterOption(p.Id, p.Name)));
        _filters.SetOptions(RequestFilterId.Status, RequestFilterCatalog.Statuses.Select(s => new RequestFilterOption(s, Loc.T("status." + s))));
        _filters.SetOptions(RequestFilterId.Origin, RequestFilterCatalog.Origins.Select(o => new RequestFilterOption(o, Loc.T("requests.origin." + o))));
        _filters.SetState(_vm.Model, _vm.Filters, _vm.ShowOrigin);
    }

    private void BuildBody()
    {
        var want = new List<Control>(3);
        if (_vm.IsLoading) want.Add(new LoadingState(4) { Margin = new Thickness(0, 24) });
        if (_vm.IsError) want.Add(new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = _vm.ErrorMessage, Margin = new Thickness(0, 0, 0, 12) });
        if (_vm.HasData && _vm.Items.Count == 0) want.Add(Empty());
        if (_vm.HasData && _vm.Items.Count > 0) want.Add(Table());
        if (_vm.HasData && _vm.PageCount > 1) want.Add(Pager());
        // Same children (the usual refresh: the table and pager are kept): leave them attached. Taking the table out and back in
        // re-attaches every cell (styles, resources, text shaping) — thousands of visuals, the page's main cost.
        if (want.SequenceEqual(_body.Children)) return;
        _body.Children.Clear();
        _body.Children.AddRange(want);
    }

    private Control Empty()
    {
        var filtered = _vm.HasFilters;
        ArcButton? clear = null;
        if (filtered)
        {
            clear = new ArcButton { Variant = ButtonVariant.Secondary, Content = Loc.T("requests.clearFilters"), HorizontalAlignment = HorizontalAlignment.Center };
            clear.Click += (_, _) => _vm.ClearFilters();
        }
        return new EmptyStateView(
            Loc.T(filtered ? "requests.filteredEmpty" : "requests.empty"),
            Loc.T(filtered ? "requests.filteredEmpty.detail" : "requests.empty.detail"),
            new LucideIcon { Kind = LucideIconKind.ScrollText, Size = 24 },
            clear);
    }

    private Control Table()
    {
        if (_table is null)
        {
            _table = RequestTable.Create(_vm.Items, _vm.TableContext);
            _table.RowActivated += row => _vm.Open(row.Id);
            _tableVersion = _vm.ItemsVersion;
        }
        else if (_tableVersion != _vm.ItemsVersion)
        {
            // A new list instance: the table follows rows by id, so unchanged rows keep their place and motion.
            _table.ItemsSource = _vm.Items;
            _tableVersion = _vm.ItemsVersion;
        }
        return _table;
    }

    private Control Pager()
    {
        var key = (_vm.Page, _vm.PageCount, Loc.Language);
        if (_pager is not null && _pagerKey == key) return _pager;
        _pagerKey = key;
        var pager = new Pagination
        {
            PageCount = _vm.PageCount,
            CurrentPage = _vm.Page,
            Label = Loc.T("requests.page", ("page", _vm.Page), ("pages", _vm.PageCount)),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        pager.PageChanged += (_, e) => _vm.GoToPage(e.NewPage);
        return _pager = new Border { Margin = new Thickness(0, 16, 0, 0), Child = pager };
    }

    private void OnDetail()
    {
        if (_sheet is not null)
        {
            _sheet.Detach();
            _root.Children.Remove(_sheet);
            _sheet = null;
        }
        if (_vm.Detail is not { } detail) return;
        var sheet = new RequestDetailSheet(detail, _vm.TableContext, _vm.OpenTrace);
        sheet.Closed += (_, _) =>
        {
            if (ReferenceEquals(_sheet, sheet)) _vm.CloseDetail();
        };
        _sheet = sheet;
        _root.Children.Add(sheet);
        sheet.IsOpen = true;
    }
}
