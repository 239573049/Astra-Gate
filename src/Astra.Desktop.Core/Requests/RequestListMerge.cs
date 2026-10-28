using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Requests;

/// <summary>
/// How the live feed joins the stored list on page 1 (web <c>RequestsView</c>'s <c>liveOnly</c> / <c>items</c>, and
/// <c>matchesFilters</c>): in-flight rows that are not in the stored page yet, narrowed by the same filters the server applies,
/// go in front of the stored rows.
/// </summary>
public static class RequestListMerge
{
    /// <summary>Client-side mirror of the server's list filters (RequestRepository), for live rows not stored yet (web <c>matchesFilters</c>).</summary>
    public static bool Matches(RequestSummaryDto r, string model, RequestFilterValues filters)
    {
        static bool Allows(IReadOnlyList<string> picked, string? value) => picked.Count == 0 || picked.Contains(value ?? "");
        if (!Allows(filters.Get(RequestFilterId.Client), r.ClientKind)) return false;
        if (!Allows(filters.Get(RequestFilterId.Token), r.TokenId)) return false;
        if (!Allows(filters.Get(RequestFilterId.Provider), r.ProviderId)) return false;
        if (!Allows(filters.Get(RequestFilterId.Status), r.Status)) return false;
        // One origin narrows the list; none or both mean any (the web builds the live set the same way).
        var origins = filters.Get(RequestFilterId.Origin);
        if (origins.Count == 1 && origins[0] != (r.Remote ? "remote" : "local")) return false;
        if (model != "")
        {
            string?[] names = [r.RequestedModel, r.UpstreamModel, r.SystemModelId, r.ResponseModel];
            if (!names.Any(m => m is not null && m.Contains(model, StringComparison.OrdinalIgnoreCase))) return false;
        }
        return true;
    }

    /// <summary>
    /// The live-only rows: page 1 only, not already in the stored page, matching the filters (web <c>liveOnly</c>).
    /// </summary>
    public static List<RequestSummaryDto> LiveOnly(int page, IReadOnlyList<RequestSummaryDto>? stored, IReadOnlyList<RequestSummaryDto> live, string model, RequestFilterValues filters)
    {
        if (page != 1) return [];
        var storedIds = new HashSet<string>((stored ?? []).Select(r => r.Id), StringComparer.Ordinal);
        return live.Where(r => !storedIds.Contains(r.Id) && Matches(r, model, filters)).ToList();
    }

    /// <summary>The table's rows: live-only rows first, then the stored page (web <c>items</c>).</summary>
    public static List<RequestSummaryDto> Items(IReadOnlyList<RequestSummaryDto> liveOnly, IReadOnlyList<RequestSummaryDto>? stored)
        => [.. liveOnly, .. stored ?? []];

    /// <summary>Pages of a total (web <c>Math.max(1, Math.ceil(total / PAGE_SIZE))</c>); 1 before any data.</summary>
    public static int PageCount(long? total, int pageSize = RequestListQuery.DefaultPageSize)
        => total is null ? 1 : (int)Math.Max(1, (total.Value + pageSize - 1) / pageSize);

    /// <summary>
    /// The page's own polling fallback: the stored list is polled every 5 s only on page 1 while the live stream is down
    /// (web <c>useRequests(query, page === 1 &amp;&amp; !liveConnected)</c>).
    /// </summary>
    public static bool PollsList(int page, bool liveConnected) => page == 1 && !liveConnected;

    /// <summary>The request (summary) shown in the detail while it is in flight is the live copy; otherwise the stored one (web <c>useLiveRequest(id) ?? r</c>).</summary>
    public static RequestSummaryDto Freshest(RequestSummaryDto stored, RequestSummaryDto? live) => live ?? stored;
}
