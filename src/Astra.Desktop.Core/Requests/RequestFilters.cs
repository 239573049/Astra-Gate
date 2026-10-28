using System.Collections.ObjectModel;
using System.Text;

namespace Astra.Desktop.Core.Requests;

/// <summary>The request log's list filters (web <c>RequestFilterId</c>).</summary>
public enum RequestFilterId
{
    /// <summary>Client kind.</summary>
    Client,

    /// <summary>Token id.</summary>
    Token,

    /// <summary>Provider id.</summary>
    Provider,

    /// <summary>Request status.</summary>
    Status,

    /// <summary><c>remote</c> | <c>local</c>.</summary>
    Origin,
}

/// <summary>
/// Picked values per list filter; an empty list means "any" (web <c>RequestFilterValues</c>). Immutable with value equality, so the
/// page can compare "what the debounced query used" with "what the user has now" the way the web's state does.
/// </summary>
public sealed class RequestFilterValues : IEquatable<RequestFilterValues>
{
    private readonly string[][] _values;

    private RequestFilterValues(string[][] values) => _values = values;

    /// <summary>No filter set (web <c>EMPTY_REQUEST_FILTERS</c>).</summary>
    public static RequestFilterValues Empty { get; } = new(Enumerable.Range(0, RequestFilterCatalog.Ids.Count).Select(_ => Array.Empty<string>()).ToArray());

    /// <summary>The picked values of <paramref name="id"/>.</summary>
    public IReadOnlyList<string> Get(RequestFilterId id) => _values[(int)id];

    /// <summary>A copy with <paramref name="id"/> set to <paramref name="values"/>.</summary>
    public RequestFilterValues With(RequestFilterId id, IEnumerable<string> values)
    {
        var copy = (string[][])_values.Clone();
        copy[(int)id] = values.ToArray();
        return new RequestFilterValues(copy);
    }

    /// <summary>True when any filter has a value.</summary>
    public bool Any => _values.Any(v => v.Length > 0);

    /// <inheritdoc />
    public bool Equals(RequestFilterValues? other)
    {
        if (other is null) return false;
        for (var i = 0; i < _values.Length; i++)
            if (!_values[i].AsSpan().SequenceEqual(other._values[i])) return false;
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is RequestFilterValues v && Equals(v);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (var list in _values) foreach (var v in list) h.Add(v);
        return h.ToHashCode();
    }
}

/// <summary>The constants and small rules of <c>web/src/components/RequestFilters.tsx</c>.</summary>
public static class RequestFilterCatalog
{
    /// <summary>The statuses the status filter offers (web <c>REQUEST_STATUSES</c>); the label of each is <c>status.&lt;id&gt;</c>.</summary>
    public static readonly IReadOnlyList<string> Statuses = new ReadOnlyCollection<string>(["success", "blocked", "upstream_error", "gateway_error", "client_cancelled"]);

    /// <summary>The origins (web <c>REQUEST_ORIGINS</c>); the label of each is <c>requests.origin.&lt;id&gt;</c>.</summary>
    public static readonly IReadOnlyList<string> Origins = new ReadOnlyCollection<string>(["remote", "local"]);

    /// <summary>The filters in the bar's order (web <c>FIELDS</c>).</summary>
    public static readonly IReadOnlyList<RequestFilterId> Ids = new ReadOnlyCollection<RequestFilterId>(
        [RequestFilterId.Client, RequestFilterId.Token, RequestFilterId.Provider, RequestFilterId.Status, RequestFilterId.Origin]);

    /// <summary>The multi-select's label key (web <c>FIELD_LABEL</c>).</summary>
    public static string LabelKey(RequestFilterId id) => id switch
    {
        RequestFilterId.Client => "requests.col.client",
        RequestFilterId.Token => "requests.col.token",
        RequestFilterId.Provider => "requests.col.provider",
        RequestFilterId.Status => "requests.col.status",
        _ => "requests.col.origin",
    };

