using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Requests;

/// <summary>
/// Rows of the live request log (<c>GET /api/requests/live</c>) outside any view, so an update touches only what shows that row
/// (port of <c>LiveRequestStore</c> in <c>web/src/api/liveRequests.ts</c>). <see cref="ListChanged"/> fires when a row joins or
/// leaves the feed or a field the list filters on changes (and when <see cref="Connected"/> flips); <see cref="RowChanged"/> fires for
/// that one row's every update (TTFT, status, cost…). Not thread safe: call it on the UI thread (the feed marshals).
/// </summary>
public sealed class LiveRequestStore
{
    private readonly Dictionary<string, RequestSummaryDto> _rows = new(StringComparer.Ordinal);
    private readonly IUiDispatcher _ui;
    private IReadOnlyList<RequestSummaryDto> _list = [];
    private bool _connected;
    private bool _rebuildQueued;

    /// <param name="ui">Where list rebuilds are queued; the web does it in a microtask so one batch of events sorts once.</param>
    public LiveRequestStore(IUiDispatcher? ui = null) => _ui = ui ?? ImmediateDispatcher.Instance;

    /// <summary>In-flight rows, newest first; a new list only when membership or a filtered-on field changes.</summary>
    public IReadOnlyList<RequestSummaryDto> List => _list;

    /// <summary>Whether the live stream is up (callers poll instead while it is not).</summary>
    public bool Connected => _connected;

    /// <summary>The live copy of one request, or null once it left the feed.</summary>
    public RequestSummaryDto? Get(string id) => _rows.GetValueOrDefault(id);

    /// <summary>The list was rebuilt (membership, a filtered-on field, or <see cref="Connected"/> changed).</summary>
    public event Action? ListChanged;

    /// <summary>One row changed (or left); the argument is its id.</summary>
    public event Action<string>? RowChanged;

    /// <summary>Fields the request list (and a trace session) filters on; a change to any of them can move a row in or out of the filtered list (web <c>listKey</c>).</summary>
    internal static string ListKey(RequestSummaryDto r)
        => string.Join('\0', r.ClientKind, r.TokenId, r.ProviderId, r.Status, r.RequestedModel, r.UpstreamModel, r.SystemModelId, r.ResponseModel, r.Remote, r.SessionKey, r.TurnId);

    /// <summary>Adds or replaces a row.</summary>
    public void Set(RequestSummaryDto row)
    {
        var prev = _rows.GetValueOrDefault(row.Id);
        _rows[row.Id] = row;
        RowChanged?.Invoke(row.Id);
        if (prev is null || ListKey(prev) != ListKey(row)) RebuildList();
    }

    /// <summary>Drops a row.</summary>
    public void Delete(string id)
    {
        if (!_rows.Remove(id)) return;
        RowChanged?.Invoke(id);
        RebuildList();
    }

    /// <summary>Forgets every row (the connection dropped: its rows may never see their "persisted" event).</summary>
    public void Reset(bool connected)
    {
        var ids = _rows.Keys.ToArray();
        _rows.Clear();
        foreach (var id in ids) RowChanged?.Invoke(id);
        var changed = _connected != connected || ids.Length > 0;
        _connected = connected;
        if (changed) RebuildList();
    }

    /// <summary>Marks the stream up or down.</summary>
    public void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        RebuildList();
    }

    private void RebuildList()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        _ui.Post(() =>
        {
            _rebuildQueued = false;
            // Newest first; ULID ids sort by arrival, so ties on the timestamp stay stable.
            _list = _rows.Values
                .OrderByDescending(r => r.StartedAtUtc)
                .ThenByDescending(r => r.Id, StringComparer.Ordinal)
                .ToList();
            ListChanged?.Invoke();
        });
    }
}
