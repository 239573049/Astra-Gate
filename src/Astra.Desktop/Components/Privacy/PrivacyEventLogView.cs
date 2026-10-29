using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Requests;
using Astra.Desktop.Core.Traces;
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
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Privacy;

/// <summary>
/// The interception log (web <c>PrivacyEventLogs</c>): every request the guard produced an outcome for — blocked, redacted, only detected or
/// warned — with a model search, four filters, a refresh button and pages of 50. The first page follows new events every 5 s. A row opens the
/// request (<see cref="RequestOpener"/>).
/// </summary>
public sealed class PrivacyEventLogView : LiveView
{
    /// <summary>
    /// Replaces how a row opens its request (tests); null (the default) opens the request-detail sheet over the log, as the web's
    /// <c>RequestDetailSheet</c> in PrivacyEventLogs.tsx does.
    /// </summary>
    public static Action<PageContext, string>? RequestOpener { get; set; }

    private readonly PrivacyApi _api;
    private readonly PrivacyEventFilters _filters = new();
    private Query<PageDto<PrivacyEventDto>>? _events;
    private IDisposable? _token;
    private PageDto<PrivacyEventDto>? _shown;
    private DispatcherTimer _debounce = null!;
    private string _typedModel = "";
    private SearchField _search = null!;
    private TKCode.Arc.Button _refresh = null!;
    private FilterToolbar _toolbar = null!;
    private ContentControl _body = null!;
    private Pagination _pager = null!;
    private Border _pagerHost = null!;
    private bool _quiet;
    private IReadOnlyList<(string Kind, string Name)> _clients = [];
    private Grid _root = null!;
    private RequestDetailSheet? _sheet;

    /// <summary>Creates the view.</summary>
    public PrivacyEventLogView(PageContext context, PrivacyApi api) : base(context)
    {
        _api = api;
        Watch(api.Policy);
        Watch(api.Clients);
    }

    /// <inheritdoc />
    protected override void Build()
    {
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (_typedModel == _filters.Model) return;
            _filters.SetModel(_typedModel);
            Swap();
        };
        _search = new SearchField { Width = 208, PlaceholderText = Loc.T("requests.searchModel"), Text = _filters.Model };
        Avalonia.Automation.AutomationProperties.SetName(_search, Loc.T("requests.searchModel"));
        _search.TextChanged += (_, _) =>
        {
            if (_quiet) return;
            _typedModel = _search.Text ?? "";
            _debounce.Stop();
            _debounce.Start();
        };
        _refresh = Ui.Button(null, () => _ = _events?.RefetchAsync(), ButtonVariant.Ghost, LucideIconKind.RefreshCw, tip: Loc.T("common.refresh"), size: ControlSize.Medium);
        var top = Ui.Inline(8, _search, _refresh);
        top.Margin = new Thickness(0, 0, 0, 12);

        _toolbar = new FilterToolbar { Label = Loc.T("requests.filters"), AddFilterLabel = Loc.T("requests.addFilter"), EmptyText = Loc.T("requests.noFilters"), ClearAllLabel = Loc.T("requests.clearFilters"), IsSearchVisible = false };
        _toolbar.FiltersChanged += OnFiltersChanged;
        _toolbar.Margin = new Thickness(0, 0, 0, 12);

        _body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _pager = new Pagination { Label = "", PageLabelFormat = Loc.T("requests.page", ("page", "{0}"), ("pages", "")) };
        _pager.PageChanged += (_, e) =>
        {
            if (_quiet) return;
            _filters.GoTo(e.NewPage);
            Swap();
        };
        _pagerHost = new Border { Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Center, Child = _pager, IsVisible = false };

