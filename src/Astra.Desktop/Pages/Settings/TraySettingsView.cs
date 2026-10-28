using Astra.Desktop.Components.Kit;
using Astra.Desktop.Components.Settings;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// Settings › Tray panel (web <c>TraySettings</c>): what the menu bar shows next to the icon, which blocks the panel shows, and a live preview
/// of the panel itself. Changes go to the desktop host (which stores them in the prefs file) and reach the open panel at once; the host's
/// <c>PrefsChanged</c> keeps this tab and the panel in step.
/// </summary>
public sealed class TraySettingsView : LiveView
{
    private const double SideBySideFrom = 760;
    private const double PreviewWidth = 392;

    private TrayPrefs _prefs = TrayPrefs.Default;
    private TrayPanelState _state = new(false, null, null, false, null);
    private RadioCards _titleCards = null!;
    private readonly Dictionary<TraySection, ControlledSwitch> _switches = [];
    private Border _previewBody = null!;
    private Grid _grid = null!;
    private Control? _previewCard;
    private bool _quiet;

    /// <summary>Creates the view.</summary>
    public TraySettingsView(PageContext context) : base(context)
    {
    }

    /// <inheritdoc />
    protected override void Build()
    {
        _switches.Clear();
        _titleCards = new RadioCards { MinColumnWidth = 150 };
        Avalonia.Automation.AutomationProperties.SetName(_titleCards, Loc.T("settings.tray.titleHeading"));
        foreach (var mode in TrayLogic.TitleModes)
            _titleCards.Items.Add(new RadioCardOption(TrayLogic.Name(mode), TrayLogic.TitleLabel(mode).Text, TrayLogic.TitleHint(mode).Text));
        _titleCards.SelectionChanged += (_, _) =>
        {
            if (_quiet || _titleCards.SelectedValue is not string name) return;
            var mode = TrayLogic.TitleModes.First(m => TrayLogic.Name(m) == name);
            _ = SetPrefsAsync(TrayLogic.WithTitle(_prefs, mode));
        };

        var left = new StackPanel { Spacing = 16, MinWidth = 0 };
        left.Children.Add(Card(Loc.T("settings.tray.titleHeading"), Loc.T("settings.tray.titleHint"), new Border { Padding = new Thickness(16), Child = _titleCards }));
        var usage = new StackPanel();
        foreach (var section in TrayLogic.UsageSections) usage.Children.Add(SectionRow(section, usage.Children.Count > 0));
        left.Children.Add(Card(Loc.T("settings.tray.usageHeading"), null, usage));
        left.Children.Add(Card(Loc.T("settings.tray.clientsHeading"), null, SectionRow(TraySection.Clients, false)));
        left.Children.Add(Ui.Button(Loc.T("settings.tray.reset"), () => _ = SetPrefsAsync(TrayPrefs.Default), ButtonVariant.Ghost, LucideIconKind.RotateCcw));

        _previewBody = new Border { Padding = new Thickness(16, 20), HorizontalAlignment = HorizontalAlignment.Stretch };
        _previewBody.Token(Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        var footer = Ui.GroupNote(Loc.T("settings.tray.previewFooter"), ArcDesignTokenKind.TextMuted, 11);
        _previewCard = Card(Loc.T("settings.tray.previewHeading"), Loc.T("settings.tray.previewHint"), Ui.Stack(0, _previewBody, TopBorder(footer)));

        _grid = new Grid { MaxWidth = 1120, HorizontalAlignment = HorizontalAlignment.Stretch };
        Layout(left);
        _grid.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) Layout(left);
        };
        Content = _grid;
    }

    private bool _sideBySide;

    private void Layout(Control left)
    {
        var wide = _grid.Bounds.Width >= SideBySideFrom || _grid.Bounds.Width == 0 && Bounds.Width >= SideBySideFrom;
        var preview = TrayPreview.Factory is not null;
        if (_grid.Children.Count > 0 && wide == _sideBySide && _grid.Children.Contains(left)) return;
        _sideBySide = wide;
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();
        left.Margin = default;
        if (wide && preview)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(PreviewWidth)));
            _grid.ColumnSpacing = 16;
            Grid.SetColumn(_previewCard!, 1);
            _previewCard!.VerticalAlignment = VerticalAlignment.Top;
            _grid.Children.Add(left);
            _grid.Children.Add(_previewCard);
        }
        else
        {
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _grid.RowSpacing = 16;
            _grid.Children.Add(left);
            if (preview)
            {
                Grid.SetRow(_previewCard!, 1);
                _grid.Children.Add(_previewCard!);
            }
        }
    }

    private static Control TopBorder(Control content)
    {
        var border = new Border { BorderThickness = new Thickness(0, 0.5, 0, 0), Child = content };
        border.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        return border;
    }

    private Control SectionRow(TraySection section, bool divider)
    {
        var sw = new ControlledSwitch();
        Avalonia.Automation.AutomationProperties.SetName(sw, TrayLogic.SectionLabel(section).Text);
        sw.ToggleRequested += on => _ = SetPrefsAsync(TrayLogic.WithSection(_prefs, section, on));
        _switches[section] = sw;
        var row = RowBuilder.Row(TrayLogic.SectionLabel(section).Text, TrayLogic.SectionHint(section).Text, sw);
        return divider ? Ui.Stack(0, Ui.Divider(), row) : row;
    }

    /// <summary>A card with the tab's name as a small kicker, a title and an optional hint (web <c>Card</c> of TraySettings).</summary>
    private static Control Card(string title, string? hint, Control body)
    {
        var header = new StackPanel { Margin = new Thickness(16, 12) };
        header.Children.Add(KitStyle.Text(Loc.T("settings.tab.tray"), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted));
        header.Children.Add(KitStyle.Text(title, ArcDesignTokenKind.TypeBase, ArcDesignTokenKind.Foreground, Avalonia.Media.FontWeight.SemiBold));
        if (!string.IsNullOrEmpty(hint))
        {
            var h = Ui.Note(hint, ArcDesignTokenKind.TextSecondary);
            h.Margin = new Thickness(0, 2, 0, 0);
            header.Children.Add(h);
        }
        var headBox = new Border { BorderThickness = new Thickness(0, 0, 0, 0.5), Child = header };
        headBox.Token(Border.BorderBrushProperty, ArcDesignTokenKind.Border);
        return Ui.Card(Ui.Stack(0, headBox, body));
    }

    /// <inheritdoc />
    protected override void Refresh()
    {
        _quiet = true;
        try
        {
            _titleCards.SelectedValue = TrayLogic.Name(_prefs.Title);
            foreach (var (section, sw) in _switches) sw.SetValue(TrayLogic.IsOn(_prefs, section));
        }
        finally
        {
            _quiet = false;
        }
        _previewBody.Child = TrayPreview.Factory?.Invoke(_prefs, _state);
        if (_previewBody.Child is Control c)
        {
            c.HorizontalAlignment = HorizontalAlignment.Center;
            c.Width = 360;
        }
    }

    /// <inheritdoc />
    protected override void OnShown()
    {
        var tray = Context.Host.Tray;
        tray.PrefsChanged += OnPrefs;
        tray.StateChanged += OnState;
        _ = LoadAsync();
    }

    /// <inheritdoc />
    protected override void OnHidden()
    {
        var tray = Context.Host.Tray;
        tray.PrefsChanged -= OnPrefs;
        tray.StateChanged -= OnState;
    }

    private async Task LoadAsync()
    {
        var prefs = await Context.Host.Tray.GetPrefsAsync();
        var state = await Context.Host.Tray.GetStateAsync();
        Dispatcher.UIThread.Post(() =>
        {
            _prefs = prefs;
            _state = state;
            RequestRefresh();
        });
    }

    private void OnPrefs(TrayPrefs prefs) => Dispatcher.UIThread.Post(() =>
    {
        _prefs = prefs;
        RequestRefresh();
    });

    private void OnState(TrayPanelState state) => Dispatcher.UIThread.Post(() =>
    {
        _state = state;
        RequestRefresh();
    });

    private async Task SetPrefsAsync(TrayPrefs next)
    {
        _prefs = next;
        Refresh();
        var stored = await Context.Host.Tray.SetPrefsAsync(next);
        _prefs = stored;
        Refresh();
    }
}
