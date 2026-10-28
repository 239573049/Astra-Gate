using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using TabItem = TKCode.Arc.TabItem;

namespace Astra.Desktop.Pages.Settings;

/// <summary>
/// The Settings body: Arc <see cref="Tabs"/> over the tab views (web <c>Tabs</c> / <c>TabsList</c> with icons). Arc's tabs host one
/// content at a time, so a tab's view is attached — and its queries observed — only while it is the selected one.
/// </summary>
public sealed class SettingsView : UserControl
{
    private readonly SettingsPageViewModel _page;
    private Tabs _tabs = null!;
    private bool _quiet;

    /// <summary>Builds the view for <paramref name="page"/>.</summary>
    public SettingsView(SettingsPageViewModel page)
    {
        _page = page;
        Build();
        _page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsPageViewModel.Tab)) Dispatcher.UIThread.Post(() => Select(_page.Tab));
        };
    }

    /// <summary>The view of one tab (scenes and tests open sheets through it).</summary>
    public Control? TabView(SettingsTab tab) => _tabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Tag, tab))?.Content as Control;

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

    private void Build()
    {
        _tabs = new Tabs { ContentMargin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(_tabs, Loc.T("nav.settings"));
        _tabs.Items.Add(Item(SettingsTab.General, LucideIconKind.SlidersHorizontal, "settings.tab.general", () => new GeneralSettingsView(_page)));
        _tabs.Items.Add(Item(SettingsTab.Notifications, LucideIconKind.Bell, "settings.tab.notifications", () => new NotificationSettingsView(_page.Context!, _page.Api)));
        _tabs.Items.Add(Item(SettingsTab.Remote, LucideIconKind.Globe, "settings.tab.remote", () => new RemoteAccessView(_page.Context!, _page.Api)));
        if (_page.TrayAvailable) _tabs.Items.Add(Item(SettingsTab.Tray, LucideIconKind.PanelTop, "settings.tab.tray", () => new TraySettingsView(_page.Context!)));
        Select(_page.Tab);
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_quiet || _tabs.SelectedItem is not TabItem { Tag: SettingsTab tab }) return;
            _page.Tab = tab;
        };
        Content = _tabs;
    }

    private void Select(SettingsTab tab)
    {
        var index = _tabs.Items.OfType<TabItem>().ToList().FindIndex(t => Equals(t.Tag, tab));
        if (index < 0 || _tabs.SelectedIndex == index) return;
        _quiet = true;
        try { _tabs.SelectTab(index); }
        finally { _quiet = false; }
    }

    private static TabItem Item(SettingsTab tab, LucideIconKind icon, string key, Func<Control> view)
        => new()
        {
            Tag = tab,
            Header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new LucideIcon { Kind = icon, Size = 14, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = Loc.T(key), VerticalAlignment = VerticalAlignment.Center },
                },
            },
            Content = view(),
        };
}
