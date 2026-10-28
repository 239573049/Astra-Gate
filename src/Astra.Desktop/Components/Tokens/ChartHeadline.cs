using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Astra.Desktop.Theme;
using Avalonia.Media;
using Avalonia.Styling;
using TKCode.Arc;

namespace Astra.Desktop.Components.Tokens;

/// <summary>
/// ARC-GAP: Arc's Avalonia <c>BarChart</c> sizes its headline lines for a 14px body (19.6px) and lets the 26px value take its glyph height
/// (~31px), while the web chart (<c>components/arc/bar-chart/bar-chart.module.css</c>) uses 13px × 1.4 lines and a 1.1 line height for the
/// value — so the desktop plot sat ~10px lower than the web's. The template parts are adjusted once applied.
/// </summary>
public static class ChartHeadline
{
    /// <summary>
    /// Gives <paramref name="chart"/> the web's headline metrics and bar colours; returns it. The bars are the accent mixed into the card
    /// (62% light / 76% dark): Arc mixes its own Accent token, which it lightens in dark mode, into its opaque Surface, so dark bars came
    /// out pastel. The web's accent (<c>KitAccent</c>) and the colour a web card actually ends up as over the canvas are used instead.
    /// </summary>
    public static BarChart Fit(BarChart chart)
    {
        void Colors()
        {
            var dark = chart.ActualThemeVariant == ThemeVariant.Dark;
            // ARC-GAP: the chart's palette brushes are internal; set through the property registry (no reflection).
            if (chart.TryFindResource(KitStyles.AccentKey, chart.ActualThemeVariant, out var accent) && accent is IBrush brush
                && AvaloniaPropertyRegistry.Instance.FindRegistered(chart, "AccentBrush") is { } accentProperty)
                chart.SetValue(accentProperty, brush);
            if (AvaloniaPropertyRegistry.Instance.FindRegistered(chart, "SurfaceBrush") is { } surfaceProperty)
                chart.SetValue(surfaceProperty, new SolidColorBrush(dark ? Color.FromRgb(37, 37, 40) : Color.FromRgb(251, 252, 253)));
        }
        Colors();
        chart.ActualThemeVariantChanged += (_, _) => Colors();
        chart.TemplateApplied += (_, e) =>
        {
            // `.kind, .when`: min-height 1em × --leading-body at --text-sm.
            if (e.NameScope.Find<Control>("PART_Kind") is { } kind) kind.MinHeight = 13 * 1.4;
            if (e.NameScope.Find<Control>("PART_When") is { } when) when.MinHeight = 13 * 1.4;
            // `.value`: 26px at line-height 1.1; the digits keep their own glyph box, centred in the shorter line.
            if (e.NameScope.Find<Control>("PART_Value") is { Parent: StackPanel row } value)
            {
                row.Height = 26 * 1.1;
                value.VerticalAlignment = VerticalAlignment.Center;
                if (e.NameScope.Find<Control>("PART_ValueText") is { } text) text.VerticalAlignment = VerticalAlignment.Center;
            }
        };
        return chart;
    }
}
