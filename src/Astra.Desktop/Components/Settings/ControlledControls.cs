using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TKCode.Arc;
using ArcSwitch = TKCode.Arc.Switch;

namespace Astra.Desktop.Components.Settings;

/// <summary>
/// A switch that, like the web's controlled <c>Switch</c>, never flips itself: a click only asks (<see cref="ToggleRequested"/>);
/// the page decides — after a confirmation, after the server saved — and then calls <see cref="SetValue"/>. A refused confirmation
/// therefore leaves it where it was, with no flicker.
/// </summary>
public sealed class ControlledSwitch : ArcSwitch
{
    /// <summary>Raised on a click with the state the user asked for.</summary>
    public event Action<bool>? ToggleRequested;

    /// <summary>Sets the shown state without asking.</summary>
    public void SetValue(bool on)
    {
        if (IsChecked != on) IsChecked = on;
    }

    /// <inheritdoc />
    protected override void Toggle() => ToggleRequested?.Invoke(IsChecked != true);
}

/// <summary>An Arc segmented control with the web <c>Segmented</c>'s controlled behaviour: <see cref="Chose"/> only fires for the user's pick.</summary>
public sealed class ControlledSegmented : SegmentedControl
{
    private bool _quiet;

    /// <summary>Raised with the value the user picked.</summary>
    public event Action<string>? Chose;

    /// <summary>Creates the control with its options (value, label).</summary>
    public ControlledSegmented(IEnumerable<(string Value, string Label)> options, string? value)
    {
        // A selection change must not scroll the page the control sits in (the request to bring the item into view bubbles).
        AutoScrollToSelectedItem = false;
        ItemsSource = options.Select(o => new SegmentedOption(o.Value, o.Label)).ToList();
        _quiet = true;
        SelectedValue = value;
        _quiet = false;
        SelectionChanged += (_, _) =>
        {
            if (!_quiet && SelectedValue is string v) Chose?.Invoke(v);
        };
    }

    /// <summary>Shows a value without raising <see cref="Chose"/>.</summary>
    public void SetValue(string? value)
    {
        _quiet = true;
        try { SelectedValue = value; }
        finally { _quiet = false; }
    }
}

/// <summary>An Arc select with the web <c>Select</c>'s controlled behaviour: <see cref="Chose"/> only fires for the user's pick.</summary>
public sealed class ControlledSelect : Select
{
    private bool _quiet;

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(Select);

    /// <summary>Raised with the value the user picked.</summary>
    public event Action<string>? Chose;

    /// <summary>Creates the select with its options (value, label).</summary>
    public ControlledSelect(IEnumerable<(string Value, string Label)> options, string? value, double width = 160)
    {
        Width = width;
        MinWidth = 0; // Arc's theme sets MinWidth 180, which would win over the web's w-40 (160)
        AutoScrollToSelectedItem = false;
        ItemsSource = options.Select(o => new SelectOption(o.Value, o.Label)).ToList();
        _quiet = true;
        SelectedValue = value;
        _quiet = false;
        SelectionChanged += (_, _) =>
        {
            if (!_quiet && SelectedValue is string v) Chose?.Invoke(v);
        };
    }

    /// <summary>Shows a value without raising <see cref="Chose"/>.</summary>
    public void SetValue(string? value)
    {
        _quiet = true;
        try { SelectedValue = value; }
        finally { _quiet = false; }
    }
}

/// <summary>
/// Web <c>DeferredNumber</c> (SettingsPage): an Arc number field that saves shortly after the value settles, so a PATCH is not sent
/// for every keystroke or step. A new value from the server replaces the shown one.
/// </summary>
public sealed class DeferredNumberField : NumberField
{
    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(NumberField);

    private readonly DispatcherTimer _timer;
    private readonly Action<decimal> _commit;
    private decimal _shownFromServer;
    private bool _quiet;

    /// <summary>Creates the field.</summary>
    /// <param name="label">Accessible name (the row's label shows it).</param>
    /// <param name="value">The saved value.</param>
    /// <param name="commit">Saves a settled value.</param>
    /// <param name="suffix">Unit shown after the number (" s", " tokens").</param>
    /// <param name="step">Stepper step.</param>
    /// <param name="delay">How long the value must rest before it is saved.</param>
    public DeferredNumberField(string label, decimal value, Action<decimal> commit, string? suffix = null, decimal step = 1, TimeSpan? delay = null)
    {
        _commit = commit;
        Width = 144;
        Size = ControlSize.Small;
        Minimum = 0;
        Step = step;
        MaximumFractionDigits = 0;
        UseGrouping = false;
        if (suffix is not null) SuffixFormatter = _ => suffix;
        Avalonia.Automation.AutomationProperties.SetName(this, label);
        _quiet = true;
        Value = _shownFromServer = value;
        _quiet = false;
        _timer = new DispatcherTimer { Interval = delay ?? TimeSpan.FromMilliseconds(600) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            if (Value != _shownFromServer) _commit(Value);
        };
        ValueChanged += (_, _) =>
        {
            if (_quiet) return;
            _timer.Stop();
            if (Value != _shownFromServer) _timer.Start();
        };
    }

    /// <summary>The server's value changed: show it (and drop a pending save of the old edit when it caught up).</summary>
    public void SetSaved(decimal value)
    {
        if (value == _shownFromServer) return;
        _shownFromServer = value;
        _timer.Stop();
        _quiet = true;
        try { Value = value; }
        finally { _quiet = false; }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }
}

/// <summary>An Arc chip group (multi-select) with controlled behaviour: <see cref="Changed"/> only fires for the user's picks (web <c>ChipGroup</c>).</summary>
public sealed class ControlledChips : ChipGroup
{
    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(ChipGroup);

    private readonly List<string> _values;
    private bool _quiet;

    /// <summary>Raised with the selected values after the user toggled a chip.</summary>
    public event Action<IReadOnlyList<string>>? Changed;

    /// <summary>Creates the group from its options and the selected values.</summary>
    public ControlledChips(string label, IEnumerable<(string Value, string Label)> options, IEnumerable<string> selected)
    {
        Label = label;
        AutoScrollToSelectedItem = false;
        _values = [];
        foreach (var (value, text) in options)
        {
            _values.Add(value);
            Items.Add(new SelectItem { Content = text, Value = value });
        }
        SetValues(selected);
        SelectionChanged += (_, _) =>
        {
            if (!_quiet) Changed?.Invoke(SelectedValues.OfType<string>().ToList());
        };
    }

    /// <summary>Shows a selection without raising <see cref="Changed"/>.</summary>
    public void SetValues(IEnumerable<string> selected)
    {
        _quiet = true;
        try
        {
            Selection.Clear();
            var set = selected.ToHashSet();
            for (var i = 0; i < _values.Count; i++)
                if (set.Contains(_values[i])) Selection.Select(i);
        }
        finally
        {
            _quiet = false;
        }
    }
}
