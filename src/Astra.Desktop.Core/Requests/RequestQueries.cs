using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Requests;

/// <summary>
/// One request-log list query (web <c>RequestQuery</c> as the page builds it): the keyword, the comma-joined filter lists the server
/// matches any of (RequestRepository), the single origin, and the page. <see cref="Path"/> is the GET it makes, <see cref="Key"/> its
/// react-query key (<c>['requests', …]</c>, so the live feed's <c>invalidate('requests')</c> hits every page and filter).
/// </summary>
public sealed record RequestListQuery(string Model, RequestFilterValues Filters, int Page, int PageSize = RequestListQuery.DefaultPageSize)
{
    /// <summary>Rows per page (web <c>PAGE_SIZE</c>).</summary>
    public const int DefaultPageSize = 50;

    /// <summary>The comma list of one filter.</summary>
    public string List(RequestFilterId id) => string.Join(',', Filters.Get(id));

    /// <summary>The origin the server filters on (one picked, else none).</summary>
    public string Origin => RequestFilterCatalog.OriginQuery(Filters.Get(RequestFilterId.Origin));

    /// <summary><c>/api/requests?model=…&amp;client=…&amp;page=1&amp;pageSize=50</c> (empty values are skipped, as web <c>qs</c> does).</summary>
    public string Path => "/api/requests" + ApiClient.Qs(
        ("model", Model), ("client", List(RequestFilterId.Client)), ("token", List(RequestFilterId.Token)),
        ("provider", List(RequestFilterId.Provider)), ("status", List(RequestFilterId.Status)), ("origin", Origin),
        ("page", Page), ("pageSize", PageSize));

    /// <summary>The react-query key.</summary>
    public QueryKey Key => QueryKey.Of("requests", Model, List(RequestFilterId.Client), List(RequestFilterId.Token), List(RequestFilterId.Provider),
        List(RequestFilterId.Status), Origin, Page.ToString(System.Globalization.CultureInfo.InvariantCulture), PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// The live-rate query under the same filters, paging excluded (web <c>RequestRateFilter</c>): <c>/api/stats/rate?client=…</c>,
    /// key <c>['stats', 'rate', …]</c> (web <c>keys.stats('rate', filters)</c>).
    /// </summary>
    public (string Path, QueryKey Key) Rate()
    {
        var path = "/api/stats/rate" + ApiClient.Qs(
            ("client", List(RequestFilterId.Client)), ("token", List(RequestFilterId.Token)), ("provider", List(RequestFilterId.Provider)),
            ("model", Model), ("status", List(RequestFilterId.Status)), ("origin", Origin));
        return (path, QueryKey.Of("stats", "rate", Model, List(RequestFilterId.Client), List(RequestFilterId.Token), List(RequestFilterId.Provider),
            List(RequestFilterId.Status), Origin));
    }
}

/// <summary>Endpoints of one request's detail and captured bodies (web <c>useRequest</c> / <c>useRequestBody</c>).</summary>
public static class RequestPaths
{
    /// <summary>The captured parts in display order and their whole-body endpoint names (web <c>REQUEST_BODY_PARTS</c>).</summary>
    public static readonly IReadOnlyList<(string Key, string Part)> BodyParts =
    [
        ("clientRequest", "client-request"),
        ("upstreamRequest", "upstream-request"),
        ("upstreamRequestHeaders", "upstream-request-headers"),
        ("upstreamResponse", "upstream-response"),
        ("clientResponse", "client-response"),
    ];

    /// <summary>GET one request's detail (served from the live feed while it is in flight).</summary>
    public static string Detail(string id) => "/api/requests/" + ApiClient.Enc(id);

    /// <summary>GET one whole captured part as text.</summary>
    public static string Body(string id, string part) => Detail(id) + "/bodies/" + ApiClient.Enc(part);

    /// <summary>The key of a request's detail (web <c>keys.request</c>).</summary>
    public static QueryKey DetailKey(string id) => QueryKey.Of("request", id);

    /// <summary>The key of a whole body (web <c>[...keys.request(id), 'body', part]</c>).</summary>
    public static QueryKey BodyKey(string id, string part) => QueryKey.Of("request", id, "body", part);
}
