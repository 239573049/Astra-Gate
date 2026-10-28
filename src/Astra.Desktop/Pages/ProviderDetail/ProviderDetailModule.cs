using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.ProviderDetail;

/// <summary>
/// The provider detail route (web <c>/providers/:id</c>, ProviderDetailPage.tsx). The Providers list opens it with
/// <c>Navigator.Push("ProviderDetail", id)</c>; its card "test" button can open this package's
/// <see cref="Components.ProviderDetail.ProviderTestDialog.Opener"/> directly.
/// </summary>
public sealed class ProviderDetailModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.ProviderDetail, (ctx, param) => new ProviderDetailPage(ctx, param), vm => new ProviderDetailView(vm));
}
