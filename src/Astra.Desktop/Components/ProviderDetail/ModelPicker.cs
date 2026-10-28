using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using SettingsGroup = Astra.Desktop.Components.Kit.SettingsGroup;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.ProviderDetail;

/// <summary>
/// The test-model picker (web <c>ModelPicker</c> in ProviderTestDialog.tsx): a searchable list of the provider's models that also accepts any
/// model id typed by hand. Opening shows every model; typing filters. Keyboard: Down / Up move, Enter picks, Escape closes.
/// </summary>
// ARC-GAP: Arc's Combobox is select-only (no free text), so this is an Arc Input with a popup list of its own.
public sealed class ModelPicker : UserControl
{
    private readonly Input _input;
    private readonly Popup _popup;
    private readonly StackPanel _list = new();
    private IReadOnlyList<string> _models = [];
    private string? _query;
    private int _active;
    private bool _writing;
    private string? _codeValue;

    /// <summary>An empty picker.</summary>
    public ModelPicker()
    {
        _input = new Input { Placeholder = Loc.T("providers.testDialog.modelPlaceholder"), IsLabelVisible = false, Label = Loc.T("providers.testDialog.model"), Padding = new Thickness(10, 0, 32, 0) };
        PdUi.Mono(_input);
        var chevron = PdUi.Button(null, PdUi.Look.Plain, LucideIconKind.ChevronDown, small: true, name: Loc.T("providers.testDialog.showModels"), click: () => { if (_popup!.IsOpen) _popup.IsOpen = false; else OpenList(); });
        chevron.HorizontalAlignment = HorizontalAlignment.Right;
        chevron.Margin = new Thickness(0, 0, 2, 0);
        var listCard = new Border { BorderThickness = new Thickness(0.5), Padding = new Thickness(0, 4), CornerRadius = new CornerRadius(8), Child = new ScrollViewer { MaxHeight = 256, Content = _list } };
        listCard.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceRaised).Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        listCard.BoxShadow = BoxShadows.Parse("0 8 24 0 #26000000");
        _popup = new Popup { PlacementTarget = _input, Placement = PlacementMode.BottomEdgeAlignedLeft, IsLightDismissEnabled = true, VerticalOffset = 4, Child = listCard };
        // keyboard focus opens the list like the web's onFocus; the dialog's initial focus does not
        _input.GotFocus += (_, e) =>
        {
            if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional) OpenList();
        };
        _input.PointerPressed += (_, _) => OpenList();
        _input.TextChanged += (_, _) =>
        {
            // TextChanged can arrive after the setter returned: a text equal to what code wrote is not typing
            if (_writing || _input.Text == _codeValue) return;
            _codeValue = null;
            ValueChanged?.Invoke(_input.Text ?? "");
            _query = _input.Text ?? "";
            _active = 0;
            _popup.IsOpen = true;
            Fill();
        };
        _input.KeyDown += OnKey;
        Content = new Panel { Children = { _input, chevron, _popup } };
    }

    /// <summary>The typed or picked model id changed.</summary>
    public event Action<string>? ValueChanged;

    /// <summary>The ids offered.</summary>
    public IReadOnlyList<string> Models
    {
        get => _models;
        set
        {
            _models = value;
            if (_popup.IsOpen) Fill();
        }
    }

    /// <summary>The model id shown.</summary>
    public string Value
    {
        get => _input.Text ?? "";
        set
        {
            if (Value == value) return;
            _writing = true;
            _codeValue = value;
            try { _input.Text = value; }
            finally { _writing = false; }
        }
    }

    private List<string> Matches => _query is null ? _models.ToList() : _models.Where(id => id.Contains(_query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    private void OpenList()
    {
        _query = null;
        _active = Math.Max(0, _models.ToList().IndexOf(Value));
        _popup.MinWidth = _input.Bounds.Width;
        _popup.IsOpen = true;
        Fill();
    }

    private void Pick(string id)
    {
        Value = id;
        ValueChanged?.Invoke(id);
        _query = null;
        _popup.IsOpen = false;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        var matches = Matches;
        if (e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            if (!_popup.IsOpen) { OpenList(); return; }
            if (matches.Count == 0) return;
            _active = (_active + (e.Key == Key.Down ? 1 : -1) + matches.Count) % matches.Count;
            Fill();
        }
        else if (e.Key == Key.Enter && _popup.IsOpen)
        {
            e.Handled = true;
            if (_active < matches.Count) Pick(matches[_active]);
        }
        else if (e.Key == Key.Escape) _popup.IsOpen = false;
    }

    private void Fill()
    {
        _list.Children.Clear();
        var matches = Matches;
        if (matches.Count == 0)
        {
            _list.Children.Add(new Border { Padding = new Thickness(12, 8), Child = PdUi.Text(Loc.T("providers.testDialog.noModels"), 12, ArcDesignTokenKind.TextMuted) });
            return;
        }
        for (var i = 0; i < matches.Count; i++)
        {
            var id = matches[i];
            var index = i;
            var tint = new Border { IsVisible = false };
            tint.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
            var row = new Panel
            {
                Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                Children = { tint, new Border { Padding = new Thickness(12, 6), Child = PdUi.Text(id, 12, ArcDesignTokenKind.Foreground, mono: true) } },
            };
            row.PointerEntered += (_, _) => { _active = index; Highlight(); };
            row.PointerPressed += (_, e) => { e.Handled = true; Pick(id); };
            _list.Children.Add(row);
        }
        Highlight();
    }

    private void Highlight()
    {
        for (var i = 0; i < _list.Children.Count; i++)
        {
            if (_list.Children[i] is Panel { Children: [Border tint, ..] }) tint.IsVisible = i == _active;
        }
    }
}
