using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Clients;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Clients;

/// <summary>
/// ARC-GAP: GAP-6 text diff view (Arc has no diff viewer; plan inventory §4). The web's <c>DiffView</c> (ClientsPage.tsx): a unified diff in a
/// bordered, muted, monospace 11 px box at most 300 px tall that scrolls, "+" lines green on a 12 % green tint, "-" lines red on a 10 % red
/// tint, "@@" lines muted, every line wrapping, no line-number gutter (layout parity, plan avalonia-client §23).
/// Delete this when Arc ships a DiffViewer (or a CodeBlock diff mode).
/// </summary>
public sealed class DiffView : UserControl
{
    /// <summary>The 11 px font and relaxed (1.625) leading of the web box.</summary>
    private const double FontPx = 11, LineHeightPx = 11 * 1.625;

    /// <summary>A view of <paramref name="diff"/>.</summary>
    public DiffView(string? diff, double maxHeight = 300)
    {
        var lines = DiffLines.Parse(diff);
        var stack = new StackPanel { Margin = new Thickness(0, 8) };
        foreach (var line in lines) stack.Children.Add(Line(line));

        var scroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var box = new Border { BorderThickness = new Thickness(1), MaxHeight = maxHeight, ClipToBounds = true, Child = scroll };
        box.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusControl);
        Content = box;
    }

    private static Control Line(DiffLine line)
    {
        var text = new SelectableTextBlock { Text = line.Text.Length == 0 ? " " : line.Text, TextWrapping = TextWrapping.Wrap, FontSize = FontPx, LineHeight = LineHeightPx };
        text.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        text.Token(TextBlock.ForegroundProperty, line.Kind switch
        {
            DiffLineKind.Added => ArcDesignTokenKind.Success,
            DiffLineKind.Removed => ArcDesignTokenKind.Danger,
            DiffLineKind.Hunk => ArcDesignTokenKind.TextMuted,
            _ => ArcDesignTokenKind.Foreground,
        });

        // web: each line is `px-3 whitespace-pre-wrap break-all`
        text.Margin = new Thickness(12, 0);
        var row = new Border { Child = text };
        if (line.Kind is DiffLineKind.Added or DiffLineKind.Removed)
        {
            // color-mix(in oklab, var(--success) 12% / var(--danger) 10%, transparent)
            var opacity = line.Kind == DiffLineKind.Added ? 0.12 : 0.10;
            var tint = new Border { Opacity = opacity, IsHitTestVisible = false };
            tint.Token(Border.BackgroundProperty, line.Kind == DiffLineKind.Added ? ArcDesignTokenKind.Success : ArcDesignTokenKind.Danger);
            return new Panel { Children = { tint, row } };
        }
        return row;
    }
}
