using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Traces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Traces;

// ARC-GAP-12: Arc has no gantt / span-waterfall control (AgentTimeline is a chat-style agent log, BrushChart a series chart). The session's
// timeline is rows on one time axis with nested labels, phase segments and a tick header, so the rows and their bars are drawn here.

/// <summary>
/// The turn on one time axis (session.tsx <c>Timeline</c>): a header with time ticks, then a row per call (split into the model's phases),
/// retried attempt, tool gap and call in flight; click a row to select it. The selected row's inspector sits beside the card, wrapping
/// under it when there is no room (web <c>flex-[999_1_640px]</c> / <c>flex-[1_1_300px]</c>).
/// </summary>
public sealed class TraceTimelineView : UserControl
{
    private readonly Dictionary<string, Control> _rows = new(StringComparer.Ordinal);
    private readonly ScrollViewer _scroll;
    private readonly List<TimelineBar> _live = [];
    private IDisposable? _pulse;

    /// <summary>Builds the timeline.</summary>
    /// <param name="timeline">The rows (built with the live calls at the current second).</param>
    /// <param name="selected">The selected row's <c>selectId</c>.</param>
    /// <param name="onSelect">A row was clicked.</param>
    /// <param name="inspector">The selected row's inspector, or null.</param>
    /// <param name="palette">Colours.</param>
    public TraceTimelineView(Timeline timeline, string? selected, Action<string> onSelect, Control? inspector, TracePalette palette)
    {
        var rows = new StackPanel { Spacing = 2, MinWidth = 720 };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*,64"), ColumnSpacing = 12, Height = 24, Margin = new Thickness(8, 0) };
        header.Children.Add(Bottom(TraceUi.Text(Loc.T("traces.timeline.node"), 11, ArcDesignTokenKind.TextMuted)));
        var ticks = new TickPanel(timeline.TotalMs) { Height = 16, VerticalAlignment = VerticalAlignment.Bottom };
        foreach (var ms in TimelineBuilder.Ticks(timeline.TotalMs))
        {
            var tick = TraceUi.Text(TraceFormat.Ms(ms), 11, ArcDesignTokenKind.TextMuted);
            tick.Tag = ms;
            ticks.Children.Add(tick);
        }
        Grid.SetColumn(ticks, 1);
        header.Children.Add(ticks);
        var dur = Bottom(TraceUi.Text(Loc.T("traces.timeline.duration"), 11, ArcDesignTokenKind.TextMuted));
        dur.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(dur, 2);
        header.Children.Add(dur);
        rows.Children.Add(header);

        foreach (var row in timeline.Rows)
        {
            var on = selected == row.SelectId;
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("240,*,64"), ColumnSpacing = 12, Height = 30, Background = Brushes.Transparent,
            };
            var cell = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 0), Child = grid, Cursor = new Cursor(StandardCursorType.Hand) };
            if (on) cell.Background = new SolidColorBrush(TracePalette.WithAlpha(palette.Accent, 0.14));
            else
            {
                cell.PointerEntered += (_, _) => cell.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
                cell.PointerExited += (_, _) => { cell.ClearValue(Border.BackgroundProperty); cell.Background = null; };
            }
            if (row.Detail is not null) ToolTip.SetTip(cell, row.Detail);
            var id = row.SelectId;
            cell.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) return;
                e.Handled = true;
                onSelect(id);
            };

            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(row.Depth * 18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var text = TraceUi.Text(row.Label, 12, ArcDesignTokenKind.Foreground, row.Depth == 0 ? FontWeight.SemiBold : FontWeight.Normal);
            label.Children.Add(text);
            if (row.Flag is { } flag)
            {
                label.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(flag == Anomaly.Error ? palette.Red : palette.Orange),
                    Child = TraceUi.Text(Loc.T("traces.flag." + flag.ToString().ToLowerInvariant()), 10, Colors.White, FontWeight.SemiBold),
                });
            }
            grid.Children.Add(label);
            var bar = new TimelineBar(row, timeline.TotalMs, palette) { Height = 14, VerticalAlignment = VerticalAlignment.Center };
            if (row.Kind == TimelineRowKind.Live) _live.Add(bar);
            Grid.SetColumn(bar, 1);
            grid.Children.Add(bar);
            var d = TraceUi.Text(TraceFormat.Ms(row.DurationMs), 12, ArcDesignTokenKind.TextSecondary);
            d.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(d, 2);
            grid.Children.Add(d);
            _rows[row.Id] = cell;
            rows.Children.Add(cell);
        }

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = rows,
        };
        var card = TraceUi.Card(_scroll, new Thickness(12));
        card.VerticalAlignment = VerticalAlignment.Top;
        var layout = new SideBySide(640, 300, 12) { Children = { card } };
        if (inspector is not null)
        {
            var aside = TraceUi.Card(inspector, new Thickness(14));
            aside.VerticalAlignment = VerticalAlignment.Top;
            layout.Children.Add(aside);
        }
        _outer = new ScrollViewer { Content = layout, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Content = _outer;
    }

    private readonly ScrollViewer _outer;
    private int _revealSeq;

    /// <summary>
    /// Scrolls the row of a focused call into view (the "next anomaly" button; session.tsx <c>scrollIntoView({ block: 'nearest',
    /// behavior: 'smooth' })</c>): only as far as needed, eased over 300 ms; at once when motion is off.
    /// </summary>
    public void Reveal(string id)
    {
        if (!_rows.TryGetValue(id, out var row)) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null || !TraceUi.Animates(this) || _outer.Content is not Visual content || row.TranslatePoint(default, content) is not { } at)
        {
            row.BringIntoView();
            return;
        }
        var from = _outer.Offset.Y;
        var target = TimelineScroll.Nearest(from, _outer.Viewport.Height, at.Y, row.Bounds.Height, _outer.Extent.Height);
        if (Math.Abs(target - from) < 0.5) return;
        var seq = ++_revealSeq;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Step(TimeSpan _)
        {
            if (seq != _revealSeq) return;
            var f = Math.Min(1, clock.Elapsed.TotalMilliseconds / TimelineScroll.SmoothMs);
            _outer.Offset = _outer.Offset.WithY(from + (target - from) * ViewportTween.EaseCubicInOut(f));
            if (f < 1) (TopLevel.GetTopLevel(this) ?? top).RequestAnimationFrame(Step);
        }
        top.RequestAnimationFrame(Step);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_live.Count == 0 || !TraceUi.Animates(this)) return;
        var start = Environment.TickCount64;
        // a call in flight: its current phase pulses (Tailwind animate-pulse: opacity 1 → 0.5 → 1 over 2 s)
        _pulse = TraceTicker.Frame(() =>
        {
            var o = 1 - 0.5 * (1 - Math.Cos((Environment.TickCount64 - start) / 2000.0 * 2 * Math.PI)) / 2;
            foreach (var b in _live) b.Pulse = o;
        });
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _pulse?.Dispose();
        _pulse = null;
    }

    private static Control Bottom(Control c)
    {
        c.VerticalAlignment = VerticalAlignment.Bottom;
        return c;
    }

    /// <summary>Places each child (Tag = ms) centred on its share of the width (<c>left: pct; -translate-x-1/2</c>).</summary>
    private sealed class TickPanel(double totalMs) : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            foreach (var c in Children) c.Measure(Size.Infinity);
            return new Size(0, Children.Count == 0 ? 0 : Children.Max(c => c.DesiredSize.Height));
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            foreach (var c in Children)
            {
                var x = TimelineBuilder.Percent(c.Tag is double ms ? ms : 0, totalMs) / 100 * finalSize.Width;
                c.Arrange(new Rect(x - c.DesiredSize.Width / 2, 0, c.DesiredSize.Width, c.DesiredSize.Height));
            }
            return finalSize;
        }
    }
}

