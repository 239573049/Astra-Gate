using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Astra.Desktop.Components.Requests;

/// <summary>
/// The web's content material (web/src/styles/index.css): <c>--surface</c> is translucent, laid over the wallpaper (<c>#ffffff</c> at
/// 78 % light, <c>#3a3a3e</c> at 42 % dark), and <c>--border</c> is the <c>--separator</c> hairline (black 9 % / white 10 %). The desktop
/// theme still uses Arc's opaque Surface / Border greys (it remaps Arc's sizes, not its colours), so the request table and the trace
/// cards state the web's values here, like <c>TracePalette</c> does for the trace colours. When the theme maps those two tokens, callers
/// can bind the tokens again and this goes away.
/// </summary>
public static class WebMaterial
{
    /// <summary><c>--surface</c> for the theme.</summary>
    public static Color Surface(bool dark) => dark ? Color.FromArgb(0x6B, 0x3A, 0x3A, 0x3E) : Color.FromArgb(0xC7, 0xFF, 0xFF, 0xFF);

    /// <summary><c>--border</c> (<c>--separator</c>) for the theme.</summary>
    public static Color Separator(bool dark) => dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x17, 0, 0, 0);

    /// <summary>Gives <paramref name="border"/> the material's background (and, with <paramref name="edge"/>, its border colour), following theme changes.</summary>
    public static T Apply<T>(T border, bool edge = true) where T : Border
    {
        void Paint()
        {
            var dark = border.ActualThemeVariant == ThemeVariant.Dark;
            border.Background = new SolidColorBrush(Surface(dark));
            if (edge) border.BorderBrush = new SolidColorBrush(Separator(dark));
        }
        Paint();
        border.ActualThemeVariantChanged += (_, _) => Paint();
        return border;
    }
}
