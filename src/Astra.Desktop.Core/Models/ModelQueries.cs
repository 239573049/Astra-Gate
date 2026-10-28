using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Models;

/// <summary>
/// The model catalog's queries and the cache invalidation its mutations do (web <c>useModels</c>, <c>useModel</c>, <c>usePriceKeys</c> and
/// <c>invalidateModels</c> in web/src/api/hooks.ts). The keys equal the web's so a page that reads the same data shares the cached request.
/// </summary>
public static class ModelQueries
{
    /// <summary>['price-keys']</summary>
    public static readonly QueryKey PriceKeys = "price-keys";

    /// <summary>['models', search, vendor]: the catalog list for a search text and vendor (both "" when unfiltered).</summary>
    public static QueryKey List(string? search, string? vendor) => QueryKey.Of("models", search ?? "", vendor ?? "");

    /// <summary>['model', id]</summary>
    public static QueryKey Detail(string id) => QueryKey.Of("model", id);

    /// <summary><c>GET /api/models</c> with the search and vendor filters.</summary>
    public static Query<List<ModelDto>> ListQuery(QueryClient queries, ApiClient api, string? search, string? vendor)
        => queries.Query(List(search, vendor), async ct => await api.GetAsync<List<ModelDto>>(ModelCatalog.ListPath(search, vendor), ct) ?? []);

    /// <summary><c>GET /api/models/{id}</c>.</summary>
    public static Query<ModelDetailDto> DetailQuery(QueryClient queries, ApiClient api, string id)
        => queries.Query(Detail(id), async ct => await api.GetAsync<ModelDetailDto>(ModelCatalog.DetailPath(id), ct) ?? throw new ApiException(0, "empty model response"));

    /// <summary><c>GET /api/price-keys</c>: every known price key (from templates and existing prices).</summary>
    public static Query<List<PriceKeyInfo>> PriceKeysQuery(QueryClient queries, ApiClient api)
        => queries.Query(PriceKeys, async ct => await api.GetAsync<List<PriceKeyInfo>>("/api/price-keys", ct) ?? []);

    /// <summary>
    /// What every model mutation refreshes (<c>invalidateModels</c>): the lists, the one model (or all details), the price keys and the
    /// other pages' model caches (provider models, client models, remote models).
    /// </summary>
    public static void Invalidate(QueryClient queries, string? id = null)
    {
        queries.Invalidate("models");
        if (id is null) queries.Invalidate("model");
        else queries.Invalidate("model", id);
        queries.Invalidate("price-keys");
        queries.Invalidate("provider-models");
        queries.Invalidate("client-models");
        queries.Invalidate("remote-models");
    }
}
