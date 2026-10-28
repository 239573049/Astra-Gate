using Astra.Desktop.Theme;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Astra.Desktop.Platform.Glass;

/// <summary>
/// The web's <c>.wallpaper</c> (web/src/styles/index.css): soft accent-derived color fields over <c>--canvas</c> that the glass
/// sidebar sits on. Drawn directly (one pass, no child controls) and redrawn when the accent or the light / dark appearance changes.
/// </summary>
public sealed class WallpaperView : Control
{
    private ThemeService? _theme;

    public WallpaperView() => IsHitTestVisible = false;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _theme = AppServices.Current.Theme;
        _theme.AppearanceChanged += OnChanged;
        ActualThemeVariantChanged += OnVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_theme is not null) _theme.AppearanceChanged -= OnChanged;
        ActualThemeVariantChanged -= OnVariantChanged;
    }

    private void OnChanged() => InvalidateVisual();

    private void OnVariantChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var accent = WallpaperPalette.ParseHex(AppServices.Current.Theme.AccentHex) ?? 0x007AFF;
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.FromUInt32(WallpaperPalette.Canvas(dark))), bounds);
        // Painted bottom-up, as CSS lists the layers top-down with the canvas last.
        foreach (var layer in WallpaperPalette.Layers(accent, dark).Reverse())
        {
            var from = Color.FromUInt32(layer.Argb);
            var to = Color.FromArgb(0, from.R, from.G, from.B);
            var center = new RelativePoint(layer.CenterX, layer.CenterY, RelativeUnit.Relative);
            context.FillRectangle(new RadialGradientBrush
            {
                Center = center,
                GradientOrigin = center,
                RadiusX = new RelativeScalar(layer.RadiusX, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(layer.RadiusY, RelativeUnit.Relative),
                GradientStops = [new GradientStop(from, 0), new GradientStop(to, WallpaperPalette.FadeStop)],
            }, bounds);
        }
    }
}
