using System.ComponentModel;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Components.Clients;

/// <summary>
/// The queries a redrawn view depends on, like the <c>useQuery</c> calls of a React component: each redraw declares what it read
/// (<see cref="Use{T}"/>); a query read for the first time is observed (fetch, poll) and its changes call back, one no longer read is
/// released after the redraw (<see cref="EndPass"/>). <see cref="Clear"/> releases everything (the view left the screen).
/// <para>
/// A change calls back only when what the redraw read is different now (react-query's tracked properties / <c>select</c>): whether the
/// query has data or failed, and its data — the instance, or the part a <c>select</c> picked. A poll that brings the same answer (see
/// <see cref="QueryOptions{T}.SameData"/>), or one that only changes what the view does not show, redraws nothing (issue #16).
/// </para>
/// </summary>
internal sealed class QueryWatch(Action changed)
{
    private readonly Dictionary<QueryBase, IDisposable> _active = [];
    private readonly HashSet<QueryBase> _used = [];
    private readonly Dictionary<QueryBase, (Func<QueryView> Now, QueryView Read)> _views = [];
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

    /// <summary>Marks <paramref name="query"/> as read by this redraw and returns it; a later change of its data instance calls back.</summary>
    public Query<T> Use<T>(Query<T> query) => Use(query, null);

    /// <summary>
    /// Marks <paramref name="query"/> as read by this redraw and returns it. <paramref name="select"/> names the part of the data the redraw
    /// shows (compared by value, so a tuple works); without it the data instance is compared.
    /// </summary>
    public Query<T> Use<T>(Query<T> query, Func<T?, object?>? select)
    {
        _used.Add(query);
        QueryView Now() => new(query.HasData, query.IsError, select is null ? query.Data : select(query.Data));
        _views[query] = (Now, Now());
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
        foreach (var q in _views.Keys.Where(q => !_used.Contains(q)).ToList()) _views.Remove(q);
    }

    /// <summary>Releases every query.</summary>
    public void Clear()
    {
        foreach (var q in _active.Keys.ToList()) Release(q);
        _views.Clear();
    }

    private void Release(QueryBase q)
    {
        q.PropertyChanged -= OnChanged;
        _active[q].Dispose();
        _active.Remove(q);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("Data" or "Status" or "Error")) return;
        if (sender is QueryBase q && _views.TryGetValue(q, out var view) && view.Now().Equals(view.Read)) return;
        changed();
    }

    /// <summary>What a redraw read of a query.</summary>
    private readonly record struct QueryView(bool HasData, bool IsError, object? Data);
}
