using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Astra.Desktop.Components.Overview;

/// <summary>
/// The daily bill's drawn pieces that CSS paints with gradients and borders (web <c>components/bill/bill.css</c>): torn receipt edges, dashed /
/// dotted / double rules, the barcode, the halftone screen, the speech bubble's tail and a dashed box. Colors are fixed: a bill is a picture.
/// </summary>
internal static class BillShapes
{
    /// <summary>A solid brush of <paramref name="hex"/>.</summary>
    public static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    /// <summary>
    /// A text line as the web lays it out: CSS <c>line-height</c> (default 1.5 on both bill roots), letter spacing in pixels (CSS em × size).
    /// </summary>
    public static TextBlock Text(string? text, double size, FontWeight weight, IBrush color, double? lineHeight = null, double letterSpacing = 0,
        bool italic = false, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        Foreground = color,
        LineHeight = lineHeight ?? size * 1.5,
        LetterSpacing = letterSpacing,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
    };

    /// <summary>A two-column row (CSS <c>display:flex; justify-content:space-between</c>): <paramref name="left"/> takes the rest, <paramref name="right"/> its size.</summary>
    public static Grid Row(Control left, Control right, double gap = 12)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = gap };
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        grid.Children.Add(right);
        return grid;
    }
}

/// <summary>The receipt's torn edge (<c>.zz-t</c> / <c>.zz-b</c>): 16 × 8 px paper teeth, pointing up above the paper or down below it.</summary>
internal sealed class ZigzagEdge : Control
{
    private readonly bool _up;
    private readonly IBrush _paper;

    public ZigzagEdge(bool up, IBrush paper)
    {
        _up = up;
        _paper = paper;
        Height = 8;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            for (double x = 0; x < w; x += 16)
            {
                // Up: base on the paper (y = 8), tip at the top; down: base on the paper (y = 0), tip at the bottom.
                g.BeginFigure(new Point(x, _up ? 8 : 0), true);
                g.LineTo(new Point(x + 8, _up ? 0 : 8));
                g.LineTo(new Point(x + 16, _up ? 8 : 0));
                g.EndFigure(true);
            }
        }
        context.DrawGeometry(_paper, null, geometry);
    }
}

/// <summary>The rules: <c>.dash</c> (1.5 px dashed), <c>.dbl</c> (4 px double), the subtotal's 1.5 px dotted filler, the comic rows' 2 px dashes.</summary>
internal sealed class BillRule : Control
{
    public enum Kind { Dashed, Double, Dotted }

    private readonly Kind _kind;
    private readonly IBrush _ink;
    private readonly double _thickness;

    public BillRule(Kind kind, IBrush ink, double thickness)
    {
        _kind = kind;
        _ink = ink;
        _thickness = thickness;
        Height = thickness;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        if (_kind == Kind.Double)
        {
            // Chrome's 4 px double border: two thirds of ink, one third of gap.
            var line = _thickness / 3;
            context.FillRectangle(_ink, new Rect(0, 0, w, line));
            context.FillRectangle(_ink, new Rect(0, _thickness - line, w, line));
            return;
        }
        // As Chrome draws them: the receipt's 1.5 px dashes 3 px on / 3 px off, the comic page's 2 px ones 6 on / 4 off; dots one width apart.
        var dashes = _kind == Kind.Dotted ? new[] { 1.0, 1.0 } : _thickness < 2 ? new[] { 2.0, 2.0 } : new[] { 3.0, 2.0 };
        var pen = new Pen(_ink, _thickness, new DashStyle(dashes, 0), PenLineCap.Flat);
        var y = _thickness / 2;
        context.DrawLine(pen, new Point(0, y), new Point(w, y));
    }
}

/// <summary>The receipt's barcode (<c>.barcode</c>): the same 53 px bar pattern repeated across 286 × 48 px.</summary>
internal sealed class BillBarcode : Control
{
    // [start, end) of each bar within one 53 px period, as the CSS repeating-linear-gradient stops.
    private static readonly (double From, double To)[] Bars =
    [
        (0, 2), (4, 5), (7, 10), (11, 12), (15, 17), (18, 19), (22, 25), (26, 27), (29, 31), (34, 35), (36, 39), (41, 42), (45, 47), (48, 49),
    ];

    private readonly IBrush _ink;

    public BillBarcode(IBrush ink)
    {
        _ink = ink;
        Width = 286;
        Height = 48;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        for (double x0 = 0; x0 < Bounds.Width; x0 += 53)
            foreach (var (from, to) in Bars)
                context.FillRectangle(_ink, new Rect(x0 + from, 0, to - from, Bounds.Height));
    }
}

/// <summary>The comic page's screen tone (<c>.tone</c>): white with #111 dots of about 1.2 px radius, one per 7 px tile, centered in the tile.</summary>
internal sealed class Halftone : Control
{
    private static readonly IBrush Paper = Brushes.White;
    private readonly IBrush _ink;

    public Halftone(IBrush ink)
    {
        _ink = ink;
        ClipToBounds = true;
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Paper, new Rect(size));
        for (var y = 3.5; y < size.Height; y += 7)
            for (var x = 3.5; x < size.Width; x += 7)
                context.DrawEllipse(_ink, null, new Point(x, y), 1.15, 1.15);
    }
}

/// <summary>
/// The speech bubble's tail (<c>.bubble::before</c> / <c>::after</c>): a #111 wedge 26 × 24 pointing left, and inside it a white wedge that
/// covers the bubble's border where the tail joins it. Placed 23 px left of the bubble's border box, 39 px down (the pseudo elements sit on the
/// padding box, 3 px inside the border).
/// </summary>
internal sealed class BubbleTail : Control
{
    private readonly IBrush _ink;
    private readonly IBrush _paper;

    public BubbleTail(IBrush ink, IBrush paper)
    {
        _ink = ink;
        _paper = paper;
        Width = 27;
        Height = 24;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        context.DrawGeometry(_ink, null, Wedge(0, 0, 26, 24));
        context.DrawGeometry(_paper, null, Wedge(7, 4, 20, 16));
    }

    private static StreamGeometry Wedge(double x, double y, double w, double h)
    {
        var geometry = new StreamGeometry();
        using var g = geometry.Open();
        g.BeginFigure(new Point(x, y + h / 2), true);
        g.LineTo(new Point(x + w, y));
        g.LineTo(new Point(x + w, y + h));
        g.EndFigure(true);
        return geometry;
    }
}

/// <summary>A box with a dashed border (<c>.bill-mg .none</c>: 3 px dashed #111); the child sits inside its padding.</summary>
internal sealed class DashedBox : Decorator
{
    private readonly IBrush _ink;
    private readonly double _thickness;

    public DashedBox(IBrush ink, double thickness, Thickness padding, Control child)
    {
        _ink = ink;
        _thickness = thickness;
        Padding = new Thickness(padding.Left + thickness, padding.Top + thickness, padding.Right + thickness, padding.Bottom + thickness);
        Child = child;
    }

    public override void Render(DrawingContext context)
    {
        var half = _thickness / 2;
        var pen = new Pen(_ink, _thickness, new DashStyle([3.0, 2.0], 0), PenLineCap.Flat);
        context.DrawRectangle(null, pen, new Rect(half, half, Bounds.Width - _thickness, Bounds.Height - _thickness));
    }
}
