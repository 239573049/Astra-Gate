using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// Token plumbing shared by the kit: every color, radius and size of a kit control binds to an Arc design token (so it follows
/// light/dark and the accent), and the few sizes the web writes as literals (<c>text-[12px]</c>) are named here once.
/// </summary>
public static class KitStyle
{
    /// <summary>Web <c>text-[12px]</c> (group titles, key labels): between Arc's TypeXS (11) and TypeSM (13).</summary>
    public const double Type12 = 12;

    /// <summary>Binds <paramref name="property"/> of <paramref name="target"/> to a token.</summary>
    public static T Token<T>(this T target, AvaloniaProperty property, ArcDesignTokenKind kind) where T : StyledElement
    {
        ArcTokens.Bind(target, property, kind);
        return target;
    }

    /// <summary>A text block with token font size and color; trims with an ellipsis like the web's <c>truncate</c>.</summary>
    public static TextBlock Text(string? text, ArcDesignTokenKind size, ArcDesignTokenKind color, FontWeight weight = FontWeight.Normal)
    {
        var block = new TextBlock { Text = text, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        block.Token(TextBlock.FontSizeProperty, size).Token(TextBlock.ForegroundProperty, color);
        return block;
    }

    /// <summary>Sets visibility of a control from a nullable text (hidden when empty).</summary>
    public static bool HasText(string? text) => !string.IsNullOrEmpty(text);

    /// <summary>An object (string or control) as something a presenter can show: a string becomes a trimmed text block.</summary>
    public static object? AsContent(object? value, ArcDesignTokenKind size, ArcDesignTokenKind color)
        => value is string s ? Text(s, size, color) : value;
}
