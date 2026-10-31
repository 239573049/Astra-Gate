using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using TKCode.Arc;
using TKCode.Arc.Primitives;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// Arc's <see cref="Select"/> menu is as wide as the widest option it lists (GitHub issue #15). Arc sets the menu surface's
/// <c>Width</c> to the trigger's on every open, and a select sized to its value (the clients page's model pickers read "不设置",
/// about 90 px) then cut every model name to "dee…". The web's menu is never narrower than 12rem and wraps a long label
/// (<c>select.module.css</c> <c>.content</c>); Arc's rows are one line, so here the menu takes the widest label's width instead:
/// at least the trigger and <see cref="MinMenuWidth"/>, at most <see cref="MaxMenuWidth"/> (web <c>max-width: 24rem</c>) and the
/// window less <see cref="WindowGutter"/>; only labels wider than that end in an ellipsis. The width goes on the surface's
/// <c>MinWidth</c>, which wins over the <c>Width</c> Arc keeps setting. The menu opens start-aligned under its trigger (Radix
/// popper's <c>align="start"</c>) and Arc's positioner flips it to end-aligned when it would leave the window: centred, as Arc
/// places it, a menu wider than a trigger at a row's end was slid flush against the window's edge.
/// Installed once for every <see cref="Select"/> in the app (<see cref="Theme.KitStyles.Install"/>).
/// </summary>
public static class SelectMenuFit
{
    /// <summary>Narrowest menu (web <c>min-width: max(var(--radix-select-trigger-width), 12rem)</c>).</summary>
    public const double MinMenuWidth = 192;

    /// <summary>Widest menu (web <c>max-width: min(24rem, calc(100vw - 20px))</c>).</summary>
    public const double MaxMenuWidth = 384;

    /// <summary>Room the menu leaves of the window's width (the web's <c>100vw - 20px</c>).</summary>
    public const double WindowGutter = 20;

    /// <summary>The template parts read here; the options view is Arc-internal, so its row metrics are read by property name.</summary>
    private sealed record Parts(Border Surface, ScrollViewer? Scroll, Control Options);

    private static readonly ConditionalWeakTable<Select, Parts> s_parts = new();
    private static readonly Dictionary<(string Text, string Family, FontWeight Weight, double Size), double> s_widths = [];
    private static bool _installed;

    /// <summary>Registers the class handlers (idempotent).</summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<Select>((select, e) => Attach(select, e.NameScope));
        // After Arc's own handler has opened the popup and set the surface's Width, before the layout pass that sizes the menu.
        Select.IsOpenProperty.Changed.AddClassHandler<Select>((select, e) =>
        {
            if (e.NewValue is true) Fit(select);
        });
    }

    private static void Attach(Select select, INameScope scope)
    {
        if (scope.Find<Border>(Select.SurfacePart) is not { } surface || scope.Find<Control>(Select.OptionsPart) is not { } options) return;
        s_parts.AddOrUpdate(select, new Parts(surface, scope.Find<ScrollViewer>(Select.ScrollPart), options));
        scope.Find<ArcPopup>(Select.PopupPart)?.SetCurrentValue(ArcPopup.RequestedPlacementProperty, PlacementMode.BottomEdgeAlignedLeft);
        if (select.IsOpen) Fit(select);
    }

    /// <summary>Sizes the open menu of <paramref name="select"/> to its options.</summary>
    public static void Fit(Select select)
    {
        if (!s_parts.TryGetValue(select, out var parts)) return;
        var cap = MaxMenuWidth;
        if (TopLevel.GetTopLevel(select) is { } top && top.ClientSize.Width > 0) cap = Math.Min(cap, top.ClientSize.Width - WindowGutter);
        var width = Math.Min(Math.Max(MenuWidth(select, parts), MinMenuWidth), Math.Max(0, cap));
        parts.Surface.SetCurrentValue(Layoutable.MinWidthProperty, Math.Ceiling(width));
    }

    /// <summary>The surface width that shows every option's label (and detail) whole: the widest row plus the menu's own padding.</summary>
    private static double MenuWidth(Select select, Parts parts)
    {
        var options = parts.Options;
        var family = TextElement.GetFontFamily(options);
        var size = TextElement.GetFontSize(options);
        var spacing = Read(options, "ContentSpacing", 12.0);
        var weight = Read(options, "LabelFontWeight", FontWeight.Normal);
        var metaSize = Read(options, "MetaFontSize", 12.0);
        var padding = Read(options, "RowPadding", new Thickness(12, 0));
        var widest = 0.0;
        foreach (var item in select.ItemsView)
        {
            var (text, meta, icon) = Describe(select, item);
            var row = TextWidth(text, family, weight, size);
            if (!string.IsNullOrEmpty(meta))
            {
                // Arc gives the detail at most half the text area and the label what the detail leaves.
                var detail = TextWidth(meta, family, FontWeight.Normal, metaSize);
                row = Math.Max(row + spacing + detail, 2 * detail);
            }
            if (icon) row += 28;
            widest = Math.Max(widest, row);
        }
        // A check drawn at the inset sits in the row padding; without an inset Arc moves the label's end left of it.
        if (double.IsNaN(Read(options, "CheckInset", double.NaN)) && FindProperty(options, "CheckMode") is { } mode && options.GetValue(mode)?.ToString() != "None")
            widest += 16 + spacing;
        var chrome = padding.Left + padding.Right + options.Margin.Left + options.Margin.Right
            + parts.Surface.Padding.Left + parts.Surface.Padding.Right + parts.Surface.BorderThickness.Left + parts.Surface.BorderThickness.Right;
        if (parts.Scroll is { } scroll) chrome += scroll.Padding.Left + scroll.Padding.Right;
        // One pixel for the text layout's rounding, so a label that fits is not trimmed.
        return widest + chrome + 1;
    }

    /// <summary>An item's label, detail and whether it has an icon, read the way Arc's entry source reads them (<c>DisplayMemberBinding</c> is not used here).</summary>
    private static (string Text, string? Meta, bool Icon) Describe(Select select, object? item) => item switch
    {
        SelectOption o => (select.TextSelector?.Invoke(item) ?? o.Label, o.Meta, o.Icon is not null),
        SelectItem i => (select.TextSelector?.Invoke(item) ?? TextOf(i.Content), i.Meta, i.Icon is not null),
        _ => (select.TextSelector?.Invoke(item) ?? TextOf(item), null, false),
    };

    private static AvaloniaProperty? FindProperty(Control control, string name) => AvaloniaPropertyRegistry.Instance.FindRegistered(control, name);

    private static T Read<T>(Control control, string name, T fallback)
        => FindProperty(control, name) is { } property && control.GetValue(property) is T value ? value : fallback;

    private static string TextOf(object? value) => value switch
    {
        null => "",
        string s => s,
        TextBlock t => t.Text ?? "",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.CurrentCulture),
        _ => value.ToString() ?? "",
    };

    private static double TextWidth(string text, FontFamily family, FontWeight weight, double size)
    {
        if (text.Length == 0) return 0;
        var key = (text, family.Name, weight, size);
        if (s_widths.TryGetValue(key, out var width)) return width;
        // Model lists repeat across a page's pickers; the cache only bounds itself.
        if (s_widths.Count > 4096) s_widths.Clear();
        using var layout = new TextLayout(text, new Typeface(family, FontStyle.Normal, weight), size, null);
        return s_widths[key] = layout.WidthIncludingTrailingWhitespace;
    }
}
