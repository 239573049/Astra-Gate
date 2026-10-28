using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Astra.Desktop.Components.Settings;

/// <summary>
/// The base of a Settings / Privacy view that shows live queries. It is the page-view equivalent of a React component with hooks:
/// while the view is on screen it observes its queries (fetching and polling them like the mounted hooks do), and when any of them
/// changes — or the UI language does — it asks the view to bring itself up to date.
/// <para>
/// Two methods, on purpose: <see cref="Build"/> draws the static structure (once, and again after a language change, because every
/// text is read from the catalog when it is drawn); <see cref="Refresh"/> only moves data into that structure in place. A view with
/// text boxes and number fields must never be rebuilt by a poll, or the user's typing (and a pending debounced save) would be lost —
/// the web gets this for free from React's identity-preserving re-render.
/// </para>
/// </summary>
public abstract class LiveView : UserControl
{
    private readonly List<Func<IDisposable>> _watches = [];
    private readonly List<IDisposable> _active = [];
    private bool _built;
    private bool _attached;
    private bool _refreshQueued;

    /// <summary>What the page may use of the app.</summary>
    protected PageContext Context { get; }

    /// <summary>Creates the view over the app context.</summary>
    protected LiveView(PageContext context) => Context = context;

    /// <summary>Observes <paramref name="query"/> while the view is attached: activates it (fetch when stale, poll) and refreshes on every change.</summary>
    protected void Watch<T>(Query<T> query) => _watches.Add(() =>
    {
        query.PropertyChanged += OnQueryChanged;
        var token = query.Activate();
        return new Disposer(() =>
        {
            query.PropertyChanged -= OnQueryChanged;
            token.Dispose();
        });
    });

    /// <summary>Draws the static structure; assign <c>Content</c>. Called when the view attaches and again after a language change.</summary>
    protected abstract void Build();

    /// <summary>Moves the current data into the structure (in place; never recreate inputs here). Called after <see cref="Build"/> and on every change.</summary>
    protected abstract void Refresh();

    /// <summary>Asks for a <see cref="Refresh"/> on the next turn of the UI thread (several changes in one turn cost one refresh).</summary>
    protected void RequestRefresh()
    {
        if (_refreshQueued || !_built) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            if (_built && _attached) Refresh();
        });
    }

    /// <summary>The view is on screen (after the first <see cref="Refresh"/>): subscribe to events that are not queries here.</summary>
    protected virtual void OnShown() { }

    /// <summary>The view left the screen: unsubscribe what <see cref="OnShown"/> subscribed.</summary>
    protected virtual void OnHidden() { }

    /// <summary>Raised after the structure was drawn again because of a language change (sheets that are open stay as they are).</summary>
    protected virtual void OnRebuilt() { }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Loc.LanguageChanged += OnLanguage;
        if (!_built)
        {
            Build();
            _built = true;
        }
        foreach (var watch in _watches) _active.Add(watch());
        Refresh();
        OnShown();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Loc.LanguageChanged -= OnLanguage;
        foreach (var d in _active) d.Dispose();
        _active.Clear();
        OnHidden();
    }

    private void OnQueryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => RequestRefresh();

    private void OnLanguage() => Dispatcher.UIThread.Post(() =>
    {
        if (!_built || !_attached) return;
        Build();
        Refresh();
        OnRebuilt();
    });

    /// <summary>Toasts, confirm, clipboard and overlays for this view's parts.</summary>
    protected UiHost Host => _host ??= new UiHost(Context, () => this);

    private UiHost? _host;

    private sealed class Disposer(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
