using System.Globalization;
using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Theme;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Overview;

/// <summary>
/// ARC-GAP: Arc's <c>ActivityHeatmap</c> does not stretch its columns to the card, labels only every other weekday, draws its own
/// "N requests in …" header and an English "Less/More" legend that can be neither localized nor hidden, and its cell tooltip cannot
/// take the web's text. This is the web's own heatmap (<c>web/src/components/arc/activity-heatmap/activity-heatmap.tsx</c> + its
/// module CSS) reproduced: a month band (16px) over a weekday column and Monday-first week columns that spread across the card with
/// 12px cells and 3px gaps, one bubble over the hovered cell, and a foot with the summary and the localized legend.
/// </summary>
public sealed class ActivityHeatmapView : UserControl
{
    /// <summary>The heatmap of <paramref name="model"/>; <paramref name="summary"/> is the line under the grid.</summary>
    public ActivityHeatmapView(ActivityHeatmapModel model, string label, string summary, string less, string more, string unit, string none)
    {
        var plot = new HeatmapPlot(model, unit, none);
        // The web's `.scroll` lets a card too narrow for the columns be dragged sideways; here such a card clips the plot instead, because a
        // ScrollViewer measures its content at infinite width and the columns would then never spread to the card.
        var summaryText = KitStyle.Text(summary, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextSecondary);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        legend.Children.Add(LegendText(less));
        for (var level = 0; level <= 4; level++) legend.Children.Add(new HeatSwatch(level));
        legend.Children.Add(LegendText(more));

        // `.foot`: 14px above, a hairline, 12px below it; summary left, legend right.
        var foot = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(legend, Dock.Right);
        foot.Children.Add(legend);
        foot.Children.Add(summaryText);
        var footBorder = new Border { Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 12, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0), Child = foot };
        footBorder.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);

        Avalonia.Automation.AutomationProperties.SetName(plot, $"{label}. {summary}");
        Content = new StackPanel { Children = { plot, footBorder } };
    }

    private static TextBlock LegendText(string text)
    {
        var block = KitStyle.Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        block.FontSize = 11;
        return block;
    }

    /// <summary>The five level colours of the web CSS (<c>:where(.cell, .swatch)[data-level]</c>) for the theme and accent of <paramref name="owner"/>.</summary>
    internal static Color[] Levels(Control owner)
    {
        var dark = owner.ActualThemeVariant == ThemeVariant.Dark;
        var accent = owner.TryFindResource(KitStyles.AccentKey, owner.ActualThemeVariant, out var res) && res is ISolidColorBrush brush
            ? brush.Color
            : Color.FromRgb(0, 0x7a, 0xff);
        // --surface: #ffffff at 78% (light) / #3a3a3e at 42% (dark); --fill-2: rgb(120 120 128 / .07 | .14).
        (byte, byte, byte, double) surface = dark ? ((byte)0x3a, (byte)0x3a, (byte)0x3e, 0.42) : ((byte)255, (byte)255, (byte)255, 0.78);
        double[] shares = dark ? [0.36, 0.54, 0.70, 0.88] : [0.26, 0.45, 0.63, 0.84];
        var colors = new Color[5];
        colors[0] = Color.FromArgb((byte)Math.Round((dark ? 0.14 : 0.07) * 255), 120, 120, 128);
        for (var i = 0; i < 4; i++)
        {
            var (r, g, b, a) = OklabMix.Mix((accent.R, accent.G, accent.B, 1.0), shares[i], surface);
            colors[i + 1] = Color.FromArgb((byte)Math.Round(a * 255), r, g, b);
        }
        return colors;
    }

    /// <summary>A legend swatch: 11px, radius 3, 2px side margins (<c>.swatch</c>).</summary>
    private sealed class HeatSwatch : Control
    {
        private readonly int _level;

        public HeatSwatch(int level)
        {
            _level = level;
            Width = 11;
            Height = 11;
            Margin = new Thickness(2, 0);
            VerticalAlignment = VerticalAlignment.Center;
            ActualThemeVariantChanged += (_, _) => InvalidateVisual();
            ResourcesChanged += (_, _) => InvalidateVisual();
        }

        public override void Render(DrawingContext context)
            => context.DrawRectangle(new SolidColorBrush(Levels(this)[_level]), null, new RoundedRect(new Rect(Bounds.Size), 3));
    }

    /// <summary>The plot: month band, weekday column, cells and the hover bubble, drawn in one pass so 371 cells cost no controls.</summary>
    private sealed class HeatmapPlot : Control
    {
        private const double Cell = 12, Gap = 3, ColumnGap = 6, MonthBand = 16, RowGap = 4, LabelSize = 11, BubbleSize = 13;
        private readonly ActivityHeatmapModel _model;
        private readonly string _unit;
        private readonly string _none;
        private int _hover = -1;

        public HeatmapPlot(ActivityHeatmapModel model, string unit, string none)
        {
            _model = model;
            _unit = unit;
            _none = none;
            ClipToBounds = false;
            ActualThemeVariantChanged += (_, _) => InvalidateVisual();
            ResourcesChanged += (_, _) => InvalidateVisual();
        }

        private static double GridTop => MonthBand + RowGap;

        private Typeface Face => new(GetValue(TextElement.FontFamilyProperty));

        private FormattedText Text(string text, double size, Color color) =>
            new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, size, new SolidColorBrush(color));

        // Every cell's position goes through it (CellRect, hit-testing on each pointer move) and each read laid out seven labels:
        // thousands of text layouts per render. It only changes with the language or the font.
        private (string Language, FontFamily Font, double Width)? _weekdayWidth;

        private double WeekdayWidth
        {
            get
            {
                var font = GetValue(TextElement.FontFamilyProperty);
                if (_weekdayWidth is { } cached && cached.Language == Loc && Equals(cached.Font, font)) return cached.Width;
                var width = Enumerable.Range(0, 7).Max(r => Text(ActivityHeatmapModel.WeekdayName(r, Loc), LabelSize, Colors.Black).WidthIncludingTrailingWhitespace);
                _weekdayWidth = (Loc, font, width);
                return width;
            }
        }

        private static string Loc => Core.I18n.Loc.Language;

        private double GridLeft => Math.Ceiling(WeekdayWidth) + ColumnGap;

        protected override Size MeasureOverride(Size availableSize)
        {
            var min = GridLeft + _model.Weeks * Cell + (_model.Weeks - 1) * Gap;
            var width = double.IsInfinity(availableSize.Width) ? min : Math.Max(min, availableSize.Width);
            return new Size(width, GridTop + 7 * Cell + 6 * Gap);
        }

        /// <summary>Left edge of a week column: each column takes an equal share of what is left after the gaps (<c>flex: 1 1 0</c>).</summary>
        private double ColumnWidth => Math.Max(Cell, (Bounds.Width - GridLeft - (_model.Weeks - 1) * Gap) / _model.Weeks);

        private double ColumnLeft(int column) => GridLeft + column * (ColumnWidth + Gap);

        private Rect CellRect(int index)
        {
            var column = index / 7;
            var row = index % 7;
            var x = ColumnLeft(column) + (ColumnWidth - Cell) / 2;
            return new Rect(x, GridTop + row * (Cell + Gap), Cell, Cell);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            var hit = -1;
            var column = (int)Math.Floor((p.X - GridLeft) / (ColumnWidth + Gap));
            if (column >= 0 && column < _model.Weeks)
            {
                for (var row = 0; row < 7; row++)
                {
                    var index = column * 7 + row;
                    if (CellRect(index).Contains(p)) { hit = index; break; }
                }
            }
            if (hit == _hover) return;
            _hover = hit;
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (_hover < 0) return;
            _hover = -1;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            var muted = ArcTokens.GetColor(this, ArcDesignTokenKind.TextMuted);
            var levels = Levels(this);

            // Month band: each label starts at its column's left edge and may run on over the quiet columns beside it.
            for (var column = 0; column < _model.Weeks; column++)
            {
                if (_model.MonthLabels[column] is not { } label) continue;
                var text = Text(label, LabelSize, muted);
                context.DrawText(text, new Point(ColumnLeft(column), (LabelSize - text.Height) / 2));
            }

            // Weekday column: right-aligned, centred on each 12px row.
            var weekdayRight = Math.Ceiling(WeekdayWidth);
            for (var row = 0; row < 7; row++)
            {
                var text = Text(ActivityHeatmapModel.WeekdayName(row, Loc), LabelSize, muted);
                context.DrawText(text, new Point(weekdayRight - text.WidthIncludingTrailingWhitespace, GridTop + row * (Cell + Gap) + (Cell - text.Height) / 2));
            }

            var brushes = levels.Select(c => new SolidColorBrush(c)).ToArray();
            for (var i = 0; i < _model.Days.Count; i++)
                context.DrawRectangle(brushes[_model.Levels[i]], null, new RoundedRect(CellRect(i), 3));

            if (_hover >= 0) DrawBubble(context, _hover);
        }

        /// <summary>The bubble (<c>.bubble</c>): foreground fill, background text, 13px, padding 6/10, radius 10, 8px off the cell.</summary>
        private void DrawBubble(DrawingContext context, int index)
        {
            var dark = ActualThemeVariant == ThemeVariant.Dark;
            var fill = ArcTokens.GetColor(this, ArcDesignTokenKind.Foreground);
            var back = dark ? Color.FromRgb(0x16, 0x16, 0x18) : Color.FromRgb(0xee, 0xf0, 0xf5); // --canvas
            var (er, eg, eb, ea) = OklabMix.Mix((back.R, back.G, back.B, 1.0), 0.14, (fill.R, fill.G, fill.B, fill.A / 255.0));
            var text = Text(ActivityHeatmapModel.DayCount(_model.Days[index], _model.Counts[index], Loc, _unit, _none), BubbleSize, back);
            var lineHeight = BubbleSize * 1.4;
            var size = new Size(text.WidthIncludingTrailingWhitespace + 20 + 2, lineHeight + 12 + 2);
            var cell = CellRect(index);
            var place = ActivityHeatmapModel.Place(cell.Center.X, cell.Top, Bounds.Width);
            var x = place.Align switch { BubbleAlign.Start => place.Left, BubbleAlign.End => place.Left - size.Width, _ => place.Left - size.Width / 2 };
            var y = place.Above ? place.Top - size.Height - 8 : place.Top + 8;
            var rect = new RoundedRect(new Rect(new Point(x, y), size), 10);
            var shadow = new BoxShadows(new BoxShadow { OffsetY = dark ? 8 : 6, Blur = dark ? 22 : 18, Color = Color.FromArgb((byte)(dark ? 56 : 17), 0, 0, 0) });
            context.DrawRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(ea * 255), er, eg, eb)), 1), rect, shadow);
            context.DrawText(text, new Point(x + 11, y + 7 + (lineHeight - text.Height) / 2));
        }
    }
}
