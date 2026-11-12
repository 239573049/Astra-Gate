using System.Globalization;
using System.Text;
using System.Xml;

namespace Astra.Desktop.Core.Plugins.V2;

/// <summary>One filled / stroked shape of an <see cref="SvgDocument"/>: SVG path data in user space, its transform and paint.</summary>
/// <param name="PathData">Path data (M/L/H/V/C/S/Q/T/A/Z, absolute or relative) — shapes are converted to it.</param>
/// <param name="Transform">The accumulated transform as a, b, c, d, e, f (SVG matrix order).</param>
/// <param name="Fill">A colour, "currentColor", or null for none.</param>
/// <param name="Stroke">A colour, "currentColor", or null for none.</param>
public sealed record SvgShape(string PathData, double[] Transform, string? Fill, string? Stroke, double StrokeWidth, double Opacity,
    double FillOpacity, double StrokeOpacity, bool EvenOdd, string? LineCap, string? LineJoin);

/// <summary>
/// A safe subset of SVG for plugin images on the desktop (design/plugin-v2 04, Image): path, rect, circle, ellipse, line,
/// polyline, polygon and g, with transforms, fill / stroke / opacity (attributes, inherited, or in <c>style</c>) and the
/// viewBox. Nothing external is ever read: no DTD, entities, scripts, <c>use</c> / <c>image</c> references, text or
/// gradients (a gradient paint falls back to none). Too big or too many elements is a parse failure, not a slow draw.
/// </summary>
public sealed class SvgDocument
{
    /// <summary>Largest document read, in characters.</summary>
    public const int MaxLength = 1024 * 1024;

    /// <summary>Most shapes kept.</summary>
    public const int MaxShapes = 5000;

    public double X { get; private init; }
    public double Y { get; private init; }
    public double Width { get; private init; }
    public double Height { get; private init; }
    public IReadOnlyList<SvgShape> Shapes { get; private init; } = [];

    private sealed record Style(string? Fill, string? Stroke, double StrokeWidth, double Opacity, double FillOpacity, double StrokeOpacity,
        bool EvenOdd, string? LineCap, string? LineJoin, double[] Transform, bool Hidden);

    private static readonly double[] Identity = [1, 0, 0, 1, 0, 0];

