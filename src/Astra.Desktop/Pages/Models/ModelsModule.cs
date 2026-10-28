using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Models;

/// <summary>
/// The Models area: the catalog list (<see cref="AppRoute.Models"/>) and a model's detail page (<see cref="AppRoute.ModelDetail"/>, the route
/// parameter is the model id). Registered by the one line in <see cref="AppModules"/> (plan avalonia-client §24).
/// </summary>
public sealed class ModelsModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
    {
        pages.Add(AppRoute.Models, (ctx, param) => new ModelsPage(ctx, param), vm => new ModelsView((ModelsPage)vm));
        pages.Add(AppRoute.ModelDetail, (ctx, param) => new ModelDetailPage(ctx, param), vm => new ModelDetailView((ModelDetailPage)vm));
    }
}
