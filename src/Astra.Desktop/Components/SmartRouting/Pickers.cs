using Astra.Desktop.Core.I18n;
using Avalonia.Controls;
using TKCode.Arc;

namespace Astra.Desktop.Components.SmartRouting;

/// <summary>
/// Web <c>Select</c> (overlays.tsx): a plain select for a few options, a searchable combobox for many (more than 12), values travelling as
/// strings. The control is built without a visible label (the name is the accessible one), as rows and toolbars show their own.
/// </summary>
internal sealed class Picker
{
    /// <summary>Web <c>COMBOBOX_THRESHOLD</c>.</summary>
    public const int ComboboxThreshold = 12;

    private readonly Select? _select;
    private readonly Combobox? _combo;
    private bool _syncing;

    /// <summary>The control to place.</summary>
    public Control Control => (Control?)_select ?? _combo!;

    /// <summary>Raised when the user chose a value.</summary>
    public event Action<string>? Chosen;

    /// <summary>Creates a picker over <paramref name="options"/> showing <paramref name="value"/>.</summary>
    public Picker(string name, IReadOnlyList<SelectOption> options, string? value, string? placeholder, double? width = null)
    {
        if (options.Count > ComboboxThreshold)
        {
            _combo = new Combobox { Placeholder = placeholder ?? Loc.T("common.search"), EmptyText = Loc.T("common.noResults"), ItemsSource = options.ToList() };
            if (width is { } w) _combo.Width = w;
            _combo.SelectionChanged += OnChanged;
            Avalonia.Automation.AutomationProperties.SetName(_combo, name);
        }
        else
        {
            _select = new Select { Placeholder = placeholder ?? Loc.T("common.select"), ItemsSource = options.ToList() };
            if (width is { } w) _select.Width = w;
            _select.SelectionChanged += OnChanged;
            Avalonia.Automation.AutomationProperties.SetName(_select, name);
        }
        _signature = string.Join('\n', options.Select(o => $"{o.Value}\t{o.Label}"));
        Value = value;
    }

    /// <summary>The value shown; setting it does not raise <see cref="Chosen"/>.</summary>
    public string? Value
    {
        get => (_select?.SelectedValue ?? _combo?.SelectedValue) as string;
        set
        {
            _syncing = true;
            try
            {
                if (_select is not null) _select.SelectedValue = string.IsNullOrEmpty(value) ? null : value;
                else _combo!.SelectedValue = string.IsNullOrEmpty(value) ? null : value;
            }
            finally { _syncing = false; }
        }
    }

    private string _signature = "";

    /// <summary>Replaces the options when they differ from the current ones (a grade was renamed) and shows <paramref name="value"/>; no event.</summary>
    public void SetOptions(IReadOnlyList<SelectOption> options, string? value)
    {
        var signature = string.Join('\n', options.Select(o => $"{o.Value}\t{o.Label}"));
        if (signature != _signature)
        {
            _syncing = true;
            try
            {
                if (_select is not null) _select.ItemsSource = options.ToList();
                else _combo!.ItemsSource = options.ToList();
            }
            finally { _syncing = false; }
            _signature = signature;
        }
        Value = value;
    }

    private void OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (Value is { } v) Chosen?.Invoke(v);
    }
}
