using System.ComponentModel;
using Astra.Desktop.Core.Api;
using Avalonia;
using Avalonia.Controls;

namespace Astra.Desktop.Components.Tokens;

/// <summary>
/// A control whose content is a function of one query, like a React component that calls <c>useQuery</c>: while it is on screen
/// it observes the query (fetching, polling) and rebuilds its content each time the query changes state (loading, data, error);
/// leaving the tree releases the observation. Swap <see cref="Query"/> when a filter changes (a new key is a different query).
/// This is what keeps a page's sections independent: a late answer rebuilds only the section that waits for it.
/// </summary>
/// <typeparam name="T">The query's data type.</typeparam>
public sealed class QueryHost<T> : ContentControl
{
    private readonly Func<Query<T>, Control?> _render;
    private Query<T>? _query;
    private IDisposable? _observer;
    private bool _queued;

    /// <summary>Hosts content built by <paramref name="render"/> from the query's current state.</summary>
    public QueryHost(Query<T>? query, Func<Query<T>, Control?> render)
    {
        _render = render;
        _query = query;
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        Rebuild();
    }

    /// <summary>The observed query; setting another moves the observation and rebuilds.</summary>
    public Query<T>? Query
    {
        get => _query;
        set
        {
            if (ReferenceEquals(_query, value)) return;
            Detach();
            _query = value;
            if (VisualRoot is not null) Attach();
            Rebuild();
        }
    }

    /// <summary>Builds the content again from the current state (for a change the query does not report: a language switch, a sort).</summary>
    public void Rebuild() => Content = _query is null ? null : _render(_query);

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach();
        Rebuild();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Detach();
    }

    private void Attach()
    {
        if (_query is null || _observer is not null) return;
        _query.PropertyChanged += OnChanged;
        _observer = _query.Activate();
    }

    private void Detach()
    {
        if (_query is not null) _query.PropertyChanged -= OnChanged;
        _observer?.Dispose();
        _observer = null;
    }

    // A fetch reports up to eight properties in one go: build once per dispatcher turn, from the state they all describe.
    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_queued) return;
        _queued = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (_observer is not null) Rebuild();
        });
    }
}
