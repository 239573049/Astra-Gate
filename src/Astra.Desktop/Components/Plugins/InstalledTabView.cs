using Astra.Desktop.Components.Kit;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Avalonia.Controls;
using Avalonia.Layout;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Components.Plugins;

/// <summary>
/// The "installed" tab (web <c>InstalledList</c>): the magpie import banner, then the plugins in the user's order in one card (or the
/// empty state with a way to the market), then the hint that the order is the middleware chain's.
/// </summary>
internal sealed class InstalledTabView : LiveView
{
    private readonly PluginsPageModel _model;

    /// <summary>Creates the tab over the page model.</summary>
    public InstalledTabView(PluginsPageModel model)
    {
        _model = model;
        Watch(model.Api.Plugins, "Data", "Status");
        Watch(model.Api.Host, "Data");
        Watch(model.Api.MagpiePreview, "Data");
    }

    /// <inheritdoc />
    protected override void Rebuild()
    {
        var q = _model.Api.Plugins;
        var stack = new StackPanel { Spacing = 0 };
        if (q.HasData && q.Data is { } data)
        {
            // web: the error alert and the last good list are both shown when a refetch fails.
            if (q.IsError) stack.Children.Add(PageUi.Alert(AlertTone.Danger, Loc.T("common.error"), PluginsPageModel.ErrorText(q.Error)));
            stack.Children.Add(List(data));
        }
        else if (q.IsError) stack.Children.Add(PageUi.Alert(AlertTone.Danger, Loc.T("common.error"), PluginsPageModel.ErrorText(q.Error)));
        else stack.Children.Add(new LoadingState(4));
        Content = stack;
    }

    private Control List(Astra.Api.Contracts.PluginsDto data)
    {
        var plugins = data.Plugins;
        var stack = new StackPanel { Spacing = 16 };
        if (PluginRows.ImportableCount(_model.Api.MagpiePreview.Data) is { } count) stack.Children.Add(ImportBanner(count));

        if (plugins.Count == 0)
        {
            var empty = new EmptyStateView(Loc.T("plugins.empty"), Loc.T("plugins.emptyDetail"), PageUi.Icon(LucideIconKind.Puzzle, 24),
                PageUi.Button(Loc.T("plugins.browseMarket"), _model.BrowseMarket, ButtonVariant.Primary));
            stack.Children.Add(PageUi.Card(empty, new Avalonia.Thickness(24)));
            return stack;
        }

        var host = _model.Api.Host.Data;
        var rows = plugins.Select((p, i) => PluginRowView.Create(PluginRows.Build(p, host, data.Preferences), _model, i, plugins.Count));
        stack.Children.Add(PageUi.Card(PageUi.Divided(rows)));
        if (PluginRows.ShowsOrderHint(plugins)) stack.Children.Add(PageUi.Note(Loc.T("plugins.orderHint")));
        return stack;
    }

    private Control ImportBanner(int count)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        var text = PageUi.Text(Loc.T("plugins.magpie.banner", ("count", count)), ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, wrap: true);
        text.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        var button = PageUi.Button(Loc.T("plugins.magpie.import"), () => _model.Importing = true, size: ControlSize.Small, icon: LucideIconKind.Download);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return PageUi.Card(grid, new Avalonia.Thickness(16));
    }
}
