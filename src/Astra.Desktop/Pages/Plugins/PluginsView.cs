using Astra.Desktop.Components.Plugins;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TabItem = TKCode.Arc.TabItem;

namespace Astra.Desktop.Pages.Plugins;

/// <summary>
/// The body of the Plugins page: the "plugins are off" warning, the three tabs (installed with its count, market, runtime) and the page's
/// sheets. The tab views are built once and keep their own state; the page model decides which queries each keeps alive.
/// </summary>
internal sealed class PluginsView : LiveView
{
    private readonly PluginsPage _page;
    private readonly Tabs _tabs = new();
    private readonly TabItem _installed = new();
    private readonly TabItem _market = new();
    private readonly TabItem _runtime = new();
    private readonly StackPanel _top = new();
    private bool _syncing;

    /// <summary>Builds the view for <paramref name="page"/>.</summary>
    public PluginsView(PluginsPage page)
    {
        _page = page;
        var model = page.Model;
        _installed.Content = new InstalledTabView(model) { Margin = new Thickness(0, 4, 0, 0) };
        _market.Content = new MarketTabView(model) { Margin = new Thickness(0, 4, 0, 0) };
        _runtime.Content = new RuntimeTabView(model) { Margin = new Thickness(0, 4, 0, 0) };
        _tabs.Items.Add(_installed);
        _tabs.Items.Add(_market);
        _tabs.Items.Add(_runtime);
        _tabs.SelectTab((int)model.Tab);
        _tabs.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _tabs.SelectedIndex is >= 0 and <= 2) model.Tab = (PluginTab)_tabs.SelectedIndex;
        };
        _tabs.ContainBringIntoView();
        Avalonia.Automation.AutomationProperties.SetName(_tabs, Loc.T("nav.plugins"));

        var root = new StackPanel { MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(_top);
        root.Children.Add(_tabs);
        var host = new Panel();
        host.Children.Add(root);
        host.Children.Add(new PluginSheets(model));
        Content = host;

        Watch(model.Api.Plugins, "Data");
        Watch(model, nameof(PluginsPageModel.Tab));
    }

    /// <inheritdoc />
    protected override void Rebuild()
    {
        _syncing = true;
        try
        {
            var data = _page.Model.Api.Plugins.Data;
            _top.Children.Clear();
            if (data?.Off == true)
            {
                var off = PageUi.Alert(AlertTone.Warning, Loc.T("plugins.off"), Loc.T("plugins.offDetail"));
                off.Margin = new Thickness(0, 0, 0, 16);
                _top.Children.Add(off);
            }
            _installed.Header = InstalledHeader(data?.Plugins.Count ?? 0);
            _market.Header = Loc.T("plugins.tab.market");
            _runtime.Header = Loc.T("plugins.tab.runtime");
            if (_tabs.SelectedIndex != (int)_page.Model.Tab) _tabs.SelectTab((int)_page.Model.Tab);
        }
        finally { _syncing = false; }
    }

    // Web: the label, then the count in a small tabular figure at 60% opacity.
    private static object InstalledHeader(int count)
    {
        if (count == 0) return Loc.T("plugins.tab.installed");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new TextBlock { Text = Loc.T("plugins.tab.installed"), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }
}
