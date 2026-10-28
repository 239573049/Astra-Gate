using Astra.Desktop.Core.Pricing;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using TKCode.Arc;

namespace Astra.Desktop.Components.Pricing;

/// <summary>
/// The web's <c>NumberInput</c> (web/src/components/ui/controls.tsx): an optional number in a right-aligned text box — an empty box is
/// <c>null</c>, which Arc's <c>NumberField</c> (steppers, always a value) cannot express, so it is Arc's <see cref="Input"/> with decimal
/// handling. The value is a <see cref="decimal"/> parsed by <see cref="PriceNumber"/>, so <c>0.000003</c> and <c>1e-7</c> are exact and
/// nothing is rounded through a float; the text the person typed is never rewritten while they type (<c>1.</c> stays <c>1.</c>).
/// Text that is not a number marks the box invalid and keeps the last good <see cref="Value"/> (the web ignores such an edit).
/// </summary>
public class NumberBox : Input
{
    /// <summary>The number, or null while the box is empty. Two-way.</summary>
    public static readonly StyledProperty<decimal?> ValueProperty =
        AvaloniaProperty.Register<NumberBox, decimal?>(nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>Only whole numbers are valid (token counts, thresholds).</summary>
    public static readonly StyledProperty<bool> IsIntegerProperty = AvaloniaProperty.Register<NumberBox, bool>(nameof(IsInteger));

    /// <summary>Smallest valid value (the web's <c>min</c>, which only styles the browser spinner; here it also marks the box invalid).</summary>
    public static readonly StyledProperty<decimal?> MinimumProperty = AvaloniaProperty.Register<NumberBox, decimal?>(nameof(Minimum));

    private bool _writing;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Input);

    /// <inheritdoc cref="ValueProperty"/>
    public decimal? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <inheritdoc cref="IsIntegerProperty"/>
    public bool IsInteger { get => GetValue(IsIntegerProperty); set => SetValue(IsIntegerProperty, value); }

    /// <inheritdoc cref="MinimumProperty"/>
    public decimal? Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    /// <summary>Raised when a valid edit (or an empty box) changes the value, with the new value. Not raised for a change made through <see cref="Value"/>.</summary>
    public event EventHandler<decimal?>? ValueEdited;

    /// <summary>A right-aligned, label-less box with tabular figures.</summary>
    public NumberBox()
    {
        IsLabelVisible = false;
        FontFeatures = FontFeatureCollection.Parse("tnum");
    }

    /// <summary>A box named <paramref name="name"/> for assistive technology (the web's <c>aria-label</c>).</summary>
    public NumberBox(string name, double? width = null) : this()
    {
        Label = name;
        if (width is { } w) Width = w;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty && !_writing)
        {
            // set from outside: show it unless the text already means this value
            var shown = ParseText(out var ok);
            if (!ok || shown != Value) SetText(Value is { } v ? PriceNumber.ToText(v) : "");
            IsInvalid = false;
        }
        else if (change.Property == TextProperty && !_writing) OnTextEdited();
    }

    private void SetText(string text)
    {
        _writing = true;
        try
        {
            Text = text;
        }
        finally
        {
            _writing = false;
        }
    }

    private decimal? ParseText(out bool ok)
    {
        ok = PriceNumber.TryParse(Text, out var parsed);
        return parsed;
    }

    private void OnTextEdited()
    {
        var parsed = ParseText(out var ok);
        if (ok && parsed is { } d && (IsInteger && d != decimal.Truncate(d) || Minimum is { } min && d < min)) ok = false;
        IsInvalid = !ok;
        if (!ok) return;
        _writing = true;
        try
        {
            Value = parsed;
        }
        finally
        {
            _writing = false;
        }
        ValueEdited?.Invoke(this, parsed);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(Avalonia.Controls.Primitives.TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        // right-align the digits only: TextBox.TextAlignment is inherited by the label rows too
        if (e.NameScope.Find<Avalonia.Controls.Presenters.TextPresenter>("PART_TextPresenter") is { } presenter) presenter.TextAlignment = TextAlignment.Right;
    }

    /// <inheritdoc />
    protected override void OnTextInput(TextInputEventArgs e)
    {
        // the browser's number input refuses anything but digits, a point, a sign and an exponent mark
        if (e.Text is { Length: > 0 } t && t.Any(c => !(char.IsAsciiDigit(c) || c is '.' or '-' or '+' or 'e' or 'E'))) return;
        base.OnTextInput(e);
    }
}
