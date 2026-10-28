using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Plugins;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Plugins;

/// <summary>
/// A plugin's picture (web <c>PluginIcon</c>): the one it gives (a data-URL image), a white glyph on a blue (middleware) or teal
/// (agent) rounded tile, else the brand Astra knows by its name, else its initials. The web also draws https icons as images; the
/// desktop client does not download icons (<see cref="BrandIcon"/> shows initials for them, and most of the registry's are SVG).
/// </summary>
internal static class PluginIconView
{
    /// <summary>Builds the icon of <paramref name="size"/> px.</summary>
    public static Control Create(string name, string? icon, string kind, double size = 32)
    {
        switch (PluginIcons.Resolve(icon, kind))
        {
            case PluginIconKind.MiddlewareGlyph:
                return Tile(PluginIcons.MiddlewareColor, LucideIconKind.ShieldCheck, size);
            case PluginIconKind.AgentGlyph:
                return Tile(PluginIcons.AgentColor, LucideIconKind.AppWindow, size);
            default:
                var brand = BrandIcon.Provider(name, icon, size);
                brand.VerticalAlignment = VerticalAlignment.Top;
                return brand;
        }
    }

    private static Control Tile(string color, LucideIconKind glyph, double size)
    {
        var icon = new LucideIcon { Kind = glyph, Size = size * 0.55, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(PluginIcons.Radius(size)),
            Background = new SolidColorBrush(Color.Parse(color)),
            Child = icon,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Focusable = false,
        };
    }
}
