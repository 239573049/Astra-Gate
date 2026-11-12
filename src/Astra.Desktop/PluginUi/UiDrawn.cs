using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Desktop.Core.Plugins.V2;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Astra.Desktop.Components.Kit;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.PluginUi;

/// <summary>
/// A drawn component's colours, bound to the Arc design tokens (so they follow the theme like every other control): text,
/// muted text, lines, the raised surface and the tones.
/// </summary>
internal abstract class TokenCanvas : Control
{
    public static readonly StyledProperty<IBrush?> TextBrushProperty = AvaloniaProperty.Register<TokenCanvas, IBrush?>(nameof(TextBrush));
    public static readonly StyledProperty<IBrush?> MutedBrushProperty = AvaloniaProperty.Register<TokenCanvas, IBrush?>(nameof(MutedBrush));
    public static readonly StyledProperty<IBrush?> LineBrushProperty = AvaloniaProperty.Register<TokenCanvas, IBrush?>(nameof(LineBrush));
    public static readonly StyledProperty<IBrush?> SurfaceBrushProperty = AvaloniaProperty.Register<TokenCanvas, IBrush?>(nameof(SurfaceBrush));
    public static readonly StyledProperty<IBrush?> AccentBrushProperty = AvaloniaProperty.Register<TokenCanvas, IBrush?>(nameof(AccentBrush));
    public static readonly StyledProperty<IBrush?> SuccessBrushProperty = AvaloniaProperty.Register<TokenCanvas, IBrush?>(nameof(SuccessBrush));
    public static readonly StyledProperty<IBrush?> WarningBrushProperty = AvaloniaProperty.Register<TokenCanvas, IBrush?>(nameof(WarningBrush));
    public static readonly StyledProperty<IBrush?> DangerBrushProperty = AvaloniaProperty.Register<TokenCanvas, IBrush?>(nameof(DangerBrush));
    public static readonly StyledProperty<IBrush?> SecondaryBrushProperty = AvaloniaProperty.Register<TokenCanvas, IBrush?>(nameof(SecondaryBrush));

    static TokenCanvas() => AffectsRender<TokenCanvas>(TextBrushProperty, MutedBrushProperty, LineBrushProperty, SurfaceBrushProperty, AccentBrushProperty,
        SuccessBrushProperty, WarningBrushProperty, DangerBrushProperty, SecondaryBrushProperty);

    protected TokenCanvas()
    {
        this.Token(TextBrushProperty, ArcDesignTokenKind.Foreground).Token(MutedBrushProperty, ArcDesignTokenKind.TextMuted)
            .Token(LineBrushProperty, ArcDesignTokenKind.Border).Token(SurfaceBrushProperty, ArcDesignTokenKind.SurfaceRaised)
            .Token(AccentBrushProperty, ArcDesignTokenKind.Accent).Token(SuccessBrushProperty, ArcDesignTokenKind.Success)
            .Token(WarningBrushProperty, ArcDesignTokenKind.Warning).Token(DangerBrushProperty, ArcDesignTokenKind.Danger)
            .Token(SecondaryBrushProperty, ArcDesignTokenKind.TextSecondary);
    }

