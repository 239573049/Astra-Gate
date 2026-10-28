using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Metadata;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Kit;

/// <summary>Web <c>SectionTitle</c>: a 13px medium heading with an optional action on the right, above a card or list.</summary>
public sealed class SectionTitle : UserControl
{
    /// <summary>The heading text.</summary>
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<SectionTitle, string?>(nameof(Text));

    /// <summary>Right side: a button, link or menu.</summary>
    public static readonly StyledProperty<object?> ActionProperty = AvaloniaProperty.Register<SectionTitle, object?>(nameof(Action));

    private readonly TextBlock _text = KitStyle.Text(null, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium);
    private readonly ContentPresenter _action = new() { VerticalAlignment = VerticalAlignment.Center };

    /// <inheritdoc cref="TextProperty"/>
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    /// <inheritdoc cref="ActionProperty"/>
    public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }

    /// <summary>An empty title.</summary>
    public SectionTitle()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(4, 0, 4, 8) };
        Grid.SetColumn(_action, 1);
        grid.Children.Add(_text);
        grid.Children.Add(_action);
        Content = grid;
    }

    /// <summary>A title with text.</summary>
    public SectionTitle(string text, object? action = null) : this()
    {
        Text = text;
        Action = action;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) _text.Text = Text;
        else if (change.Property == ActionProperty) _action.Content = Action;
    }
}

/// <summary>Web <c>DetailSection</c>: a titled block inside a sheet (a 12px secondary heading row over its content).</summary>
public sealed class DetailSection : UserControl
{
    /// <summary>The heading text.</summary>
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<DetailSection, string?>(nameof(Title));

    /// <summary>Right side of the heading row.</summary>
    public static readonly StyledProperty<object?> ActionProperty = AvaloniaProperty.Register<DetailSection, object?>(nameof(Action));

    /// <summary>The block's content.</summary>
    public static readonly StyledProperty<object?> BodyProperty = AvaloniaProperty.Register<DetailSection, object?>(nameof(Body));

    private readonly TextBlock _title = KitStyle.Text(null, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, FontWeight.Medium);
    private readonly ContentPresenter _action = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentPresenter _body = new();

    /// <inheritdoc cref="TitleProperty"/>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <inheritdoc cref="ActionProperty"/>
    public object? Action { get => GetValue(ActionProperty); set => SetValue(ActionProperty, value); }

    /// <inheritdoc cref="BodyProperty"/>
    [Content]
    public object? Body { get => GetValue(BodyProperty); set => SetValue(BodyProperty, value); }

    /// <summary>An empty section.</summary>
    public DetailSection()
    {
        _title.FontSize = KitStyle.Type12;
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 8) };
        Grid.SetColumn(_action, 1);
        head.Children.Add(_title);
        head.Children.Add(_action);
        Content = new StackPanel { Children = { head, _body } };
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty) _title.Text = Title;
        else if (change.Property == ActionProperty) _action.Content = Action;
        else if (change.Property == BodyProperty) _body.Content = Body;
    }
}

/// <summary>Web <c>KV</c>: a label on the left, a right-aligned selectable value on the right (12px; mono 11px for ids).</summary>
public sealed class KeyValueRow : UserControl
{
    /// <summary>The key.</summary>
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<KeyValueRow, string?>(nameof(Label));

    /// <summary>The value (text, so it can be selected and copied).</summary>
    public static readonly StyledProperty<string?> ValueProperty = AvaloniaProperty.Register<KeyValueRow, string?>(nameof(Value));

    /// <summary>Monospace value (ids, URLs, keys).</summary>
    public static readonly StyledProperty<bool> IsMonoProperty = AvaloniaProperty.Register<KeyValueRow, bool>(nameof(IsMono));

    private readonly TextBlock _label = KitStyle.Text(null, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
    private readonly SelectableTextBlock _value = new() { TextAlignment = TextAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    /// <inheritdoc cref="LabelProperty"/>
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <inheritdoc cref="ValueProperty"/>
    public string? Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <inheritdoc cref="IsMonoProperty"/>
    public bool IsMono { get => GetValue(IsMonoProperty); set => SetValue(IsMonoProperty, value); }

    /// <summary>An empty row.</summary>
    public KeyValueRow()
    {
        _label.FontSize = KitStyle.Type12;
        _value.Token(TextBlock.ForegroundProperty, ArcDesignTokenKind.Foreground);
        ApplyMono();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12, Margin = new Thickness(0, 4) };
        Grid.SetColumn(_value, 1);
        grid.Children.Add(_label);
        grid.Children.Add(_value);
        Content = grid;
    }

    /// <summary>A row with label and value.</summary>
    public KeyValueRow(string label, string? value, bool mono = false) : this()
    {
        Label = label;
        Value = value;
        IsMono = mono;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LabelProperty) _label.Text = Label;
        else if (change.Property == ValueProperty) _value.Text = Value;
        else if (change.Property == IsMonoProperty) ApplyMono();
    }

    private void ApplyMono()
    {
        _value.FontSize = IsMono ? 11 : KitStyle.Type12;
        if (IsMono) _value.Token(TextBlock.FontFamilyProperty, ArcDesignTokenKind.MonoFontFamily);
        else _value.ClearValue(TextBlock.FontFamilyProperty);
    }
}
