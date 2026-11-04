using Astra.Api.Contracts;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Pricing;
using Astra.Desktop.Components.Tokens;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Providers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Providers;

/// <summary>
/// Colors of the provider charts (web <c>SHARE_COLORS</c> in components/ProviderOverview.tsx: accent, success, warning, then three fixed
/// hues; "other" is the muted text color) and of a success rate (web <c>successColor</c>).
/// </summary>
internal static class ProviderPalette
{
    private static readonly Color[] Fixed = [Color.Parse("#af52de"), Color.Parse("#5ac8fa"), Color.Parse("#ff2d55")];

    /// <summary>The color of series slot <paramref name="index"/> (-1: "other") as the theme of <paramref name="host"/> paints it.</summary>
    public static Color Series(Control host, int index) => index switch
    {
        0 => ArcTokens.GetColor(host, ArcDesignTokenKind.Accent),
        1 => ArcTokens.GetColor(host, ArcDesignTokenKind.Success),
        2 => ArcTokens.GetColor(host, ArcDesignTokenKind.Warning),
        >= 3 and < 6 => Fixed[index - 3],
        _ => ArcTokens.GetColor(host, ArcDesignTokenKind.TextMuted),
    };

    /// <summary>Paints <paramref name="border"/>'s background with series slot <paramref name="index"/>, following theme changes.</summary>
    public static T Fill<T>(T border, int index) where T : Border
    {
        if (index is >= 3 and < 6) border.Background = new SolidColorBrush(Fixed[index - 3]);
        else border.Token(Border.BackgroundProperty, index switch
        {
            0 => ArcDesignTokenKind.Accent,
            1 => ArcDesignTokenKind.Success,
            2 => ArcDesignTokenKind.Warning,
            _ => ArcDesignTokenKind.TextMuted,
        });
        return border;
    }

    /// <summary>Green from 97 %, orange from the low-success line, red below it.</summary>
    public static ArcDesignTokenKind Success(double rate)
        => rate >= 0.97 ? ArcDesignTokenKind.Success : rate >= ProviderHealthRules.LowSuccessRate ? ArcDesignTokenKind.Warning : ArcDesignTokenKind.Danger;

    /// <summary>The dot tone of a health state (web <c>HEALTH_TONE</c>).</summary>
    public static Tone Tone(ProviderHealth h) => h switch
    {
        ProviderHealth.Healthy => Kit.Tone.Green,
        ProviderHealth.NoKey => Kit.Tone.Orange,
        ProviderHealth.LowSuccess => Kit.Tone.Red,
        _ => Kit.Tone.Neutral,
    };
}

/// <summary>A control drawn by hand that repaints when the theme changes.</summary>
internal abstract class ThemedDrawing : Control
{
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnTheme;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ActualThemeVariantChanged -= OnTheme;
    }

    private void OnTheme(object? sender, EventArgs e) => InvalidateVisual();
}

/// <summary>
/// Seven days of requests as small bars (web <c>MiniBars</c>): 6 px bars 2 px apart, 22 px tall, the height a square root of the maximum
/// every card and row shares (<see cref="MiniBarScale"/>); a day without requests is a 2 px stub in the border color.
/// </summary>
internal sealed class MiniBars : ThemedDrawing
{
    private const double BarWidth = 6, Gap = 2, Tall = 22;
    private readonly int[] _values;
    private readonly long _max;

    public MiniBars(int[] values, long max, IReadOnlyList<string> labels)
    {
        _values = values;
        _max = max;
        Width = values.Length * BarWidth + Math.Max(0, values.Length - 1) * Gap;
        Height = Tall;
        VerticalAlignment = VerticalAlignment.Bottom;
        ToolTip.SetTip(this, string.Join("\n", values.Select((v, i) => $"{(i < labels.Count ? labels[i] : "")}: {v}")));
    }

