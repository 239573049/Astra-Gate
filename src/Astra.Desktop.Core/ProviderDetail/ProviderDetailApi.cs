using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Models;
using Astra.Desktop.Core.Providers;

namespace Astra.Desktop.Core.ProviderDetail;

/// <summary>
/// The queries and writes of one provider's detail page (web/src/api/hooks.ts: <c>useProvider</c>, <c>useProviderModels</c>, <c>useRemoteModels</c>,
/// <c>useTemplateUpdate</c>, <c>useProviderUsage</c>, <c>useQuotaTemplates</c>, <c>useModels</c>, <c>usePriceKeys</c>, <c>useTemplates</c> and the
/// mutations the page, <c>ProviderQuotaSection</c> and <c>TemplateSwitch</c> call). Keys equal the web's, and the data types equal the other
/// desktop packages' for shared keys (<c>provider-templates</c>: <see cref="ProviderTemplateInfo"/>, <c>price-keys</c>, <c>models</c>), so the
/// cache is shared instead of throwing on a type clash. Every write invalidates what the web's <c>onSuccess</c> invalidates.
/// </summary>
public sealed class ProviderDetailApi
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>The page's queries for provider <paramref name="id"/>.</summary>
    public ProviderDetailApi(QueryClient queries, ApiClient api, string id)
    {
        _api = api;
        _queries = queries;
        Id = id;
        var path = Base(id);
        Provider = queries.Query(ProviderDetailKeys.Provider(id), async ct => await api.GetAsync<ProviderDto>(path, ct) ?? throw new ApiException(404, "Provider not found"));
        Models = queries.Query(ProviderDetailKeys.ProviderModels(id), async ct => await api.GetAsync<List<ProviderModelDto>>(path + "/models", ct) ?? []);
        // retry: false like the web: an upstream refusal (401) is the answer, a second try only doubles the wait
        RemoteModels = queries.Query(ProviderDetailKeys.RemoteModels(id), async ct => await api.GetAsync<List<RemoteModelDto>>(path + "/remote-models", ct) ?? [],
            new QueryOptions<List<RemoteModelDto>> { Retries = 0 });
        RemoteModels.Enabled = false;
        TemplateUpdate = queries.Query(ProviderDetailKeys.TemplateUpdate(id), async ct => await api.GetAsync<TemplateUpdateDto>(path + "/template-update", ct) ?? new TemplateUpdateDto(0, 0, []));
        TemplateUpdate.Enabled = false;
        Usage = queries.Query(ProviderDetailKeys.ProviderUsage(id), async ct => await api.GetAsync<ProviderUsageDto>(path + "/usage", ct) ?? new ProviderUsageDto("", "", []),
            new QueryOptions<ProviderUsageDto> { RefetchInterval = _ => TimeSpan.FromSeconds(5) });
        QuotaTemplates = queries.Query(ProviderDetailKeys.QuotaTemplates, async ct => await api.GetAsync<List<QuotaTemplateDto>>("/api/provider-quota/templates", ct) ?? [],
            new QueryOptions<List<QuotaTemplateDto>> { StaleTime = TimeSpan.MaxValue });
        Catalog = ModelQueries.ListQuery(queries, api, null, null);
        PriceKeys = ModelQueries.PriceKeysQuery(queries, api);
        Templates = queries.Query(new QueryKey("provider-templates"),
            async ct => ProviderTemplates.Parse(await api.GetAsync<JsonArray>("/api/provider-templates", ct)),
            new QueryOptions<List<ProviderTemplateInfo>> { StaleTime = TimeSpan.FromMinutes(5) });
    }

    /// <summary>The provider id.</summary>
    public string Id { get; }

    /// <summary>GET /api/providers/{id}; key ['provider', id].</summary>
    public Query<ProviderDto> Provider { get; }

    /// <summary>GET …/models; key ['provider-models', id].</summary>
    public Query<List<ProviderModelDto>> Models { get; }

    /// <summary>GET …/remote-models (enabled only while the remote sheet is open); key ['remote-models', id].</summary>
    public Query<List<RemoteModelDto>> RemoteModels { get; }

    /// <summary>GET …/template-update (enabled while its sheet is open); key ['template-update', id].</summary>
    public Query<TemplateUpdateDto> TemplateUpdate { get; }

    /// <summary>GET …/usage, polled every 5 s; key ['provider-usage', id].</summary>
    public Query<ProviderUsageDto> Usage { get; }

    /// <summary>GET /api/provider-quota/templates (never stale); key ['provider-quota-templates'].</summary>
    public Query<List<QuotaTemplateDto>> QuotaTemplates { get; }

    /// <summary>GET /api/models (the "linked model" choices); key ['models','',''].</summary>
    public Query<List<ModelDto>> Catalog { get; }

    /// <summary>GET /api/price-keys; key ['price-keys'].</summary>
    public Query<List<PriceKeyInfo>> PriceKeys { get; }

    /// <summary>GET /api/provider-templates; key ['provider-templates'].</summary>
    public Query<List<ProviderTemplateInfo>> Templates { get; }

    private static string Base(string id) => "/api/providers/" + ApiClient.Enc(id);

    // ---------- invalidation (web invalidateProviders / invalidateProviderModels) ----------

    private void InvalidateProviders(string? id)
    {
        _queries.Invalidate("providers");
        if (id is null) _queries.Invalidate("provider");
        else _queries.Invalidate("provider", id);
        _queries.Invalidate("clients");
        _queries.Invalidate("client-models");
    }

    private void InvalidateProviderModels()
    {
        _queries.Invalidate("provider-models", Id);
        _queries.Invalidate("remote-models", Id);
        _queries.Invalidate("model");
        InvalidateProviders(Id);
    }

    private void Written(ProviderDto? p)
    {
        if (p is not null) Provider.SetData(p);
        InvalidateProviders(Id);
    }

    // ---------- provider ----------

    /// <summary>PATCH /api/providers/{id} with the changed fields (<c>useUpdateProvider</c>).</summary>
    public async Task<ProviderDto?> UpdateAsync(JsonObject patch, CancellationToken ct = default)
    {
        var p = await _api.PatchAsync<JsonObject, ProviderDto>(Base(Id), patch, ct);
        Written(p);
        _queries.Invalidate("provider-models", Id);
        _queries.Invalidate("remote-models", Id);
        return p;
    }

    /// <summary>PUT …/model-map (<c>useSetProviderModelMap</c>).</summary>
    public async Task<ProviderDto?> SetModelMapAsync(IReadOnlyDictionary<string, string> map, CancellationToken ct = default)
    {
        var inner = new JsonObject();
        foreach (var (from, to) in map) inner[from] = to;
        var p = await _api.PutAsync<JsonObject, ProviderDto>(Base(Id) + "/model-map", new JsonObject { ["map"] = inner }, ct);
        Written(p);
        return p;
    }

    /// <summary>DELETE /api/providers/{id} (<c>useDeleteProvider</c>).</summary>
    public async Task DeleteAsync(CancellationToken ct = default)
    {
        await _api.DeleteAsync(Base(Id), ct);
        InvalidateProviders(null);
    }

    /// <summary>POST …/duplicate: the copy (<c>useDuplicateProvider</c>).</summary>
    public async Task<ProviderDto?> DuplicateAsync(CancellationToken ct = default)
    {
        var p = await _api.PostAsync<ProviderDto>(Base(Id) + "/duplicate", ct);
        InvalidateProviders(null);
        return p;
    }

    /// <summary>POST …/template-update (<c>useApplyTemplateUpdate</c>).</summary>
    public async Task ApplyTemplateUpdateAsync(CancellationToken ct = default)
    {
        await _api.PostAsync<ProviderDto>(Base(Id) + "/template-update", ct);
        InvalidateProviders(Id);
        _queries.Invalidate("provider-models", Id);
        _queries.Invalidate("remote-models", Id);
        _queries.Invalidate("template-update", Id);
    }

    /// <summary>POST …/template-switch (<c>useSwitchTemplate</c>): the key, models and edited settings stay.</summary>
    public async Task<ProviderDto?> SwitchTemplateAsync(string templateId, string? variantId, CancellationToken ct = default)
    {
        var body = new JsonObject { ["templateId"] = templateId };
        if (variantId is not null) body["variantId"] = variantId;
        var p = await _api.PostAsync<JsonObject, ProviderDto>(Base(Id) + "/template-switch", body, ct);
        Written(p);
        _queries.Invalidate("provider-models", Id);
        _queries.Invalidate("remote-models", Id);
        _queries.Invalidate("template-update", Id);
        return p;
    }

    // ---------- provider models ----------

    /// <summary>POST …/models with model ids (<c>useAddProviderModels</c>): the added rows.</summary>
    public async Task<List<ProviderModelDto>> AddModelsAsync(IReadOnlyList<string> modelIds, CancellationToken ct = default)
    {
        var ids = new JsonArray(modelIds.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
        var added = await _api.PostAsync<JsonObject, List<ProviderModelDto>>(Base(Id) + "/models", new JsonObject { ["modelIds"] = ids }, ct);
        InvalidateProviderModels();
        return added ?? [];
    }

    /// <summary>PATCH …/models/{pmId} (<c>useUpdateProviderModel</c>); <paramref name="body"/> holds the fields to change.</summary>
    public async Task UpdateModelAsync(long pmId, JsonObject body, CancellationToken ct = default)
    {
        await _api.PatchAsync<JsonObject, ProviderModelDto>(Base(Id) + "/models/" + pmId.ToString(System.Globalization.CultureInfo.InvariantCulture), body, ct);
        InvalidateProviderModels();
    }

    /// <summary>DELETE …/models/{pmId} (<c>useDeleteProviderModel</c>).</summary>
    public async Task DeleteModelAsync(long pmId, CancellationToken ct = default)
    {
        await _api.DeleteAsync(Base(Id) + "/models/" + pmId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        InvalidateProviderModels();
    }

    // ---------- quota ----------

    private void ApplyQuota(ProviderQuotaDto? state)
    {
        // web applyQuotaState: patch the cached provider instead of refetching everything
        if (state is not null && Provider.Data is { } p)
            Provider.SetData(p with { QuotaConfig = state.Config, Quota = state.Snapshot, QuotaCheckedAtUtc = state.CheckedAtUtc });
        _queries.Invalidate("providers");
    }

    /// <summary>POST …/quota: runs the balance query now (<c>useFetchProviderQuota</c>). A failure is also recorded on the snapshot, so the provider is refetched then.</summary>
    public async Task FetchQuotaAsync(CancellationToken ct = default)
    {
        try
        {
            ApplyQuota(await _api.PostAsync<ProviderQuotaDto>(Base(Id) + "/quota", ct));
        }
        catch (ApiException)
        {
            _queries.Invalidate("provider", Id);
            throw;
        }
    }

    /// <summary>PUT …/quota/config (<c>useSaveProviderQuotaConfig</c>).</summary>
    public async Task SaveQuotaConfigAsync(JsonObject body, CancellationToken ct = default)
        => ApplyQuota(await _api.PutAsync<JsonObject, ProviderQuotaDto>(Base(Id) + "/quota/config", body, ct));

    /// <summary>POST …/quota/test: one unsaved run (<c>useTestProviderQuota</c>).</summary>
    public Task<ProviderQuotaTestDto?> TestQuotaAsync(JsonObject body, CancellationToken ct = default)
        => _api.PostAsync<JsonObject, ProviderQuotaTestDto>(Base(Id) + "/quota/test", body, ct);

    // ---------- connection test ----------

    /// <summary>POST …/test as an event stream (<c>runProviderTest</c>).</summary>
    public IAsyncEnumerable<SseMessage> TestStream(JsonObject request, CancellationToken ct) => _api.StreamPostAsync(Base(Id) + "/test", request, ct);

    /// <summary>A test run ended: the request log has a new row (<c>afterRun</c>).</summary>
    public void AfterTestRun() => _queries.Invalidate(QueryKeys.Requests);
}

/// <summary>The react-query keys of web/src/api/hooks.ts the detail page uses.</summary>
public static class ProviderDetailKeys
{
    /// <summary>['provider', id]</summary>
    public static QueryKey Provider(string id) => QueryKey.Of("provider", id);

    /// <summary>['provider-models', id]</summary>
    public static QueryKey ProviderModels(string id) => QueryKey.Of("provider-models", id);

    /// <summary>['remote-models', id]</summary>
    public static QueryKey RemoteModels(string id) => QueryKey.Of("remote-models", id);

    /// <summary>['template-update', id]</summary>
    public static QueryKey TemplateUpdate(string id) => QueryKey.Of("template-update", id);

    /// <summary>['provider-usage', id]</summary>
    public static QueryKey ProviderUsage(string id) => QueryKey.Of("provider-usage", id);

    /// <summary>['provider-quota-templates']</summary>
    public static readonly QueryKey QuotaTemplates = "provider-quota-templates";
}
