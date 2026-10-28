using System.ComponentModel;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Components.Clients;

/// <summary>
/// The queries a redrawn view depends on, like the <c>useQuery</c> calls of a React component: each redraw declares what it read
/// (<see cref="Use{T}"/>); a query read for the first time is observed (fetch, poll) and its changes call back, one no longer read is
/// released after the redraw (<see cref="EndPass"/>). <see cref="Clear"/> releases everything (the view left the screen).
/// </summary>
internal sealed class QueryWatch(Action changed)
{
    private readonly Dictionary<QueryBase, IDisposable> _active = [];
    private readonly HashSet<QueryBase> _used = [];
    private bool _attached;

    /// <summary>Whether queries are observed (the view is on screen).</summary>
    public bool Attached
    {
        get => _attached;
        set
        {
            _attached = value;
            if (!value) Clear();
        }
    }

    /// <summary>Starts a redraw.</summary>
    public void BeginPass() => _used.Clear();

    /// <summary>Marks <paramref name="query"/> as read by this redraw and returns it.</summary>
    public Query<T> Use<T>(Query<T> query)
    {
        _used.Add(query);
        if (_attached && !_active.ContainsKey(query))
        {
            query.PropertyChanged += OnChanged;
            _active[query] = query.Activate();
        }
        return query;
    }

    /// <summary>Releases the queries this redraw did not read.</summary>
    public void EndPass()
    {
        foreach (var q in _active.Keys.Where(q => !_used.Contains(q)).ToList()) Release(q);
    }

    /// <summary>Releases every query.</summary>
    public void Clear()
    {
        foreach (var q in _active.Keys.ToList()) Release(q);
    }

    private void Release(QueryBase q)
    {
        q.PropertyChanged -= OnChanged;
        _active[q].Dispose();
        _active.Remove(q);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "Data" or "Status" or "Error") changed();
    }
}
