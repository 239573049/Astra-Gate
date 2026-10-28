using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using TKCode.Arc;

namespace Astra.Desktop.Components.Tokens;

/// <summary>
/// An optional number (web <c>NumberInput</c>: an empty field is null, "no limit"). Arc's <c>NumberField</c> always holds a number and
/// has no empty state (ARC-GAP: a nullable NumberField), so this is Arc's <see cref="Input"/> that parses what is typed:
/// a valid number at or above <see cref="Minimum"/> sets <see cref="Value"/>, an empty field clears it, anything else marks the field
/// invalid and leaves the last good value. Integers only unless <see cref="AllowFraction"/>.
/// </summary>
public sealed class NumberBox : Input
{
    private bool _setting;

    /// <summary>The value; null while the field is empty.</summary>
    public static readonly StyledProperty<decimal?> ValueProperty = AvaloniaProperty.Register<NumberBox, decimal?>(nameof(Value));

    /// <summary>The smallest accepted value (web <c>min</c>).</summary>
    public static readonly StyledProperty<decimal> MinimumProperty = AvaloniaProperty.Register<NumberBox, decimal>(nameof(Minimum), 0);

    /// <summary>Whether a fraction may be typed (cost limits: web <c>step="any"</c>); counts are whole.</summary>
    public static readonly StyledProperty<bool> AllowFractionProperty = AvaloniaProperty.Register<NumberBox, bool>(nameof(AllowFraction));

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Input);

    /// <inheritdoc cref="ValueProperty"/>
    public decimal? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <inheritdoc cref="MinimumProperty"/>
    public decimal Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }

    /// <inheritdoc cref="AllowFractionProperty"/>
    public bool AllowFraction { get => GetValue(AllowFractionProperty); set => SetValue(AllowFractionProperty, value); }

    /// <summary>Raised when the user changed the value.</summary>
    public event Action<decimal?>? ValueEdited;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ValueProperty && !_setting) ShowValue();
        else if (change.Property == TextProperty && !_setting) Parse();
    }

    private void ShowValue()
    {
        _setting = true;
        try
        {
            Text = Value?.ToString("0.##########", CultureInfo.InvariantCulture) ?? "";
            IsInvalid = false;
        }
        finally
        {
            _setting = false;
        }
    }

    private void Parse()
    {
        var text = (Text ?? "").Trim();
        decimal? parsed;
        if (text.Length == 0) parsed = null;
        else if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v) && v >= Minimum && (AllowFraction || v == Math.Floor(v))) parsed = v;
        else
        {
            IsInvalid = true;
            return;
        }
        IsInvalid = false;
        if (parsed == Value) return;
        _setting = true;
        try
        {
            Value = parsed;
        }
        finally
        {
            _setting = false;
        }
        ValueEdited?.Invoke(parsed);
    }
}
