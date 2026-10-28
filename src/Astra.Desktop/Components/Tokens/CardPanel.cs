using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Astra.Desktop.Components.Kit;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Tokens;

/// <summary>
/// The web's <c>.card</c> utility: surface fill, a 0.5px separator edge, the 14px panel radius and clipped corners. The kit's
/// <c>SettingsGroup</c> draws the same card around rows; pages that put free content in a card (stat tiles, charts, tables) use this.
/// </summary>
public sealed class CardPanel : Border
{
    /// <summary>A card around <paramref name="child"/> with <paramref name="padding"/> inside (web <c>p-5</c> = 20, <c>px-4 py-3</c> = 16 / 12).</summary>
    public CardPanel(Control? child = null, Thickness? padding = null)
    {
        BorderThickness = new Thickness(0.5);
        ClipToBounds = true;
        Padding = padding ?? default;
        Child = child;
        Background = SurfaceBrush(null);
        ActualThemeVariantChanged += (_, _) => Background = SurfaceBrush(ActualThemeVariant);
        this.Token(BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
    }

    /// <summary>
    /// The web's translucent <c>--surface</c> (index.css): <c>#ffffff</c> at 78% in light, <c>#3a3a3e</c> at 42% in dark, laid over the
    /// canvas. Arc's <c>Surface</c> token is opaque (#fff / #191919), which made every card a shade whiter / darker than the web's.
    /// </summary>
    public static IBrush SurfaceBrush(ThemeVariant? theme) => theme == ThemeVariant.Dark
        ? new SolidColorBrush(Color.FromArgb(107, 0x3a, 0x3a, 0x3e))
        : new SolidColorBrush(Color.FromArgb(199, 255, 255, 255));


    /// <summary>The web's <c>--surface-muted</c>: the label colour at 6% (a tint, not a layer); Arc's SurfaceMuted is opaque.</summary>
    public static IBrush SurfaceMutedBrush(ThemeVariant? theme) => theme == ThemeVariant.Dark
        ? new SolidColorBrush(Color.FromArgb(14, 255, 255, 255))
        : new SolidColorBrush(Color.FromArgb(13, 0, 0, 0));

    /// <summary>Keeps <paramref name="border"/>'s background on <paramref name="brush"/> of its current theme.</summary>
    public static T Themed<T>(T border, Func<ThemeVariant?, IBrush> brush) where T : Border
    {
        border.Background = brush(null);
        border.ActualThemeVariantChanged += (_, _) => border.Background = brush(border.ActualThemeVariant);
        return border;
    }

    /// <summary>The web's <c>p-5</c> section card.</summary>
    public static CardPanel Section(Control child) => new(child, new Thickness(20));
}
