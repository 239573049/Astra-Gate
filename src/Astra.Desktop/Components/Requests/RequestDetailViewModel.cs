using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Shell;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Astra.Desktop.Components.Requests;

/// <summary>
/// The open request detail (web <c>useRequest</c>): the detail query for one id, polled every second while the request is in flight
/// (the server answers from the live feed until it is stored), watched only while the sheet is open (web: <c>gcTime: 0</c>, "a detail
/// carries captured bodies"). Whole captured bodies are separate lazy queries (<see cref="Body"/>).
/// </summary>
public sealed class RequestDetailViewModel : ObservableObject, IDisposable
{
    private readonly PageContext _ctx;
    private readonly Dictionary<string, RequestBodyViewModel> _bodies = [];
    private readonly IDisposable _watch;

    /// <summary>Starts watching request <paramref name="id"/>.</summary>
    public RequestDetailViewModel(PageContext ctx, string id)
    {
        _ctx = ctx;
        Id = id;
        Detail = ctx.Queries.Query(RequestPaths.DetailKey(id), async ct => await ctx.Api.GetAsync<RequestDetailDto>(RequestPaths.Detail(id), ct)
                ?? throw new InvalidOperationException("empty answer"),
            new QueryOptions<RequestDetailDto>
            {
                // An open detail is always fresh; an in-flight request is followed until it finishes.
                StaleTime = TimeSpan.Zero,
                RefetchInterval = d => d?.Status == "pending" ? TimeSpan.FromSeconds(1) : null,
                // One entry per request ever opened: let it go a while after the sheet closed.
                CacheTime = TimeSpan.FromMinutes(2),
            });
        _watch = Detail.Activate();
    }

    /// <summary>The request's id.</summary>
    public string Id { get; }

    /// <summary>Which raw section (usage, price snapshot, a body) is open, or -1. Kept here because the sheet redraws from every refetch while a request is in flight (web: the accordion is keyed by the request id).</summary>
    public int ExpandedRawIndex { get; set; } = -1;

    /// <summary>The detail query.</summary>
    public Query<RequestDetailDto> Detail { get; }

    /// <summary>The lazy whole-body query of one captured part (<c>clientRequest</c>, ...).</summary>
    public RequestBodyViewModel Body(string key, string part)
    {
        if (_bodies.TryGetValue(key, out var body)) return body;
        return _bodies[key] = new RequestBodyViewModel(_ctx, Id, part);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _watch.Dispose();
        foreach (var body in _bodies.Values) body.Dispose();
        _bodies.Clear();
    }
}

/// <summary>
/// One whole captured body (web <c>useRequestBody</c>): fetched only when asked for (<see cref="LoadAll"/>), never refetched (the body of a stored
/// request does not change), shown as plain text — no pretty-printing or highlighting, which is what made large bodies cost hundreds of MB.
/// </summary>
public sealed class RequestBodyViewModel : ObservableObject, IDisposable
{
    private readonly IDisposable _watch;

    /// <summary>A body query for <paramref name="part"/> of request <paramref name="id"/>, idle until <see cref="LoadAll"/>.</summary>
    public RequestBodyViewModel(PageContext ctx, string id, string part)
    {
        Query = ctx.Queries.Query(RequestPaths.BodyKey(id, part), ct => ctx.Api.GetTextAsync(RequestPaths.Body(id, part), ct),
            // Fetched once; a whole body can run to megabytes, so it goes soon after the sheet closed.
            new QueryOptions<string> { StaleTime = TimeSpan.FromDays(3650), CacheTime = TimeSpan.FromSeconds(30) });
        // Observed from the start but disabled: enabling it is what fetches.
        Query.Enabled = false;
        _watch = Query.Activate();
    }

    /// <summary>The whole body as text.</summary>
    public Query<string> Query { get; }

    /// <summary>Fetches the whole part (the "加载全部" button).</summary>
    public void LoadAll() => Query.Enabled = true;

    /// <inheritdoc />
    public void Dispose() => _watch.Dispose();
}
