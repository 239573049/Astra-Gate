using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Tokens;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Pages.Tokens;

/// <summary>
/// A token's detail body (web <c>TokenDetailPage</c> markup): a range selector, five number cards, the pool's live health, then the
/// empty state or the request trend with the provider and model breakdowns, and the latest requests. Each block watches its own
/// query; a range change hands the blocks the queries of the new range.
/// </summary>
public sealed class TokenDetailView : UserControl
{
    private readonly TokenDetailPage _page;
    private readonly QueryHost<TokenDto> _gate;
    private readonly StackPanel _body = new() { Spacing = 16, MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Center };
    private QueryHost<StatsSummaryDto>? _cards;
    private QueryHost<StatsSummaryDto>? _lower;
    private QueryHost<TokenDto>? _pool;
    private Control? _bodyControl;
    private bool _wired;

    /// <summary>Builds the body for <paramref name="page"/>.</summary>
    public TokenDetailView(TokenDetailPage page)
    {
        _page = page;
        // The token's own query decides between the error page and the body (web: token.isError).
        _gate = new QueryHost<TokenDto>(page.Token, q => q.IsError ? new Alert { Tone = AlertTone.Danger, Title = Loc.T("common.error"), Content = q.Error?.Message ?? "" } : _bodyControl ??= Body());
        Content = _gate;
    }

    private Translate T => (key, args) => Loc.T(key, args);

