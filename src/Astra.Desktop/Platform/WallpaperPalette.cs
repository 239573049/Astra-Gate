namespace Astra.Desktop.Platform;

/// <summary>One radial color field of the web's <c>.wallpaper</c>: an ellipse (radii and center are fractions of the box) fading to transparent at 72 %.</summary>
public readonly record struct WallpaperLayer(double CenterX, double CenterY, double RadiusX, double RadiusY, uint Argb);

/// <summary>The three glass materials of web/src/styles/index.css, as straight ARGB values.</summary>
public readonly record struct GlassColors(uint Tint, uint Edge, uint Highlight, uint Shadow);

/// <summary>
/// The wallpaper gradients and the glass tints (web <c>.wallpaper</c>, <c>.glass</c>, <c>--canvas</c>, <c>--glass-*</c>), computed with no
/// Avalonia dependency. <c>--accent-2</c> is <c>oklch(from var(--accent) l c calc(h + 70))</c>.
/// </summary>
public static class WallpaperPalette
{
    /// <summary>Where each gradient fades out (CSS <c>transparent 72%</c>).</summary>
    public const double FadeStop = 0.72;

    public static uint Canvas(bool dark) => dark ? 0xFF161618u : 0xFFEEF0F5u;

    public static IReadOnlyList<WallpaperLayer> Layers(uint accentRgb, bool dark)
    {
        var accent2 = RotateHue(accentRgb, 70);
        // color-mix(in oklab, <accent> 34%, transparent) is the accent at 34 % alpha.
        return
        [
            new(0.06, 0.12, 0.55, 0.50, WithAlpha(accentRgb, 0.34)),
            new(0.14, 0.92, 0.45, 0.45, WithAlpha(accent2, 0.26)),
            new(0.96, 0.04, 0.40, 0.40, WithAlpha(accentRgb, 0.18)),
        ];
    }

    /// <summary>The <c>.glass</c> variables: light is white 55 % with a white edge, dark is #3a3a3e 52 %.</summary>
    public static GlassColors Glass(bool dark) => dark
        ? new(WithAlpha(0x3A3A3Eu, 0.52), WithAlpha(0xFFFFFFu, 0.14), WithAlpha(0xFFFFFFu, 0.20), WithAlpha(0x000000u, 0.45))
        : new(WithAlpha(0xFFFFFFu, 0.55), WithAlpha(0xFFFFFFu, 0.65), WithAlpha(0xFFFFFFu, 0.85), WithAlpha(0x000000u, 0.12));

    /// <summary>The status card in the sidebar footer: <c>color-mix(--glass-tint 45%, transparent)</c>.</summary>
    public static uint StatusCardFill(bool dark) => WithAlpha(dark ? 0x3A3A3Eu : 0xFFFFFFu, 0.45);

    public static uint WithAlpha(uint rgb, double alpha)
        => ((uint)Math.Round(Math.Clamp(alpha, 0, 1) * 255) << 24) | (rgb & 0xFFFFFF);

    /// <summary>"#rrggbb" to 0xRRGGBB; null when malformed.</summary>
    public static uint? ParseHex(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#') return null;
        return uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : null;
    }

    /// <summary>Rotates an sRGB color's OKLCH hue by <paramref name="degrees"/>, keeping lightness and chroma (clamped into sRGB).</summary>
    public static uint RotateHue(uint rgb, double degrees)
    {
        var r = ToLinear(((rgb >> 16) & 0xFF) / 255.0);
        var g = ToLinear(((rgb >> 8) & 0xFF) / 255.0);
        var b = ToLinear((rgb & 0xFF) / 255.0);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        var lightness = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        var a = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        var bb = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        var chroma = Math.Sqrt(a * a + bb * bb);
        var hue = Math.Atan2(bb, a) + degrees * Math.PI / 180;
        a = chroma * Math.Cos(hue);
        bb = chroma * Math.Sin(hue);
        var l2 = lightness + 0.3963377774 * a + 0.2158037573 * bb;
        var m2 = lightness - 0.1055613458 * a - 0.0638541728 * bb;
        var s2 = lightness - 0.0894841775 * a - 1.2914855480 * bb;
        l2 = l2 * l2 * l2;
        m2 = m2 * m2 * m2;
        s2 = s2 * s2 * s2;
        var lr = 4.0767416621 * l2 - 3.3077115913 * m2 + 0.2309699292 * s2;
        var lg = -1.2684380046 * l2 + 2.6097574011 * m2 - 0.3413193965 * s2;
        var lb = -0.0041960863 * l2 - 0.7034186147 * m2 + 1.7076147010 * s2;
        return (ToByte(lr) << 16) | (ToByte(lg) << 8) | ToByte(lb);
    }

    private static double ToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    private static uint ToByte(double linear)
    {
        var c = Math.Clamp(linear, 0, 1);
        c = c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;
        return (uint)Math.Round(Math.Clamp(c, 0, 1) * 255);
    }
}