/// <summary>
/// One bar of the timeline: the track (<c>--surface-muted</c>, 4 px corners) with either the call drawn faint under its phase segments,
/// or a single bar in the row kind's colour (a failed attempt striped red; a flagged error red). Minimum widths 3 / 2 px as on the web.
/// </summary>
internal sealed class TimelineBar(TimelineRow row, double totalMs, TracePalette palette) : Control
{
    private double _pulse = 1;

    /// <summary>The opacity of the live part (pulsing in flight).</summary>
    public double Pulse
    {
        get => _pulse;
        set
        {
            _pulse = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.DrawRectangle(new SolidColorBrush(palette.SurfaceMuted), null, new Rect(0, 0, w, h), 4, 4);
        double X(double ms) => TimelineBuilder.Percent(ms, totalMs) / 100 * w;
        Rect Span(double from, double to, double min) => new(X(from), 0, Math.Max(min, X(to) - X(from)), h);
        var baseColor = palette.Bar(row.Kind);
        if (row.Segments is { Count: > 0 } segments)
        {
            // the call, faint, under the phases the model went through
            context.DrawRectangle(new SolidColorBrush(TracePalette.WithAlpha(baseColor, 0.22)), null, Span(row.StartMs, row.EndMs, 3), 4, 4);
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var opacity = row.Kind == TimelineRowKind.Live && i == segments.Count - 1 ? _pulse : 1;
                context.DrawRectangle(new SolidColorBrush(TracePalette.WithAlpha(palette.Phase(seg.Kind), opacity)), null,
                    Span(seg.From, Math.Max(seg.From, seg.To), 2), 3, 3);
            }
            return;
        }
        var rect = Span(row.StartMs, row.EndMs, 3);
        IBrush brush;
        if (row.Flag == Anomaly.Error) brush = new SolidColorBrush(palette.Red);
        else if (row.Kind == TimelineRowKind.AttemptFail)
        {
            // repeating-linear-gradient(135deg, red 0 4px, red 45 % 4px 8px)
            var faint = TracePalette.WithAlpha(palette.Red, 0.45);
            brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
                EndPoint = new RelativePoint(5.657, 5.657, RelativeUnit.Absolute),
                SpreadMethod = GradientSpreadMethod.Repeat,
                GradientStops = { new GradientStop(palette.Red, 0), new GradientStop(palette.Red, 0.5), new GradientStop(faint, 0.5), new GradientStop(faint, 1) },
            };
        }
        else brush = new SolidColorBrush(TracePalette.WithAlpha(baseColor, row.Kind == TimelineRowKind.Live ? _pulse : 1));
        context.DrawRectangle(brush, null, rect, 4, 4);
    }
}

