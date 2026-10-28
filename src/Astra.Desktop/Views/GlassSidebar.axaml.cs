using Astra.Desktop.Core.I18n;
using Astra.Desktop.Platform;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Views;

/// <summary>
/// The shell's navigation panel as the web draws it (<c>Sidebar</c> in web/src/components/layout/AppShell.tsx): floating glass, 224 px open
/// or the icon rail (78 where the traffic lights live inside it, else 68), icons that never move while only the labels fade, pill-shaped rows
/// with an accent wash for the current page. The window drag region and the traffic-light clearance are the header row's job.
/// </summary>
public partial class GlassSidebar : UserControl
{
    private sealed record Row(NavEntry Entry, Border Item, TextBlock Label);

    private readonly List<Row> _rows = new();
    private readonly List<(TextBlock Title, Border Hairline, string TitleKey)> _groupTitles = new();
    private readonly List<(Grid Content, Border Hairline)> _padded = new();
    private double _itemPad;
    private double _railWidth;
    private ServiceStatusView? _footer;
    private bool _collapsed;
    private string? _selectedKey;

    public GlassSidebar()
    {
        InitializeComponent();
        var spec = WindowChrome.Spec;
        Width = ChromeSpec.SidebarExpanded;
        Transitions = [new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = new CubicEaseOut() }];
        Logo.Source = ShellView.LoadLogo();
        BrandLabel.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }];
        BuildRows();
        ApplyTrafficLights(spec.NativeTrafficLights);
        ActualThemeVariantChanged += (_, _) => ApplyAppearance();
        ApplyAppearance();
        if (spec.HasAppDragRegion) WindowChrome.AttachDragRegion(HeaderRow);
    }

    /// <summary>Raised when a row is pressed.</summary>
    public event EventHandler<NavEntry>? ItemInvoked;

    public bool IsCollapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            var width = value ? _railWidth : ChromeSpec.SidebarExpanded;
            if (IsLoaded) Width = width;
            else
            {
                // The remembered state is applied before the first frame: no slide on startup.
                var transitions = Transitions;
                Transitions = null;
                Width = width;
                Transitions = transitions;
            }
            SyncCollapsed();
        }
    }

    public string? SelectedKey
    {
        get => _selectedKey;
        set
        {
            _selectedKey = value;
            foreach (var row in _rows) row.Item.Classes.Set("selected", row.Entry.Key == value);
        }
    }

    public ServiceStatusView? Footer
    {
        get => _footer;
        set
        {
            _footer = value;
            FooterHost.Content = value;
            ApplyAppearance();
            SyncCollapsed();
        }
    }

    /// <summary>Re-reads every translated label (nav rows, group titles).</summary>
    public void RefreshTexts()
    {
        foreach (var row in _rows)
        {
            row.Label.Text = Loc.T(row.Entry.LabelKey);
            ToolTip.SetTip(row.Item, _collapsed ? row.Label.Text : null);
        }
        foreach (var (title, _, key) in _groupTitles) title.Text = Loc.T(key);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The traffic lights live in the header row only in the app's main window, the one WindowChrome.Apply gave the macOS chrome to.
        // Any other host (headless scene capture, a test window) has none, so it gets the web's browser layout: the logo row and the
        // 68 px rail, like web AppShell's `isDesktop && isMac` test.
        var lights = WindowChrome.Spec.NativeTrafficLights && TopLevel.GetTopLevel(this) is MainWindow;
        ApplyTrafficLights(lights);
        // Chromium draws a 0.5px border as one device pixel (never thinner), so at 1x the web's glass edge is a full 1px line.
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        GlassPanel.BorderThickness = new Thickness(Math.Max(0.5, 1 / scaling));
    }

    /// <summary>Header row content, rail width and the icon column's inset for a window with or without native traffic lights.</summary>
    private void ApplyTrafficLights(bool lights)
    {
        // web: sidebarRailWidth() is 78 where the traffic lights sit inside the rail, else 68; ITEM_PAD keeps icons fixed in both states.
        _railWidth = lights ? 78 : 68;
        _itemPad = (_railWidth - 16 - 22) / 2;
        Brand.IsVisible = !lights;
        Brand.Margin = new Thickness((_railWidth - 24) / 2, 0, 0, 0);
        foreach (var (content, hairline) in _padded)
        {
            content.Margin = new Thickness(_itemPad, 0, 10, 0);
            hairline.Margin = new Thickness(_itemPad, 0, 0, 0);
        }
        if (_collapsed && !IsLoaded)
        {
            var transitions = Transitions;
            Transitions = null;
            Width = _railWidth;
            Transitions = transitions;
        }
        else if (_collapsed) Width = _railWidth;
    }

    private void BuildRows()
    {
        foreach (var group in NavTable.Groups)
        {
            if (group.TitleKey is { } titleKey)
            {
                var title = new TextBlock { Text = Loc.T(titleKey), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 0, 9) };
                // web `absolute bottom-1` on a 16.5px line box (11px text, inherited 1.5 leading): the glyphs sit 9px above the 24px row's bottom.
                title.Classes.Add("groupTitle");
                title.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }];
                // On the rail the title gives way to a short hairline above the group's icons.
                var hairline = new Border
                {
                    Height = 1, Width = 22, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(_itemPad, 0, 0, 0), Opacity = 0, IsHitTestVisible = false,
                    Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }],
                };
                hairline.Classes.Add("hair");
                _padded.Add((new Grid(), hairline));
                Groups.Children.Add(new Grid { Height = 24, Margin = new Thickness(0, 8, 0, 0), Children = { title, hairline } });
                _groupTitles.Add((title, hairline, titleKey));
            }
            foreach (var entry in group.Items) Groups.Children.Add(BuildRow(entry));
        }
    }

    private Border BuildRow(NavEntry entry)
    {
        var label = new TextBlock { Text = Loc.T(entry.LabelKey) };
        label.Classes.Add("label");
        label.Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }];
        var icon = new LucideIcon { Kind = entry.Icon, Size = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var content = new Grid
        {
            ColumnDefinitions = [new ColumnDefinition(22, GridUnitType.Pixel), new ColumnDefinition(GridLength.Star)],
            Margin = new Thickness(_itemPad, 0, 10, 0),
            Children = { icon },
        };
        label.Margin = new Thickness(10, 0, 0, 0);
        _padded.Add((content, new Border()));
        Grid.SetColumn(label, 1);
        content.Children.Add(label);
        var item = new Border
        {
            Child = new Grid { Children = { new Border { Classes = { "hover" } }, new Border { Classes = { "selection" } }, content } },
        };
        item.Classes.Add("navItem");
        item.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) ItemInvoked?.Invoke(this, entry);
        };
        _rows.Add(new Row(entry, item, label));
        return item;
    }

    private void SyncCollapsed()
    {
        BrandLabel.Opacity = _collapsed ? 0 : 1;
        foreach (var row in _rows)
        {
            row.Label.Opacity = _collapsed ? 0 : 1;
            ToolTip.SetTip(row.Item, _collapsed ? row.Label.Text : null);
        }
        foreach (var (title, hairline, _) in _groupTitles)
        {
            title.Opacity = _collapsed ? 0 : 1;
            hairline.Opacity = _collapsed ? 1 : 0;
        }
        if (_footer is not null) _footer.IsRail = _collapsed;
    }

    /// <summary>Glass tint, edge, highlight and shadow for the current light / dark appearance (<c>--glass-*</c>).</summary>
    private void ApplyAppearance()
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var glass = WallpaperPalette.Glass(dark);
        GlassPanel.Background = new SolidColorBrush(Color.FromUInt32(glass.Tint));
        GlassPanel.BorderBrush = new SolidColorBrush(Color.FromUInt32(glass.Edge));
        GlassPanel.BoxShadow = new BoxShadows(new BoxShadow { IsInset = true, OffsetX = 0, OffsetY = 1, Blur = 0, Spread = 0, Color = Color.FromUInt32(glass.Highlight) });
        ShadowHost.BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 8, Blur = 30, Spread = 0, Color = Color.FromUInt32(glass.Shadow) });
        _footer?.SetCardFill(new SolidColorBrush(Color.FromUInt32(WallpaperPalette.StatusCardFill(dark))));
    }
}
