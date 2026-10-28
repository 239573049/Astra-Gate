using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Traces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Motion;

namespace Astra.Desktop.Components.Traces;

/// <summary>
/// Small building blocks of the trace views, each the desktop form of a web class or component: <c>.card</c>, <c>TraceChip</c>
/// (topology.tsx <c>Chip</c>), <c>Legend</c> / <c>LiveLegend</c>, a link, text with the web's literal sizes, and <c>TraceError</c> (error.tsx).
/// </summary>
public static class TraceUi
{
    /// <summary>Text at a literal web size (<c>text-[12.5px]</c> …) in a token colour; trims like <c>truncate</c>.</summary>
    public static TextBlock Text(string? text, double size, ArcDesignTokenKind color, FontWeight weight = FontWeight.Normal, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        block.Token(TextBlock.ForegroundProperty, color);
        return block;
    }

    /// <summary>Text in a fixed colour.</summary>
    public static TextBlock Text(string? text, double size, Color color, FontWeight weight = FontWeight.Normal)
        => new()
        {
            Text = text, FontSize = size, FontWeight = weight, Foreground = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

    /// <summary>Monospace text (web <c>font-mono</c>).</summary>
    public static TextBlock Mono(string? text, double size, ArcDesignTokenKind color)
    {
        var block = Text(text, size, color);
        block.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        return block;
    }

    /// <summary>Web <c>.card</c>: surface fill, 0.5 px separator edge, 14 px corners, content clipped.</summary>
    public static Border Card(Control child, Thickness? padding = null)
    {
        var card = new Border { BorderThickness = new Thickness(0.5), ClipToBounds = true, Child = child, Padding = padding ?? new Thickness(0), CornerRadius = new CornerRadius(14) };
        // Web .card: the translucent --surface with a --separator hairline (WebMaterial; the theme's Surface token is opaque).
        return Requests.WebMaterial.Apply(card);
    }

    /// <summary>A hairline between rows (web <c>divide-y divide-[var(--separator)]</c>).</summary>
    public static Border Divider()
    {
        var line = new Border { Height = 0.5, HorizontalAlignment = HorizontalAlignment.Stretch };
        line.Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);
        return line;
    }

    /// <summary>
    /// The chips of the views' top lines (<c>TraceChip</c>): a 24 px pill on <c>--surface-muted</c>, 12 px secondary text, bold parts in the
    /// foreground. <paramref name="parts"/> are strings (plain), <see cref="Bold"/> strings or controls (icons, dots).
    /// </summary>
    public static Border Chip(params object?[] parts)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var part in parts)
        {
            switch (part)
            {
                case null: break;
                case BoldText b: row.Children.Add(Text(b.Text, 12, ArcDesignTokenKind.Foreground, FontWeight.SemiBold)); break;
                case string s: row.Children.Add(Text(s, 12, ArcDesignTokenKind.TextSecondary)); break;
                case Control c: row.Children.Add(c); break;
            }
        }
        var chip = new Border { Height = 24, Padding = new Thickness(10, 0), CornerRadius = new CornerRadius(12), Child = row, VerticalAlignment = VerticalAlignment.Center };
        chip.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        return chip;
    }

    /// <summary>A bold run inside a <see cref="Chip"/> (web <c>&lt;b&gt;</c>).</summary>
    public sealed record BoldText(string Text);

    /// <summary>Marks a chip part bold.</summary>
    public static BoldText Bold(string text) => new(text);

    /// <summary>A coloured dot (web <c>size-[7px] rounded-full bg-…</c>).</summary>
    public static Ellipse Dot(Color color, double size = 7) => new() { Width = size, Height = size, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>The pulsing accent dot of live things (flow.css <c>.trace-live-pulse</c>: 6 px, opacity wave 1.2 s).</summary>
    public static StatusDot LivePulse(double size = 6) => new(Kit.Tone.Accent) { Width = size, Height = size, IsPulsing = true };

    /// <summary>Accent link text (web <c>text-[12.5px] text-[var(--accent)]</c> on a <c>Link</c>).</summary>
    public static TextBlock Link(string text, Action onClick, double size = 12.5)
    {
        var link = new TextBlock { Text = text, FontSize = size, Cursor = new Cursor(StandardCursorType.Hand), VerticalAlignment = VerticalAlignment.Center, Focusable = true };
        link.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Accent);
        link.PointerEntered += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.PointerExited += (_, _) => link.TextDecorations = null;
        link.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(link).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            onClick();
        };
        link.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            onClick();
        };
        return link;
    }

    /// <summary>A legend entry: a swatch (rounded square), a line (solid or dashed) or the tool pill, then the text (session.tsx <c>Legend</c>).</summary>
    public static Control Legend(string text, LegendMark mark, Color color)
    {
        Control swatch = mark switch
        {
            LegendMark.Swatch => new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(color) },
            LegendMark.Dot => new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color) },
            LegendMark.Pill => new Border
            {
                Width = 16, Height = 10, CornerRadius = new CornerRadius(5), Child = new DashedOutline(color),
            }.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted),
            _ => new Line
            {
                StartPoint = new Point(0, 1), EndPoint = new Point(18, 1), Stroke = new SolidColorBrush(color), StrokeThickness = 2,
                StrokeDashArray = mark == LegendMark.DashedLine ? new Avalonia.Collections.AvaloniaList<double> { 2.5, 2 } : null,
                Width = 18, Height = 2,
            },
        };
        swatch.VerticalAlignment = VerticalAlignment.Center;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(swatch);
        row.Children.Add(Text(text, 11.5, ArcDesignTokenKind.TextSecondary));
        return row;
    }

    /// <summary>
    /// Why a trace view has nothing to show (error.tsx <c>TraceError</c>): a warning alert; a 404 on <c>/api/traces</c> means the connected
    /// server predates tracing.
    /// </summary>
    public static Control Error(Exception? error)
    {
        var unsupported = TraceQueries.IsUnsupported(error);
        var alert = new Alert
        {
            Tone = AlertTone.Warning,
            Title = Loc.T(unsupported ? "traces.error.unsupportedTitle" : "traces.error.title"),
            Content = Text(unsupported ? Loc.T("traces.error.unsupported") : error?.Message ?? "", 13, ArcDesignTokenKind.Foreground, wrap: true),
            VerticalAlignment = VerticalAlignment.Top,
        };
        return alert;
    }

    /// <summary>A horizontal wrapping row (web <c>flex flex-wrap items-center gap-x gap-y</c>).</summary>
    public static WrapPanel Wrap(double gapX, double gapY) => new() { Orientation = Orientation.Horizontal, ItemSpacing = gapX, LineSpacing = gapY, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>Whether animations should run for <paramref name="visual"/> (Arc's motion level; the visual tests turn it off).</summary>
    public static bool Animates(Visual visual) => ArcMotion.GetEffectiveLevel(visual) != ArcMotionLevel.Off;

    /// <summary>
    /// Shows <paramref name="control"/> only while <paramref name="shows"/> accepts its window's width (a Tailwind <c>max-*:hidden</c>
    /// class: the web's breakpoints measure the viewport, which is the window), following the window as it is resized.
    /// </summary>
    public static T HideBelowWindowWidth<T>(T control, Func<double, bool> shows) where T : Control
    {
        TopLevel? top = null;
        void Sync() => control.IsVisible = top is null || shows(top.ClientSize.Width);
        void OnTop(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TopLevel.ClientSizeProperty) Sync();
        }
        control.AttachedToVisualTree += (_, _) =>
        {
            top = TopLevel.GetTopLevel(control);
            if (top is not null) top.PropertyChanged += OnTop;
            Sync();
        };
        control.DetachedFromVisualTree += (_, _) =>
        {
            if (top is not null) top.PropertyChanged -= OnTop;
            top = null;
        };
        return control;
    }
}

