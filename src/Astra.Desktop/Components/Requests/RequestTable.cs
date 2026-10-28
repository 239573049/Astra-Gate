using System.ComponentModel;
using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Traces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Requests;

/// <summary>What the table's cells need besides their row: the clock (today's rows show seconds, a waiting request counts up) and the provider list (icons).</summary>
public sealed class RequestTableContext
{
    /// <summary>The page's clock.</summary>
    public required IClock Clock { get; init; }

    /// <summary>A provider by id, for its icon (a request row names its provider but not its icon).</summary>
    public required Func<string?, ProviderDto?> Provider { get; init; }

    /// <summary>The zone times are shown in; null = the machine's (tests pin UTC, the web capture's zone).</summary>
    public TimeZoneInfo? TimeZone { get; set; }
}

/// <summary>
/// The request log's table (web <c>RequestTable</c> of RequestsPage.tsx): its eleven columns, widths and cells over <see cref="RequestGrid"/>
/// (ARC-GAP: Arc's table has fixed 52 px rows and no row activation). Cell text follows the web's <c>--leading-body</c> (1.4): 18.2 px
/// lines at 13 px, 15.4 px at 11 px, so the model cell's three lines make the same 52–54 px rows.
/// </summary>
public static class RequestTable
{
    private static TraceText T => TraceTexts.FromLoc;

    /// <summary>Web <c>--leading-body</c> line height of 13 px text.</summary>
    internal const double Line13 = 13 * 1.4;

    /// <summary>Web <c>--leading-body</c> line height of 11 px text.</summary>
    internal const double Line11 = 11 * 1.4;

    /// <summary>The columns of the request list.</summary>
    public static IReadOnlyList<RequestGridColumn> Columns(RequestTableContext ctx)
    {
        var locale = Loc.Language;
        return
        [
            // The web's time sort value is a number (Date.parse), which makes the column numeric: right-aligned.
            new("time", Loc.T("requests.col.time"), 92, true, r => r.Time, r => Right(Plain(RequestFormat.Time(r.Summary.StartedAtUtc, locale, ctx.Clock.Now, ctx.TimeZone), true))),
            new("model", Loc.T("requests.col.model"), 320, false, r => r.Model, ModelCell),
            new("client", Loc.T("requests.col.client"), 72, false, r => r.ClientName, ClientCell),
            new("token", Loc.T("requests.col.token"), 110, false, r => r.Token, TokenCell),
            new("provider", Loc.T("requests.col.provider"), 130, false, r => r.Provider, r => ProviderCell(r, ctx)),
            new("status", Loc.T("requests.col.status"), 104, false, r => r.Status, StatusCell),
            new("ttft", Loc.T("requests.col.ttft"), 88, true, r => r.Ttft, r => TtftCell(r, ctx)),
            new("tps", Loc.T("requests.col.tps"), 92, true, r => r.Tps, r => Right(Plain(RequestFormat.Tps(r.Summary.OutputTps), true, Loc.T("requests.detail.tps")))),
            new("tokens", Loc.T("requests.col.tokens"), 116, true, r => r.Tokens, TokensCell),
            new("cache", Loc.T("requests.col.cache"), 136, true, r => r.Cache, CacheCell),
            new("cost", Loc.T("requests.col.cost"), 92, true, r => r.Cost, CostCell),
        ];
    }

    /// <summary>Builds the table for <paramref name="rows"/>.</summary>
    public static RequestGrid Create(IReadOnlyList<RequestRowViewModel> rows, RequestTableContext ctx)
        => new(Columns(ctx), Loc.T("nav.requests"), Loc.T("requests.empty")) { ItemsSource = rows };

    private static TextBlock Plain(string text, bool num = false, string? title = null)
    {
        var block = KitStyle.Text(text, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground);
        block.LineHeight = Line13;
        if (num) block.Num();
        if (title is not null) Tooltip.SetTip(block, title);
        return block;
    }

    private static Control Right(Control c)
    {
        c.HorizontalAlignment = HorizontalAlignment.Right;
        return c;
    }

    /// <summary>A block line of <paramref name="height"/> (web <c>display: block</c> at the body line height), content centred.</summary>
    private static Control Line(Control content, double height)
    {
        content.VerticalAlignment = VerticalAlignment.Center;
        content.HorizontalAlignment = HorizontalAlignment.Left;
        return new Border { MinHeight = height, Child = content };
    }

