using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Traces;

/// <summary>
/// Where the Traces page is: its tab, the open session or request, and the search params the web keeps in the address
/// (<c>range</c>, <c>client</c>, <c>turn</c>, <c>view</c>). The desktop's route parameter for the Traces page is the web path
/// itself (<see cref="TraceRoutes"/>: <c>/traces</c>, <c>/traces/sessions/KEY?turn=T</c>, <c>/traces/requests/ID</c>), so a link built
/// for the web (inspector actions, the Requests page's "open trace") navigates the desktop unchanged. Port of the routing at the top of
/// <c>pages/TracesPage.tsx</c> (<c>useParams</c> + <c>useLocation</c>) and the <c>useSearchParams</c> reads of session.tsx.
/// </summary>
/// <param name="Tab">The view (segmented control).</param>
/// <param name="SessionKey">The open session (<c>/traces/sessions/:key</c>), else null.</param>
/// <param name="RequestId">The open request (<c>/traces/requests/:id</c>), else null.</param>
/// <param name="Query">The search params, decoded.</param>
public sealed record TracesLocation(TraceRoutes.Tab Tab, string? SessionKey, string? RequestId, IReadOnlyDictionary<string, string> Query)
{
    /// <summary>The topology (<c>/traces</c>).</summary>
    public static TracesLocation Topology { get; } = new(TraceRoutes.Tab.Topology, null, null, new Dictionary<string, string>());

    /// <summary>A search param, or null when absent or empty.</summary>
    public string? Get(string key) => Query.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    /// <summary>
    /// The location with one search param set (null / empty removes it); the web's <c>set(key, value)</c> with
    /// <c>setParams(next, { replace: true })</c>.
    /// </summary>
    public TracesLocation With(string key, string? value)
    {
        var next = new Dictionary<string, string>(Query, StringComparer.Ordinal);
        if (string.IsNullOrEmpty(value)) next.Remove(key);
        else next[key] = value;
        return this with { Query = next };
    }

    /// <summary>
    /// Parses the route parameter. Accepts the full web path (<c>/traces/requests/ID</c>), the path without <c>/traces</c>
    /// (<c>requests/ID</c>), a bare tab name, or null (the topology). Path segments and query values are percent-decoded.
    /// </summary>
    public static TracesLocation Parse(string? param)
    {
        if (string.IsNullOrWhiteSpace(param)) return Topology;
        var text = param.Trim();
        var q = text.IndexOf('?');
        var path = q >= 0 ? text[..q] : text;
        var query = q >= 0 ? ParseQuery(text[(q + 1)..]) : new Dictionary<string, string>(StringComparer.Ordinal);
        path = path.Trim('/');
        if (path == "traces") path = "";
        else if (path.StartsWith("traces/", StringComparison.Ordinal)) path = path["traces/".Length..];
        var slash = path.IndexOf('/');
        var head = slash >= 0 ? path[..slash] : path;
        var rest = slash >= 0 ? Unescape(path[(slash + 1)..]) : null;
        if (string.IsNullOrEmpty(rest)) rest = null;
        return head switch
        {
            "sessions" => new TracesLocation(TraceRoutes.Tab.Sessions, rest, null, query),
            "requests" => new TracesLocation(TraceRoutes.Tab.Requests, null, rest, query),
            _ => new TracesLocation(TraceRoutes.Tab.Topology, null, null, query),
        };
    }

    /// <summary>The web path of this location (the route parameter that shows it again).</summary>
    public string ToParam()
    {
        var path = Tab switch
        {
            TraceRoutes.Tab.Sessions when SessionKey is not null => "/traces/sessions/" + TraceRoutes.EncodeSegment(SessionKey),
            TraceRoutes.Tab.Requests when RequestId is not null => "/traces/requests/" + TraceRoutes.EncodeSegment(RequestId),
            _ => TraceRoutes.PathOf(Tab),
        };
        var qs = string.Join('&', Query.Where(kv => kv.Value.Length > 0)
            .Select(kv => $"{TraceRoutes.EncodeSegment(kv.Key)}={TraceRoutes.EncodeSegment(kv.Value)}"));
        return qs.Length > 0 ? $"{path}?{qs}" : path;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var key = Unescape(eq >= 0 ? part[..eq] : part);
            var value = eq >= 0 ? Unescape(part[(eq + 1)..]) : "";
            if (key.Length > 0) map[key] = value;
        }
        return map;
    }

    // URLSearchParams decodes '+' as a space; a path segment keeps it.
    private static string Unescape(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
}

