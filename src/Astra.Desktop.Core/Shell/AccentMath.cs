namespace Astra.Desktop.Core.Shell;

/// <summary>Color rules of the web's appearance code (web/src/shell/appearance.tsx) that the desktop theme needs.</summary>
public static class AccentMath
{
    /// <summary>
    /// Text color on an accent fill (web <c>onAccentColor</c>): near-black <c>#1d1d1f</c> when the accent's relative luminance is above 0.45,
    /// else white. Arc derives its own <c>AccentForeground</c> from a 4.5:1 contrast rule, which turns the default blue <c>#007aff</c>
    /// (contrast 4.0 against white) black — the web keeps it white.
    /// </summary>
    public static string OnAccent(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#' || !int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var n)) return "#ffffff";
        static double Lin(int c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        var lum = 0.2126 * Lin((n >> 16) & 255) + 0.7152 * Lin((n >> 8) & 255) + 0.0722 * Lin(n & 255);
        return lum > 0.45 ? "#1d1d1f" : "#ffffff";
    }
}