    /// <summary>Parses <paramref name="text"/>; null when it isn't an SVG this subset can draw.</summary>
    public static SvgDocument? TryParse(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength) return null;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersInDocument = MaxLength,
        };
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);
            var shapes = new List<SvgShape>();
            var stack = new Stack<Style>();
            double x = 0, y = 0, w = double.NaN, h = double.NaN, vbW = double.NaN, vbH = double.NaN;
            var root = false;
            var skipDepth = -1;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (skipDepth == reader.Depth) skipDepth = -1;
                    else if (skipDepth < 0 && stack.Count > 0 && IsContainer(reader.LocalName)) stack.Pop();
                    continue;
                }
                if (reader.NodeType != XmlNodeType.Element || skipDepth >= 0) continue;
                var name = reader.LocalName;
                var empty = reader.IsEmptyElement;
                if (!root)
                {
                    if (name != "svg") return null;
                    root = true;
                    if (ParseNumbers(reader.GetAttribute("viewBox")) is { Count: 4 } vb && vb[2] > 0 && vb[3] > 0)
                        (x, y, vbW, vbH) = (vb[0], vb[1], vb[2], vb[3]);
                    w = Length(reader.GetAttribute("width"));
                    h = Length(reader.GetAttribute("height"));
                    stack.Push(Apply(new Style("#000000", null, 1, 1, 1, 1, false, null, null, Identity, false), reader, transform: false));
                    if (empty) stack.Pop();
                    continue;
                }
                if (name is "defs" or "script" or "style" or "title" or "desc" or "metadata" or "clipPath" or "mask" or "symbol" or "pattern"
                    or "linearGradient" or "radialGradient" or "foreignObject" or "text" or "use" or "image" or "marker" or "filter")
                {
                    if (!empty) skipDepth = reader.Depth;
                    continue;
                }
                var parent = stack.Count > 0 ? stack.Peek() : null;
                if (parent is null) continue;
                var style = Apply(parent, reader, transform: true);
                if (IsContainer(name))
                {
                    if (!empty) stack.Push(style);
                    continue;
                }
                if (style.Hidden) continue;
                var data = name switch
                {
                    "path" => reader.GetAttribute("d"),
                    "rect" => Rect(reader),
                    "circle" => Ellipse(Num(reader, "cx"), Num(reader, "cy"), Num(reader, "r"), Num(reader, "r")),
                    "ellipse" => Ellipse(Num(reader, "cx"), Num(reader, "cy"), Num(reader, "rx"), Num(reader, "ry")),
                    "line" => $"M{F(Num(reader, "x1"))},{F(Num(reader, "y1"))} L{F(Num(reader, "x2"))},{F(Num(reader, "y2"))}",
                    "polyline" => Poly(reader.GetAttribute("points"), false),
                    "polygon" => Poly(reader.GetAttribute("points"), true),
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(data) || !SafePath(data)) continue;
                // a line has nothing to fill
                var fill = name is "line" or "polyline" && reader.GetAttribute("fill") is null && !HasStyle(reader, "fill") ? null : style.Fill;
                shapes.Add(new SvgShape(data.Trim(), style.Transform, fill, style.Stroke, style.StrokeWidth, style.Opacity, style.FillOpacity,
                    style.StrokeOpacity, style.EvenOdd, style.LineCap, style.LineJoin));
                if (shapes.Count > MaxShapes) return null;
            }
            if (!root) return null;
            if (double.IsNaN(vbW))
            {
                vbW = double.IsNaN(w) ? 100 : w;
                vbH = double.IsNaN(h) ? 100 : h;
            }
            return new SvgDocument
            {
                X = x, Y = y, Width = vbW, Height = vbH, Shapes = shapes,
            };
        }
        catch (Exception e) when (e is XmlException or FormatException or OverflowException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The text of an image source that is SVG: a data URI (base64 or url-encoded) or the bytes of an .svg asset.</summary>
    public static string? TextOf(string src, byte[]? bytes)
    {
        if (src.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            var comma = src.IndexOf(',');
            if (comma < 0) return null;
            var meta = src[..comma];
            var payload = src[(comma + 1)..];
            try
            {
                return meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                    ? Encoding.UTF8.GetString(Convert.FromBase64String(payload))
                    : Uri.UnescapeDataString(payload);
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Whether a source names an SVG (by data URI type or file extension).</summary>
    public static bool IsSvg(string src) =>
        src.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase)
        || src.Split('?', '#')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    private static bool IsContainer(string name) => name is "g" or "svg" or "a" or "switch";

    private static Style Apply(Style parent, XmlReader r, bool transform)
    {
        var styleAttr = ParseStyle(r.GetAttribute("style"));
        string? Get(string key) => styleAttr.TryGetValue(key, out var v) ? v : r.GetAttribute(key);
        var fill = Get("fill") is { } f ? Paint(f) : parent.Fill;
        var stroke = Get("stroke") is { } s ? Paint(s) : parent.Stroke;
        var width = Length(Get("stroke-width")) is var sw && !double.IsNaN(sw) ? sw : parent.StrokeWidth;
        var opacity = Fraction(Get("opacity"), 1) * parent.Opacity;
        var fillOpacity = Get("fill-opacity") is { } fo ? Fraction(fo, 1) : parent.FillOpacity;
        var strokeOpacity = Get("stroke-opacity") is { } so ? Fraction(so, 1) : parent.StrokeOpacity;
        var evenOdd = Get("fill-rule") is { } rule ? rule.Trim() == "evenodd" : parent.EvenOdd;
        var cap = Get("stroke-linecap")?.Trim() ?? parent.LineCap;
        var join = Get("stroke-linejoin")?.Trim() ?? parent.LineJoin;
        var hidden = parent.Hidden || Get("display")?.Trim() == "none" || Get("visibility")?.Trim() == "hidden";
        var matrix = transform && r.GetAttribute("transform") is { } t ? Multiply(parent.Transform, ParseTransform(t)) : parent.Transform;
        return new Style(fill, stroke, width, opacity, fillOpacity, strokeOpacity, evenOdd, cap, join, matrix, hidden);
    }

    private static bool HasStyle(XmlReader r, string key) => ParseStyle(r.GetAttribute("style")).ContainsKey(key);

    private static Dictionary<string, string> ParseStyle(string? style)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(style)) return map;
        foreach (var part in style.Split(';'))
        {
            var colon = part.IndexOf(':');
            if (colon > 0) map[part[..colon].Trim()] = part[(colon + 1)..].Trim();
        }
        return map;
    }

    // A paint: none, a colour or currentColor; url(#gradient) and anything unknown draw nothing.
    private static string? Paint(string value)
    {
        var v = value.Trim();
        if (v.Length == 0 || v == "none" || v.StartsWith("url(", StringComparison.Ordinal)) return null;
        return v;
    }

    private static double Fraction(string? value, double fallback)
    {
        if (value is null) return fallback;
        var v = value.Trim();
        var percent = v.EndsWith('%');
        if (!double.TryParse(percent ? v[..^1] : v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return fallback;
        return Math.Clamp(percent ? d / 100 : d, 0, 1);
    }

    private static double Length(string? value)
    {
        if (value is null) return double.NaN;
        var v = value.Trim();
        foreach (var unit in new[] { "px", "pt" })
            if (v.EndsWith(unit, StringComparison.Ordinal)) v = v[..^unit.Length];
        if (v.EndsWith('%') || v.EndsWith("em", StringComparison.Ordinal)) return double.NaN;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }

    private static double Num(XmlReader r, string name) => Length(r.GetAttribute(name)) is var d && !double.IsNaN(d) ? d : 0;

    private static string F(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);

    private static string? Rect(XmlReader r)
    {
        double x = Num(r, "x"), y = Num(r, "y"), w = Num(r, "width"), h = Num(r, "height");
        if (w <= 0 || h <= 0) return null;
        var rx = Length(r.GetAttribute("rx"));
        var ry = Length(r.GetAttribute("ry"));
        if (double.IsNaN(rx)) rx = double.IsNaN(ry) ? 0 : ry;
        if (double.IsNaN(ry)) ry = rx;
        rx = Math.Min(rx, w / 2);
        ry = Math.Min(ry, h / 2);
        if (rx <= 0 || ry <= 0) return $"M{F(x)},{F(y)} H{F(x + w)} V{F(y + h)} H{F(x)} Z";
        return $"M{F(x + rx)},{F(y)} H{F(x + w - rx)} A{F(rx)},{F(ry)} 0 0 1 {F(x + w)},{F(y + ry)} V{F(y + h - ry)} " +
               $"A{F(rx)},{F(ry)} 0 0 1 {F(x + w - rx)},{F(y + h)} H{F(x + rx)} A{F(rx)},{F(ry)} 0 0 1 {F(x)},{F(y + h - ry)} " +
               $"V{F(y + ry)} A{F(rx)},{F(ry)} 0 0 1 {F(x + rx)},{F(y)} Z";
    }

    private static string? Ellipse(double cx, double cy, double rx, double ry)
    {
        if (rx <= 0 || ry <= 0) return null;
        return $"M{F(cx - rx)},{F(cy)} A{F(rx)},{F(ry)} 0 1 0 {F(cx + rx)},{F(cy)} A{F(rx)},{F(ry)} 0 1 0 {F(cx - rx)},{F(cy)} Z";
    }

    private static string? Poly(string? points, bool close)
    {
        var n = ParseNumbers(points);
        if (n is null || n.Count < 4) return null;
        var sb = new StringBuilder();
        for (var i = 0; i + 1 < n.Count; i += 2) sb.Append(i == 0 ? 'M' : 'L').Append(F(n[i])).Append(',').Append(F(n[i + 1])).Append(' ');
        if (close) sb.Append('Z');
        return sb.ToString().Trim();
    }

    private static List<double>? ParseNumbers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = new List<double>();
        foreach (var part in text.Split([' ', ',', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return null;
            list.Add(d);
        }
        return list;
    }

    // Path data may only hold path commands and numbers (no arbitrary text reaches the geometry parser).
    private static bool SafePath(string d)
    {
        foreach (var c in d)
            if (!(char.IsAsciiDigit(c) || "MmLlHhVvCcSsQqTtAaZz.,-+eE \t\r\n".Contains(c))) return false;
        return true;
    }

    /// <summary>Parses an SVG transform list into a matrix (a, b, c, d, e, f).</summary>
    public static double[] ParseTransform(string text)
    {
        var m = Identity;
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
            var open = text.IndexOf('(', i);
            if (open < 0) break;
            var close = text.IndexOf(')', open);
            if (close < 0) break;
            var fn = text[i..open].Trim();
            var a = ParseNumbers(text[(open + 1)..close]) ?? [];
            double[]? next = fn switch
            {
                "matrix" when a.Count == 6 => [a[0], a[1], a[2], a[3], a[4], a[5]],
                "translate" when a.Count >= 1 => [1, 0, 0, 1, a[0], a.Count > 1 ? a[1] : 0],
                "scale" when a.Count >= 1 => [a[0], 0, 0, a.Count > 1 ? a[1] : a[0], 0, 0],
                "rotate" when a.Count >= 1 => Rotate(a[0], a.Count >= 3 ? a[1] : 0, a.Count >= 3 ? a[2] : 0),
                "skewX" when a.Count == 1 => [1, 0, Math.Tan(a[0] * Math.PI / 180), 1, 0, 0],
                "skewY" when a.Count == 1 => [1, Math.Tan(a[0] * Math.PI / 180), 0, 1, 0, 0],
                _ => null,
            };
            if (next is not null) m = Multiply(m, next);
            i = close + 1;
        }
        return m;
    }

    private static double[] Rotate(double degrees, double cx, double cy)
    {
        var r = degrees * Math.PI / 180;
        double cos = Math.Cos(r), sin = Math.Sin(r);
        double[] rot = [cos, sin, -sin, cos, 0, 0];
        return cx == 0 && cy == 0 ? rot : Multiply(Multiply([1, 0, 0, 1, cx, cy], rot), [1, 0, 0, 1, -cx, -cy]);
    }

    /// <summary>m1 · m2 (apply m2 first, then m1), both in SVG (a, b, c, d, e, f) order.</summary>
    public static double[] Multiply(double[] m1, double[] m2) =>
    [
        m1[0] * m2[0] + m1[2] * m2[1],
        m1[1] * m2[0] + m1[3] * m2[1],
        m1[0] * m2[2] + m1[2] * m2[3],
        m1[1] * m2[2] + m1[3] * m2[3],
        m1[0] * m2[4] + m1[2] * m2[5] + m1[4],
        m1[1] * m2[4] + m1[3] * m2[5] + m1[5],
    ];
}