    private string RangeLabel => Loc.T(StatsRange.LabelKey(_page.Range));

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_wired) return;
        _wired = true;
        _page.RangeChanged += Refilter;
        _page.Providers.PropertyChanged += OnProviders;
        Loc.LanguageChanged += OnLanguage;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!_wired) return;
        _wired = false;
        _page.RangeChanged -= Refilter;
        _page.Providers.PropertyChanged -= OnProviders;
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnProviders(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "Data") return;
        _pool?.Rebuild();
        _lower?.Rebuild();
    }

    private void OnLanguage() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        _bodyControl = null;
        _gate.Rebuild();
    });

    private void Refilter()
    {
        if (_cards is not null) _cards.Query = _page.Summary();
        if (_lower is not null)
        {
            _lower.Query = _page.Summary();
            _lower.Rebuild();
        }
    }

    private Control Body()
    {
        _body.Children.Clear();
        var range = Pick.Segmented(
            StatsRange.Choices.Select(r => new SegmentedOption(r, Loc.T(StatsRange.LabelKey(r)))), _page.Range, _page.SetRange, Loc.T("overview.range"));
        range.HorizontalAlignment = HorizontalAlignment.Right;
        _body.Children.Add(range);

        _cards = new QueryHost<StatsSummaryDto>(_page.Summary(), q =>
            !q.HasData ? (q.IsError ? new CardPanel(new ErrorState { Message = q.Error?.Message }) : new LoadingState(3))
                : new StatGrid(OverviewShaping.TokenCards(q.Data!, RangeLabel, T), 5));
        _body.Children.Add(_cards);

        _pool = new QueryHost<TokenDto>(_page.Token, q => q.Data?.Pool is { } pool ? PoolSection(pool) : null);
        _body.Children.Add(_pool);

        _lower = new QueryHost<StatsSummaryDto>(_page.Summary(), q =>
        {
            var empty = q.Data is { Requests: 0 };
            var stack = new StackPanel { Spacing = 16 };
            if (empty)
                stack.Children.Add(new CardPanel(new EmptyStateView(Loc.T("overview.tokenEmpty.title", ("token", _page.Token.Data?.Name ?? "")), Loc.T("overview.tokenEmpty.detail", ("range", RangeLabel)))));
            else
            {
                stack.Children.Add(TrendCard());
                stack.Children.Add(ProvidersCard());
                stack.Children.Add(ModelsCard());
            }
            stack.Children.Add(RecentCard());
            return stack;
        });
        _body.Children.Add(_lower);
        return _body;
    }

    private static TextBlock Heading(string text)
    {
        var h = KitStyle.Text(text, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        h.Margin = new Thickness(0, 0, 0, 12);
        return h;
    }

    /// <summary>The pool and each member's name with its live health (web "pool" section).</summary>
    private Control PoolSection(TokenPoolDto pool)
    {
        var zone = TimeSpan.FromMinutes(_page.Now.OffsetMinutes);
        var members = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 20, LineSpacing = 8 };
        foreach (var m in TokenPresentation.PoolMembers(pool, _page.Providers.Data ?? [], _page.Clock, zone, Loc.Language, T))
        {
            var name = KitStyle.Text(m.Name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground);
            var health = KitStyle.Text(m.HealthText, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
            health.FontSize = KitStyle.Type12;
            members.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new StatusDot(TokenPoolEditor.ToTone(m.Tone)), name, health } });
        }
        var title = Loc.T("tokens.pool.title") + " · " + Loc.T(PoolStrategies.NameKey(pool.Strategy));
        return CardPanel.Section(new StackPanel { Children = { Heading(title), members } });
    }

    private Control TrendCard()
    {
        var host = new QueryHost<List<TimeseriesPointDto>>(_page.Series(), q =>
        {
            if (!q.HasData) return q.IsError ? new ErrorState { Message = q.Error?.Message } : new LoadingState(4);
            var today = _page.Range == StatsRange.Today;
            var bars = OverviewShaping.RequestsTrend(q.Data!, _page.Range, _page.Now);
            return ChartHeadline.Fit(new BarChart
            {
                Label = Loc.T("overview.requests"), Period = RangeLabel, PlotHeight = 160,
                AverageLabel = Loc.T(today ? "overview.hourlyAverage" : "overview.dailyAverage"), ValueLabel = Loc.T("overview.total"),
                CategoryLabel = Loc.T(today ? "overview.hour" : "overview.day"), EmptyLabel = Loc.T("common.noData"),
                ValueFormatter = c => StatsFormat.Tokens((decimal)c.Value), TickFormatter = v => StatsFormat.Tokens((decimal)v),
                ItemsSource = bars.Select(b => new ChartPoint(b.Key, b.Value) { Key = b.Key, Label = b.Label, AxisLabel = b.AxisLabel }).ToList(),
            });
        });
        return CardPanel.Section(new StackPanel { Children = { Heading(Loc.T("overview.usageTrend") + " · " + RangeLabel), host } });
    }

    private Control ProvidersCard()
    {
        var host = new QueryHost<List<ProviderStatDto>>(_page.ByProvider(), q =>
        {
            if (!q.HasData) return q.IsError ? new ErrorState { Message = q.Error?.Message } : new LoadingState(3);
            var rows = TokenPresentation.ProviderRows(q.Data!, _page.Providers.Data ?? []);
            return Tables.Create(Loc.T("tokens.detail.byProvider"), rows, r => r.Id, Loc.T("common.noData"),
            [
                Tables.Column<ProviderRow>(Loc.T("tokens.detail.provider"), "name", r => r.Name),
                Tables.Column<ProviderRow>(Loc.T("overview.requests"), "requests", r => r.Requests, v => StatsFormat.Tokens((long)v!), numeric: true),
                Tables.Column<ProviderRow>(Loc.T("tokens.detail.share"), "share", r => r.Share, v => StatsFormat.Percent((double)v!), numeric: true),
                // A provider with no requests has no success rate to show.
                Tables.Cell<ProviderRow>(Loc.T("overview.successRate"), "success", r => r.Success,
                    r => new TextBlock { Text = r.Requests > 0 ? StatsFormat.Percent(r.Success) : StatsFormat.Dash, TextAlignment = TextAlignment.Right, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center },
                    numeric: true),
                Tables.Column<ProviderRow>(Loc.T("overview.avgTtft"), "ttft", r => r.Ttft, v => StatsFormat.Ms((double?)v), numeric: true),
                Tables.Column<ProviderRow>(Loc.T("overview.cost"), "cost", r => r.Cost, v => StatsFormat.Usd((decimal)v!), numeric: true),
                Tables.Column<ProviderRow>(Loc.T("tokens.detail.failoversOut"), "out", r => r.Out, null, numeric: true),
                Tables.Column<ProviderRow>(Loc.T("tokens.detail.failoversIn"), "in", r => r.In, null, numeric: true),
            ]);
        });
        var hint = KitStyle.Text(Loc.T("tokens.detail.byProviderHint"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        hint.FontSize = KitStyle.Type12;
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextTrimming = TextTrimming.None;
        hint.Margin = new Thickness(0, 0, 0, 12);
        var heading = Heading(Loc.T("tokens.detail.byProvider"));
        heading.Margin = new Thickness(0, 0, 0, 4);
        return CardPanel.Section(new StackPanel { Children = { heading, hint, host } });
    }

    private Control ModelsCard()
    {
        var host = new QueryHost<List<TopModelDto>>(_page.Models(), q =>
        {
            if (!q.HasData) return q.IsError ? new ErrorState { Message = q.Error?.Message } : new LoadingState(3);
            var rows = TokenPresentation.ModelRows(q.Data!);
            return Tables.Create(Loc.T("tokens.detail.byModel"), rows, r => r.Model, Loc.T("common.noData"),
            [
                Tables.Column<ModelRow>(Loc.T("tokens.detail.model"), "model", r => r.Model),
                Tables.Column<ModelRow>(Loc.T("overview.requests"), "requests", r => r.Requests, v => StatsFormat.Tokens((long)v!), numeric: true),
                Tables.Column<ModelRow>(Loc.T("overview.tokens"), "tokens", r => r.Tokens, v => StatsFormat.Tokens((long)v!, true), numeric: true),
                Tables.Column<ModelRow>(Loc.T("overview.cost"), "cost", r => r.Cost, v => StatsFormat.Usd((decimal)v!), numeric: true),
            ]);
        });
        return CardPanel.Section(new StackPanel { Children = { Heading(Loc.T("tokens.detail.byModel")), host } });
    }

    private Control RecentCard()
    {
        var host = new QueryHost<RequestPageDto>(_page.Recent(), q =>
        {
            if (!q.HasData) return q.IsError ? new ErrorState { Message = q.Error?.Message } : new LoadingState(3);
            var rows = TokenPresentation.LogRows(q.Data!.Items);
            var zone = TimeSpan.FromMinutes(_page.Now.OffsetMinutes);
            return Tables.Create(Loc.T("tokens.detail.recent"), rows, r => r.Id, Loc.T("common.noData"),
            [
                Tables.Column<TokenLogRow>(Loc.T("tokens.detail.time"), "time", r => r.Time, v => StatsFormat.Time((DateTimeOffset)v!, _page.Clock, zone, Loc.Language), sortable: false),
                Tables.Cell<TokenLogRow>(Loc.T("tokens.detail.model"), "model", r => r.Model, r => OpenLink(r), sortable: false),
                Tables.Column<TokenLogRow>(Loc.T("tokens.detail.provider"), "provider", r => r.Provider, sortable: false),
                Tables.Cell<TokenLogRow>(Loc.T("tokens.detail.status"), "status", r => r.Status,
                    r => KitBadge.Create(Loc.T("status." + r.Status), r.Status == "success" ? Tone.Green : Tone.Red), sortable: false),
                Tables.Column<TokenLogRow>(Loc.T("overview.cost"), "cost", r => r.Cost, v => StatsFormat.Usd((decimal)v!), numeric: true, sortable: false),
            ]);
        });
        var logs = new ArcButton { Variant = ButtonVariant.Ghost, Size = ControlSize.Small, Content = Loc.T("tokens.detail.viewLogs") };
        // web: a plain accent-coloured link
        logs.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.Accent);
        logs.Click += (_, _) => _page.OpenLogs();
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        var title = KitStyle.Text(Loc.T("tokens.detail.recent"), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        Grid.SetColumn(logs, 1);
        head.Children.Add(title);
        head.Children.Add(logs);
        return CardPanel.Section(new StackPanel { Children = { head, host } });
    }

    /// <summary>A model name that opens the request in the log.</summary>
    private Control OpenLink(TokenLogRow r)
    {
        var text = new TextBlock { Text = r.Model, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        text.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(text).Properties.IsLeftButtonPressed) _page.OpenRequest(r.Id);
        };
        return text;
    }
}