    public override void Render(DrawingContext context)
    {
        var on = new SolidColorBrush(ArcTokens.GetColor(this, ArcDesignTokenKind.Accent));
        var off = new SolidColorBrush(ArcTokens.GetColor(this, ArcDesignTokenKind.Border));
        for (var i = 0; i < _values.Length; i++)
        {
            var x = i * (BarWidth + Gap);
            var share = MiniBarScale.Share(_values[i], _max);
            if (share <= 0) context.FillRectangle(off, new Rect(x, Tall - 2, BarWidth, 2));
            else
            {
                var h = share * Tall;
                context.DrawRectangle(on, null, new RoundedRect(new Rect(x, Tall - h, BarWidth, h), 2, 2, 0, 0));
            }
        }
    }
}

/// <summary>The cost share ring (web <c>Donut</c>: 104 px, 17 px ring, a <c>conic-gradient</c> from 12 o'clock clockwise).</summary>
internal sealed class DonutRing : ThemedDrawing
{
    private const double Size = 104, Ring = 17;
    private readonly IReadOnlyList<ShareSlice> _slices;
    private readonly double _total;

    public DonutRing(IReadOnlyList<ShareSlice> slices, double total)
    {
        _slices = slices;
        _total = total;
        Width = Height = Size;
    }

    public override void Render(DrawingContext context)
    {
        var r = (Size - Ring) / 2;
        var center = new Point(Size / 2, Size / 2);
        double at = 0;
        foreach (var s in _slices)
        {
            var sweep = _total > 0 ? s.Value / _total * 360 : 0;
            if (sweep <= 0) continue;
            var pen = new Pen(new SolidColorBrush(ProviderPalette.Series(this, s.ColorIndex)), Ring);
            if (sweep >= 359.99) context.DrawEllipse(null, pen, center, r, r);
            else
            {
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(Polar(center, r, at), false);
                    g.ArcTo(Polar(center, r, at + sweep), new Size(r, r), 0, sweep > 180, SweepDirection.Clockwise);
                    g.EndFigure(false);
                }
                context.DrawGeometry(null, pen, geometry);
            }
            at += sweep;
        }
    }

    private static Point Polar(Point c, double r, double degrees)
    {
        var rad = (degrees - 90) * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }
}

/// <summary>
/// The summary above the provider list (web <c>ProviderOverview</c>): four figures over the page's range, the request share bar with its
/// legend, and — while the charts are open — the ranking, the cost share and the health tiles.
/// </summary>
internal static class ProviderOverviewView
{
    public static Control Build(ProviderOverviewData d, string range, bool chartsOpen)
    {
        var rangeLabel = Loc.T("range." + range);
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(0, 0, 0, 12) };

        var figures = new AutoFillGrid { MinColumnWidth = 160, Gap = 12 };
        figures.Children.Add(Figure(Loc.T("providers.overview.requests"), ProviderFormat.FormatTokens(d.Requests, true), rangeLabel));
        figures.Children.Add(Figure(Loc.T("providers.overview.cost"), ProviderFormat.FormatUsd(d.CostUsd), rangeLabel));
        figures.Children.Add(Figure(Loc.T("providers.overview.successRate"), d.SuccessRate is { } rate ? StatsFormat.Percent(rate) : "—",
            d.Failovers > 0 ? Loc.T("providers.overview.failoverCount", ("count", d.Failovers)) : rangeLabel,
            d.SuccessRate is { } r2 ? ProviderPalette.Success(r2) : null));
        figures.Children.Add(Figure(Loc.T("providers.overview.enabled"), $"{d.Enabled} / {d.Total}", Loc.T("providers.overview.withTraffic", ("count", d.WithTraffic))));
        var summary = new StackPanel { Children = { figures } };
        if (chartsOpen && d.RequestShare.Count > 0)
        {
            summary.Children.Add(ShareBar(d.RequestShare, d.Requests));
            summary.Children.Add(Legend(d.RequestShare, d.Requests, column: false));
        }
        stack.Children.Add(new CardPanel(summary, new Thickness(16, 12)));

