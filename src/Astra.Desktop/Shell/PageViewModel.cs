using System.ComponentModel;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Astra.Desktop.Shell;

/// <summary>
/// A page in the content host. The shell draws the 52 px header (sidebar toggle, back, <see cref="HeaderLeading"/>, title, subtitle,
/// <see cref="HeaderActions"/>; web <c>Page</c>) from the active page's view model and puts the page's view below it inside the
/// scrolling, edge-fading content area (web <c>Page</c> body) — so a page's view is only its body: no <c>PageScaffold</c> inside the shell.
/// Pages are registered by an <see cref="IPageModule"/>.
/// <para>
/// Lifecycle: <see cref="Activate"/> when the page becomes the visible one, <see cref="Deactivate"/> when it is covered (Push) or
/// left; the queries a page watches with <see cref="Observe{T}"/> are observed (fetch, poll) only in between, like a mounted react-query hook.
/// <see cref="Dispose"/> when the navigator drops the page (replaced, or popped by Back): the shared queries and stores outlive every page,
/// so a handler a page hangs on them must go through <see cref="Subscribe"/> / <see cref="OnDispose"/>, or it keeps the page — and the view
/// it holds — alive for the rest of the process.
/// </para>
/// </summary>
public abstract class PageViewModel : ObservableObject, IPageLoadState, IDisposable
{
    private readonly List<Func<IDisposable>> _watched = [];
    private readonly List<IDisposable> _tokens = [];
    private readonly List<Action> _undo = [];
    private object? _headerActions;
    private object? _headerLeading;
    private bool _active;
    private bool _disposed;

    /// <param name="route">The route this page serves.</param>
    /// <param name="param">The route parameter (an id, a tab).</param>
    /// <param name="context">The app context; null only for the placeholder.</param>
    protected PageViewModel(AppRoute route, string? param, PageContext? context = null)
    {
        Route = route;
        Param = param;
        Context = context;
    }

    public AppRoute Route { get; }

    /// <summary>The route parameter: an id (detail pages) or a tab / sub-view (list pages); null for the page's default.</summary>
    public string? Param { get; }

    /// <summary>What the page may use of the app (null for the placeholder).</summary>
    public PageContext? Context { get; }

    /// <summary>The header title (web <c>Page</c> <c>title</c>). Raise <c>OnPropertyChanged(nameof(Title))</c> when it changes (a language switch does it through <see cref="OnLanguageChanged"/>).</summary>
    public virtual string Title => Loc.T(RouteInfo.Of(Route).TitleKey);

    /// <summary>The smaller line under the title (web <c>subtitle</c>).</summary>
    public virtual string? Subtitle => Param;

    /// <summary>Whether the header shows <see cref="Subtitle"/> in the monospace font (web <c>subtitle={&lt;code className="font-mono"&gt;}</c>, a key).</summary>
    public virtual bool SubtitleMono => false;

    /// <summary>Content at the right end of the header (web <c>actions</c>): any control — usually an <c>ActionGroup</c> of buttons — or null. Set with <see cref="SetHeaderActions"/>.</summary>
    public object? HeaderActions => _headerActions;

    /// <summary>Content left of the title, after the toggle and back button (web <c>leading</c>).</summary>
    public object? HeaderLeading => _headerLeading;

    /// <summary>
    /// Edits the user has not saved yet. The app restarts itself into the tray only when no page holds any (closing the window must
    /// never cost them), so a page with a form that can be left dirty says so.
    /// </summary>
    public virtual bool HoldsUnsavedWork => false;

    /// <summary>
    /// True (default): the shell wraps the view in a vertical scroller with the standard padding. A page that lays out its own
    /// scrolling regions (a table that fills the height) returns false and gets the remaining height instead.
    /// </summary>
    public virtual bool HostScrolls => true;

    /// <summary>Sets the header's right-hand content.</summary>
    protected void SetHeaderActions(object? actions)
    {
        SetProperty(ref _headerActions, actions, nameof(HeaderActions));
    }

    /// <summary>Sets the header's left-hand content.</summary>
    protected void SetHeaderLeading(object? leading) => SetProperty(ref _headerLeading, leading, nameof(HeaderLeading));

    /// <summary>
    /// Watches <paramref name="query"/> while the page is active: <see cref="Query{T}.Activate"/> on <see cref="Activate"/>, the
    /// token released on <see cref="Deactivate"/>. Call it from the constructor.
    /// </summary>
    protected void Observe<T>(Query<T> query)
    {
        _watched.Add(query.Activate);
        if (_active) _tokens.Add(query.Activate());
    }

    /// <summary>
    /// Hangs <paramref name="handler"/> on <paramref name="source"/>'s <c>PropertyChanged</c> until the page is disposed. Use it for every
    /// source that outlives the page (a shared <see cref="Query{T}"/>, a store); an object the page owns may be subscribed directly.
    /// </summary>
    protected void Subscribe(INotifyPropertyChanged source, PropertyChangedEventHandler handler)
    {
        source.PropertyChanged += handler;
        _undo.Add(() => source.PropertyChanged -= handler);
    }

    /// <summary>Runs <paramref name="undo"/> when the page is disposed (unhooking any other event of a longer-lived object).</summary>
    protected void OnDispose(Action undo) => _undo.Add(undo);

    /// <summary>The page became the visible one.</summary>
    public void Activate()
    {
        if (_active || _disposed) return;
        _active = true;
        foreach (var watch in _watched) _tokens.Add(watch());
        OnActivated();
    }

    /// <summary>The page was covered or left.</summary>
    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        foreach (var token in _tokens) token.Dispose();
        _tokens.Clear();
        OnDeactivated();
    }

    /// <summary>Hook after <see cref="Activate"/>.</summary>
    protected virtual void OnActivated() { }

    /// <summary>Hook after <see cref="Deactivate"/>.</summary>
    protected virtual void OnDeactivated() { }

    /// <summary>The navigator dropped the page: deactivates it and undoes its <see cref="Subscribe"/> / <see cref="OnDispose"/> registrations.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Deactivate();
        _disposed = true;
        foreach (var undo in _undo) undo();
        _undo.Clear();
        _watched.Clear();
    }

    /// <summary>
    /// A page-scoped menu / shortcut command (<see cref="MenuCommands.Find"/>, <see cref="MenuCommands.Refresh"/> is handled by the shell,
    /// <see cref="MenuCommands.NewProvider"/> on Providers). Return true when handled.
    /// </summary>
    public virtual bool OnMenuCommand(string command) => false;

    /// <inheritdoc />
    /// <remarks>Default: every request on the shared query client has been answered. A page with work outside the query layer overrides it.</remarks>
    public virtual Task WhenSettledAsync(CancellationToken ct = default)
        => Context is null ? Task.CompletedTask : Context.Queries.WhenIdleAsync(ct: ct);

    /// <summary>The shell calls this after the UI language changed, so computed texts are read again.</summary>
    public virtual void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
    }
}

/// <summary>The empty page every route shows until its real page lands: just the route's title (and parameter).</summary>
public sealed class PlaceholderPageViewModel(AppRoute route, string? param) : PageViewModel(route, param)
{
    /// <summary>The web path this page mirrors, so a placeholder says what it stands for.</summary>
    public string WebPath => RouteInfo.Of(Route).WebPath;
}