    public IBrush? TextBrush { get => GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public IBrush? MutedBrush { get => GetValue(MutedBrushProperty); set => SetValue(MutedBrushProperty, value); }
    public IBrush? LineBrush { get => GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public IBrush? SurfaceBrush { get => GetValue(SurfaceBrushProperty); set => SetValue(SurfaceBrushProperty, value); }
    public IBrush? AccentBrush { get => GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }
    public IBrush? SuccessBrush { get => GetValue(SuccessBrushProperty); set => SetValue(SuccessBrushProperty, value); }
    public IBrush? WarningBrush { get => GetValue(WarningBrushProperty); set => SetValue(WarningBrushProperty, value); }
    public IBrush? DangerBrush { get => GetValue(DangerBrushProperty); set => SetValue(DangerBrushProperty, value); }
    public IBrush? SecondaryBrush { get => GetValue(SecondaryBrushProperty); set => SetValue(SecondaryBrushProperty, value); }
}

/// <summary>Colours the drawn components use: the data palette and the token brushes of the canvas.</summary>
internal static class UiPaint
{
    public static Color Palette(int i) => Color.Parse(PluginUiProtocol.Palette[((i % 8) + 8) % 8]);

    public static IBrush Tone(string? tone, Control owner)
    {
        var c = owner as TokenCanvas;
        return tone switch
        {
            "accent" => c?.AccentBrush ?? new SolidColorBrush(Color.Parse("#0a84ff")),
            "success" => c?.SuccessBrush ?? new SolidColorBrush(Color.Parse("#30d158")),
            "warning" => c?.WarningBrush ?? new SolidColorBrush(Color.Parse("#ff9f0a")),
            "danger" => c?.DangerBrush ?? new SolidColorBrush(Color.Parse("#ff453a")),
            _ => c?.SecondaryBrush ?? Brushes.Gray,
        };
    }

    public static IBrush Series(JsonObject s, int index, Control owner)
    {
        if (s["color"] is JsonValue c && c.TryGetValue<double>(out var n)) return new SolidColorBrush(Palette((int)n));
        if (s["color"] is JsonValue h && h.TryGetValue<string>(out var hex) && hex.Length == 7 && hex[0] == '#' && Color.TryParse(hex, out var col)) return new SolidColorBrush(col);
        if (s["tone"]?.GetValue<string>() is { } tone) return Tone(tone, owner);
        return new SolidColorBrush(Palette(index));
    }

    public static IBrush Text(Control owner) => (owner as TokenCanvas)?.TextBrush ?? Brushes.Gray;

    public static IBrush Muted(Control owner) => (owner as TokenCanvas)?.MutedBrush ?? Brushes.Gray;

    public static IBrush Line(Control owner) => (owner as TokenCanvas)?.LineBrush ?? new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));

    public static IBrush Surface(Control owner) => (owner as TokenCanvas)?.SurfaceBrush ?? Brushes.White;
}

/// <summary>
/// The <c>Chart</c> component (04 §7): line, area, bar, stacked bar and donut drawn from the same spec the web draws, with the
/// same tick algorithm (<see cref="PluginUiProtocol.NiceTicks"/>). Hover shows the values at the nearest x locally; a click
/// selects a datum; a drag selects a range when zoom is on.
/// </summary>
internal sealed class ChartControl : TokenCanvas
{
    private readonly JsonObject _spec;
    private readonly List<JsonObject> _rows;
    private readonly string _locale;
    private readonly Action<JsonObject, string>? _select;
    private readonly Action<JsonNode?, JsonNode?>? _range;
    private readonly HashSet<string> _hidden = [];
    private int? _hover;
    private (int A, int B)? _drag;

    public ChartControl(JsonObject spec, List<JsonObject> rows, string locale, Action<JsonObject, string>? select, Action<JsonNode?, JsonNode?>? range)
    {
        _spec = spec;
        _rows = rows;
        _locale = locale;
        _select = select;
        _range = range;
        Height = spec["height"] is JsonValue hv && hv.TryGetValue<double>(out var h) ? h : 240;
        ClipToBounds = true;
        Focusable = true;
    }

    private string Kind => _spec["kind"]?.GetValue<string>() ?? "line";
    private string XKey => _spec["x"]?["key"]?.GetValue<string>() ?? "x";
    private string? XType => _spec["x"]?["type"]?.GetValue<string>();
    private List<JsonObject> Series => (_spec["series"] as JsonArray ?? []).OfType<JsonObject>().Where(s => !_hidden.Contains(s["key"]?.GetValue<string>() ?? "")).ToList();
    private IReadOnlyList<JsonObject> AllSeries => (_spec["series"] as JsonArray ?? []).OfType<JsonObject>().ToList();

    private static double Num(JsonNode? n) => PluginUiProtocol.ToDouble(n);