        if (!chartsOpen) return stack;
        var charts = new AutoFillGrid { MinColumnWidth = 280, Gap = 12 };
        charts.Children.Add(Chart(Loc.T("providers.overview.ranking"), rangeLabel, Ranking(d.RequestShare)));
        charts.Children.Add(Chart(Loc.T("providers.overview.costShare"), ProviderFormat.FormatUsd(d.CostUsd), CostShare(d)));
        charts.Children.Add(Chart(Loc.T("providers.overview.health"), Loc.T("range.7d"), Health(d.Health)));
        stack.Children.Add(charts);
        return stack;
    }

    private static Control Figure(string label, string value, string? hint, ArcDesignTokenKind? color = null)
    {
        var v = KitStyle.Text(value, ArcDesignTokenKind.TypeXL, color ?? ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        v.FontSize = 19;
        v.TextTrimming = TextTrimming.CharacterEllipsis;
        var panel = new StackPanel { Children = { Small(label, ArcDesignTokenKind.TextMuted), v } };
        if (hint is not null) panel.Children.Add(Small(hint, ArcDesignTokenKind.TextMuted));
        return panel;
    }

    private static TextBlock Small(string text, ArcDesignTokenKind color)
    {
        var t = KitStyle.Text(text, ArcDesignTokenKind.TypeXS, color);
        t.FontSize = 11;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    private static Control ShareBar(IReadOnlyList<ShareSlice> slices, long total)
    {
        var grid = new Grid { Height = 10 };
        foreach (var s in slices)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(s.Value, 0.0001), GridUnitType.Star)));
            var part = ProviderPalette.Fill(new Border(), s.ColorIndex);
            ToolTip.SetTip(part, $"{s.Name}: {s.Value:0} ({StatsFormat.Percent(s.Value / total)})");
            Grid.SetColumn(part, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(part);
        }
        var track = new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true, Margin = new Thickness(0, 12, 0, 0), Child = grid };
        track.Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);
        return track;
    }

    private static Control Legend(IReadOnlyList<ShareSlice> slices, double total, bool column)
    {
        Panel panel = column ? new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center } : new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var s in slices)
        {
            var swatch = ProviderPalette.Fill(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center }, s.ColorIndex);
            var name = Small(s.Name, ArcDesignTokenKind.TextSecondary);
            name.MaxWidth = column ? 120 : 200;
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, Margin = column ? default : new Thickness(0, 2, 14, 2),
                Children = { swatch, name, Small(StatsFormat.Percent(total > 0 ? s.Value / total : 0), ArcDesignTokenKind.TextMuted) },
            };
            ToolTip.SetTip(item, s.Name);
            panel.Children.Add(item);
        }
        return panel;
    }

    private static Control Chart(string title, string hint, Control body)
    {
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 10) };
        var t = KitStyle.Text(title, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, FontWeight.Medium);
        t.FontSize = 12;
        head.Children.Add(t);
        var h = Small(hint, ArcDesignTokenKind.TextMuted);
        h.FontSize = 12;
        Grid.SetColumn(h, 1);
        head.Children.Add(h);
        return new CardPanel(new StackPanel { Children = { head, body } }, new Thickness(16, 12)) { MinHeight = 164 };
    }

    private static Control Empty(string text)
    {
        var t = Small(text, ArcDesignTokenKind.TextMuted);
        t.FontSize = 12;
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.VerticalAlignment = VerticalAlignment.Center;
        return new Panel { Height = 110, Children = { t } };
    }

    private static Control Ranking(IReadOnlyList<ShareSlice> share)
    {
        var top = share.Where(s => s.Key != ProviderOverviewData.OtherKey).ToList();
        if (top.Count == 0) return Empty(Loc.T("providers.overview.noData"));
        var rows = new StackPanel { Spacing = 6 };
        foreach (var s in top)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*,44"), ColumnSpacing = 8 };
            var name = Small(s.Name, ArcDesignTokenKind.Foreground);
            name.FontSize = 12;
            ToolTip.SetTip(name, s.Name);
            row.Children.Add(name);
            var fill = ProviderPalette.Fill(new Border { CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left }, s.ColorIndex);
            var bar = new Border { Height = 8, CornerRadius = new CornerRadius(4), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Child = fill };
            bar.Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);
            var ratio = s.Value / top[0].Value;
            bar.SizeChanged += (_, e) => fill.Width = e.NewSize.Width * ratio;
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);
            var count = Small(ProviderFormat.FormatTokens((long)s.Value, true), ArcDesignTokenKind.TextSecondary);
            count.FontSize = 12;
            count.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(count, 2);
            row.Children.Add(count);
            rows.Children.Add(row);
        }
        return rows;
    }

    private static Control CostShare(ProviderOverviewData d)
    {
        if (d.CostShare.Count == 0) return Empty(Loc.T("providers.overview.noCost"));
        var total = (double)d.CostUsd;
        var label = KitStyle.Text(ProviderFormat.FormatUsd(d.CostUsd), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
        label.FontSize = 12;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        var donut = new Panel { Width = 104, Height = 104, Children = { new DonutRing(d.CostShare, total), label } };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        row.Children.Add(donut);
        var legend = Legend(d.CostShare, total, column: true);
        Grid.SetColumn(legend, 1);
        row.Children.Add(legend);
        return row;
    }

    private static Control Health(IReadOnlyDictionary<ProviderHealth, int> counts)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8 };
        (ProviderHealth State, string Label)[] tiles =
        [
            (ProviderHealth.Healthy, Loc.T("providers.overview.healthy")),
            (ProviderHealth.LowSuccess, Loc.T("providers.overview.lowSuccess")),
            (ProviderHealth.NoKey, Loc.T("providers.noKey")),
            (ProviderHealth.Disabled, Loc.T("providers.card.disabled")),
        ];
        for (var i = 0; i < tiles.Length; i++)
        {
            var value = KitStyle.Text(counts.GetValueOrDefault(tiles[i].State).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ArcDesignTokenKind.TypeLG, ArcDesignTokenKind.Foreground, FontWeight.SemiBold);
            value.FontSize = 18;
            var label = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { new StatusDot(ProviderPalette.Tone(tiles[i].State)) { VerticalAlignment = VerticalAlignment.Center }, Small(tiles[i].Label, ArcDesignTokenKind.TextMuted) },
            };
            var tile = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8), Child = new StackPanel { Children = { value, label } } };
            CardPanel.Themed(tile, CardPanel.SurfaceMutedBrush);
            Grid.SetColumn(tile, i % 2);
            Grid.SetRow(tile, i / 2);
            grid.Children.Add(tile);
        }
        return grid;
    }
}

