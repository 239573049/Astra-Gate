using Astra.Desktop.Core.Shell;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Astra.Desktop.Shell;

/// <summary>
/// The content host's navigation: the current page plus a back stack. A sidebar choice replaces the stack
/// (<see cref="Navigate(AppRoute, string?)"/>); opening a detail page from a list keeps the list below it (<see cref="Push(AppRoute, string?)"/>).
/// Implements <see cref="INavigator"/> for the pages (they name routes by <see cref="AppRoute"/> member name). Pages are built by the
/// <see cref="PageRegistry"/> over the <see cref="PageContext"/>, activated / deactivated as they are shown / covered, and disposed once dropped.
/// </summary>
public sealed class Navigator : ObservableObject, INavigator
{
    private readonly Stack<PageViewModel> _back = new();
    private readonly PageRegistry _registry;
    private PageContext? _context;
    private PageViewModel _current;
    private bool _suspended;

    /// <param name="registry">The route table.</param>
    public Navigator(PageRegistry registry)
    {
        _registry = registry;
        // Until the context is attached (the shell does it right after construction) the placeholder stands in.
        _current = new PlaceholderPageViewModel(AppRoute.Overview, null);
    }

    /// <summary>Hands the navigator the context its pages are built with and shows the start page.</summary>
    public void Attach(PageContext context, AppRoute start = AppRoute.Overview)
    {
        _context = context;
        Navigate(start);
    }

    public PageViewModel Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    /// <inheritdoc />
    public bool CanGoBack => _back.Count > 0;

    /// <summary>Raised after <see cref="Current"/> changed, for the sidebar's selection.</summary>
    public event Action? Changed;

    /// <summary>
    /// The window went to the tray (true) or came back (false): the current page is deactivated meanwhile — its queries stop polling — and
    /// activated again (stale data refetches) when the window is shown. A page navigated to while suspended waits for the resume too.
    /// </summary>
    public void SetSuspended(bool suspended)
    {
        if (_suspended == suspended) return;
        _suspended = suspended;
        if (suspended) _current.Deactivate();
        else _current.Activate();
    }

    /// <summary>Whether the shown page or one under it holds edits not saved yet (<see cref="PageViewModel.HoldsUnsavedWork"/>).</summary>
    public bool HoldsUnsavedWork => _current.HoldsUnsavedWork || _back.Any(p => p.HoldsUnsavedWork);

    /// <summary>The control for a page of this navigator's registry.</summary>
    public Control CreateView(PageViewModel page) => _registry.CreateView(page);

    /// <summary>Shows a top-level page and forgets the history (what a sidebar click does).</summary>
    public void Navigate(AppRoute route, string? param = null)
    {
        // The replaced page and the forgotten history are never shown again: dispose them, or the handlers they hang on the shared
        // queries keep each one (and its view) alive.
        var dropped = _current;
        var history = _back.ToArray();
        _back.Clear();
        dropped.Dispose();
        foreach (var page in history) page.Dispose();
        Show(Build(route, param));
    }

    /// <summary>Shows a page on top of the current one.</summary>
    public void Push(AppRoute route, string? param = null)
    {
        _current.Deactivate();
        _back.Push(Current);
        Show(Build(route, param));
    }

    /// <inheritdoc />
    public void GoBack()
    {
        if (_back.Count == 0) return;
        var dropped = _current;
        dropped.Dispose();
        Show(_back.Pop());
    }

    /// <inheritdoc cref="INavigator.Navigate"/>
    public void Navigate(string route, string? param = null) => Navigate(Parse(route), param);

    /// <inheritdoc cref="INavigator.Push"/>
    public void Push(string route, string? param = null) => Push(Parse(route), param);

    private static AppRoute Parse(string route)
        => Enum.TryParse<AppRoute>(route, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new ArgumentException($"Unknown route '{route}' (use an AppRoute member name).", nameof(route));

    private PageViewModel Build(AppRoute route, string? param)
        => _context is null ? new PlaceholderPageViewModel(route, param) : _registry.Create(route, param, _context);

    private void Show(PageViewModel page)
    {
        Current = page;
        OnPropertyChanged(nameof(CanGoBack));
        if (!_suspended) page.Activate();
        Changed?.Invoke();
    }
}
