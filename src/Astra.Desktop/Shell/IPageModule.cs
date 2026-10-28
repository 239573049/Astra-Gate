using Avalonia.Controls;

namespace Astra.Desktop.Shell;

/// <summary>
/// One area of the app's pages (Tokens, Providers, ...) as the shell sees it: the routes it serves and how each is built. A page agent
/// adds <c>Pages/&lt;Area&gt;/&lt;Area&gt;Module.cs</c> with one of these and ONE line in <see cref="AppModules"/>; no shared file
/// is edited otherwise. Registering a route twice throws at startup (and in the smoke test), naming both modules.
/// </summary>
public interface IPageModule
{
    /// <summary>Registers this area's routes.</summary>
    void Register(IPageRegistrar pages);
}

/// <summary>Where a module registers its routes.</summary>
public interface IPageRegistrar
{
    /// <summary>
    /// Serves <paramref name="route"/>: <paramref name="create"/> builds the view model (given the app's context and the route
    /// parameter), <paramref name="view"/> its control. Both are plain delegates (no reflection, AOT).
    /// </summary>
    void Add(AppRoute route, Func<PageContext, string?, PageViewModel> create, Func<PageViewModel, Control> view);
}

/// <summary>Typed registration: the view factory receives the view model type the model factory returns.</summary>
public static class PageRegistrarExtensions
{
    /// <summary>Serves <paramref name="route"/> with <typeparamref name="TViewModel"/> and its view.</summary>
    public static void Add<TViewModel>(this IPageRegistrar pages, AppRoute route, Func<PageContext, string?, TViewModel> create, Func<TViewModel, Control> view)
        where TViewModel : PageViewModel
        => pages.Add(route, (ctx, param) => create(ctx, param), vm => view((TViewModel)vm));
}
