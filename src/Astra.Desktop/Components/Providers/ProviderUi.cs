using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using Astra.Desktop.Components.Kit;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Providers;

/// <summary>Small builders shared by the providers page, the add wizard and the import sheet (web <c>Button</c> with <c>icon</c>, section headings, hints).</summary>
internal static class ProviderUi
{
    /// <summary>A button with an optional leading Lucide icon (web <c>Button icon=...</c>: 14px icon, 6px gap).</summary>
    public static ArcButton Button(string text, LucideIconKind? icon = null, ButtonVariant variant = ButtonVariant.Secondary, ControlSize size = ControlSize.Medium, Action? onClick = null)
    {
        var button = new ArcButton { Variant = variant, Size = size, VerticalAlignment = VerticalAlignment.Center };
        if (icon is { } kind)
        {
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new LucideIcon { Kind = kind, Size = 14, VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            };
        }
        else button.Content = text;
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>An icon-only plain button (web <c>variant="plain" size="sm"</c> with an aria-label that doubles as tooltip).</summary>
    public static ArcButton IconButton(LucideIconKind icon, string label, Action? onClick = null, bool isLoading = false)
    {
        var button = new ArcButton
        {
            Variant = ButtonVariant.Ghost,
            Size = ControlSize.Small,
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsLoading = isLoading,
            Content = new LucideIcon { Kind = icon, Size = 14 },
        };
        ToolTip.SetTip(button, label);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>12px secondary medium heading (web <c>h3 text-[12px] font-medium text-secondary</c>).</summary>
    public static TextBlock Heading(string text)
        => KitStyle.Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, FontWeight.Medium).With(t =>
        {
            t.FontSize = KitStyle.Type12;
            t.Margin = new Thickness(0, 0, 0, 8);
        });

    /// <summary>11px muted hint text, wrapped (web <c>text-[11px] text-muted</c>).</summary>
    public static TextBlock Hint(string text)
        => KitStyle.Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted).With(t =>
        {
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
        });

    /// <summary>12px secondary body text, wrapped.</summary>
    public static TextBlock Secondary(string text)
        => KitStyle.Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary).With(t =>
        {
            t.FontSize = KitStyle.Type12;
            t.TextWrapping = TextWrapping.Wrap;
            t.TextTrimming = TextTrimming.None;
        });

    /// <summary>A visible label over a group of controls (web <c>Labeled</c>: 13px medium, 8px gap, optional hint below).</summary>
    public static Control Labeled(string label, Control content, string? hint = null)
    {
        var stack = new StackPanel { Spacing = 0 };
        var title = KitStyle.Text(label, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        title.Margin = new Thickness(0, 0, 0, 8);
        stack.Children.Add(title);
        stack.Children.Add(content);
        if (hint is not null)
        {
            var h = Hint(hint);
            h.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(h);
        }
        return stack;
    }
}

/// <summary>
/// A grid of equal-width columns that fills the width the way CSS <c>grid-template-columns: repeat(auto-fill, minmax(min, 1fr))</c> does:
/// as many columns as fit (empty tracks included, so a lone card stays one track wide), the leftover width shared out evenly.
/// // ARC-GAP: Arc has no auto-fill grid panel (UniformGrid takes a fixed column count, RadioCardsPanel is private to it).
/// </summary>
internal sealed class AutoFillGrid : Panel
{
    /// <summary>Smallest column width (web <c>minmax(min(100%,260px),1fr)</c>).</summary>
    public static readonly StyledProperty<double> MinColumnWidthProperty = AvaloniaProperty.Register<AutoFillGrid, double>(nameof(MinColumnWidth), 260);

    /// <summary>Gap between columns and rows.</summary>
    public static readonly StyledProperty<double> GapProperty = AvaloniaProperty.Register<AutoFillGrid, double>(nameof(Gap), 12);

    static AutoFillGrid()
    {
        AffectsMeasure<AutoFillGrid>(MinColumnWidthProperty, GapProperty);
    }

    /// <inheritdoc cref="MinColumnWidthProperty"/>
    public double MinColumnWidth { get => GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }

    /// <inheritdoc cref="GapProperty"/>
    public double Gap { get => GetValue(GapProperty); set => SetValue(GapProperty, value); }

    private (int Columns, double Width) Columns(double available)
    {
        if (double.IsInfinity(available)) return (1, MinColumnWidth);
        var min = Math.Min(MinColumnWidth, Math.Max(available, 1));
        var n = Math.Max(1, (int)Math.Floor((available + Gap) / (min + Gap)));
        return (n, (available - Gap * (n - 1)) / n);
    }

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        var (n, width) = Columns(availableSize.Width);
        double total = 0, rowHeight = 0;
        var col = 0;
        var rows = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Measure(new Size(width, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if (++col == n) { total += rowHeight; rows++; col = 0; rowHeight = 0; }
        }
        if (col > 0) { total += rowHeight; rows++; }
        total += Gap * Math.Max(0, rows - 1);
        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, total);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var (n, width) = Columns(finalSize.Width);
        var visible = Children.Where(c => c.IsVisible).ToList();
        double y = 0;
        for (var start = 0; start < visible.Count; start += n)
        {
            var row = visible.Skip(start).Take(n).ToList();
            var height = row.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++) row[i].Arrange(new Rect(i * (width + Gap), y, width, height));
            y += height + Gap;
        }
        return finalSize;
    }
}
