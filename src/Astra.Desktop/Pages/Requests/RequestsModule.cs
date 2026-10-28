using Astra.Desktop.Shell;

namespace Astra.Desktop.Pages.Requests;

/// <summary>The request log's route (web <c>/requests</c>); the route parameter carries the web's address filters (<c>token=…&amp;model=…</c>).</summary>
public sealed class RequestsModule : IPageModule
{
    /// <inheritdoc />
    public void Register(IPageRegistrar pages)
        => pages.Add(AppRoute.Requests, (ctx, param) => new RequestsPageViewModel(ctx, param), vm => new RequestsView(vm));
}
