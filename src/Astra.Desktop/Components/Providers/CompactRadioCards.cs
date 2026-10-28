using Astra.Desktop.Components.Kit;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Providers;

/// <summary>
/// Web <c>RadioCards density="compact"</c> (arc radio-cards.module.css), the add-provider wizard's template tiles: the icon beside a
/// one-line label (13 px medium, the app's --text-sm) and description (11 px secondary, --text-xs), both cut with an ellipsis; no radio dot — a 1.5 px ring in the
/// control color and a 4 % tint mark the choice; 12 px radius, 9 / 12 / 9 / 10 padding, 8 px gaps; a grid of
/// <c>repeat(auto-fill, minmax(min(100%, minColumnWidth), 1fr))</c> or one column (<c>layout="list"</c>).
/// // ARC-GAP: Arc's RadioCards has no compact density (its tiles always carry the radio indicator and the roomy padding).
/// </summary>
internal sealed class CompactRadioCards : UserControl
{
    private readonly List<(RadioCardOption Option, Border Tile, Border Tint)> _tiles = [];
    private object? _selected;

    /// <summary>Tiles for <paramref name="options"/>; <paramref name="list"/> = one column (web <c>layout="list"</c>).</summary>
    public CompactRadioCards(IReadOnlyList<RadioCardOption> options, bool list = false, double minColumnWidth = 168)
    {
        Options = options;
        var panel = new AutoFillPanel { MinColumnWidth = list ? double.PositiveInfinity : minColumnWidth, Gap = 8 };
        foreach (var option in options) panel.Children.Add(Tile(option));
        Content = panel;
    }

    /// <summary>The options shown.</summary>
    public IReadOnlyList<RadioCardOption> Options { get; }

    /// <summary>The chosen option's value (null = none).</summary>
    public object? SelectedValue
    {
        get => _selected;
        set
        {
            if (Equals(_selected, value)) return;
            _selected = value;
            Paint();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised when <see cref="SelectedValue"/> changes.</summary>
    public event EventHandler? SelectionChanged;

    private Control Tile(RadioCardOption option)
    {
        var label = KitStyle.Text(option.Label as string ?? option.Label?.ToString(), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        label.FontSize = 13;
        label.LineHeight = 13 * 1.4;
        var body = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center, Children = { label } };
        if (!string.IsNullOrEmpty(option.Description))
        {
            var description = KitStyle.Text(option.Description, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
            description.FontSize = 11;
            description.LineHeight = 11 * 1.35;
            ToolTip.SetTip(description, option.Description);
            body.Children.Add(description);
        }
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        if (option.Icon is Control icon)
        {
            icon.VerticalAlignment = VerticalAlignment.Center;
            if (option.IsDisabled) icon.Opacity = 0.45;
            row.Children.Add(icon);
        }
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        if (option.IsDisabled) label.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.TextMuted);

        // The 4 % control-color tint of a chosen tile, over the tile's surface (web color-mix(… 4%, var(--surface))).
        var tint = new Border { Opacity = 0.04, IsHitTestVisible = false };
        tint.Token(Border.BackgroundProperty, ArcDesignTokenKind.ControlOn);
        var tile = new Border
        {
            Padding = new Thickness(10, 9, 12, 9),
            CornerRadius = new CornerRadius(12),
            Focusable = !option.IsDisabled,
            Cursor = new Cursor(option.IsDisabled ? StandardCursorType.No : StandardCursorType.Hand),
            Child = new Panel { Children = { tint, row } },
        };
        AutomationProperties.SetName(tile, option.Label?.ToString());
        AutomationProperties.SetControlTypeOverride(tile, AutomationControlType.RadioButton);
        if (option.IsDisabled && option.DisabledReason is { } reason) ToolTip.SetTip(tile, reason);
        tile.PointerPressed += (_, e) =>
        {
            if (option.IsDisabled || !e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            tile.Focus();
            SelectedValue = option.Value;
        };
        tile.KeyDown += (_, e) =>
        {
            if (option.IsDisabled || e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            SelectedValue = option.Value;
        };
        tile.PointerEntered += (_, _) => Paint();
        tile.PointerExited += (_, _) => Paint();
        _tiles.Add((option, tile, tint));
        PaintTile(option, tile, tint);
        return tile;
    }

    private void Paint()
    {
        foreach (var (option, tile, tint) in _tiles) PaintTile(option, tile, tint);
    }

    private void PaintTile(RadioCardOption option, Border tile, Border tint)
    {
        var chosen = !option.IsDisabled && Equals(option.Value, _selected);
        tint.IsVisible = chosen;
        // web: 1px var(--border); the moving ring (1.5px control color) over it when chosen; border-strong on hover; muted when disabled.
        tile.BorderThickness = new Thickness(chosen ? 1.5 : 1);
        tile.Token(Border.BorderBrushProperty, chosen ? ArcDesignTokenKind.ControlOn : tile.IsPointerOver && !option.IsDisabled ? ArcDesignTokenKind.BorderStrong : ArcDesignTokenKind.Border);
        tile.Token(Border.BackgroundProperty, option.IsDisabled ? ArcDesignTokenKind.SurfaceMuted : ArcDesignTokenKind.Surface);
    }

    /// <summary>CSS <c>repeat(auto-fill, minmax(min(100%, W), 1fr))</c> with a gap: as many equal columns of at least W as fit.</summary>
    private sealed class AutoFillPanel : Panel
    {
        public double MinColumnWidth { get; init; }
        public double Gap { get; init; }

        private int Columns(double width) => double.IsInfinity(MinColumnWidth) || double.IsInfinity(width)
            ? 1
            : Math.Max(1, (int)Math.Floor((width + Gap) / (MinColumnWidth + Gap)));

        protected override Size MeasureOverride(Size availableSize)
        {
            var columns = Columns(availableSize.Width);
            var cell = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : (availableSize.Width - Gap * (columns - 1)) / columns;
            double height = 0, rowHeight = 0, widest = 0;
            for (var i = 0; i < Children.Count; i++)
            {
                Children[i].Measure(new Size(cell, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
                widest = Math.Max(widest, Children[i].DesiredSize.Width);
                if (i % columns == columns - 1 || i == Children.Count - 1)
                {
                    height += rowHeight + (height > 0 ? Gap : 0);
                    rowHeight = 0;
                }
            }
            return new Size(double.IsInfinity(cell) ? widest : availableSize.Width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var columns = Columns(finalSize.Width);
            var cell = (finalSize.Width - Gap * (columns - 1)) / columns;
            double y = 0;
            for (var start = 0; start < Children.Count; start += columns)
            {
                var end = Math.Min(start + columns, Children.Count);
                double rowHeight = 0;
                for (var i = start; i < end; i++) rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
                for (var i = start; i < end; i++) Children[i].Arrange(new Rect((i - start) * (cell + Gap), y, cell, rowHeight));
                y += rowHeight + Gap;
            }
            return finalSize;
        }
    }
}
