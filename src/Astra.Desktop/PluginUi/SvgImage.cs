using System.Globalization;
using Astra.Desktop.Core.Plugins.V2;
using Avalonia;
using Avalonia.Media;

namespace Astra.Desktop.PluginUi;

/// <summary>Draws a parsed <see cref="SvgDocument"/> (the safe subset) as an Avalonia <see cref="DrawingImage"/> sized to its viewBox.</summary>
internal static class SvgImage
{
    /// <summary>The drawing, or null when nothing in it could be drawn. <paramref name="currentColor"/> paints "currentColor".</summary>
    public static DrawingImage? Create(SvgDocument doc, Color currentColor)
    {
        var shapes = new DrawingGroup();
        var drawn = 0;
        foreach (var s in doc.Shapes)
        {
            Geometry geometry;
            try
            {
                // StreamGeometry's "F1" is SVG's default nonzero rule, "F0" even-odd
                geometry = Geometry.Parse((s.EvenOdd ? "F0 " : "F1 ") + s.PathData);
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
            {
                continue;
            }
            var t = s.Transform;
            geometry.Transform = new MatrixTransform(new Matrix(t[0], t[1], t[2], t[3], t[4], t[5]));
            var fill = Brush(s.Fill, s.FillOpacity, currentColor);
            var strokeBrush = Brush(s.Stroke, s.StrokeOpacity, currentColor);
            Pen? pen = null;
            if (strokeBrush is not null && s.StrokeWidth > 0)
            {
                // the stroke width scales with the shape's transform, as in SVG
                var scale = Math.Sqrt(Math.Abs(t[0] * t[3] - t[1] * t[2]));
                pen = new Pen(strokeBrush, s.StrokeWidth * (scale > 0 ? scale : 1))
                {
                    LineCap = s.LineCap switch { "round" => PenLineCap.Round, "square" => PenLineCap.Square, _ => PenLineCap.Flat },
                    LineJoin = s.LineJoin switch { "round" => PenLineJoin.Round, "bevel" => PenLineJoin.Bevel, _ => PenLineJoin.Miter },
                };
            }
            if (fill is null && pen is null) continue;
            var drawing = new GeometryDrawing { Geometry = geometry, Brush = fill, Pen = pen };
            if (s.Opacity < 1) shapes.Children.Add(new DrawingGroup { Opacity = s.Opacity, Children = { drawing } });
            else shapes.Children.Add(drawing);
            drawn++;
        }
        if (drawn == 0) return null;
        var viewBox = new Rect(doc.X, doc.Y, doc.Width, doc.Height);
        // a transparent frame the size of the viewBox, so the image measures as the viewBox (not the drawn bounds)
        var root = new DrawingGroup { ClipGeometry = new RectangleGeometry(viewBox) };
        root.Children.Add(new GeometryDrawing { Geometry = new RectangleGeometry(viewBox), Brush = Brushes.Transparent });
        root.Children.Add(shapes);
        return new DrawingImage { Drawing = root };
    }

    private static IBrush? Brush(string? paint, double opacity, Color currentColor)
    {
        if (paint is null || ParseColor(paint, currentColor) is not { } color) return null;
        if (opacity < 1) color = new Color((byte)Math.Round(color.A * opacity), color.R, color.G, color.B);
        return new SolidColorBrush(color);
    }

    /// <summary>An SVG colour: #rgb, #rrggbb, #rrggbbaa, rgb()/rgba(), a CSS name, or currentColor.</summary>
    internal static Color? ParseColor(string paint, Color currentColor)
    {
        var v = paint.Trim();
        if (v.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return currentColor;
        if (v.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return Colors.Transparent;
        if (v.StartsWith('#') && v.Length == 9)
        {
            // CSS order is #rrggbbaa; Avalonia reads #aarrggbb
            v = "#" + v[7..9] + v[1..7];
        }
        if (v.StartsWith("rgb", StringComparison.OrdinalIgnoreCase) && v.IndexOf('(') is var open and > 0 && v.EndsWith(')'))
        {
            var parts = v[(open + 1)..^1].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            byte Channel(string s) => s.EndsWith('%')
                ? (byte)Math.Clamp(Math.Round(double.Parse(s[..^1], CultureInfo.InvariantCulture) * 2.55), 0, 255)
                : (byte)Math.Clamp(Math.Round(double.Parse(s, CultureInfo.InvariantCulture)), 0, 255);
            try
            {
                var a = parts.Length > 3
                    ? (byte)Math.Clamp(Math.Round((parts[3].EndsWith('%') ? double.Parse(parts[3][..^1], CultureInfo.InvariantCulture) / 100 : double.Parse(parts[3], CultureInfo.InvariantCulture)) * 255), 0, 255)
                    : (byte)255;
                return new Color(a, Channel(parts[0]), Channel(parts[1]), Channel(parts[2]));
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return Color.TryParse(v, out var c) ? c : null;
    }
}
