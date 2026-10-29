using Astra.Desktop.Components.Kit;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Astra.Desktop.Components.Pricing;

/// <summary>
/// The web control kit's small vocabulary (<c>Button</c> variants and sizes, <c>Input</c>, <c>Select</c>, labels), built from Arc
/// controls and tokens for the pricing and models views. It lives here (not in <c>Components/Kit</c>, which another package owns) and is
/// internal; a control the shared kit later gains should replace the matching helper.
/// </summary>
// ARC-GAP: the web's <Button icon=…> / size="sm" / variant="plain|glass|destructive" has no Arc equivalent beyond Variant + Size; the
// icon-with-text content is composed here.
internal static class PricingUi
{
    /// <summary>Web <c>Button</c> variant names mapped to Arc's (<c>ARC_VARIANT</c>: primary, glass → secondary, plain → ghost, destructive → danger).</summary>
    public enum Look
    {
        /// <summary>The one main action (accent fill).</summary>
        Primary,

        /// <summary>The default: a quiet bordered button (<c>glass</c>).</summary>
        Glass,

        /// <summary>No chrome (<c>plain</c>).</summary>
        Plain,

        /// <summary>A destructive action.</summary>
        Destructive,
    }

    /// <summary>A button; <paramref name="text"/> null makes it an icon-only square (web <c>iconOnly</c>).</summary>
    public static ArcButton Button(string? text, Look look = Look.Glass, LucideIconKind? icon = null, bool small = false, Action? click = null, string? name = null)
    {
        var button = new ArcButton
        {
            Variant = look switch { Look.Primary => ButtonVariant.Primary, Look.Plain => ButtonVariant.Ghost, Look.Destructive => ButtonVariant.Danger, _ => ButtonVariant.Secondary },
            Size = small ? ControlSize.Small : ControlSize.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (text is null)
        {
            button.Padding = new Thickness(0);
            button.Width = small ? 28 : 32;
            button.Content = icon is { } i ? Icon(i, small ? 12 : 16) : null;
        }
        else if (icon is { } k)
        {
            button.Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { Icon(k, small ? 14 : 14), new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
            };
        }
        else button.Content = text;
        if (name is not null || text is null) AutomationProperties.SetName(button, name ?? text);
        if (click is not null) button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A Lucide glyph in the current foreground.</summary>
    public static LucideIcon Icon(LucideIconKind kind, double size) => new() { Kind = kind, Size = size, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>A single-line text box (web <c>Input</c>) whose label is only the accessible name unless <paramref name="visibleLabel"/>.</summary>
    public static Input TextBox(string? text, Action<string>? changed = null, double? width = null, bool mono = false, string? name = null, string? placeholder = null, bool visibleLabel = false)
    {
        var input = new Input { Text = text ?? "", Label = name, IsLabelVisible = visibleLabel, Placeholder = placeholder, VerticalAlignment = VerticalAlignment.Center };
        if (width is { } w) input.Width = w;
        if (mono) Mono(input);
        if (name is not null) AutomationProperties.SetName(input, name);
        if (changed is not null) input.TextChanged += (_, _) => changed(input.Text ?? "");
        return input;
    }

    /// <summary>The web's <c>mono</c> input look: the token monospace font at 12 px.</summary>
    public static T Mono<T>(T control) where T : Avalonia.Controls.TextBox
    {
        control.Token(Avalonia.Controls.TextBox.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        control.FontSize = 12;
        return control;
    }

    /// <summary>A decimal box (<see cref="NumberBox"/>) showing <paramref name="value"/>, calling <paramref name="changed"/> with each valid edit.</summary>
    public static NumberBox Number(decimal? value, Action<decimal?>? changed, double? width = null, string? name = null, bool integer = false, decimal? minimum = null, bool visibleLabel = false)
    {
        var box = new NumberBox { IsInteger = integer, Minimum = minimum, VerticalAlignment = VerticalAlignment.Center, Label = name, IsLabelVisible = visibleLabel };
        if (width is { } w) box.Width = w;
        if (name is not null) AutomationProperties.SetName(box, name);
        box.Value = value;
        if (changed is not null) box.ValueEdited += (_, v) => changed(v);
        return box;
    }

    /// <summary>A choice field (web <c>Select</c>) over <paramref name="options"/> with <paramref name="value"/> selected.</summary>
    public static Select Choice(IEnumerable<SelectOption> options, string? value, Action<string>? changed, double? width = null, string? placeholder = null, string? name = null)
    {
        var list = options.ToList();
        var select = new Select { ItemsSource = list, VerticalAlignment = VerticalAlignment.Center };
        if (placeholder is not null) select.Placeholder = placeholder;
        if (width is { } w) select.Width = w;
        if (name is not null) AutomationProperties.SetName(select, name);
        if (value is not null && list.Any(o => Equals(o.Value, value))) select.SelectedValue = value;
        if (changed is not null)
            select.SelectionChanged += (_, _) =>
            {
                if (select.SelectedValue is string v) changed(v);
            };
        return select;
    }

    /// <summary>A text block in a token size and color (trims like the web's <c>truncate</c>).</summary>
    public static TextBlock Text(string? text, ArcDesignTokenKind size, ArcDesignTokenKind color, FontWeight weight = FontWeight.Normal, bool mono = false)
    {
        var block = KitStyle.Text(text, size, color, weight);
        if (mono) block.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        return block;
    }

    /// <summary>
    /// A checkbox with its label to the right, centered on the 32 px hit area like the web's (<c>.copy</c>: label padding-top =
    /// (control height - line) / 2). Arc's own label slot is offset for a 44 px hit area, so at the web's 32 px density it sits low.
    /// Clicking the text toggles the box.
    /// </summary>
    // ARC-GAP: Checkbox.Content is positioned for ControlSizeMD = 44; with the web's 32 the label drops ~6px below the square.
    public static Control LabeledCheckbox(string label, bool isChecked, Action<bool> changed)
    {
        var box = new Checkbox { IsChecked = isChecked, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(box, label);
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        var text = KitStyle.Text(label, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
        text.Margin = new Thickness(1, 0, 0, 0);
        text.PointerPressed += (_, _) => box.IsChecked = box.IsChecked != true;
        text.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), HorizontalAlignment = HorizontalAlignment.Left };
        row.Children.Add(box);
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }

    /// <summary>
    /// Keeps a <see cref="Table"/> in its table layout whatever its width. The web folds a table into two-line rows only when
    /// the VIEWPORT is at most 620 px wide (a media query, so never in a desktop window), while Arc folds by the table's own width and
    /// exposes the breakpoint only as an internal property; a narrow column (the simulator's card) must still show a table.
    /// </summary>
    // ARC-GAP: Table.CompactBreakpoint is internal; set through the property registry. Make it public (or width-independent) upstream.
    public static Table WithoutCompactLayout(this Table table)
    {
        if (AvaloniaPropertyRegistry.Instance.FindRegistered(table, "CompactBreakpoint") is { } property) table.SetValue(property, 0d);
        return table;
    }

    /// <summary>Web <c>text-[12px] text-[var(--text-secondary)]</c>.</summary>
    public static TextBlock Secondary12(string? text) => Muted(text, ArcDesignTokenKind.TextSecondary, 12);

    /// <summary>Web <c>text-[12px] text-[var(--text-muted)]</c>.</summary>
    public static TextBlock Muted12(string? text) => Muted(text, ArcDesignTokenKind.TextMuted, 12);

    private static TextBlock Muted(string? text, ArcDesignTokenKind color, double size)
    {
        var block = KitStyle.Text(text, ArcDesignTokenKind.TypeXS, color);
        block.FontSize = size;
        block.TextTrimming = TextTrimming.None;
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    /// <summary>Web <c>rounded-[var(--radius-panel)] border border-[var(--border)]</c> with <paramref name="padding"/>.</summary>
    public static Border Panel(Control child, Thickness padding)
    {
        var border = new Border { BorderThickness = new Thickness(1), Padding = padding, Child = child };
        border.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border).Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);
        return border;
    }

    /// <summary>A two-column grid with the web's <c>grid-cols-2 gap-4</c> (or three columns).</summary>
    public static Grid Columns(int count, params Control[] cells)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        for (var i = 0; i < count; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i].VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(cells[i], i % count);
            Grid.SetRow(cells[i], i / count);
            if (i % count == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            grid.Children.Add(cells[i]);
        }
        return grid;
    }

    /// <summary>
    /// A labelled field row for a control that has no label of its own (web <c>Field</c>): the label above, the control, then the
    /// hint or the error below. Arc's inputs and selects carry their own label, so this is only for a composite.
    /// </summary>
    public static Control Labelled(string label, Control editor, string? hint = null, string? error = null) => FormField.Wrap(label, editor, hint, error);
}
