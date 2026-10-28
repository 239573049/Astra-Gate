using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Clients;

/// <summary>The Clients route (web <c>/clients[/:kind]</c>, ClientsPage.tsx); the parameter is the client kind to show first.</summary>
public sealed class ClientsModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Clients, (ctx, param) => new ClientsPageViewModel(ctx, param), vm => new ClientsView(vm));
}
