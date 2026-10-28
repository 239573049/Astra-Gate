using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Overview;

/// <summary>The Overview route (web <c>OverviewPage</c>).</summary>
public sealed class OverviewModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Overview, (ctx, param) => new OverviewPage(ctx, param), vm => new OverviewView(vm));
}