        // A grid so the request-detail sheet can live beside the content (web: RequestDetailSheet rendered by the log itself).
        _root = new Grid { Children = { new StackPanel { Children = { top, _toolbar, _body, _pagerHost } } } };
        if (_sheet is not null) _root.Children.Add(_sheet);
        Content = _root;
        BuildFields();
    }

    /// <inheritdoc />
    protected override void OnShown() => Swap();

    /// <inheritdoc />
    protected override void OnHidden()
    {
        _debounce.Stop();
        Unwatch();
    }

    private void Unwatch()
    {
        if (_events is not null) _events.PropertyChanged -= OnEvents;
        _token?.Dispose();
        _token = null;
        _events = null;
    }

    private void Swap()
    {
        Unwatch();
        _events = _api.Events(_filters);
        _events.PropertyChanged += OnEvents;
        _token = _events.Activate();
        RequestRefresh();
    }

    private void OnEvents(object? sender, PropertyChangedEventArgs e) => RequestRefresh();

    // ---------------------------------------------------------------- filters

    private IEnumerable<string> Categories => PrivacyPolicyLogic.Categories(_api.Policy.Data?.Rules ?? []);

    private IReadOnlyList<(string Kind, string Name)> ClientOptions()
        => (_api.Clients.Data ?? []).OfType<System.Text.Json.Nodes.JsonObject>()
            .Select(c => (Kind: Str(c, "kind") ?? "", Name: Str(c, "name") ?? Str(c, "kind") ?? ""))
            .Where(c => c.Kind != "").ToList();

    private static string? Str(System.Text.Json.Nodes.JsonObject o, string key) => o[key] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private void BuildFields()
    {
        _toolbar.Fields.Clear();
        _clients = ClientOptions();
        foreach (var f in Enum.GetValues<PrivacyFilter>())
        {
            var field = new FilterField { Id = PrivacyEventFilters.Id(f), Label = PrivacyEventFilters.Label(f).Text };
            foreach (var o in PrivacyEventFilters.Options(f, _clients, Categories)) field.Options.Add(new FilterOption(o.Label));
            _toolbar.Fields.Add(field);
        }
        SyncChips();
    }

    private void SyncChips()
    {
        var chips = new List<FilterChip>();
        foreach (var f in Enum.GetValues<PrivacyFilter>())
        {
            if (_filters[f] == "") continue;
            var label = PrivacyEventFilters.Options(f, _clients, Categories).FirstOrDefault(o => o.Value == _filters[f]).Label ?? _filters[f];
            chips.Add(new FilterChip(PrivacyEventFilters.Id(f), PrivacyEventFilters.Label(f).Text, label));
        }
        _quiet = true;
        try { _toolbar.ActiveFilters = new Avalonia.Collections.AvaloniaList<FilterChip>(chips); }
        finally { _quiet = false; }
    }

    private void OnFiltersChanged(object? sender, FilterToolbarFiltersChangedEventArgs e)
    {
        if (_quiet) return;
        switch (e.Kind)
        {
            case FilterChangeKind.Cleared:
                _filters.ClearAll();
                break;
            case FilterChangeKind.Removed when e.Filter is { } removed:
                _filters.Set(PrivacyEventFilters.FromId(removed.Id), "");
                break;
            case FilterChangeKind.Added or FilterChangeKind.Changed when e.Filter is { } chip:
                var filter = PrivacyEventFilters.FromId(chip.Id);
                var value = PrivacyEventFilters.Options(filter, _clients, Categories).FirstOrDefault(o => o.Label == chip.Value).Value;
                if (!string.IsNullOrEmpty(value)) _filters.Set(filter, value);
                break;
        }
        Swap();
    }

    // ---------------------------------------------------------------- rendering

    /// <inheritdoc />
    protected override void Refresh()
    {
        var clients = ClientOptions();
        if (!clients.SequenceEqual(_clients) || _toolbar.Fields.Count == 0) BuildFields();
        else if (_toolbar.Fields.Count > 0 && _toolbar.Fields[1].Options.Count != Categories.Count()) BuildFields();
        SyncChips();

        var q = _events;
        if (q?.Data is { } data) _shown = data; // keep the last page on screen while another loads (react-query placeholderData)
        _refresh.IsLoading = q?.IsFetching == true && _shown is not null;
        var shown = _shown;
        if (shown is null)
        {
            _body.Content = q is { IsError: true }
                ? new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = UiHost.ErrorText(q.Error!) }
                : new LoadingState(4) { Margin = new Thickness(0, 24) };
            _pagerHost.IsVisible = false;
            return;
        }

        // web: a failed refetch shows the error above the page it still has.
        Control? stale = q is { IsError: true } ? new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = UiHost.ErrorText(q.Error!), Margin = new Thickness(0, 0, 0, 12) } : null;
        if (shown.Items.Count == 0)
        {
            var icon = new LucideIcon { Kind = LucideIconKind.ShieldOff, Size = 24 };
            icon.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.TextMuted);
            var empty = new EmptyStateView(Loc.T("privacy.logs.empty"), Loc.T("privacy.logs.empty.detail"), icon);
            _body.Content = stale is null ? empty : new StackPanel { Children = { stale, empty } };
        }
        else
        {
            var total = KitStyle.Text(Loc.T("privacy.logs.total", ("count", SettingsFormat.Count(shown.Total))), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
            total.FontSize = KitStyle.Type12;
            total.Margin = new Thickness(0, 0, 0, 8);
            var list = new StackPanel { Children = { total, Table(shown) } };
            if (stale is not null) list.Children.Insert(0, stale);
            _body.Content = list;
        }

        var pages = PrivacyEventFilters.Pages(shown.Total);
        _quiet = true;
        try
        {
            _pager.PageCount = pages;
            _pager.CurrentPage = Math.Min(_filters.Page, pages);
            _pager.Label = Loc.T("requests.page", ("page", _filters.Page), ("pages", pages));
        }
        finally { _quiet = false; }
        _pagerHost.IsVisible = pages > 1;
    }

    private Control Table(PageDto<PrivacyEventDto> page)
    {
        var rows = PrivacyEventRows.Rows(page.Items);
        var now = Context.Clock.Now;
        // web: every column sorts the shown page client-side (SortableDataTable columns are sortable unless they opt out).
        var table = new Table
        {
            Caption = Loc.T("privacy.tab.logs"),
            EmptyMessage = Loc.T("privacy.logs.empty"),
            ItemsSource = rows.ToList(),
            RowKeySelector = r => ((PrivacyEventRow)r).Id,
        };
        table.Columns.Add(new DataTableColumn
        {
            Header = Loc.T("requests.col.time"), Key = "time", Width = new GridLength(100), IsSortable = true, IsNumeric = false,
            ValueSelector = r => ((PrivacyEventRow)r).Time,
            CellTemplate = new FuncDataTemplate<PrivacyEventRow>((row, _) => row is null ? null : KitStyle.Text(SettingsFormat.Time(row.Time, now, Loc.Language), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground)),
        });
        table.Columns.Add(new DataTableColumn
        {
            // web: an auto-layout table gives the model about 170 px and the hits the rest (hits fit on one line)
            Header = Loc.T("requests.col.model"), Key = "model", Width = new GridLength(172), IsSortable = true, IsNumeric = false,
            ValueSelector = r => ((PrivacyEventRow)r).Model,
            CellTemplate = new FuncDataTemplate<PrivacyEventRow>((row, _) => row is null ? null : ModelCell(row)),
        });
        table.Columns.Add(new DataTableColumn
        {
            Header = Loc.T("privacy.logs.col.outcome"), Key = "outcome", Width = new GridLength(130), IsSortable = true, IsNumeric = false,
            ValueSelector = r => ((PrivacyEventRow)r).Outcome,
            CellTemplate = new FuncDataTemplate<PrivacyEventRow>((row, _) =>
            {
                if (row is null) return null;
                var (text, tone) = PrivacyEventRows.OutcomeBadge(row.Event);
                return Ui.Badge(text.Text, tone);
            }),
        });
        table.Columns.Add(new DataTableColumn
        {
            Header = Loc.T("privacy.logs.col.hits"), Key = "hits", IsSortable = true, IsNumeric = false,
            ValueSelector = r => ((PrivacyEventRow)r).Hits,
            CellTemplate = new FuncDataTemplate<PrivacyEventRow>((row, _) => row is null ? null : HitsCell(row.Event)),
        });
        table.Columns.Add(new DataTableColumn
        {
            Header = Loc.T("requests.col.client"), Key = "client", Width = new GridLength(110), IsSortable = true, IsNumeric = false,
            ValueSelector = r => ((PrivacyEventRow)r).Client,
            CellTemplate = new FuncDataTemplate<PrivacyEventRow>((row, _) => row is null ? null : KitStyle.Text(string.IsNullOrEmpty(row.Client) ? "—" : row.Client, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground)),
        });
        return table;
    }

    private Control ModelCell(PrivacyEventRow row)
    {
        var model = KitStyle.Text(string.IsNullOrEmpty(row.Model) ? "—" : row.Model, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        var client = KitStyle.Text(row.Client, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        client.FontSize = 11;
        var stack = new StackPanel { Children = { model, client }, Background = Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        Avalonia.Automation.AutomationProperties.SetName(stack, Loc.T("privacy.logs.viewRequest", ("model", string.IsNullOrEmpty(row.Model) ? row.Id : row.Model)));
        stack.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(stack).Properties.IsLeftButtonPressed) return;
            OpenRequest(row.Id);
        };
        // web: the cell is a <button>, so Tab reaches it and Enter / Space open the request.
        stack.Focusable = true;
        stack.KeyDown += (_, e) =>
        {
            if (e.Key is not (Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space)) return;
            e.Handled = true;
            OpenRequest(row.Id);
        };
        return stack;
    }

    /// <summary>Opens a request from a row: the request-detail sheet (the Requests page's own), over this log.</summary>
    public void OpenRequest(string id)
    {
        if (RequestOpener is { } open)
        {
            open(Context, id);
            return;
        }
        CloseSheet();
        var detail = new RequestDetailViewModel(Context, id);
        // The providers query the Requests page shares (same key): only for the provider icon in the sheet.
        var providers = Context.Queries.Query(QueryKey.Of("request-filters", "providers"),
            async ct => await Context.Api.GetAsync<List<ProviderDto>>("/api/providers", ct) ?? []);
        var providersWatch = providers.Activate();
        var table = new RequestTableContext { Clock = Context.Clock, Provider = pid => pid is null ? null : providers.Data?.FirstOrDefault(p => p.Id == pid) };
        var sheet = new RequestDetailSheet(detail, table, traceId => Context.Navigator.Push(nameof(AppRoute.Traces), TraceRoutes.Request(traceId).Path));
        sheet.Closed += (_, _) =>
        {
            providersWatch.Dispose();
            if (ReferenceEquals(_sheet, sheet)) CloseSheet();
        };
        _sheet = sheet;
        _sheetVm = detail;
        _root.Children.Add(sheet);
        sheet.IsOpen = true;
    }

    private RequestDetailViewModel? _sheetVm;

    private void CloseSheet()
    {
        if (_sheet is null) return;
        _sheet.Detach();
        _root.Children.Remove(_sheet);
        _sheet = null;
        _sheetVm?.Dispose();
        _sheetVm = null;
    }

    private static Control HitsCell(PrivacyEventDto e)
    {
        if (e.Hits.Count == 0) return KitStyle.Text("—", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextMuted);
        var (shown, more) = PrivacyEventRows.HitsSummary(e);
        var panel = new WrapPanel { ItemSpacing = 8, LineSpacing = 2 };
        foreach (var hit in shown)
        {
            var inline = Ui.Inline(4, PrivacyBadges.Action(hit.Action), KitStyle.Text(PrivacyEventRows.HitText(hit), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground));
            if (hit.Samples.Count > 0)
            {
                var samples = KitStyle.Text(PrivacyEventRows.Samples(hit, 2), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
                samples.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
                samples.FontSize = 11;
                inline.Children.Add(samples);
            }
            panel.Children.Add(inline);
        }
        if (more > 0) panel.Children.Add(KitStyle.Text(Loc.T("privacy.logs.moreHits", ("count", more)), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted));
        return panel;
    }
}
