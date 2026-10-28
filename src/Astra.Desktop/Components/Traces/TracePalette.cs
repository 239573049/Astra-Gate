using Astra.Desktop.Core.Traces;
using Astra.Desktop.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Traces;

/// <summary>
/// The colours the trace views draw with, resolved for one control's theme: the web's CSS variables (<c>--label</c>, <c>--label-2</c>,
/// <c>--label-3</c>, <c>--separator</c>, <c>--surface-solid</c>, <c>--surface-muted</c>, <c>--accent</c>, <c>--accent-2</c>, <c>--green</c>,
/// <c>--red</c>, <c>--orange</c>, <c>--fill</c>) and the semantic mappings of traces/model.ts (<c>COLORS</c>, <c>PHASE_COLOR</c>) and session.tsx
/// (<c>BAR</c>). Arc tokens where Arc has the same value (accent, foreground, secondary text, status colours), the web's literals where it does
/// not (label-3, separator, surface-solid: index.css). <c>color-mix(in oklab, X p%, transparent)</c> is X at alpha p;
/// <c>color-mix(in oklab, A p%, B)</c> is approximated in sRGB.
/// </summary>
public sealed record TracePalette(bool Dark, Color Accent, Color Accent2, Color Label, Color Label2, Color Label3, Color Separator,
    Color SurfaceSolid, Color SurfaceMuted, Color Green, Color Red, Color Orange, Color TextMuted, Color Fill)
{
    /// <summary>The palette of <paramref name="owner"/>'s theme (call again after a theme or accent change).</summary>
    public static TracePalette For(Control owner)
    {
        var dark = owner.ActualThemeVariant == ThemeVariant.Dark;
        var accent = ArcTokens.GetColor(owner, ArcDesignTokenKind.Accent);
        if (accent.A == 0) accent = Color.FromRgb(0, 0x7a, 0xff);
        var rgb = ((uint)accent.R << 16) | ((uint)accent.G << 8) | accent.B;
        var a2 = WallpaperPalette.RotateHue(rgb, 70);
        var label = ArcTokens.GetColor(owner, ArcDesignTokenKind.Foreground);
        if (label.A == 0) label = dark ? Color.FromArgb(230, 255, 255, 255) : Color.FromArgb(224, 0, 0, 0);
        var label2 = ArcTokens.GetColor(owner, ArcDesignTokenKind.TextSecondary);
        if (label2.A == 0) label2 = WithAlpha(label, 0.55);
        var muted = ArcTokens.GetColor(owner, ArcDesignTokenKind.TextMuted);
        // index.css --green / --red / --orange (the web's --success / --danger / --warning point at them); Arc's status tokens are darker
        // shades in this theme, so the macOS literals are used for both themes.
        var green = dark ? Color.FromRgb(0x30, 0xd1, 0x58) : Color.FromRgb(0x34, 0xc7, 0x59);
        var red = dark ? Color.FromRgb(0xff, 0x45, 0x3a) : Color.FromRgb(0xff, 0x3b, 0x30);
        var orange = dark ? Color.FromRgb(0xff, 0x9f, 0x0a) : Color.FromRgb(0xff, 0x95, 0x00);
        var baseLabel = dark ? Colors.White : Colors.Black;
        return new TracePalette(
            dark, accent, Color.FromRgb((byte)(a2 >> 16), (byte)(a2 >> 8), (byte)a2), label, label2,
            WithAlpha(baseLabel, 0.3),
            WithAlpha(baseLabel, dark ? 0.1 : 0.09),
            dark ? Color.FromRgb(0x2a, 0x2a, 0x2d) : Colors.White,
            WithAlpha(label, label.A / 255.0 * 0.06),
            green, red, orange, muted.A == 0 ? WithAlpha(label, 0.4) : muted,
            Color.FromArgb((byte)Math.Round((dark ? 0.28 : 0.14) * 255), 120, 120, 128));
    }

    /// <summary><paramref name="c"/> with its alpha multiplied by <paramref name="alpha"/>.</summary>
    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(c.A * Math.Clamp(alpha, 0, 1)), c.R, c.G, c.B);

    /// <summary><c>color-mix(a p%, b)</c> (premultiplied blend in sRGB).</summary>
    public static Color Mix(Color a, double p, Color b)
    {
        var q = 1 - p;
        var aa = a.A / 255.0 * p;
        var ba = b.A / 255.0 * q;
        var alpha = aa + ba;
        if (alpha <= 0) return Colors.Transparent;
        byte Ch(byte x, byte y) => (byte)Math.Round((x * aa + y * ba) / alpha);
        return Color.FromArgb((byte)Math.Round(alpha * 255), Ch(a.R, b.R), Ch(a.G, b.G), Ch(a.B, b.B));
    }

    /// <summary>A brush of a colour.</summary>
    public static IBrush Brush(Color c) => new SolidColorBrush(c);

    /// <summary>The stroke of an edge (model.ts <c>COLORS</c>).</summary>
    public Color Edge(EdgeTone tone) => tone switch
    {
        EdgeTone.Flow => WithAlpha(Accent, 0.55),
        EdgeTone.Request => Label3,
        EdgeTone.Response => WithAlpha(Accent, 0.7),
        EdgeTone.Warn => WithAlpha(Orange, 0.8),
        EdgeTone.Error => Red,
        EdgeTone.Ok => Green,
        _ => Accent2,
    };

    /// <summary>A model phase (<c>PHASE_COLOR</c>).</summary>
    public Color Phase(PhaseKind kind) => kind switch
    {
        PhaseKind.Wait => WithAlpha(Label3, 0.7),
        PhaseKind.Think => Accent2,
        PhaseKind.Write => Accent,
        _ => Green,
    };

    /// <summary>A time-bar part.</summary>
    public Color Swatch(TraceSwatch swatch) => swatch switch
    {
        TraceSwatch.Accent => Accent,
        TraceSwatch.Accent2 => Accent2,
        TraceSwatch.Muted => Label3,
        TraceSwatch.Orange => Orange,
        TraceSwatch.Red => Red,
        TraceSwatch.AccentSoft45 => WithAlpha(Accent, 0.45),
        TraceSwatch.AccentSoft35 => WithAlpha(Accent, 0.35),
        TraceSwatch.PhaseWait => Phase(PhaseKind.Wait),
        TraceSwatch.PhaseThink => Phase(PhaseKind.Think),
        TraceSwatch.PhaseWrite => Phase(PhaseKind.Write),
        _ => Phase(PhaseKind.Tool),
    };

    /// <summary>A timeline bar (session.tsx <c>BAR</c>); the failed attempt's stripes are drawn by the timeline itself.</summary>
    public Color Bar(TimelineRowKind kind) => kind switch
    {
        TimelineRowKind.Turn => Fill,
        TimelineRowKind.Llm => Accent,
        TimelineRowKind.Sub => Accent2,
        TimelineRowKind.Tool => Label3,
        TimelineRowKind.AttemptOk => WithAlpha(Accent, 0.7),
        TimelineRowKind.AttemptFail => Red,
        _ => Accent,
    };

    /// <summary>Glass toolbar fill (web <c>.glass</c>: <c>--glass-tint</c> at <c>--glass-alpha</c>).</summary>
    public Color Glass => Dark ? Color.FromArgb((byte)Math.Round(0.52 * 255), 0x3a, 0x3a, 0x3e) : Color.FromArgb((byte)Math.Round(0.55 * 255), 255, 255, 255);

    /// <summary>Glass edge (<c>--glass-edge</c>).</summary>
    public Color GlassEdge => Dark ? Color.FromArgb((byte)Math.Round(0.14 * 255), 255, 255, 255) : Color.FromArgb((byte)Math.Round(0.65 * 255), 255, 255, 255);
}

/// <summary>
/// Repaints a view when its theme or accent changes: the trace views compute colours (accent-2, label-3, mixes) that no single token
/// binding can follow. Attach once; <c>repaint</c> runs after a change.
/// </summary>
public sealed class PaletteWatch : Control
{
    private static readonly StyledProperty<object?> AccentTokenProperty = AvaloniaProperty.Register<PaletteWatch, object?>("AccentToken");
    private readonly Action _repaint;

    private PaletteWatch(Action repaint)
    {
        _repaint = repaint;
        IsVisible = false;
        ArcTokens.Bind(this, AccentTokenProperty, ArcDesignTokenKind.Accent);
    }

    /// <summary>Adds an invisible watcher to <paramref name="panel"/> that calls <paramref name="repaint"/> on a theme or accent change.</summary>
    public static void Attach(Panel panel, Action repaint)
    {
        var watch = new PaletteWatch(repaint);
        panel.Children.Add(watch);
        panel.ActualThemeVariantChanged += (_, _) => repaint();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AccentTokenProperty && change.OldValue is not null) _repaint();
    }
}
