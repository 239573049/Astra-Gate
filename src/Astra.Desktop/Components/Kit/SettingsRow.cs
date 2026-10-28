using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Metadata;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// One row of a <see cref="SettingsGroup"/> (web <c>Row</c>): optional icon, a label with an optional detail line, and trailing
/// controls (switches, buttons, values). A row with <see cref="Invoked"/> handlers or a <see cref="Command"/> is clickable
/// (hover tint, Enter / Space) and may contain its own controls, which keep their clicks.
/// </summary>
public sealed class SettingsRow : UserControl
{
    /// <summary>The row's name: a string, or any control.</summary>
    public static readonly StyledProperty<object?> LabelProperty = AvaloniaProperty.Register<SettingsRow, object?>(nameof(Label));

    /// <summary>A second, smaller line: a string, or any control.</summary>
    public static readonly StyledProperty<object?> DetailProperty = AvaloniaProperty.Register<SettingsRow, object?>(nameof(Detail));

    /// <summary>Leading glyph or avatar.</summary>
    public static readonly StyledProperty<object?> IconProperty = AvaloniaProperty.Register<SettingsRow, object?>(nameof(Icon));

    /// <summary>Highlights the row with the accent tint (web <c>selected</c>).</summary>
    public static readonly StyledProperty<bool> IsSelectedProperty = AvaloniaProperty.Register<SettingsRow, bool>(nameof(IsSelected));

    /// <summary>Makes the row clickable; the command runs on press (web <c>onClick</c>).</summary>
    public static readonly StyledProperty<System.Windows.Input.ICommand?> CommandProperty = AvaloniaProperty.Register<SettingsRow, System.Windows.Input.ICommand?>(nameof(Command));

    /// <summary>Raised when a clickable row is pressed.</summary>
    public static readonly RoutedEvent<RoutedEventArgs> InvokedEvent = RoutedEvent.Register<SettingsRow, RoutedEventArgs>(nameof(Invoked), RoutingStrategies.Bubble);

    private readonly ContentPresenter _icon = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentPresenter _label = new();
    private readonly ContentPresenter _detail = new();
    private readonly StackPanel _trailing = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly Controls _items = new();
    private readonly Border _surface;
    private bool _hover;

    /// <inheritdoc cref="LabelProperty"/>
    public object? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <inheritdoc cref="DetailProperty"/>
    public object? Detail { get => GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    /// <inheritdoc cref="IconProperty"/>
    public object? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <inheritdoc cref="IsSelectedProperty"/>
    public bool IsSelected { get => GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

    /// <inheritdoc cref="CommandProperty"/>
    public System.Windows.Input.ICommand? Command { get => GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <inheritdoc cref="InvokedEvent"/>
    public event EventHandler<RoutedEventArgs>? Invoked
    {
        add { AddHandler(InvokedEvent, value); _hasHandler = true; Refresh(); }
        remove { RemoveHandler(InvokedEvent, value); }
    }

    private bool _hasHandler;

    /// <summary>The trailing controls (web <c>children</c>; the XAML content).</summary>
    [Content]
    public Controls Trailing => _items;

    /// <summary>An empty row.</summary>
    public SettingsRow()
    {
        MinHeight = 44;
        _label.Content = null;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _label, _detail } };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(16, 8) };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_trailing, 2);
        grid.Children.Add(_icon);
        grid.Children.Add(text);
        grid.Children.Add(_trailing);
        _surface = new Border { MinHeight = 44, Background = Brushes.Transparent, Child = grid };
        Content = _surface;
        _icon.IsVisible = false;
        _icon.Margin = new Thickness(0, 0, 12, 0);
        _trailing.Margin = new Thickness(12, 0, 0, 0);
        _trailing.IsVisible = false;
        _detail.IsVisible = false;
        _items.CollectionChanged += (_, _) =>
        {
            _trailing.Children.Clear();
            foreach (var c in _items) _trailing.Children.Add(c);
            _trailing.IsVisible = _items.Count > 0;
        };
        PointerEntered += (_, _) => { _hover = true; Refresh(); };
        PointerExited += (_, _) => { _hover = false; Refresh(); };
        PointerPressed += OnPressed;
        KeyDown += OnKeyDown;
    }

    private static object? WithLine(object? content, double lineHeight)
    {
        if (content is TextBlock block) block.LineHeight = lineHeight;
        return content;
    }

    private bool IsInteractive => Command is not null || _hasHandler;

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        // Text lines get the web's body line height (1.45: 13 px → 19, 11 px → 16), so a two-line row is the web's 51 px.
        if (p == LabelProperty) _label.Content = WithLine(KitStyle.AsContent(Label, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground), 19);
        else if (p == DetailProperty)
        {
            _detail.Content = WithLine(KitStyle.AsContent(Detail, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary), 16);
            _detail.IsVisible = Detail is not null && Detail is not "";
        }
        else if (p == IconProperty)
        {
            _icon.Content = Icon;
            _icon.IsVisible = Icon is not null;
        }
        else if (p == IsSelectedProperty || p == CommandProperty) Refresh();
    }

    private void Refresh()
    {
        Focusable = IsInteractive;
        if (IsSelected) _surface.Token(Border.BackgroundProperty, ArcDesignTokenKind.AccentSubtle);
        else if (IsInteractive && _hover) _surface.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        else _surface.Background = Brushes.Transparent;
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsInteractive || e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // A press on an inner control (switch, button) belongs to that control.
        if (e.Source is Control source && source != this && IsInside(source)) return;
        Invoke();
    }

    private bool IsInside(Control source)
    {
        for (Visual? v = source; v is not null && v != this; v = v.GetVisualParent())
            if (v is Avalonia.Controls.Primitives.TemplatedControl or Avalonia.Controls.Primitives.ToggleButton && v != _surface) return true;
        return false;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsInteractive || e.Source != this || e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        Invoke();
    }

    private void Invoke()
    {
        RaiseEvent(new RoutedEventArgs(InvokedEvent, this));
        if (Command?.CanExecute(null) == true) Command.Execute(null);
    }
}
