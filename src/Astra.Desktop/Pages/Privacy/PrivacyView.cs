using Astra.Desktop.Components.Privacy;
using Astra.Desktop.Core.I18n;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using TKCode.Arc;
using TabItem = TKCode.Arc.TabItem;

namespace Astra.Desktop.Pages.Privacy;

/// <summary>The Privacy body: Arc tabs over the four sub-pages; only the selected one is attached (and watches its queries).</summary>
public sealed class PrivacyView : UserControl
{
    private readonly PrivacyPageViewModel _page;
    private Tabs _tabs = null!;
    private bool _quiet;

    /// <summary>Builds the view for <paramref name="page"/>.</summary>
    public PrivacyView(PrivacyPageViewModel page)
    {
        _page = page;
        Build();
        _page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PrivacyPageViewModel.Tab)) Dispatcher.UIThread.Post(() => Select(_page.Tab));
        };
    }

    /// <summary>The view of one tab.</summary>
    public Control? TabView(PrivacyTab tab) => _tabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Tag, tab))?.Content as Control;

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
        var context = _page.Context!;
        _tabs = new Tabs { ContentMargin = new Thickness(0, 20, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(_tabs, Loc.T("nav.privacy"));
        _tabs.Items.Add(Item(PrivacyTab.Overview, new PrivacyOverviewView(context, _page.Api)));
        _tabs.Items.Add(Item(PrivacyTab.Logs, new PrivacyEventLogView(context, _page.Api)));
        _tabs.Items.Add(Item(PrivacyTab.Rules, new PrivacyRulesView(context, _page.Api)));
        _tabs.Items.Add(Item(PrivacyTab.Tester, new DryRunTesterView(context, _page.Api)));
        Select(_page.Tab);
        _tabs.SelectionChanged += (_, _) =>
        {
            if (_quiet || _tabs.SelectedItem is not TabItem { Tag: PrivacyTab tab }) return;
            _page.Tab = tab;
        };
        Content = _tabs;
    }

    private void Select(PrivacyTab tab)
    {
        var index = _tabs.Items.OfType<TabItem>().ToList().FindIndex(t => Equals(t.Tag, tab));
        if (index < 0 || _tabs.SelectedIndex == index) return;
        _quiet = true;
        try { _tabs.SelectTab(index); }
        finally { _quiet = false; }
    }

    private static TabItem Item(PrivacyTab tab, Control view)
        => new() { Tag = tab, Header = Loc.T("privacy.tab." + PrivacyPageViewModel.NameOf(tab)), Content = view };
}
