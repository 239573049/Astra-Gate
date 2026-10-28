using Astra.Desktop.Components.Layout;
using Astra.Desktop.Components.Plugins;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Plugins;
using Astra.Desktop.Shell;
using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Astra.Desktop.Pages.Plugins;

/// <summary>
/// The Plugins page (web <c>PluginsPage</c>): the header's "update all" and "add" buttons, and a <see cref="PluginsPageModel"/> that
/// owns the tab, the queries and the actions. Magpie-compatible plugins (<c>.plan/plugins.md</c>): what is installed, the market, and the
/// runtime (Bun and the plugin host).
/// </summary>
public sealed class PluginsPage : PageViewModel
{
    private readonly TKCode.Arc.Button _updateAll;
    private readonly TKCode.Arc.Button _add;

    /// <summary>Creates the page over the app context; <paramref name="param"/> is the tab (and a link's <c>?install=</c>).</summary>
    public PluginsPage(PageContext context, string? param) : base(AppRoute.Plugins, param, context)
    {
        var api = new PluginsApi(context.Api, context.Queries);
        Model = new PluginsPageModel(api, context.Feedback, context.Navigator, context.Queries, PluginRoute.Parse(param), context.Host.OpenPathAsync);
        OnDispose(Model.Dispose);

        _updateAll = PageUi.Button(null, () => _ = Model.UpdateAllAsync(), icon: LucideIconKind.RefreshCw);
        _add = PageUi.Button(null, () => Model.Adding = true, ButtonVariant.Primary, icon: LucideIconKind.Plus);
        SetTexts();
        SetHeaderActions(new ActionGroup(8, _updateAll, _add));
        Subscribe(api.Plugins, (_, e) =>
        {
            if (e.PropertyName == "Data") _updateAll.IsEnabled = api.Plugins.Data?.Plugins.Count > 0;
        });
        _updateAll.IsEnabled = false;
    }

    /// <summary>What the page does apart from drawing.</summary>
    public PluginsPageModel Model { get; }

    /// <inheritdoc />
    public override string? Subtitle => Loc.T("plugins.subtitle");

    /// <inheritdoc />
    protected override void OnActivated() => Model.Activate();

    /// <inheritdoc />
    protected override void OnDeactivated() => Model.Deactivate();

    /// <inheritdoc />
    public override void OnLanguageChanged()
    {
        base.OnLanguageChanged();
        SetTexts();
    }

    private void SetTexts()
    {
        _updateAll.Content = ButtonContent(LucideIconKind.RefreshCw, Loc.T("plugins.updateAll"));
        _add.Content = ButtonContent(LucideIconKind.Plus, Loc.T("plugins.add"));
    }

    private static Control ButtonContent(LucideIconKind icon, string text)
        => new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 6,
            Children = { PageUi.Icon(icon, 14), new TextBlock { Text = text, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } },
        };
}
