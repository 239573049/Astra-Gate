using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Astra.Desktop.Components.Kit;

/// <summary>One gradient stop of a brand mark (<c>#AARRGGBB</c>, stop opacity already folded into alpha).</summary>
internal sealed record BrandStop(double Offset, string Color);

/// <summary>
/// A linear gradient of a brand mark or avatar tile. <paramref name="BoundingBox"/> coordinates are fractions of the shape's
/// bounds (SVG <c>objectBoundingBox</c>); otherwise they are in the mark's 24x24 user space.
/// </summary>
internal sealed record BrandGradient(bool BoundingBox, double X1, double Y1, double X2, double Y2, BrandStop[] Stops);

/// <summary>One filled path of a brand mark. A null <paramref name="Fill"/> with no gradient means "the current foreground".</summary>
internal readonly record struct BrandLayer(string Path, string? Fill, int Gradient, bool EvenOdd, double Opacity);

/// <summary>The tile lobehub draws behind a brand's mark in its Avatar component.</summary>
internal sealed record BrandAvatarSpec(string Background, string Foreground, double Multiple, string Variant, BrandGradient? BackgroundGradient);

/// <summary>
/// A flattened brand mark: filled paths in a 24x24 view box (the form every @lobehub/icons mark takes), parsed lazily and kept
/// for the process. Data comes from <c>BrandMarkData.g.cs</c>; AOT-safe (path data through <see cref="StreamGeometry"/>, no SVG parser).
/// </summary>
internal sealed class BrandMark
{
    private readonly BrandLayer[] _layers;
    private readonly BrandGradient[] _gradients;
    private Geometry[]? _geometry;
    private IBrush?[]? _brushes;

    public BrandMark(BrandLayer[] layers, BrandGradient[] gradients)
    {
        _layers = layers;
        _gradients = gradients;
    }

    /// <summary>Number of filled paths.</summary>
    public int Count => _layers.Length;

    /// <summary>Layer <paramref name="i"/>'s geometry (parsed once).</summary>
    public Geometry GeometryOf(int i)
    {
        _geometry ??= new Geometry[_layers.Length];
        if (_geometry[i] is { } cached) return cached;
        // F0 = even-odd, F1 = non-zero: the fill rule is part of the path markup
        return _geometry[i] = StreamGeometry.Parse((_layers[i].EvenOdd ? "F0 " : "F1 ") + _layers[i].Path);
    }

    /// <summary>Layer <paramref name="i"/>'s opacity.</summary>
    public double OpacityOf(int i) => _layers[i].Opacity;

    /// <summary>The brush of layer <paramref name="i"/>: its own color or gradient, or <paramref name="foreground"/> for current-color layers.</summary>
    public IBrush? BrushOf(int i, IBrush? foreground)
    {
        var layer = _layers[i];
        if (layer.Gradient < 0 && layer.Fill is null) return foreground;
        _brushes ??= new IBrush?[_layers.Length];
        if (_brushes[i] is { } cached) return cached;
        IBrush brush = layer.Gradient >= 0
            ? MakeGradient(_gradients[layer.Gradient], GeometryOf(i).Bounds)
            : new ImmutableSolidColorBrush(Color.Parse(layer.Fill!));
        return _brushes[i] = brush;
    }

    /// <summary>An Avalonia gradient brush; user-space coordinates are converted to fractions of <paramref name="bounds"/>.</summary>
    public static LinearGradientBrush MakeGradient(BrandGradient g, Rect bounds)
    {
        double x1 = g.X1, y1 = g.Y1, x2 = g.X2, y2 = g.Y2;
        if (!g.BoundingBox && bounds.Width > 0 && bounds.Height > 0)
        {
            x1 = (x1 - bounds.X) / bounds.Width;
            x2 = (x2 - bounds.X) / bounds.Width;
            y1 = (y1 - bounds.Y) / bounds.Height;
            y2 = (y2 - bounds.Y) / bounds.Height;
        }
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(x1, y1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(x2, y2, RelativeUnit.Relative),
        };
        foreach (var stop in g.Stops) brush.GradientStops.Add(new GradientStop(Color.Parse(stop.Color), stop.Offset));
        return brush;
    }
}

/// <summary>Draws a <see cref="BrandMark"/> scaled to fit and centered; current-color layers use <see cref="Foreground"/>.</summary>
internal sealed class BrandMarkView : Control
{
    public static readonly StyledProperty<IBrush?> ForegroundProperty = AvaloniaProperty.Register<BrandMarkView, IBrush?>(nameof(Foreground));

    public BrandMarkView(BrandMark mark)
    {
        Mark = mark;
        IsHitTestVisible = false;
        Focusable = false;
    }

    static BrandMarkView() => AffectsRender<BrandMarkView>(ForegroundProperty);

    public BrandMark Mark { get; }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) return;
        var scale = side / 24.0;
        var matrix = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        using var _ = context.PushTransform(matrix);
        for (var i = 0; i < Mark.Count; i++)
        {
            var brush = Mark.BrushOf(i, Foreground);
            if (brush is null) continue;
            var opacity = Mark.OpacityOf(i);
            if (opacity < 1)
            {
                using var __ = context.PushOpacity(opacity);
                context.DrawGeometry(brush, null, Mark.GeometryOf(i));
            }
            else
            {
                context.DrawGeometry(brush, null, Mark.GeometryOf(i));
            }
        }
    }
}
