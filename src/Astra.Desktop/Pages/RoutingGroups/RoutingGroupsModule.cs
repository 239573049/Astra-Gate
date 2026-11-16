using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.RoutingGroups;

/// <summary>The Routing groups route (<c>/routing-groups</c>): virtual models <c>group/&lt;slug&gt;</c> over several providers (plan avalonia-client §24 recipe).</summary>
public sealed class RoutingGroupsModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.RoutingGroups, (ctx, param) => new RoutingGroupsPage(ctx, param), vm => new RoutingGroupsView((RoutingGroupsPage)vm));
}
