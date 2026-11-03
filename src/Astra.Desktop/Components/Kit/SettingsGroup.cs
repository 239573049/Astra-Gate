using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Metadata;
using Avalonia.Layout;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

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

    /// <summary>An explanation of the whole group, shown as a tooltip on an ⓘ after the title (the compact layout's footer; needs a title).</summary>
    public static readonly StyledProperty<string?> HintProperty = AvaloniaProperty.Register<SettingsGroup, string?>(nameof(Hint));

    private readonly Controls _rows = new();
    private readonly StackPanel _stack = new();
    private readonly TextBlock _title;
    private readonly TextBlock _footer;
    private readonly StackPanel _heading;
    private readonly LucideIcon _hintIcon;
    private readonly StackPanel _body;

    /// <inheritdoc cref="HintProperty"/>
    public string? Hint { get => GetValue(HintProperty); set => SetValue(HintProperty, value); }

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
        _title.VerticalAlignment = VerticalAlignment.Center;
        _hintIcon = new LucideIcon { Kind = LucideIconKind.Info, Size = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 0, 0), IsVisible = false };
        _hintIcon.Token(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty, ArcDesignTokenKind.TextMuted);
        _heading = new StackPanel
        {
            Orientation = Orientation.Horizontal, Background = Avalonia.Media.Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 4, 8), IsVisible = false, Children = { _title, _hintIcon },
        };
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

        _body = new StackPanel { Margin = new Thickness(0, 0, 0, 20), Children = { _heading, card, _footer } };
        Content = _body;
        _rows.CollectionChanged += (_, _) => Rebuild();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleProperty)
        {
            _title.Text = Title;
            _heading.IsVisible = KitStyle.HasText(Title);
        }
        else if (change.Property == HintProperty)
        {
            _hintIcon.IsVisible = KitStyle.HasText(Hint);
            ToolTip.SetTip(_heading, KitStyle.HasText(Hint) ? SettingsRow.HintTip(Hint!) : null);
        }
        else if (change.Property == SettingsRow.DenseProperty)
        {
            var dense = change.GetNewValue<bool>();
            _body.Margin = new Thickness(0, 0, 0, dense ? 16 : 20);
            _heading.Margin = new Thickness(4, 0, 4, dense ? 6 : 8);
            _footer.Margin = new Thickness(4, dense ? 6 : 8, 4, 0);
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
