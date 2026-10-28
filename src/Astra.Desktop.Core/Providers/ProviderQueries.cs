using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Providers;

/// <summary>
/// The queries and mutations of the providers page beyond the shared <see cref="ProvidersStore"/> list (web/src/api/hooks.ts:
/// <c>useTemplates</c>, <c>usePriceKeys</c>, <c>useTimeseries('7d','day','provider')</c>, <c>useImportSources</c>, <c>useCreateProvider</c>,
/// <c>useUpdateProvider</c>, <c>useImportProviders</c>, <c>useFetchProviderQuota</c>). Every query is keyed like the web's so a second
/// holder shares the cache. The list itself stays the store's <c>JsonArray</c> query (other pages share it); <see cref="ParseProviders"/>
/// turns it into DTOs.
/// </summary>
public sealed class ProviderQueries
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the page's queries over the shared client and cache.</summary>
    public ProviderQueries(QueryClient queries, ApiClient api, Func<DateTimeOffset> now)
    {
        _queries = queries;
        _api = api;
        // Templates change only with a release: five minutes stale like the web (staleTime 5 * 60_000).
        Templates = queries.Query(new QueryKey("provider-templates"),
            async ct => ProviderTemplates.Parse(await api.GetAsync<JsonArray>("/api/provider-templates", ct)),
            new QueryOptions<List<ProviderTemplateInfo>> { StaleTime = TimeSpan.FromMinutes(5) });
        PriceKeys = queries.Query(new QueryKey("price-keys"), async ct => await api.GetAsync<List<PriceKeyInfo>>("/api/price-keys", ct) ?? []);
        // One request for every card: per-provider daily buckets over the last week; refreshed every 30 s like the web.
        var offset = ProviderFormat.TzOffsetMinutes(now());
        Series = queries.Polling(new QueryKey("stats", "timeseries", "7d", "day", "provider", offset.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            async ct => await api.GetAsync<List<TimeseriesPointDto>>(
                "/api/stats/timeseries" + ApiClient.Qs(("range", "7d"), ("groupBy", "day"), ("by", "provider"), ("tzOffset", offset)), ct) ?? [],
            TimeSpan.FromSeconds(30));
        // staleTime 0, no cache after closing: a sheet opening rescans (Query.Enabled gates it until the sheet is open).
        ImportSources = queries.Query(new QueryKey("providers", "import-sources"),
            async ct => await api.GetAsync<List<ImportSourceDto>>("/api/providers/import/sources", ct) ?? [],
            new QueryOptions<List<ImportSourceDto>> { StaleTime = TimeSpan.Zero });
    }

    /// <summary>GET /api/provider-templates.</summary>
    public Query<List<ProviderTemplateInfo>> Templates { get; }

    /// <summary>GET /api/price-keys.</summary>
    public Query<List<PriceKeyInfo>> PriceKeys { get; }

    /// <summary>GET /api/stats/timeseries 7d by provider.</summary>
    public Query<List<TimeseriesPointDto>> Series { get; }

    /// <summary>GET /api/providers/import/sources (the sheet activates it while open).</summary>
    public Query<List<ImportSourceDto>> ImportSources { get; }

    /// <summary>Decodes the shared list query's JSON into DTOs (items that do not decode are skipped).</summary>
    public static List<ProviderDto> ParseProviders(JsonArray? array)
    {
        var list = new List<ProviderDto>();
        var info = JsonContexts.Info<ProviderDto>(Json.Api);
        foreach (var node in array ?? [])
        {
            try
            {
                if (System.Text.Json.JsonSerializer.Deserialize(node, info) is { } dto) list.Add(dto);
            }
            catch (System.Text.Json.JsonException)
            {
                // A row the DTO cannot read is left out rather than blanking the page.
            }
        }
        return list;
    }

    /// <summary>The queries a provider write makes stale (web <c>invalidateProviders</c>).</summary>
    public void InvalidateProviders(string? id = null)
    {
        _queries.Invalidate("providers");
        _queries.Invalidate("provider");
        if (id is not null) _queries.Invalidate("provider", id);
        _queries.Invalidate("clients");
        _queries.Invalidate("client-models");
    }

    /// <summary>PATCH /api/providers/{id} with the changed fields; the written provider.</summary>
    public async Task<ProviderDto?> UpdateAsync(string id, JsonObject patch, CancellationToken ct = default)
    {
        var result = await _api.PatchAsync<JsonObject, ProviderDto>("/api/providers/" + ApiClient.Enc(id), patch, ct);
        InvalidateProviders(id);
        _queries.Invalidate("provider-models");
        _queries.Invalidate("remote-models");
        return result;
    }

    /// <summary>POST /api/providers; the created provider.</summary>
    public async Task<ProviderDto?> CreateAsync(JsonObject body, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<JsonObject, ProviderDto>("/api/providers", body, ct);
        InvalidateProviders(result?.Id);
        return result;
    }

    /// <summary>POST /api/providers/import for the chosen entries.</summary>
    public async Task<ImportResultDto?> ImportAsync(IEnumerable<ImportCandidateDto> chosen, CancellationToken ct = default)
    {
        var selections = chosen.Select(c => new ImportSelectionDto(c.Source, c.Ref)).ToList();
        var result = await _api.PostAsync<List<ImportSelectionDto>, ImportResultDto>("/api/providers/import", selections, ct);
        InvalidateProviders();
        _queries.Invalidate("providers", "import-sources");
        return result;
    }

    /// <summary>POST /api/providers/{id}/quota: runs the balance query now (the card's gauge button).</summary>
    public async Task FetchQuotaAsync(string id, CancellationToken ct = default)
    {
        try
        {
            await _api.PostAsync("/api/providers/" + ApiClient.Enc(id) + "/quota", ct);
        }
        finally
        {
            // Success stores the snapshot, and a configuration error is also recorded on it: either way the list is stale.
            _queries.Invalidate("providers");
            _queries.Invalidate("provider", id);
        }
    }
}
