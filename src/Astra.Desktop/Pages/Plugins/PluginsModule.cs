using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Plugins;

/// <summary>The Plugins route (<c>/plugins[/:tab]</c>): installed, market and runtime tabs (plan avalonia-client §24 recipe).</summary>
public sealed class PluginsModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Plugins, (ctx, param) => new PluginsPage(ctx, param), vm => new PluginsView((PluginsPage)vm));
}