    private const double L = 44, T = 8, B = 22;
    private double R => AllSeries.Any(s => s["axis"]?.GetValue<string>() == "right") ? 44 : 12;
    private bool Bar => Kind is "bar" or "stackedBar";
    private bool Stacked => Kind == "stackedBar" || (Kind == "area" && _spec["stacked"] is JsonValue sv && sv.GetValueKind() == System.Text.Json.JsonValueKind.True);

    private double X(int i, double w)
    {
        if (Bar) return L + (i + 0.5) / Math.Max(1, _rows.Count) * w;
        if (XType is "time" or "number")
        {
            var xs = _rows.Select(XValue).ToList();
            var lo = xs.Min();
            var hi = xs.Max();
            return L + (hi == lo ? w / 2 : (XValue(_rows[i]) - lo) / (hi - lo) * w);
        }
        return L + (_rows.Count <= 1 ? w / 2 : (double)i / (_rows.Count - 1) * w);
    }

    private double XValue(JsonObject r) => XType == "time"
        ? DateTimeOffset.TryParse(r[XKey]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToUnixTimeMilliseconds() : 0
        : Num(r[XKey]);

    public override void Render(DrawingContext ctx)
    {
        var size = Bounds.Size;
        var text = UiPaint.Text(this);
        var muted = UiPaint.Muted(this);
        var line = UiPaint.Line(this);
        var series = Series;
        if (_rows.Count == 0 || AllSeries.Count == 0) return;
        if (Kind == "donut")
        {
            RenderDonut(ctx, size, text);
            return;
        }
        var w = size.Width - L - R;
        var h = size.Height - T - B;
        double Sum(JsonObject r, int upTo, List<JsonObject> list) => list.Take(upTo + 1).Sum(s => double.IsNaN(Num(r[s["key"]!.GetValue<string>()])) ? 0 : Num(r[s["key"]!.GetValue<string>()]));
        (double, double) Extent(List<JsonObject> list)
        {
            var lo = double.PositiveInfinity;
            var hi = double.NegativeInfinity;
            foreach (var r in _rows)
                for (var i = 0; i < list.Count; i++)
                {
                    var v = Stacked ? Sum(r, i, list) : Num(r[list[i]["key"]!.GetValue<string>()]);
                    if (double.IsNaN(v)) continue;
                    lo = Math.Min(lo, v);
                    hi = Math.Max(hi, v);
                }
            return double.IsInfinity(lo) ? (0, 1) : (Math.Min(0, lo), hi);
        }
        var left = series.Where(s => s["axis"]?.GetValue<string>() != "right").ToList();
        var right = series.Where(s => s["axis"]?.GetValue<string>() == "right").ToList();
        var (lMin, lMax) = Extent(left);
        var ticks = PluginUiProtocol.NiceTicks(_spec["y"]?["min"] is JsonValue mn && mn.TryGetValue<double>(out var m1) ? m1 : lMin,
            _spec["y"]?["max"] is JsonValue mx && mx.TryGetValue<double>(out var m2) ? m2 : lMax, _spec["y"]?["ticks"] is JsonValue tc && tc.TryGetValue<int>(out var t) ? t : 4);
        var yLo = ticks[0];
        var yHi = ticks[^1];
        double Y(double v) => T + h - (v - yLo) / (yHi - yLo == 0 ? 1 : yHi - yLo) * h;
        var rTicks = right.Count > 0 ? PluginUiProtocol.NiceTicks(Extent(right).Item1, Extent(right).Item2) : [];
        double YR(double v) => T + h - (v - rTicks[0]) / (rTicks[^1] - rTicks[0] == 0 ? 1 : rTicks[^1] - rTicks[0]) * h;
        var yFormat = _spec["y"]?["format"]?.GetValue<string>() ?? "compact";
        var typeface = new Typeface(FontFamily.Default);
        void Label(string s, Point at, IBrush brush, TextAlignment align = TextAlignment.Left)
        {
            var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, brush);
            var x = align == TextAlignment.Right ? at.X - ft.Width : align == TextAlignment.Center ? at.X - ft.Width / 2 : at.X;
            ctx.DrawText(ft, new Point(x, at.Y - ft.Height / 2));
        }
        var gridPen = new Pen(line, 1, new DashStyle([2, 3], 0));
        foreach (var tick in ticks)
        {
            ctx.DrawLine(tick == 0 ? new Pen(line, 1) : gridPen, new Point(L, Y(tick)), new Point(size.Width - R, Y(tick)));
            Label(PluginUiProtocol.Format(yFormat, JsonValue.Create(tick), _locale), new Point(L - 6, Y(tick)), muted, TextAlignment.Right);
        }
        foreach (var tick in rTicks)
            Label(PluginUiProtocol.Format(_spec["yRight"]?["format"]?.GetValue<string>() ?? "compact", JsonValue.Create(tick), _locale), new Point(size.Width - R + 6, YR(tick)), muted);
        var xLabels = Math.Min(_rows.Count, Math.Max(2, (int)(w / 90)));
        var xFormat = _spec["x"]?["format"]?.GetValue<string>() ?? (XType == "time" ? "date" : null);
        for (var k = 0; k < xLabels; k++)
        {
            var i = (int)Math.Round(k * (_rows.Count - 1) / (double)Math.Max(1, xLabels - 1));
            Label(PluginUiProtocol.Format(xFormat, _rows[i][XKey], _locale), new Point(X(i, w), size.Height - 8), muted, TextAlignment.Center);
        }
        if (Bar)
        {
            var bw = w / _rows.Count * 0.7;
            for (var i = 0; i < _rows.Count; i++)
            {
                double baseV = 0;
                for (var si = 0; si < series.Count; si++)
                {
                    var v = Num(_rows[i][series[si]["key"]!.GetValue<string>()]);
                    if (double.IsNaN(v)) v = 0;
                    var y0 = Stacked ? baseV : 0;
                    var y1 = Stacked ? baseV + v : v;
                    if (Stacked) baseV += v;
                    var x = X(i, w) - bw / 2 + (Stacked ? 0 : si * bw / series.Count);
                    var width = Stacked ? bw : bw / series.Count;
                    var rect = new Rect(x, Y(Math.Max(y0, y1)), Math.Max(1, width - 1), Math.Max(0, Math.Abs(Y(y0) - Y(y1))));
                    ctx.DrawRectangle(UiPaint.Series(series[si], AllSeries.ToList().IndexOf(series[si]), this), null, rect, 2, 2);
                }
            }
        }
        else
        {
            for (var si = 0; si < series.Count; si++)
            {
                var s = series[si];
                var key = s["key"]!.GetValue<string>();
                var brush = UiPaint.Series(s, AllSeries.ToList().IndexOf(s), this);
                var scale = s["axis"]?.GetValue<string>() == "right" ? (Func<double, double>)YR : Y;
                var gaps = _spec["gaps"]?.GetValue<string>() ?? "break";
                var geometry = new StreamGeometry();
                var points = new List<Point>();
                using (var g = geometry.Open())
                {
                    var pen = false;
                    for (var i = 0; i < _rows.Count; i++)
                    {
                        var v = Stacked ? Sum(_rows[i], si, series) : Num(_rows[i][key]);
                        if (double.IsNaN(v))
                        {
                            if (gaps == "zero") v = 0;
                            else
                            {
                                if (gaps != "connect" && pen)
                                {
                                    g.EndFigure(false);
                                    pen = false;
                                }
                                continue;
                            }
                        }
                        var p = new Point(X(i, w), scale(v));
                        points.Add(p);
                        if (!pen)
                        {
                            g.BeginFigure(p, false);
                            pen = true;
                        }
                        else if (_spec["curve"]?.GetValue<string>() == "step")
                        {
                            g.LineTo(new Point(p.X, points[^2].Y));
                            g.LineTo(p);
                        }
                        else g.LineTo(p);
                    }
                    if (pen) g.EndFigure(false);
                }
                if (Kind == "area" && points.Count > 1)
                {
                    var area = new StreamGeometry();
                    using (var g = area.Open())
                    {
                        g.BeginFigure(new Point(points[0].X, Y(Math.Max(0, yLo))), true);
                        foreach (var p in points) g.LineTo(p);
                        g.LineTo(new Point(points[^1].X, Y(Math.Max(0, yLo))));
                        g.EndFigure(true);
                    }
                    ctx.DrawGeometry(brush is ISolidColorBrush sc ? new SolidColorBrush(sc.Color, 0.15) : brush, null, area);
                }
                var dashed = s["dashed"] is JsonValue dv && dv.GetValueKind() == System.Text.Json.JsonValueKind.True;
                ctx.DrawGeometry(null, new Pen(brush, 2, dashed ? new DashStyle([5, 4], 0) : null, PenLineCap.Round, PenLineJoin.Round), geometry);
                var showPoints = _spec["points"]?.GetValue<string>() is "always" || (_spec["points"]?.GetValue<string>() is not "never" && _rows.Count <= 24);
                if (showPoints) foreach (var p in points) ctx.DrawEllipse(brush, null, p, 2.5, 2.5);
            }
        }
        foreach (var mark in (_spec["marks"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var brush = UiPaint.Tone(mark["tone"]?.GetValue<string>() ?? "warning", this);
            var pen = new Pen(brush, 1, new DashStyle([4, 3], 0));
            if (mark["y"] is JsonValue my && my.TryGetValue<double>(out var yv))
            {
                ctx.DrawLine(pen, new Point(L, Y(yv)), new Point(size.Width - R, Y(yv)));
                if (mark["label"]?.GetValue<string>() is { } l) Label(l, new Point(size.Width - R - 4, Y(yv) - 7), brush, TextAlignment.Right);
            }
            else if (mark["x"] is { } mxv)
            {
                var at = _rows.FindIndex(r => JsonNode.DeepEquals(r[XKey], mxv));
                if (at < 0) continue;
                ctx.DrawLine(pen, new Point(X(at, w), T), new Point(X(at, w), T + h));
                if (mark["label"]?.GetValue<string>() is { } l) Label(l, new Point(X(at, w) + 4, T + 8), brush);
            }
        }
        if (_hover is { } hi && !Bar)
        {
            ctx.DrawLine(new Pen(line, 1), new Point(X(hi, w), T), new Point(X(hi, w), T + h));
            var lines = new List<string> { PluginUiProtocol.Format(xFormat ?? (XType == "time" ? "datetime" : null), _rows[hi][XKey], _locale) };
            lines.AddRange(series.Select(s => $"{s["label"]?.GetValue<string>() ?? s["key"]!.GetValue<string>()}: {PluginUiProtocol.Format(s["axis"]?.GetValue<string>() == "right" ? _spec["yRight"]?["format"]?.GetValue<string>() : _spec["y"]?["format"]?.GetValue<string>(), _rows[hi][s["key"]!.GetValue<string>()], _locale)}"));
            var ft = new FormattedText(string.Join("\n", lines), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, text);
            var bx = Math.Min(X(hi, w) + 8, size.Width - ft.Width - 12);
            var surface = UiPaint.Surface(this);
            ctx.DrawRectangle(surface, new Pen(line, 1), new Rect(bx, T, ft.Width + 12, ft.Height + 8), 6, 6);
            ctx.DrawText(ft, new Point(bx + 6, T + 4));
        }
        if (_drag is { } d)
        {
            var a = X(d.A, w);
            var b = X(d.B, w);
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 10, 132, 255)), null, new Rect(Math.Min(a, b), T, Math.Abs(b - a), h));
        }
    }

    private void RenderDonut(DrawingContext ctx, Size size, IBrush text)
    {
        var s0 = AllSeries[0]["key"]?.GetValue<string>() ?? "value";
        var total = _rows.Sum(r => double.IsNaN(Num(r[s0])) ? 0 : Num(r[s0]));
        if (total <= 0) total = 1;
        var r = Math.Min(size.Height, 200) / 2 - 8;
        var c = new Point(r + 8, size.Height / 2);
        var angle = -Math.PI / 2;
        for (var i = 0; i < _rows.Count; i++)
        {
            var v = Num(_rows[i][s0]);
            if (double.IsNaN(v) || v <= 0) continue;
            var a = v / total * Math.PI * 2;
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(new Point(c.X + r * Math.Cos(angle), c.Y + r * Math.Sin(angle)), false);
                angle += a;
                gc.ArcTo(new Point(c.X + r * Math.Cos(angle), c.Y + r * Math.Sin(angle)), new Size(r, r), 0, a > Math.PI, SweepDirection.Clockwise);
                gc.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(UiPaint.Palette(i)), r * 0.35), g);
        }
        var typeface = new Typeface(FontFamily.Default);
        var ft = new FormattedText(PluginUiProtocol.Format(_spec["y"]?["format"]?.GetValue<string>() ?? "compact", JsonValue.Create(total), _locale),
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 14, text);
        ctx.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
        var labelKey = _spec["x"]?["key"]?.GetValue<string>() ?? "label";
        for (var i = 0; i < _rows.Count; i++)
        {
            var y = 10 + i * 18;
            ctx.DrawRectangle(new SolidColorBrush(UiPaint.Palette(i)), null, new Rect(c.X + r + 24, y, 10, 10), 2, 2);
            var lt = new FormattedText($"{_rows[i][labelKey]}  {PluginUiProtocol.Format(_spec["y"]?["format"]?.GetValue<string>() ?? "number", _rows[i][s0], _locale)}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 12, text);
            ctx.DrawText(lt, new Point(c.X + r + 40, y - 2));
        }
    }

    private int Index(Point p)
    {
        var w = Bounds.Width - L - R;
        var best = 0;
        for (var i = 1; i < _rows.Count; i++)
            if (Math.Abs(X(i, w) - p.X) < Math.Abs(X(best, w) - p.X)) best = i;
        return best;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_rows.Count == 0 || Kind == "donut") return;
        var i = Index(e.GetPosition(this));
        _hover = i;
        if (_drag is { } d) _drag = (d.A, i);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        _drag = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_rows.Count == 0) return;
        if (_spec["zoom"] is JsonValue z && z.GetValueKind() == System.Text.Json.JsonValueKind.True) _drag = (Index(e.GetPosition(this)), Index(e.GetPosition(this)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_rows.Count == 0) return;
        if (_drag is { } d && d.A != d.B) _range?.Invoke(_rows[Math.Min(d.A, d.B)][XKey]?.DeepClone(), _rows[Math.Max(d.A, d.B)][XKey]?.DeepClone());
        else if (Series.FirstOrDefault() is { } s) _select?.Invoke((JsonObject)_rows[Index(e.GetPosition(this))].DeepClone(), s["key"]!.GetValue<string>());
        _drag = null;
        InvalidateVisual();
    }

    /// <summary>Clicking a legend entry shows or hides its series (local, no round trip).</summary>
    public void Toggle(string key)
    {
        if (!_hidden.Remove(key)) _hidden.Add(key);
        InvalidateVisual();
    }
}

