using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;
using ArcSettingsPage = TKCode.Arc.SettingsPage;
using SettingsRow = Astra.Desktop.Components.Kit.SettingsRow;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// The Settings body in the compact layout: Arc.Blocks' <see cref="ArcSettingsPage"/> — a category list on the left under a gliding
/// selection pill (arrow keys move between categories), the selected <see cref="SettingsSection"/> in its own scroll area on the right,
/// and one column with a row of pills when the window is narrow. The block is tuned to sit on the page (no card frame, no tinted
/// navigation, no title or section header: the shell header already says "Settings") and to the dense metrics the rows use
/// (<see cref="SettingsRow.DenseProperty"/>). Only the selected section's view is attached, so only its queries are observed.
/// </summary>
public sealed class SettingsView : UserControl
{
    /// <summary>The widest a category's rows grow (the tray panel's preview grid is allowed its own, wider, width).</summary>
    private const double ContentWidth = 680;

    private readonly SettingsPageViewModel _page;
    private readonly Dictionary<SettingsTab, SettingsSection> _sections = [];
    private ArcSettingsPage _settings = null!;
    private bool _quiet;

    /// <summary>Builds the view for <paramref name="page"/>.</summary>
    public SettingsView(SettingsPageViewModel page)
    {
        _page = page;
        SettingsRow.SetDense(this, true);
        Build();
        _page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsPageViewModel.Tab)) Dispatcher.UIThread.Post(Select);
        };
    }

    /// <summary>The view of one category (scenes and tests open sheets through it).</summary>
    public Control? TabView(SettingsTab tab) => _sections.GetValueOrDefault(tab)?.Tag as Control;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Loc.LanguageChanged += OnLanguage;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Loc.LanguageChanged -= OnLanguage;
    }

    private void OnLanguage() => Dispatcher.UIThread.Post(Build);

    /// <summary>The categories in navigation order.</summary>
    private IEnumerable<(SettingsTab Tab, LucideIconKind Icon, string Key)> Entries()
    {
        yield return (SettingsTab.General, LucideIconKind.SlidersHorizontal, "settings.tab.general");
        yield return (SettingsTab.Gateway, LucideIconKind.Network, "settings.gateway");
        yield return (SettingsTab.Models, LucideIconKind.Boxes, "settings.tab.models");
        yield return (SettingsTab.Network, LucideIconKind.Globe, "settings.tab.network");
        yield return (SettingsTab.Usage, LucideIconKind.Gauge, "settings.tab.usage");
        yield return (SettingsTab.Logging, LucideIconKind.ScrollText, "settings.logging");
        yield return (SettingsTab.Notifications, LucideIconKind.Bell, "settings.tab.notifications");
        yield return (SettingsTab.Remote, LucideIconKind.Earth, "settings.tab.remote");
        if (_page.TrayAvailable) yield return (SettingsTab.Tray, LucideIconKind.PanelTop, "settings.tab.tray");
        yield return (SettingsTab.About, LucideIconKind.Info, "settings.about");
    }

    private void Build()
    {
        _sections.Clear();
        _settings = new ArcSettingsPage
        {
            Background = Brushes.Transparent,
            BorderThickness = default,
            CornerRadius = default,
            Labels = new SettingsPageLabels { NavigationName = Loc.T("nav.settings") },
        };
        Tune(_settings);
        foreach (var (tab, icon, key) in Entries())
        {
            var view = Create(tab);
            var section = new SettingsSection
            {
                Header = Loc.T(key),
                Icon = Lucide.Geometry(icon),
                Tag = view,
                Content = tab == SettingsTab.Tray ? view : Capped(view),
            };
            _sections[tab] = section;
            _settings.Items.Add(section);
        }
        Select();
        _settings.SelectionChanged += (_, _) =>
        {
            if (_quiet || _settings.SelectedItem is not SettingsSection section) return;
            _page.Tab = _sections.First(p => ReferenceEquals(p.Value, section)).Key;
        };
        Content = _settings;
    }

    private void Select()
    {
        if (!_sections.TryGetValue(_page.Tab, out var section) || ReferenceEquals(_settings.SelectedItem, section)) return;
        _quiet = true;
        try { _settings.Select(section); }
        finally { _quiet = false; }
    }

    /// <summary>
    /// Fits the block to this page: its tokens are dynamic resources, so values set here win over the theme's for this instance only
    /// (narrower navigation, 34 px items, a 16 px glyph, content next to the list); the title and section header are hidden by style.
    /// </summary>
    private static void Tune(ArcSettingsPage page)
    {
        var r = page.Resources;
        r[SettingsPageTokens.Key(SettingsPageTokenKind.NavWidth)] = 188.0;
        r[SettingsPageTokens.Key(SettingsPageTokenKind.NavBackground)] = Colors.Transparent;
        r[SettingsPageTokens.Key(SettingsPageTokenKind.NavListInset)] = new Thickness(0, 0, 14, 12);
        r[SettingsPageTokens.Key(SettingsPageTokenKind.NavPillRadius)] = new CornerRadius(9);
        r[SettingsPageTokens.Key(SettingsPageTokenKind.ContentInset)] = new Thickness(24, 0, 12, 40);
        r[SettingsPageTokens.Key(SettingsPageTokenKind.CompactNavListInset)] = new Thickness(0, 0, 0, 12);
        r[SettingsPageTokens.Key(SettingsPageTokenKind.CompactContentInset)] = new Thickness(0, 16, 12, 40);
        r[SettingsPageNavItemTokens.Key(SettingsPageNavItemTokenKind.ItemHeight)] = 34.0;
        r[SettingsPageNavItemTokens.Key(SettingsPageNavItemTokenKind.ItemInset)] = new Thickness(10, 0);
        r[SettingsPageNavItemTokens.Key(SettingsPageNavItemTokenKind.ItemRadius)] = new CornerRadius(9);
        r[SettingsPageNavItemTokens.Key(SettingsPageNavItemTokenKind.GlyphSize)] = 16.0;
        r[SettingsPageNavItemTokens.Key(SettingsPageNavItemTokenKind.IconMargin)] = new Thickness(0, 0, 10, 0);
        r[SettingsPageNavItemTokens.Key(SettingsPageNavItemTokenKind.FocusRingRadius)] = new CornerRadius(12);
        r[SettingsSectionTokens.Key(SettingsSectionTokenKind.ContentMargin)] = new Thickness(0);

        // The shell header already names the page, and the selected item names the category: no block title, no section header.
        page.Styles.Add(new Style(x => x.OfType<ArcSettingsPage>().Template().OfType<TextBlock>().Name("PART_Title"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, false) },
        });
        page.Styles.Add(new Style(x => x.OfType<SettingsSection>().Template().OfType<ContentPresenter>().Name("PART_HeaderPresenter"))
        {
            Setters = { new Setter(Visual.IsVisibleProperty, false) },
        });
    }

    private Control Create(SettingsTab tab) => tab switch
    {
        SettingsTab.Gateway => new GatewaySettingsView(_page),
        SettingsTab.Models => new ModelSettingsView(_page),
        SettingsTab.Network => new NetworkSettingsView(_page),
        SettingsTab.Usage => new UsageSettingsView(_page),
        SettingsTab.Logging => new LoggingSettingsView(_page),
        SettingsTab.About => new AboutSettingsView(_page),
        SettingsTab.Notifications => new NotificationSettingsView(_page.Context!, _page.Api),
        SettingsTab.Remote => new RemoteAccessView(_page.Context!, _page.Api),
        SettingsTab.Tray => new TraySettingsView(_page.Context!),
        _ => new GeneralSettingsView(_page),
    };

    /// <summary>
    /// Holds <paramref name="view"/> to <see cref="ContentWidth"/> at the left of the content area, next to the navigation (a star column
    /// with a maximum leaves the rest of a wide window empty instead of centring the rows away from the list).
    /// </summary>
    private static Control Capped(Control view)
    {
        var cap = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star) { MaxWidth = ContentWidth } } };
        cap.Children.Add(view);
        return cap;
    }
}