/// <summary>
/// The traces API calls and their react-query keys (web <c>keys.trace*</c> / <c>useTrace*</c> in api/hooks.ts), with their poll
/// intervals: topology 5 s live / 30 s otherwise, sessions 10 s, an active session 3 s, an in-flight request 1 s.
/// </summary>
public static class TraceQueries
{
    /// <summary>The topology ranges offered (web <c>['live', '1h', '24h']</c>).</summary>
    public static readonly string[] TopologyRanges = ["live", "1h", "24h"];

    /// <summary>The session list ranges offered (web <c>['1h', '24h', '7d']</c>).</summary>
    public static readonly string[] SessionRanges = ["1h", "24h", "7d"];

    /// <summary>Rows of the recent-requests picker (web <c>pageSize: 30</c>).</summary>
    public const int RecentPageSize = 30;

    /// <summary>GET of the topology.</summary>
    public static string TopologyPath(string range) => "/api/traces/topology" + ApiClient.Qs(("range", range));

    /// <summary>Key <c>['traces', 'topology', range]</c>.</summary>
    public static QueryKey TopologyKey(string range) => QueryKey.Of("traces", "topology", range);

    /// <summary>The topology's poll interval.</summary>
    public static TimeSpan TopologyInterval(string range) => range == "live" ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30);

    /// <summary>GET of the activity feed beside the topology (web <c>useTraceActivity(30)</c>).</summary>
    public static string ActivityPath(int limit) => "/api/traces/activity" + ApiClient.Qs(("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>Key <c>['traces', 'activity', limit]</c>; a persisted request invalidates it.</summary>
    public static QueryKey ActivityKey(int limit) => QueryKey.Of("traces", "activity", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The activity feed's poll interval: the live feed's "persisted" event refetches it at once; this only catches up when the feed is down.</summary>
    public static readonly TimeSpan ActivityInterval = TimeSpan.FromSeconds(15);

    /// <summary>GET of the session list.</summary>
    public static string SessionsPath(string range, string? client) => "/api/traces/sessions" + ApiClient.Qs(("range", range), ("client", client));

    /// <summary>Key <c>['traces', 'sessions', range, client ?? '']</c>.</summary>
    public static QueryKey SessionsKey(string range, string? client) => QueryKey.Of("traces", "sessions", range, client ?? "");

    /// <summary>The session list's poll interval.</summary>
    public static readonly TimeSpan SessionsInterval = TimeSpan.FromSeconds(10);

    /// <summary>GET of one session (its turns; the calls and spans of <paramref name="turn"/>, the latest when null).</summary>
    public static string SessionPath(string key, string? turn) => "/api/traces/sessions/" + ApiClient.Enc(key) + ApiClient.Qs(("turn", turn));

    /// <summary>Key <c>['traces', 'session', key, turn ?? '']</c>.</summary>
    public static QueryKey SessionKey(string key, string? turn) => QueryKey.Of("traces", "session", key, turn ?? "");

    /// <summary>An active session is polled every 3 s; a quiet one is not.</summary>
    public static TimeSpan? SessionInterval(bool active) => active ? TimeSpan.FromSeconds(3) : null;

    /// <summary>GET of one request's span tree.</summary>
    public static string RequestPath(string id) => "/api/traces/requests/" + ApiClient.Enc(id);

    /// <summary>Key <c>['traces', 'request', id]</c>.</summary>
    public static QueryKey RequestKey(string id) => QueryKey.Of("traces", "request", id);

    /// <summary>A request still in flight is polled every second.</summary>
    public static TimeSpan? RequestInterval(bool inFlight) => inFlight ? TimeSpan.FromSeconds(1) : null;

    /// <summary>
    /// Why a trace view has nothing to show is that the connected server predates tracing: a 404 on <c>/api/traces</c> (an older
    /// astra-server still holding the port), which an empty canvas would hide (web <c>TraceError</c>'s <c>unsupported</c>).
    /// </summary>
    public static bool IsUnsupported(Exception? error) =>
        error is ApiException { Status: 404 } e && e.Message.Contains("/api/traces", StringComparison.Ordinal);

    /// <summary>
    /// A 404 that is not "unsupported" means the session / request is gone: the view shows its "not found" state, not an error
    /// (the <c>query.isError &amp;&amp; !data &amp;&amp; !(404 …)</c> branch of session.tsx / request.tsx).
    /// </summary>
    public static bool IsNotFound(Exception? error) => error is ApiException { Status: 404 } && !IsUnsupported(error);
}
