using Avalonia.Controls;

namespace Astra.Desktop.Shell;

/// <summary>
/// The shell's route table: filled by the <see cref="IPageModule"/>s (<see cref="AppModules"/>), read by the <see cref="Navigator"/>.
/// Every route has the placeholder page until a module registers it; views are chosen by the registered delegate, not by reflection
/// (AOT, plan avalonia-client §3 D3).
/// </summary>
public sealed class PageRegistry : IPageRegistrar
{
    private sealed record Entry(Func<PageContext, string?, PageViewModel> Create, Func<PageViewModel, Control> View, string Module);

    private readonly Dictionary<AppRoute, Entry> _entries = new();
    private string _current = "?";

    /// <summary>A registry with <paramref name="modules"/> registered (none = every route is a placeholder).</summary>
    public PageRegistry(IEnumerable<IPageModule>? modules = null)
    {
        foreach (var module in modules ?? [])
        {
            _current = module.GetType().Name;
            module.Register(this);
        }
    }

    /// <inheritdoc />
    public void Add(AppRoute route, Func<PageContext, string?, PageViewModel> create, Func<PageViewModel, Control> view)
    {
        if (_entries.TryGetValue(route, out var existing))
            throw new InvalidOperationException($"Route {route} is registered by both {existing.Module} and {_current}.");
        _entries[route] = new Entry(create, view, _current);
    }

    /// <summary>Whether a module serves <paramref name="route"/> (otherwise it shows the placeholder).</summary>
    public bool IsRegistered(AppRoute route) => _entries.ContainsKey(route);

    /// <summary>The page for <paramref name="route"/>: a module's, else the placeholder.</summary>
    public PageViewModel Create(AppRoute route, string? param, PageContext context)
        => _entries.TryGetValue(route, out var e) ? e.Create(context, param) : new PlaceholderPageViewModel(route, param);

    /// <summary>The view for <paramref name="vm"/>, with its DataContext set; the placeholder view when its route has no module.</summary>
    public Control CreateView(PageViewModel vm)
    {
        var view = _entries.TryGetValue(vm.Route, out var e) && vm is not PlaceholderPageViewModel ? e.View(vm) : new Views.PlaceholderPageView();
        view.DataContext = vm;
        return view;
    }
}