    /// <summary>Icon then text that gives way (web <c>flex min-w-0 items-center gap-1.5</c> with a <c>truncate</c> label).</summary>
    private static Control IconText(Control? icon, TextBlock text, double gap = 6)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null)
        {
            icon.Margin = new Thickness(0, 0, gap, 0);
            grid.Children.Add(icon);
        }
        Grid.SetColumn(text, 1);
        text.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(text);
        return grid;
    }

    private static Control ModelCell(RequestRowViewModel row)
    {
        var r = row.Summary;
        // Web: the name group (glyph, name, mapped model) keeps its width and the reasoning / routing badges wrap below it (flex-wrap gap-x-1.5).
        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, MinHeight = Line13 };
        group.Children.Add(BrandIcon.Model(string.IsNullOrEmpty(r.UpstreamModel) ? r.RequestedModel : r.UpstreamModel, 15));
        var name = KitStyle.Text(string.IsNullOrEmpty(r.RequestedModel) ? "—" : r.RequestedModel, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        name.LineHeight = Line13;
        name.MaxWidth = 320 - 2 * RequestGrid.CellPadding - 21;
        Tooltip.SetTip(name, r.RequestedModel ?? "");
        // Web hover:text-[var(--accent)] on the name.
        name.PointerEntered += (_, _) => name.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Accent);
        name.PointerExited += (_, _) => name.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
        group.Children.Add(name);
        if (RequestBadges.MappedModel(r.RequestedModel, r.UpstreamModel) is { } mapped) group.Children.Add(mapped);
        var first = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, VerticalAlignment = VerticalAlignment.Center };
        first.Children.Add(group);
        foreach (var badge in new[] { RequestBadges.Reasoning(r.ReasoningEffort, r.ReasoningMode, r.ReasoningBudgetTokens), RequestBadges.Routing(r.Routing) })
        {
            if (badge is null) continue;
            badge.VerticalAlignment = VerticalAlignment.Center;
            first.Children.Add(badge);
        }
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(first);
        stack.Children.Add(Line(RequestBadges.ResponseModel(r.RequestedModel, r.UpstreamModel, r.ResponseModel, r.Status, labeled: true), Line11));
        stack.Children.Add(Line(RequestBadges.Protocol(r.InboundProtocol, r.UpstreamProtocol, r.Passthrough), Line11));
        return stack;
    }

    private static Control ClientCell(RequestRowViewModel row)
    {
        var r = row.Summary;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var name = row.ClientName;
        if (!string.IsNullOrEmpty(r.ClientKind))
        {
            var glyph = BrandIcon.Client(r.ClientKind, 20, glyph: true, fallbackName: name);
            var host = new Border { Child = glyph, Background = Brushes.Transparent, IsHitTestVisible = true };
            Tooltip.SetTip(host, name);
            panel.Children.Add(host);
        }
        else panel.Children.Add(KitStyle.Text(name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground));
        if (r.Remote)
        {
            var label = string.IsNullOrEmpty(r.ClientIp) ? Loc.T("requests.origin.remote") : Loc.T("requests.origin.remoteFrom", ("ip", r.ClientIp));
            var globe = RequestBadges.Glyph(LucideIconKind.Globe, 14, ArcDesignTokenKind.Accent);
            var host = new Border { Child = globe, Background = Brushes.Transparent };
            Tooltip.SetTip(host, label);
            panel.Children.Add(host);
        }
        return panel;
    }

    private static Control TokenCell(RequestRowViewModel row)
    {
        var r = row.Summary;
        var icon = string.IsNullOrEmpty(r.TokenName) ? null : RequestBadges.Glyph(LucideIconKind.KeyRound, 14, ArcDesignTokenKind.TextMuted);
        return IconText(icon, Plain(string.IsNullOrEmpty(r.TokenName) ? "—" : r.TokenName));
    }

    private static Control ProviderCell(RequestRowViewModel row, RequestTableContext ctx)
    {
        var r = row.Summary;
        Control? icon = null;
        if (!string.IsNullOrEmpty(r.ProviderName))
        {
            var p = ctx.Provider(r.ProviderId);
            icon = BrandIcon.Provider(r.ProviderName, p?.Icon, 18);
        }
        return IconText(icon, Plain(string.IsNullOrEmpty(r.ProviderName) ? "—" : r.ProviderName));
    }

    private static Control StatusCell(RequestRowViewModel row)
    {
        var r = row.Summary;
        var failed = RequestPresentation.IsFailed(r.Status);
        return RequestBadges.Status(r.Status, r.HttpStatus, failed ? Loc.T("requests.viewError") : null);
    }

    private static Control TtftCell(RequestRowViewModel row, RequestTableContext ctx)
    {
        var r = row.Summary;
        if (r.Status == "pending" && r.TtftMs is null) return Right(new ElapsedText(r.StartedAtUtc, ctx.Clock));
        return Right(Plain(TraceFormat.Ms(r.TtftMs), true, "TTFT"));
    }

    private static Control TwoLine(TwoLineCell cell, ArcDesignTokenKind firstColor, ArcDesignTokenKind secondColor, ArcDesignTokenKind firstSize)
    {
        if (cell.Empty)
        {
            var dash = Plain(TraceFormat.Dash);
            dash.HorizontalAlignment = HorizontalAlignment.Right;
            dash.Opacity = 0.5;
            Tooltip.SetTip(dash, cell.EmptyTitle ?? "");
            return Right(dash);
        }
        var stack = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var first = KitStyle.Text(cell.First, firstSize, firstColor).Num();
        first.LineHeight = firstSize == ArcDesignTokenKind.TypeSM ? Line13 : Line11;
        if (cell.FirstTitle is not null) Tooltip.SetTip(first, cell.FirstTitle);
        stack.Children.Add(first);
        if (cell.Second is not null)
        {
            var second = KitStyle.Text(cell.Second, ArcDesignTokenKind.TypeXS, secondColor).Num();
            second.LineHeight = Line11;
            if (cell.SecondTitle is not null) Tooltip.SetTip(second, cell.SecondTitle);
            stack.Children.Add(second);
        }
        return stack;
    }

    private static Control TokensCell(RequestRowViewModel row)
        => TwoLine(RequestPresentation.TokensCell(UsageSplit.Of(row.Summary), T), ArcDesignTokenKind.Foreground, ArcDesignTokenKind.TextSecondary, ArcDesignTokenKind.TypeSM);

    private static Control CacheCell(RequestRowViewModel row)
        => TwoLine(RequestPresentation.CacheCell(UsageSplit.Of(row.Summary), T), ArcDesignTokenKind.Success, ArcDesignTokenKind.TextSecondary, ArcDesignTokenKind.TypeXS);

    private static Control CostCell(RequestRowViewModel row)
    {
        var r = row.Summary;
        var text = KitStyle.Text(TraceFormat.Nanos(r.CostNanoUsd), ArcDesignTokenKind.TypeSM,
            r.UsageSource == "missing" ? ArcDesignTokenKind.Warning : ArcDesignTokenKind.Foreground).Num();
        return Right(text);
    }
}