/// <summary>A sparkline, a gauge, a QR code and the Canvas display list (02 §11): small drawn components.</summary>
internal sealed class DrawnControl : TokenCanvas
{
    private readonly Action<DrawingContext, Size, Control> _draw;
    private readonly Func<Point, JsonObject?>? _hit;
    private readonly Action<JsonObject>? _onHit;

    public DrawnControl(double width, double height, Action<DrawingContext, Size, Control> draw, Func<Point, JsonObject?>? hit = null, Action<JsonObject>? onHit = null)
    {
        if (!double.IsNaN(width)) Width = width;
        Height = height;
        _draw = draw;
        _hit = hit;
        _onHit = onHit;
    }

    public override void Render(DrawingContext context) => _draw(context, Bounds.Size, this);

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_hit?.Invoke(e.GetPosition(this)) is { } hit) _onHit?.Invoke(hit);
    }

    public static DrawnControl Sparkline(List<double> data, string? tone, bool area) => new(120, 32, (ctx, size, owner) =>
    {
        if (data.Count == 0) return;
        var lo = data.Min();
        var hi = data.Max();
        var pts = data.Select((v, i) => new Point(i / Math.Max(1.0, data.Count - 1) * size.Width, size.Height - 2 - (v - lo) / (hi - lo == 0 ? 1 : hi - lo) * (size.Height - 4))).ToList();
        var brush = UiPaint.Tone(tone ?? "accent", owner);
        var g = new StreamGeometry();
        using (var gc = g.Open())
        {
            gc.BeginFigure(pts[0], false);
            foreach (var p in pts.Skip(1)) gc.LineTo(p);
            gc.EndFigure(false);
        }
        if (area && brush is ISolidColorBrush sc)
        {
            var a = new StreamGeometry();
            using (var gc = a.Open())
            {
                gc.BeginFigure(new Point(0, size.Height), true);
                foreach (var p in pts) gc.LineTo(p);
                gc.LineTo(new Point(size.Width, size.Height));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(new SolidColorBrush(sc.Color, 0.15), null, a);
        }
        ctx.DrawGeometry(null, new Pen(brush, 1.5), g);
    });


    public static DrawnControl Gauge(double value, double max, string? label, string? tone, string locale) => new(140, 90, (ctx, size, owner) =>
    {
        var ratio = Math.Clamp(value / (max == 0 ? 1 : max), 0, 1);
        var c = new Point(size.Width / 2, size.Height - 20);
        var r = Math.Min(size.Width / 2, size.Height - 20) - 8;
        StreamGeometry Arc(double from, double to)
        {
            var g = new StreamGeometry();
            using var gc = g.Open();
            var a = Math.PI * (1 + from);
            var b = Math.PI * (1 + to);
            gc.BeginFigure(new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)), false);
            gc.ArcTo(new Point(c.X + r * Math.Cos(b), c.Y + r * Math.Sin(b)), new Size(r, r), 0, false, SweepDirection.Clockwise);
            gc.EndFigure(false);
            return g;
        }
        ctx.DrawGeometry(null, new Pen(UiPaint.Line(owner), 8, lineCap: PenLineCap.Round), Arc(0, 1));
        if (ratio > 0) ctx.DrawGeometry(null, new Pen(UiPaint.Tone(tone ?? "accent", owner), 8, lineCap: PenLineCap.Round), Arc(0, ratio));
        var typeface = new Typeface(FontFamily.Default);
        var ft = new FormattedText(PluginUiProtocol.Format("number", JsonValue.Create(value), locale), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 15, UiPaint.Text(owner));
        ctx.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height - 2));
        if (label is not null)
        {
            var lt = new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 10, UiPaint.Muted(owner));
            ctx.DrawText(lt, new Point(c.X - lt.Width / 2, c.Y + 4));
        }
    });

    public static Control Qr(string value, double size)
    {
        var matrix = QrEncoder.Encode(value);
        if (matrix is null) return new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
        var n = matrix.GetLength(0);
        return new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(4),
            Child = new DrawnControl(size, size, (ctx, s, _) =>
            {
                var cell = s.Width / (n + 8);
                for (var r = 0; r < n; r++)
                    for (var c = 0; c < n; c++)
                        if (matrix[r, c]) ctx.FillRectangle(Brushes.Black, new Rect((c + 4) * cell, (r + 4) * cell, cell + 0.2, cell + 0.2));
            }),
        };
    }

    /// <summary>The Canvas display list: rect, circle, line, path, text and groups; colours are tokens; hit targets fire actions.</summary>
    public static Control Canvas(JsonArray ops, JsonArray? viewBox, double height, Action<JsonObject> onHit)
    {
        var vb = viewBox?.Select(v => v is JsonValue d && d.TryGetValue<double>(out var x) ? x : 0).ToArray();
        var hits = new List<(Rect Box, JsonObject Hit)>();
        return new DrawnControl(double.NaN, height, (ctx, size, owner) =>
        {
            hits.Clear();
            var sx = vb is { Length: 4 } && vb[2] > 0 ? size.Width / vb[2] : 1;
            var sy = vb is { Length: 4 } && vb[3] > 0 ? size.Height / vb[3] : 1;
            var scale = Math.Min(sx, sy);
            var ox = vb is { Length: 4 } ? -vb[0] * scale + (size.Width - (vb[2] * scale)) / 2 : 0;
            var oy = vb is { Length: 4 } ? -vb[1] * scale + (size.Height - (vb[3] * scale)) / 2 : 0;
            using var _ = ctx.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(ox, oy));
            void Draw(JsonArray list)
            {
                foreach (var op in list.OfType<JsonObject>())
                {
                    IBrush? fill = Paint(op["fill"]?.GetValue<string>(), owner);
                    var strokeBrush = Paint(op["stroke"]?.GetValue<string>(), owner);
                    var pen = strokeBrush is null ? null : new Pen(strokeBrush, op["strokeWidth"] is JsonValue sw && sw.TryGetValue<double>(out var swv) ? swv : 1);
                    double[] Nums(string k) => (op[k] as JsonArray ?? []).Select(v => v is JsonValue d && d.TryGetValue<double>(out var x) ? x : 0).ToArray();
                    Rect? box = null;
                    if (op["group"] is JsonArray g) Draw(g);
                    else if (Nums("rect") is { Length: 4 } r)
                    {
                        box = new Rect(r[0], r[1], r[2], r[3]);
                        ctx.DrawRectangle(fill, pen, box.Value);
                    }
                    else if (Nums("circle") is { Length: 3 } c)
                    {
                        box = new Rect(c[0] - c[2], c[1] - c[2], c[2] * 2, c[2] * 2);
                        ctx.DrawEllipse(fill, pen, new Point(c[0], c[1]), c[2], c[2]);
                    }
                    else if (Nums("line") is { Length: 4 } l) ctx.DrawLine(pen ?? new Pen(UiPaint.Line(owner), 1), new Point(l[0], l[1]), new Point(l[2], l[3]));
                    else if (op["path"]?.GetValue<string>() is { } path)
                    {
                        try
                        {
                            var geo = StreamGeometry.Parse(path);
                            box = geo.Bounds;
                            ctx.DrawGeometry(fill, pen, geo);
                        }
                        catch (FormatException)
                        {
                        }
                    }
                    else if (op["text"] is { } t)
                    {
                        var at = Nums("at");
                        var ft = new FormattedText(t.ToString(), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default),
                            op["size"]?.GetValue<string>() switch { "lg" => 16, "sm" => 10, _ => 12 }, fill ?? UiPaint.Text(owner));
                        var x = at.Length > 0 ? at[0] : 0;
                        var anchor = op["anchor"]?.GetValue<string>();
                        if (anchor == "middle") x -= ft.Width / 2;
                        else if (anchor == "end") x -= ft.Width;
                        var y = (at.Length > 1 ? at[1] : 0) - ft.Height / 2;
                        box = new Rect(x, y, ft.Width, ft.Height);
                        ctx.DrawText(ft, new Point(x, y));
                    }
                    if (box is { } b && op["hit"] is JsonObject hit) hits.Add((b.TransformToAABB(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(ox, oy)), hit));
                }
            }
            Draw(ops);
        }, p => hits.LastOrDefault(h => h.Box.Contains(p)).Hit, onHit);
    }

    private static IBrush? Paint(string? color, Control owner)
    {
        if (color is null) return null;
        if (color.Length == 7 && color[0] == '#' && Color.TryParse(color, out var c)) return new SolidColorBrush(c);
        return color switch
        {
            "surface" => UiPaint.Surface(owner),
            "text" => UiPaint.Text(owner),
            "muted" => UiPaint.Muted(owner),
            "border" => UiPaint.Line(owner),
            _ when color.Length == 1 && char.IsDigit(color[0]) => new SolidColorBrush(UiPaint.Palette(color[0] - '0')),
            _ => UiPaint.Tone(color, owner),
        };
    }
}
