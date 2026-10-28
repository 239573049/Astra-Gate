using System.Text.Json.Nodes;
using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Api;

/// <summary>
/// The react-query keys of web/src/api/hooks.ts that more than one page shares (<c>keys</c> object). Using the same key in
/// two stores/pages is what makes them share one cached request, so keep these in step with that file.
/// </summary>
public static class QueryKeys
{
    /// <summary>['providers']</summary>
    public static readonly QueryKey Providers = "providers";

    /// <summary>['tokens']</summary>
    public static readonly QueryKey Tokens = "tokens";

    /// <summary>['clients']</summary>
    public static readonly QueryKey Clients = "clients";

    /// <summary>['settings']</summary>
    public static readonly QueryKey Settings = "settings";

    /// <summary>['requests', ...]: the request list and its filters (prefix of every list query).</summary>
    public static readonly QueryKey Requests = "requests";
}

// Skeleton shared read-only stores (plan avalonia-page-inventory §5.1): only the plumbing and one query each. A page that
// needs more (a detail query, a mutation that invalidates) appends its own members in its own file or a partial class —
// the stores are partial for that reason.

/// <summary>Providers list (GET /api/providers). Typed as raw JSON until Contracts batch C2 (<c>ProviderDto</c>) lands; then change <c>JsonArray</c> to <c>List&lt;ProviderDto&gt;</c> here.</summary>
public sealed partial class ProvidersStore
{
    /// <summary>The list; key ['providers'].</summary>
    public Query<JsonArray> Providers { get; }

    /// <summary>Creates the store over the shared client and cache.</summary>
    public ProvidersStore(QueryClient queries, ApiClient api)
    {
        Providers = queries.Query(QueryKeys.Providers, async ct => await api.GetAsync<JsonArray>("/api/providers", ct) ?? []);
    }
}

/// <summary>Tokens list (GET /api/tokens).</summary>
public sealed partial class TokensStore
{
    /// <summary>The list; key ['tokens'].</summary>
    public Query<List<TokenDto>> Tokens { get; }

    /// <summary>Creates the store over the shared client and cache.</summary>
    public TokensStore(QueryClient queries, ApiClient api)
    {
        Tokens = queries.Query(QueryKeys.Tokens, async ct => await api.GetAsync<List<TokenDto>>("/api/tokens", ct) ?? []);
    }
}

/// <summary>Clients list (GET /api/clients). Raw JSON until Contracts batch C5 (<c>ClientInfoDto</c>) lands.</summary>
public sealed partial class ClientsStore
{
    /// <summary>The list; key ['clients'].</summary>
    public Query<JsonArray> Clients { get; }

    /// <summary>Creates the store over the shared client and cache.</summary>
    public ClientsStore(QueryClient queries, ApiClient api)
    {
        Clients = queries.Query(QueryKeys.Clients, async ct => await api.GetAsync<JsonArray>("/api/clients", ct) ?? []);
    }
}