/// <summary>
/// A table cell: builds its content from the row item it is given and again whenever that row's summary changes (a live update), so a progress
/// event redraws only the cells of its own row (web <c>LiveCell</c>). Its row is its data context (a <see cref="RequestGrid"/> row hands a fresher
/// instance of the same request down that way).
/// </summary>
public sealed class RequestCell : ContentControl
{
    private readonly Func<RequestRowViewModel, Control> _build;
    private RequestRowViewModel? _row;

    /// <summary>A cell drawn by <paramref name="build"/>.</summary>
    public RequestCell(Func<RequestRowViewModel, Control> build)
    {
        _build = build;
        Padding = new Thickness(0);
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Bind(DataContext as RequestRowViewModel);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Bind(DataContext as RequestRowViewModel);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_row is not null) _row.PropertyChanged -= OnRowChanged;
        _row = null;
    }

    private void Bind(RequestRowViewModel? row)
    {
        if (ReferenceEquals(row, _row) && Content is not null) return;
        if (_row is not null) _row.PropertyChanged -= OnRowChanged;
        _row = row;
        if (_row is not null) _row.PropertyChanged += OnRowChanged;
        Render();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => Render();

    private void Render() => Content = _row is null ? null : _build(_row);
}

/// <summary>Live "time since the request arrived" for a request still waiting on its first token (web <c>Elapsed</c>: a 200 ms clock).</summary>
public sealed class ElapsedText : TextBlock
{
    private readonly DateTimeOffset _since;
    private readonly IClock _clock;
    private DispatcherTimer? _timer;

    /// <summary>A counter from <paramref name="since"/>.</summary>
    public ElapsedText(DateTimeOffset since, IClock clock)
    {
        _since = since;
        _clock = clock;
        FontSize = 13;
        TextTrimming = TextTrimming.CharacterEllipsis;
        VerticalAlignment = VerticalAlignment.Center;
        this.Token(ForegroundProperty, ArcDesignTokenKind.Foreground);
        this.Token(FontSizeProperty, ArcDesignTokenKind.TypeSM);
        this.Num();
        Tooltip.SetTip(this, Loc.T("requests.elapsed"));
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer?.Stop();
        _timer = null;
    }

    private void Refresh() => Text = RequestPresentation.Elapsed(_since, _clock.Now);
}
