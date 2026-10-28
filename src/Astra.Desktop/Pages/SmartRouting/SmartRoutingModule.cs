using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.SmartRouting;

/// <summary>The Smart routing route (<c>/smart-routing</c>): the judge, the grades and the rules (plan avalonia-client §24 recipe).</summary>
public sealed class SmartRoutingModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.SmartRouting, (ctx, param) => new SmartRoutingPage(ctx, param), vm => new SmartRoutingView((SmartRoutingPage)vm));
}
