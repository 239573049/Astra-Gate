using System.Globalization;
using Astra.Desktop.Components.Plugins;
using Avalonia;
using Avalonia.Input;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.SmartRouting;

/// <summary>
/// Web <c>NumberInput</c>: an optional number — an empty box is <c>null</c>, which Arc's <c>NumberField</c> cannot express, so it is
/// Arc's <c>Input</c> that only takes a number, right-aligned with tabular figures; Up / Down step it like the browser's number box
/// (not below <c>min</c>).
/// </summary>
internal sealed class NumberInput : Input
{
    private readonly double _step;
    private readonly double? _min;
    private double? _value;
    private bool _syncing;

    /// <summary>The value; null while the box is empty.</summary>
    public double? Number
    {
        get => _value;
        set
        {
            _value = value;
            var text = value is null ? "" : value.Value.ToString("0.################", CultureInfo.InvariantCulture);
            if (Text == text) return;
            _syncing = true;
            Text = text;
            _syncing = false;
        }
    }

    /// <summary>Raised when the user (not code) changed the number.</summary>
    public event Action<double?>? NumberChanged;

    /// <summary>Creates the box; <paramref name="name"/> is its accessible name.</summary>
    public NumberInput(string name, double? value, double step, double? min, double width = 112)
    {
        _step = step;
        _min = min;
        Label = name;
        IsLabelVisible = false;
        Width = width;
        TextAlignment = Avalonia.Media.TextAlignment.Right;
        HorizontalAlignment = HorizontalAlignment.Right;
        Number = value;
        TextChanged += (_, _) =>
        {
            if (_syncing) return;
            var raw = (Text ?? "").Trim();
            if (raw.Length == 0) Apply(null);
            else if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) Apply(n);
            // anything else is not a number yet ("-", "1e"): keep what is typed, keep the last value
        };
        KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down)) return;
            var next = (_value ?? 0) + (e.Key == Key.Up ? _step : -_step);
            if (_min is { } floor) next = Math.Max(floor, next);
            next = Math.Round(next, 6);
            Number = next;
            NumberChanged?.Invoke(next);
            e.Handled = true;
        };
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        // TextAlignment is inherited: right-aligning the figures would also right-align the field's label and description above / below
        // the box. Only the text presenter takes it (by template binding); the field around it stays left-aligned like the web's.
        if (e.NameScope.Find("PART_Field") is Avalonia.AvaloniaObject field)
            field.SetValue(Avalonia.Controls.TextBlock.TextAlignmentProperty, Avalonia.Media.TextAlignment.Left);
    }

    private void Apply(double? n)
    {
        _value = n;
        NumberChanged?.Invoke(n);
    }

    /// <summary>The integer view of <see cref="Number"/> for the whole-number fields (the server's ints).</summary>
    public int? Integer => _value is { } v ? (int)Math.Round(v) : null;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Input);
}