/// <summary>One row of the providers table (web <c>ProviderRow</c>): the sort values and what the cells draw.</summary>
internal sealed record ProviderTableRow(
    ProviderDto Provider, ProviderStatDto? Stat, ProviderHealth Health, int[] Days, long Requests, double SuccessRate, double Ttft, long Tokens, decimal Cost)
{
    public static ProviderTableRow From(ProviderDto p, ProviderStatDto? s, int[] days)
    {
        var requests = days.Sum(d => (long)d);
        return new ProviderTableRow(p, s, ProviderHealthRules.Of(p, s), days, requests,
            s is { Requests: > 0 } ? s.SuccessRate : -1, s?.AvgTtftMs ?? -1, s is null ? 0 : s.InputTokens + s.OutputTokens, s?.CostUsd ?? 0);
    }
}

/// <summary>
/// The table view (web <c>ProvidersTable</c>): one sortable row per provider over the cards' week, most requests first. The name cell opens
/// the provider (Arc's table has no row activation, as on the models page); the last cell is the enabled switch.
/// </summary>
internal static class ProvidersTableView
{
    public static Table Build(IReadOnlyList<ProviderTableRow> rows, long max, IReadOnlyList<string> labels, ProviderCardServices services)
    {
        DataTableColumn[] columns =
        [
            Tables.Cell<ProviderTableRow>(Loc.T("providers.col.provider"), "name", r => r.Provider.Name, r => NameCell(r, services)),
            Tables.Cell<ProviderTableRow>(Loc.T("providers.col.status"), "health", r => ProviderHealthRules.SortOrder(r.Health), r => StatusCell(r.Health), width: 112),
            Tables.Column<ProviderTableRow>(Loc.T("providers.col.models"), "models", r => r.Provider.ModelCount, width: 56, numeric: true),
            Tables.Cell<ProviderTableRow>(Loc.T("providers.col.trend"), "days", r => r.Requests, r => new MiniBars(r.Days, max, labels) { VerticalAlignment = VerticalAlignment.Center }, width: 84, sortable: false),
            Tables.Column<ProviderTableRow>(Loc.T("providers.col.requests"), "requests", r => r.Requests,
                v => v is long n && n > 0 ? ProviderFormat.FormatTokens(n, true) : "—", width: 80, numeric: true),
            Tables.Cell<ProviderTableRow>(Loc.T("providers.col.successRate"), "successRate", r => r.SuccessRate, SuccessCell, width: 124),
            Tables.Column<ProviderTableRow>(Loc.T("providers.col.ttft"), "ttft", r => r.Ttft, v => v is double d && d >= 0 ? StatsFormat.Ms(d) : "—", width: 92, numeric: true),
            Tables.Cell<ProviderTableRow>(Loc.T("providers.col.tokens"), "tokens", r => r.Tokens, TokensCell, width: 128, numeric: true),
            Tables.Column<ProviderTableRow>(Loc.T("providers.col.cost"), "cost", r => r.Cost, v => v is decimal c && c > 0 ? ProviderFormat.FormatUsd(c) : "—", width: 96, numeric: true),
            Tables.Cell<ProviderTableRow>(Loc.T("providers.col.enabled"), "enabled", r => r.Provider.Enabled ? 1 : 0, r => new ProviderEnabledSwitch(r.Provider, services), width: 64),
        ];
        return Tables.Create(Loc.T("nav.providers"), rows, r => r.Provider.Id, Loc.T("providers.empty"), columns, "requests", descending: true).WithoutCompactLayout();
    }