/// <summary>
/// Two children side by side, the first growing (basis <paramref name="firstBasis"/>), the second at its basis; when both bases do not fit
/// the second wraps under the first at full width (CSS <c>flex-wrap</c> with <c>flex-[999_1_640px]</c> / <c>flex-[1_1_300px]</c>).
/// </summary>
internal sealed class SideBySide(double firstBasis, double secondBasis, double gap) : Panel
{
    private bool Wraps(double width) => Children.Count > 1 && width < firstBasis + secondBasis + gap;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? firstBasis + secondBasis + gap : availableSize.Width;
        if (Children.Count == 0) return default;
        if (Children.Count == 1 || Wraps(width))
        {
            double h = 0;
            foreach (var c in Children)
            {
                c.Measure(new Size(width, double.PositiveInfinity));
                h += c.DesiredSize.Height;
            }
            return new Size(width, h + gap * (Children.Count - 1));
        }
        var second = secondBasis;
        Children[0].Measure(new Size(width - second - gap, double.PositiveInfinity));
        Children[1].Measure(new Size(second, double.PositiveInfinity));
        return new Size(width, Math.Max(Children[0].DesiredSize.Height, Children[1].DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;
        if (Children.Count == 1 || Wraps(finalSize.Width))
        {
            double y = 0;
            foreach (var c in Children)
            {
                c.Arrange(new Rect(0, y, finalSize.Width, c.DesiredSize.Height));
                y += c.DesiredSize.Height + gap;
            }
            return finalSize;
        }
        var w1 = finalSize.Width - secondBasis - gap;
        Children[0].Arrange(new Rect(0, 0, w1, Children[0].DesiredSize.Height));
        Children[1].Arrange(new Rect(w1 + gap, 0, secondBasis, Children[1].DesiredSize.Height));
        return finalSize;
    }
}
