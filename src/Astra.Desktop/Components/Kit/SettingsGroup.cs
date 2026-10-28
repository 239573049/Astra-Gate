using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Metadata;
using Avalonia.Layout;
using TKCode.Arc.DesignTokens;

namespace Astra.Desktop.Components.Kit;

/// <summary>
/// An iOS-style grouped list (web <c>Group</c>): an optional small title above, a rounded card of rows separated by hairlines,
/// an optional footnote below. Arc ships nothing for this on desktop (<c>MobileListGroup</c> is mobile only; plan inventory
/// GAP-2), so the kit builds it from tokens. Rows are usually <see cref="SettingsRow"/>s but any control goes in; the group
/// puts a separator between neighbours itself.
/// </summary>
public sealed class SettingsGroup : UserControl
{
    /// <summary>Small heading above the card (web <c>title</c>).</summary>
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<SettingsGroup, string?>(nameof(Title));

    /// <summary>Footnote under the card (web <c>footer</c>).</summary>
    public static readonly StyledProperty<string?> FooterProperty = AvaloniaProperty.Register<SettingsGroup, string?>(nameof(Footer));

    private readonly Controls _rows = new();
    private readonly StackPanel _stack = new();
    private readonly TextBlock _title;
    private readonly TextBlock _footer;

    /// <inheritdoc cref="TitleProperty"/>
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <inheritdoc cref="FooterProperty"/>
    public string? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    /// <summary>The rows (the XAML content).</summary>
    [Content]
    public Controls Rows => _rows;

    /// <summary>An empty group.</summary>
    public SettingsGroup()
    {
        _title = KitStyle.Text(null, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, Avalonia.Media.FontWeight.Medium);
        _title.FontSize = KitStyle.Type12;
        _title.Margin = new Thickness(4, 0, 4, 8);
        _title.IsVisible = false;
        _footer = KitStyle.Text(null, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted);
        _footer.TextTrimming = Avalonia.Media.TextTrimming.None;
        _footer.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _footer.Margin = new Thickness(4, 8, 4, 0);
        _footer.IsVisible = false;

        // web .card: surface fill, 0.5px separator edge, 14px radius, rows clipped to the corners
        var card = new Border { BorderThickness = new Thickness(0.5), ClipToBounds = true, Child = _stack };
        card.Token(Border.BackgroundProperty, ArcDesignTokenKind.Surface)
            .Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border)
            .Token(Border.CornerRadiusProperty, ArcDesignTokenKind.RadiusPanel);

        Content = new StackPanel { Margin = new Thickness(0, 0, 0, 20), Children = { _title, card, _footer } };
        _rows.CollectionChanged += (_, _) => Rebuild();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty)
        {
            _title.Text = Title;
            _title.IsVisible = KitStyle.HasText(Title);
        }
        else if (change.Property == FooterProperty)
        {
            _footer.Text = Footer;
            _footer.IsVisible = KitStyle.HasText(Footer);
        }
    }

    private void Rebuild()
    {
        _stack.Children.Clear();
        var first = true;
        foreach (var row in _rows)
        {
            if (!first)
            {
                var line = new Border { Height = 1, HorizontalAlignment = HorizontalAlignment.Stretch };
                line.Token(Border.BackgroundProperty, ArcDesignTokenKind.Border);
                _stack.Children.Add(line);
            }
            first = false;
            _stack.Children.Add(row);
        }
    }
}