/// <summary>How a <see cref="TraceUi.Legend"/> entry is marked.</summary>
public enum LegendMark { Line, DashedLine, Swatch, Dot, Pill }

/// <summary>The dashed pill outline of the tool legend (web <c>border border-dashed</c>), drawn because a Border has no dash.</summary>
internal sealed class DashedOutline(Color color, double dash = 2) : Control
{
    public override void Render(DrawingContext context)
    {
        var r = new Rect(Bounds.Size).Deflate(0.5);
        var pen = new Pen(new SolidColorBrush(color), 1, new DashStyle([dash, dash * 0.75], 0));
        context.DrawRectangle(null, pen, r, r.Height / 2, r.Height / 2);
    }
}

/// <summary>
/// The shared clocks of the trace views: <see cref="Second"/> ticks once a second while something shows elapsed time (live.tsx <c>useNow</c>),
/// <see cref="Frame"/> about 30 times a second while something animates (live dots, breathing cards). Both run only while subscribed.
/// </summary>
public static class TraceTicker
{
    private static readonly Ticker SecondTicker = new(TimeSpan.FromSeconds(1));
    private static readonly Ticker FrameTicker = new(TimeSpan.FromMilliseconds(33));

    /// <summary>Subscribes to the 1 s clock; dispose to stop.</summary>
    public static IDisposable Second(Action tick) => SecondTicker.Add(tick);

    /// <summary>Subscribes to the animation clock; dispose to stop.</summary>
    public static IDisposable Frame(Action tick) => FrameTicker.Add(tick);

    private sealed class Ticker(TimeSpan interval)
    {
        private readonly List<Action> _subs = [];
        private DispatcherTimer? _timer;

        public IDisposable Add(Action tick)
        {
            _subs.Add(tick);
            if (_timer is null)
            {
                _timer = new DispatcherTimer { Interval = interval };
                _timer.Tick += (_, _) =>
                {
                    foreach (var s in _subs.ToArray()) s();
                };
                _timer.Start();
            }
            return new Sub(this, tick);
        }

        private void Remove(Action tick)
        {
            _subs.Remove(tick);
            if (_subs.Count > 0 || _timer is null) return;
            _timer.Stop();
            _timer = null;
        }

        private sealed class Sub(Ticker owner, Action tick) : IDisposable
        {
            private bool _done;

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                owner.Remove(tick);
            }
        }
    }
}
