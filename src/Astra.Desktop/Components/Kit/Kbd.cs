using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// A key cap for a shortcut ("⌘K"): monospace 11px in the secondary text color inside a 1px border with radius 5. Arc's own
/// keycap (<c>PromptKbd</c>) is internal and its <c>ShortcutRecorder</c> cap is not reusable (plan inventory GAP-14), so the
/// kit draws it from tokens.
/// </summary>
public sealed class Kbd : Border
{
    /// <summary>The key or shortcut shown.</summary>
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<Kbd, string?>(nameof(Text));

    private readonly TextBlock _text;

    /// <inheritdoc cref="TextProperty"/>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <summary>An empty cap; set <see cref="Text"/>.</summary>
    public Kbd()
    {
        _text = KitStyle.Text(null, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        _text.TextTrimming = Avalonia.Media.TextTrimming.None;
        _text.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(5, 0);
        MinWidth = 20;
        Height = 20;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        Child = _text;
        this.Token(BorderBrushProperty, ArcDesignTokenKind.Border);
        CornerRadius = new CornerRadius(5);
        Focusable = false;
    }

    /// <summary>A cap showing <paramref name="text"/>.</summary>
    public Kbd(string text) : this() => Text = text;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) _text.Text = Text;
    }
}