    private static Control NameCell(ProviderTableRow r, ProviderCardServices services)
    {
        var p = r.Provider;
        var icon = BrandIcon.Provider(p.Name, p.Icon, 22, p.TemplateId);
        if (!p.Enabled) icon.Opacity = 0.5;
        var name = KitStyle.Text(p.Name, ArcDesignTokenKind.TypeSM, p.Enabled ? ArcDesignTokenKind.Foreground : ArcDesignTokenKind.TextMuted, FontWeight.Medium);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        var host = KitStyle.Text(ProviderFormat.HostOf(p.Endpoints.FirstOrDefault()?.BaseUrl) ?? "—", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        host.FontSize = 11;
        host.TextTrimming = TextTrimming.CharacterEllipsis;
        host.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Background = Brushes.Transparent, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, host } };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        grid.PointerEntered += (_, _) => name.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Accent);
        grid.PointerExited += (_, _) => name.Token(TextBlock.ForegroundProperty, p.Enabled ? ArcDesignTokenKind.Foreground : ArcDesignTokenKind.TextMuted);
        grid.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) services.Open(p);
        };
        return grid;
    }

    private static Control StatusCell(ProviderHealth h)
    {
        var label = KitStyle.Text(Loc.T(ProviderHealthRules.LabelKey(h)), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        label.FontSize = 12;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
            Children = { new StatusDot(ProviderPalette.Tone(h)) { VerticalAlignment = VerticalAlignment.Center }, label },
        };
    }

    private static Control SuccessCell(ProviderTableRow r)
    {
        if (r.SuccessRate < 0) return KitStyle.Text("—", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextMuted).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        var fill = new Border { Width = 40 * r.SuccessRate, HorizontalAlignment = HorizontalAlignment.Left };
        fill.Token(Border.BackgroundProperty, ProviderPalette.Success(r.SuccessRate));
        var track = new Border { Width = 40, Height = 5, CornerRadius = new CornerRadius(3), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Child = fill };
        track.Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);
        var text = KitStyle.Text(StatsFormat.Percent(r.SuccessRate), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground);
        text.FontSize = 12;
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { track, text } };
    }

    private static Control TokensCell(ProviderTableRow r)
    {
        var text = r.Stat is { } s && r.Tokens > 0
            ? $"{ProviderFormat.FormatTokens(s.InputTokens, true)} / {ProviderFormat.FormatTokens(s.OutputTokens, true)}"
            : "—";
        var t = KitStyle.Text(text, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground);
        t.HorizontalAlignment = HorizontalAlignment.Right;
        t.VerticalAlignment = VerticalAlignment.Center;
        return t;
    }
}
