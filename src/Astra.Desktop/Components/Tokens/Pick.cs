using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using TKCode.Arc;

namespace Astra.Desktop.Components.Tokens;

/// <summary>
/// Builders for the choice controls the pages share, with the web's <c>Segmented</c> / <c>Select</c> call shape: options, the
/// selected value, one callback. The callback fires only for a change the user made (setting the selection from code stays quiet).
/// </summary>
public static class Pick
{
    /// <summary>A segmented control (web <c>Segmented</c>); <paramref name="name"/> is its accessible name.</summary>
    public static SegmentedControl Segmented(IEnumerable<SegmentedOption> options, string selected, Action<string> onChange, string? name = null)
    {
        var control = new SegmentedControl { ItemsSource = options.ToList() };
        if (name is not null) Avalonia.Automation.AutomationProperties.SetName(control, name);
        // web `.root { background: var(--surface-muted) }` = the label at 6%, a tint over the wallpaper; Arc's SurfaceMuted is opaque.
        void Tint() => control.Background = new SolidColorBrush(control.ActualThemeVariant == ThemeVariant.Dark
            ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(13, 0, 0, 0));
        Tint();
        control.ActualThemeVariantChanged += (_, _) => Tint();
        // An icon-only segment (label "") keeps Arc's 6px label/accessory gap; the web drops the empty text node, so the segment is
        // 12 + icon + 12. Hide the empty label presenter once the segments exist.
        control.Loaded += (_, _) =>
        {
            // (Arc's segment type is internal: its label presenter is found by its template part name.)
            foreach (var presenter in control.GetVisualDescendants().OfType<ContentPresenter>())
                if (presenter.Name == "PART_Label" && presenter.Content is "") presenter.IsVisible = false;
        };
        control.SelectedValue = selected;
        ContainBringIntoView(control);
        var current = selected;
        control.SelectionChanged += (_, _) =>
        {
            if (control.SelectedValue is not string value || value == current) return;
            current = value;
            onChange(value);
        };
        return control;
    }

    /// <summary>
    /// Stops <paramref name="control"/>'s own items from asking their ancestors to scroll them into view. Arc's segmented control and
    /// reorderable list bring their selected / realized item into view on load, which bubbles past them and scrolled a sheet (the token
    /// settings opened at its pool section instead of its top) or the page (plan §27, <c>PageUi.ContainBringIntoView</c>).
    /// </summary>
    public static T ContainBringIntoView<T>(T control) where T : ItemsControl
    {
        control.AddHandler(Control.RequestBringIntoViewEvent, (object? _, RequestBringIntoViewEventArgs e) =>
        {
            if (e.TargetObject is Control target && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(target), control)) e.Handled = true;
        });
        return control;
    }

    /// <summary>A select (web <c>Select</c>) over (value, label) options; <paramref name="selected"/> null shows the placeholder. Arc's theme gives a select a 180 px minimum width; the web's fills its cell, so it is reset here.</summary>
    public static Select Select(IEnumerable<(string Value, string Label)> options, string? selected, Action<string> onChange, string? name = null, string? placeholder = null)
    {
        var control = new Select { ItemsSource = options.Select(o => new SelectOption(o.Value, o.Label)).ToList(), Placeholder = placeholder, MinWidth = 0 };
        if (name is not null) Avalonia.Automation.AutomationProperties.SetName(control, name);
        if (selected is not null) control.SelectedValue = selected;
        var current = selected;
        control.SelectionChanged += (_, _) =>
        {
            if (control.SelectedValue is not string value || value == current) return;
            current = value;
            onChange(value);
        };
        return control;
    }
}
