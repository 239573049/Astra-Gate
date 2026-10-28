using System.Globalization;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Overview;
using Astra.Desktop.Components.Requests;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcTabItem = TKCode.Arc.TabItem;

namespace Astra.Desktop.Pages.Overview;

/// <summary>
/// The overview's body (web <c>OverviewPage</c> markup): a toolbar (client filter, token filter, range), the activity card, the number
/// cards, then either the empty state or the trend card and the breakdown tabs. Every section is a <see cref="QueryHost{T}"/> on the
/// query of the current filters, so a late answer rebuilds only its own section; a filter change hands each host its new query.
/// </summary>
public sealed class OverviewView : UserControl
{
    private readonly OverviewPage _page;
    private readonly StackPanel _root = new() { Spacing = 0 };
    private readonly Border _clientSlot = new();
    private readonly Border _tokenSlot = new();
    private readonly QueryHost<List<DailyActivityDto>> _activity;
    private readonly QueryHost<StatsSummaryDto> _cards;
    private readonly QueryHost<StatsSummaryDto> _lower;
    private QueryHost<List<TimeseriesPointDto>>? _trendChart;
    private TextBlock? _trendLegend;
    private RequestDetailSheet? _sheet;
    private bool _wired;
    private bool _compact;

    /// <summary>Builds the body for <paramref name="page"/>.</summary>
    public OverviewView(OverviewPage page)
    {
        _page = page;
        _activity = new QueryHost<List<DailyActivityDto>>(page.Activity(), RenderActivity);
        _cards = new QueryHost<StatsSummaryDto>(page.Summary(), RenderCards);
        _lower = new QueryHost<StatsSummaryDto>(page.Summary(), RenderLower);

        _root.Children.Add(Toolbar());
        _root.Children.Add(new Border { Margin = new Thickness(0, 0, 0, 12), Child = _activity });
        _root.Children.Add(_cards);
        _root.Children.Add(new Border { Margin = new Thickness(0, 16, 0, 0), Child = _lower });
        Content = _root;
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_wired) return;
        _wired = true;
        _page.FiltersChanged += Refilter;
        _page.Tokens.PropertyChanged += OnTokens;
        _page.Clients.PropertyChanged += OnClients;
        _page.MeasureChanged += OnMeasure;
        _page.DetailChanged += OnDetail;
        Loc.LanguageChanged += OnLanguage;
        OnDetail();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_wired) return;
        _wired = false;
        _page.FiltersChanged -= Refilter;
        _page.Tokens.PropertyChanged -= OnTokens;
        _page.Clients.PropertyChanged -= OnClients;
        _page.MeasureChanged -= OnMeasure;
        _page.DetailChanged -= OnDetail;
        Loc.LanguageChanged -= OnLanguage;
    }

    /// <summary>The page opened or closed a request's detail: the sheet over the dashboard follows (web <c>RequestDetailSheet id={selected}</c>).</summary>
    private void OnDetail()
    {
        if (_sheet is not null)
        {
            var old = _sheet;
            _sheet = null;
            old.Detach();
            _root.Children.Remove(old);
        }
        if (_page.Detail is not { } detail) return;
        var sheet = new RequestDetailSheet(detail, _page.TableContext, _page.OpenTrace);
        sheet.Closed += (_, _) =>
        {
            if (ReferenceEquals(_sheet, sheet)) _page.CloseDetail();
        };
        _sheet = sheet;
        _root.Children.Add(sheet);
        sheet.IsOpen = true;
    }

    // The filters are built from the data alone; a fetch's other notifications (fetching, status…) would only rebuild them for nothing.
    private void OnTokens(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Data") BuildTokenFilter();
    }

    private void OnClients(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "Data") BuildClientFilter();
    }

    /// <summary>The measure changed: the chart and the legend next to the card title follow; nothing else is rebuilt.</summary>
    private void OnMeasure()
    {
        _trendChart?.Rebuild();
        if (_trendLegend is not null) _trendLegend.Text = Loc.T(OverviewShaping.MeasureKey(_page.Measure));
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        _root.Children[0] = Toolbar();
        _activity.Rebuild();
        _cards.Rebuild();
        _lower.Rebuild();
    });

    private Translate T => (key, args) => Loc.T(key, args);

    private string RangeLabel => Loc.T(StatsRange.LabelKey(_page.Range));

    /// <summary>The filters changed: every section watches the query of the new filters.</summary>
    private void Refilter()
    {
        _activity.Query = _page.Activity();
        _cards.Query = _page.Summary();
        _lower.Query = _page.Summary();
        _lower.Rebuild();
    }

    // ---------- toolbar ----------

    private Control Toolbar()
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        _clientSlot.Child = null;
        _tokenSlot.Child = null;
        left.Children.Add(_clientSlot);
        left.Children.Add(_tokenSlot);
        BuildClientFilter();
        BuildTokenFilter();

        var range = Pick.Segmented(
            StatsRange.Choices.Select(r => new SegmentedOption(r, Loc.T(StatsRange.LabelKey(r)))), _page.Range, _page.SetRange, Loc.T("overview.range"));
        range.HorizontalAlignment = HorizontalAlignment.Right;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        grid.Children.Add(left);
        Grid.SetColumn(range, 1);
        grid.Children.Add(range);
        // Too narrow for the logo strip beside the token select and the range switch: the clients become a dropdown.
        grid.SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Width < (ClientCatalog.Filterable(_page.Clients.Data, _page.ClientKind).Count() + 1) * 42 + 16 + 176 + 340 + 32;
            if (compact == _compact) return;
            _compact = compact;
            BuildClientFilter();
        };
        return grid;
    }

    /// <summary>"All" plus one official logo per installed client; the selected one stays even when it is not installed (web <c>ClientFilter</c>).</summary>
    private void BuildClientFilter()
    {
        if (_compact)
        {
            var choices = new List<(string, string)> { ("all", Loc.T("overview.allClients")) };
            choices.AddRange(ClientCatalog.Filterable(_page.Clients.Data, _page.ClientKind).Select(k => ((string)k, ClientCatalog.Name(k))));
            var dropdown = Pick.Select(choices, _page.ClientKind ?? "all", _page.SetClient, Loc.T("overview.clientFilter"));
            dropdown.Width = 160;
            _clientSlot.Child = dropdown;
            return;
        }
        var items = new List<SegmentedOption> { new("all", Loc.T("overview.allClients")) };
        foreach (var kind in ClientCatalog.Filterable(_page.Clients.Data, _page.ClientKind))
        {
            var glyph = BrandIcon.Client(kind, 18, glyph: true);
            ToolTip.SetTip(glyph, ClientCatalog.Name(kind));
            items.Add(new SegmentedOption(kind, ClientCatalog.Name(kind), glyph) { Label = "" });
        }
        _clientSlot.Child = Pick.Segmented(items, _page.ClientKind ?? "all", _page.SetClient, Loc.T("overview.clientFilter"));
    }

    private void BuildTokenFilter()
    {
        var options = new List<(string, string)> { ("", Loc.T("overview.allTokens")) };
        options.AddRange((_page.Tokens.Data ?? []).Select(t => (t.Id, t.Name)));
        var select = Pick.Select(options, _page.Token, _page.SetToken, Loc.T("overview.tokenFilter"));
        select.Width = 176;
        _tokenSlot.Child = select;
    }

    // ---------- activity ----------

    private Control? RenderActivity(Query<List<DailyActivityDto>> q)
    {
        var heading = KitStyle.Text(Loc.T("overview.activity"), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        heading.Margin = new Thickness(0, 0, 0, 12);
        Control body;
        if (!q.HasData) body = q.IsError ? new ErrorState { Message = q.Error?.Message } : new LoadingState(3);
        else body = BuildHeatmap(q.Data!);
        return CardPanel.Section(new StackPanel { Children = { heading, body } });
    }

    /// <summary>
    /// The last 365 local days as the web's heatmap (<see cref="ActivityHeatmapView"/>, a local port: Arc's control cannot take the web's
    /// labels, legend or bubble text). The line under the grid (window, active days, busiest day) is the web's own summary.
    /// </summary>
    private Control BuildHeatmap(List<DailyActivityDto> data)
    {
        var model = ActivityHeatmapModel.Build(data, OverviewPage.ActivityDays, _page.Now, Loc.Language);
        return new ActivityHeatmapView(model, Loc.T("overview.activity"), OverviewShaping.ActivitySummary(data, OverviewPage.ActivityDays, _page.Now, T),
            Loc.T("overview.activityLegend.less"), Loc.T("overview.activityLegend.more"), Loc.T("overview.activityUnit"), Loc.T("overview.activityNone"));
    }

    // ---------- number cards ----------

    private Control? RenderCards(Query<StatsSummaryDto> q)
    {
        if (!q.HasData) return q.IsError ? new CardPanel(new ErrorState { Message = q.Error?.Message }) : new LoadingState(4);
        return new StatGrid(OverviewShaping.Cards(q.Data!, RangeLabel, T), 6);
    }

    // ---------- empty / trend + breakdown ----------

    private Control? RenderLower(Query<StatsSummaryDto> q)
    {
        var empty = OverviewShaping.EmptyOf(q.Data, _page.ClientKind, _page.TokenId);
        if (empty != OverviewEmpty.None) return new CardPanel(EmptyState(empty));
        var stack = new StackPanel { Spacing = 16 };
        stack.Children.Add(TrendCard());
        stack.Children.Add(Breakdown());
        return stack;
    }

    private Control EmptyState(OverviewEmpty kind)
    {
        switch (kind)
        {
            case OverviewEmpty.Client:
            {
                var client = _page.ClientKind!;
                return new EmptyStateView(
                    Loc.T("overview.clientEmpty.title", ("client", ClientCatalog.Name(client))),
                    Loc.T("overview.clientEmpty.detail", ("range", RangeLabel)),
                    BrandIcon.Client(client, 32));
            }
            case OverviewEmpty.Token:
                return new EmptyStateView(
                    Loc.T("overview.tokenEmpty.title", ("token", _page.TokenName)),
                    Loc.T("overview.tokenEmpty.detail", ("range", RangeLabel)),
                    new LucideIcon { Kind = LucideIconKind.Activity, Size = 24 });
            default:
            {
                // The gateway address the user points their tools at; shown once the settings are known.
                var host = new QueryHost<SettingsDto>(_page.Settings, s => s.Data is { } d
                    ? new CodeBlock { Code = TokenAddress(d.GatewayBaseUrl), Language = "text", FileName = Loc.T("settings.gatewayUrl") }
                    : null);
                return new EmptyStateView(Loc.T("overview.empty.title"), Loc.T("overview.empty.detail"),
                    new LucideIcon { Kind = LucideIconKind.Activity, Size = 24 }, host);
            }
        }
    }

    private static string TokenAddress(string baseUrl) => baseUrl + "/v1";

    private Control TrendCard()
    {
        var measure = _page.Measure;
        var title = KitStyle.Text(Loc.T("overview.usageTrend") + " · " + RangeLabel, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        var legendDot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
        legendDot.Token(Border.BackgroundProperty, ArcDesignTokenKind.Accent);
        var legendText = KitStyle.Text(Loc.T(OverviewShaping.MeasureKey(measure)), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        legendText.FontSize = KitStyle.Type12;
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { title, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { legendDot, legendText } } } };

        var segmented = Pick.Segmented(
            new[] { TrendMeasure.Requests, TrendMeasure.Tokens, TrendMeasure.CostUsd }.Select(m => new SegmentedOption(m.ToString(), Loc.T(OverviewShaping.MeasureKey(m)))),
            measure.ToString(), v => _page.SetMeasure(Enum.Parse<TrendMeasure>(v)), Loc.T("overview.measure"));
        segmented.HorizontalAlignment = HorizontalAlignment.Right;

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(left);
        Grid.SetColumn(segmented, 1);
        head.Children.Add(segmented);

        var chart = new QueryHost<List<TimeseriesPointDto>>(_page.ByProvider(), q =>
            !q.HasData ? (q.IsError ? new ErrorState { Message = q.Error?.Message } : new LoadingState(4)) : Chart(q.Data!));
        _trendChart = chart;
        _trendLegend = legendText;
        return CardPanel.Section(new StackPanel { Children = { head, chart } });
    }

    private Control Chart(List<TimeseriesPointDto> series)
    {
        var measure = _page.Measure;
        var bars = OverviewShaping.Trend(series, measure, _page.Range, _page.Now);
        var today = _page.Range == StatsRange.Today;
        string Format(ChartValueContext c) => OverviewShaping.FormatMeasure(c.Value, measure);
        return ChartHeadline.Fit(new BarChart
        {
            Label = Loc.T(OverviewShaping.MeasureKey(measure)),
            Period = RangeLabel,
            PlotHeight = 180,
            AverageLabel = Loc.T(today ? "overview.hourlyAverage" : "overview.dailyAverage"),
            ValueLabel = Loc.T("overview.total"),
            CategoryLabel = Loc.T(today ? "overview.hour" : "overview.day"),
            EmptyLabel = Loc.T("common.noData"),
            ValueFormatter = Format,
            TickFormatter = v => OverviewShaping.FormatMeasure(v, measure),
            ItemsSource = bars.Select(b => new ChartPoint(b.Key, b.Value) { Key = b.Key, Label = b.Label, AxisLabel = b.AxisLabel }).ToList(),
        });
    }

    private Control Breakdown()
    {
        var tabs = new Tabs();
        void Add(string key, string headerKey, Func<Control> content) =>
            tabs.Items.Add(new ArcTabItem { Header = Loc.T(headerKey), Tag = key, Content = content() });
        Add("requests", "nav.requests", RequestsTab);
        Add("providers", "nav.providers", ProvidersTab);
        Add("models", "nav.models", ModelsTab);
        Add("tokens", "nav.tokens", TokensTab);
        tabs.SelectedIndex = Math.Max(0, new[] { "requests", "providers", "models", "tokens" }.ToList().IndexOf(_page.Section));
        tabs.SelectionChanged += (_, _) =>
        {
            if (tabs.SelectedItem is ArcTabItem { Tag: string key }) _page.SetSection(key);
        };

        // "View all" sits at the right edge of the tab row, only on the requests tab: a plain 12px secondary link that turns accent on
        // hover (web <Link className="inline-flex items-center gap-1 text-[12px] text-[var(--text-secondary)] hover:text-[var(--accent)]">).
        var viewAllText = KitStyle.Text(Loc.T("overview.viewAll"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        viewAllText.FontSize = KitStyle.Type12;
        var viewAllIcon = new LucideIcon { Kind = LucideIconKind.ArrowRight, Size = 14, VerticalAlignment = VerticalAlignment.Center };
        viewAllIcon.Token(LucideIcon.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
        var viewAll = new TKCode.Arc.Button
        {
            Variant = ButtonVariant.Ghost, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Padding = new Thickness(0), MinHeight = 0, Background = Brushes.Transparent, Margin = new Thickness(0, 8, 0, 0),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        };
        viewAll.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { viewAllText, viewAllIcon } };
        viewAll.PointerEntered += (_, _) => { viewAllText.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Accent); viewAllIcon.Token(LucideIcon.ForegroundProperty, ArcDesignTokenKind.Accent); };
        viewAll.PointerExited += (_, _) => { viewAllText.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary); viewAllIcon.Token(LucideIcon.ForegroundProperty, ArcDesignTokenKind.TextSecondary); };
        viewAll.Click += (_, _) => _page.OpenRequests();
        viewAll.IsVisible = _page.Section == "requests";
        tabs.SelectionChanged += (_, _) => viewAll.IsVisible = tabs.SelectedItem is ArcTabItem { Tag: "requests" };
        var host = new Grid { Children = { tabs, viewAll } };
        return host;
    }

    private Control RequestsTab() => new QueryHost<RequestPageDto>(_page.Requests(), q =>
    {
        if (!q.HasData) return q.IsError ? new ErrorState { Message = q.Error?.Message } : new LoadingState(4);
        return RequestsTable(OverviewShaping.LogRows(q.Data!.Items, T));
    });

    private Control ProvidersTab() => new QueryHost<List<TimeseriesPointDto>>(_page.ByProvider(), q =>
        !q.HasData ? (q.IsError ? new ErrorState { Message = q.Error?.Message } : new LoadingState(3)) : BreakdownTable(Loc.T("nav.providers"), Loc.T("requests.col.provider"), StatsMath.BreakdownRows(q.Data!)));

    private Control ModelsTab() => new QueryHost<List<TopModelDto>>(_page.TopModels(), q =>
        !q.HasData ? (q.IsError ? new ErrorState { Message = q.Error?.Message } : new LoadingState(3)) : ModelsTable(OverviewShaping.ModelRows(q.Data!)));

    private Control TokensTab() => new QueryHost<List<TimeseriesPointDto>>(_page.ByToken(), q =>
        !q.HasData ? (q.IsError ? new ErrorState { Message = q.Error?.Message } : new LoadingState(3)) : BreakdownTable(Loc.T("nav.tokens"), Loc.T("overview.by.token"), StatsMath.BreakdownRows(q.Data!)));

    // ---------- tables ----------

    private Control RequestsTable(List<LogRow> rows)
    {
        var zone = TimeSpan.FromMinutes(_page.Now.OffsetMinutes);
        var now = _page.Context!.Clock.Now;
        var columns = new List<DataTableColumn>
        {
            Tables.Column<LogRow>(Loc.T("requests.col.time"), "time", r => r.Time, v => StatsFormat.Time((DateTimeOffset)v!, now, zone, Loc.Language), 92),
            Tables.Cell<LogRow>(Loc.T("requests.col.client"), "client", r => r.Client, r => ClientCell(r), 150),
            Tables.Column<LogRow>(Loc.T("requests.col.provider"), "provider", r => r.Provider, v => string.IsNullOrEmpty((string?)v) ? StatsFormat.Dash : (string)v!, 140),
            Tables.Cell<LogRow>(Loc.T("requests.col.model"), "model", r => r.Model, r => ModelCell(r)),
            // Token counts are meaningless when the upstream reported no usage: those cells show a dash.
            UsageColumn(Loc.T("overview.col.input"), "input", r => r.Input, false, 84),
            UsageColumn(Loc.T("overview.col.output"), "output", r => r.Output, false, 84),
            UsageColumn(Loc.T("overview.col.cacheHit"), "cache", r => r.Cache, true, 96),
            // a request the upstream reported no usage for is billed on an estimate: its cost reads in the warning colour (web text-[var(--warning)])
            Tables.Cell<LogRow>(Loc.T("requests.col.cost"), "cost", r => r.CostNanos, r =>
            {
                var block = new TextBlock { Text = StatsFormat.Nanos(r.CostNanos), TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                if (r.Request.UsageSource == "missing") block.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Warning);
                return block;
            }, 104, numeric: true),
            // web: width 100, secondary text that wraps "629.1 tok/s" onto two lines in the narrow column
            Tables.Cell<LogRow>(Loc.T("overview.col.speed"), "speed", r => r.Speed, r =>
            {
                var block = new TextBlock { Text = StatsFormat.Tps(r.Speed), TextAlignment = TextAlignment.Right, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                block.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextSecondary);
                return block;
            }, 100, numeric: true),
        };
        return Tables.Create(Loc.T("nav.requests"), rows, r => r.Id, Loc.T("common.noData"), columns);
    }

    private static DataTableColumn UsageColumn(string header, string key, Func<LogRow, long> value, bool cache, double width)
        => Tables.Cell<LogRow>(header, key, r => value(r), r =>
        {
            var text = OverviewShaping.UsageCell(r.Request, cache && value(r) == 0 ? StatsFormat.Dash : StatsFormat.Tokens(value(r), true));
            var block = new TextBlock { Text = text, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
            if (r.Request.UsageSource == "missing") block.Opacity = 0.5;
            return block;
        }, width, numeric: true);

    private static Control ClientCell(LogRow r)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (r.Request.ClientKind is { } kind && ClientCatalog.Known(kind)) row.Children.Add(BrandIcon.Client(kind, 16, glyph: true));
        row.Children.Add(new TextBlock { Text = r.Client, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private Control ModelCell(LogRow r)
    {
        var model = new TextBlock
        {
            Text = r.Model.Length == 0 ? StatsFormat.Dash : r.Model, TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = KitStyle.Type12, VerticalAlignment = VerticalAlignment.Center,
        };
        model.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        ToolTip.SetTip(model, r.Model);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        row.Children.Add(model);
        if (OverviewShaping.FailureTone(r.Request.Status) is { } tone)
            row.Children.Add(KitBadge.Create(Loc.T("status." + r.Request.Status), tone == "orange" ? Tone.Orange : Tone.Red));
        Avalonia.Automation.AutomationProperties.SetName(row, Loc.T("requests.openDetail", ("model", r.Model.Length == 0 ? r.Id : r.Model)));
        row.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) _page.OpenRequest(r.Id);
        };
        return row;
    }

    private Control BreakdownTable(string caption, string nameLabel, List<BreakdownRow> rows)
        => Tables.Create(caption, rows, r => r.Key, Loc.T("common.noData"), BreakdownColumns<BreakdownRow>(nameLabel, r => r.Key, r => r.Requests, r => r.Tokens, r => r.CostUsd, r => r.Share), "costUsd", true);

    private Control ModelsTable(List<ModelBreakdownRow> rows)
    {
        var columns = BreakdownColumns<ModelBreakdownRow>(Loc.T("requests.col.model"), r => r.Key, r => r.Requests, r => r.Tokens, r => r.CostUsd, r => r.Share,
            Tables.Cell<ModelBreakdownRow>(Loc.T("usage.hitRate"), "cacheHit", r => r.CacheHit, r =>
            {
                var none = r.CacheHit < 0;
                var block = new TextBlock { Text = none ? StatsFormat.Dash : StatsFormat.Percent(r.CacheHit), TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                if (none) block.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextMuted);
                return block;
            }, 120, numeric: true));
        return Tables.Create(Loc.T("overview.topModels"), rows, r => r.Key, Loc.T("common.noData"), columns, "costUsd", true);
    }

    private List<DataTableColumn> BreakdownColumns<T>(string nameLabel, Func<T, string> key, Func<T, long> requests, Func<T, long> tokens,
        Func<T, decimal> cost, Func<T, double> share, DataTableColumn? extra = null) where T : class
    {
        var list = new List<DataTableColumn>
        {
            Tables.Cell<T>(nameLabel, "key", r => key(r), r =>
            {
                var text = new TextBlock { Text = key(r), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = KitStyle.Type12, VerticalAlignment = VerticalAlignment.Center };
                text.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
                return text;
            }),
            Tables.Column<T>(Loc.T("overview.requests"), "requests", r => requests(r), v => StatsFormat.Tokens((long)v!), 110, numeric: true),
            Tables.Column<T>(Loc.T("overview.tokens"), "tokens", r => tokens(r), v => StatsFormat.Tokens((long)v!, true), 110, numeric: true),
        };
        if (extra is not null) list.Add(extra);
        list.Add(Tables.Column<T>(Loc.T("overview.cost"), "costUsd", r => cost(r), v => StatsFormat.Usd((decimal)v!), 110, numeric: true));
        list.Add(Tables.Cell<T>(Loc.T("overview.col.share"), "share", r => share(r), r => ShareBar(share(r)), 150, numeric: true));
        return list;
    }

    /// <summary>A share as a thin bar plus its percentage (web <c>ShareBar</c>).</summary>
    private static Control ShareBar(double ratio)
    {
        var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Min(100, ratio * 100) / 100 * 64, Height = 6 };
        fill.Token(Border.BackgroundProperty, ArcDesignTokenKind.Accent);
        var track = new Border { Width = 64, Height = 6, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true, Child = fill };
        CardPanel.Themed(track, CardPanel.SurfaceMutedBrush);
        var percent = new TextBlock { Text = StatsFormat.Percent(ratio), Width = 48, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { track, percent } };
    }
}