    /// <summary>The multi-select's placeholder key (web <c>FIELD_PLACEHOLDER</c>).</summary>
    public static string PlaceholderKey(RequestFilterId id) => id switch
    {
        RequestFilterId.Client => "requests.allClients",
        RequestFilterId.Token => "requests.allTokens",
        RequestFilterId.Provider => "requests.allProviders",
        RequestFilterId.Status => "requests.allStatus",
        _ => "requests.allOrigins",
    };

    /// <summary>The query-string / route parameter name of a filter (web <c>URL_FILTERS</c>).</summary>
    public static string ParamName(RequestFilterId id) => id switch
    {
        RequestFilterId.Client => "client",
        RequestFilterId.Token => "token",
        RequestFilterId.Provider => "provider",
        RequestFilterId.Status => "status",
        _ => "origin",
    };

    /// <summary>The server's single <c>origin</c> filter: one origin picked narrows the list, none or both mean any (web <c>originQuery</c>).</summary>
    public static string OriginQuery(IReadOnlyList<string> values) => values.Count == 1 ? values[0] : "";

    /// <summary>
    /// The origin filter only matters once remote access has been used (now, or in the rows at hand); it also stays while a value
    /// is picked (web <c>showOrigin || filters.origin.length &gt; 0</c>).
    /// </summary>
    public static bool ShowOrigin(bool tunnelEnabled, bool anyRemoteRow, RequestFilterValues filters)
        => tunnelEnabled || anyRemoteRow || filters.Get(RequestFilterId.Origin).Count > 0;

    /// <summary>The fields the bar shows (web <c>fields</c>).</summary>
    public static IReadOnlyList<RequestFilterId> VisibleFields(bool showOrigin)
        => showOrigin ? Ids : Ids.Where(id => id != RequestFilterId.Origin).ToList();

    /// <summary>True when the model keyword or any filter is set (web <c>hasFilters</c> / the bar's <c>active</c>).</summary>
    public static bool HasFilters(string model, RequestFilterValues filters) => model.Trim() != "" || filters.Any;
}

/// <summary>
/// The page's filters as a route parameter: the web keeps them in the address (<c>/requests?token=&lt;id&gt;&amp;provider=…&amp;model=…</c>,
/// comma lists) so a token's "view requests" link opens the log on that token. The desktop navigator carries one string per route, so
/// the same query string (without the <c>?</c>) is the <c>param</c> of <c>AppRoute.Requests</c>.
/// </summary>
public static class RequestRouteParams
{
    /// <summary>Reads <c>token=a,b&amp;model=gpt</c>; unknown parameters and empty values are ignored (web <c>filtersFromParams</c>).</summary>
    public static (string Model, RequestFilterValues Filters) Parse(string? param)
    {
        var filters = RequestFilterValues.Empty;
        var model = "";
        if (string.IsNullOrEmpty(param)) return (model, filters);
        var text = param[0] == '?' ? param[1..] : param;
        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            if (key == "model") model = value;
            else
            {
                foreach (var id in RequestFilterCatalog.Ids)
                    if (RequestFilterCatalog.ParamName(id) == key)
                        filters = filters.With(id, value.Split(',', StringSplitOptions.RemoveEmptyEntries));
            }
        }
        return (model, filters);
    }

    /// <summary>Writes the parameter back (web <c>paramsFromFilters</c>): model first, then each non-empty filter as a comma list.</summary>
    public static string Format(string model, RequestFilterValues filters)
    {
        var sb = new StringBuilder();
        void Add(string key, string value)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
        }
        if (model != "") Add("model", model);
        foreach (var id in RequestFilterCatalog.Ids)
        {
            var values = filters.Get(id);
            if (values.Count > 0) Add(RequestFilterCatalog.ParamName(id), string.Join(',', values));
        }
        return sb.ToString();
    }
}
