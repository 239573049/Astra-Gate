using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.SmartRouting;

/// <summary>
/// The Smart routing page's reads and writes: <c>useSmartRouting</c> (polled every 30 s), <c>useRoutingModels</c>,
/// <c>useUpdateSmartRouting</c> and <c>useTestSmartRouting</c> of web/src/api/hooks.ts.
/// </summary>
public sealed class SmartRoutingApi
{
    /// <summary>['smart-routing']</summary>
    public static readonly QueryKey SettingsKey = "smart-routing";

    /// <summary>['routing-models']</summary>
    public static readonly QueryKey ModelsKey = "routing-models";

    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the API over the shared client and cache.</summary>
    public SmartRoutingApi(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
        Settings = queries.Polling(SettingsKey, async ct => await api.GetAsync<SmartRoutingDto>("/api/smart-routing", ct) ?? throw Empty("smart routing"), TimeSpan.FromSeconds(30));
        Models = queries.Query(ModelsKey, async ct => await api.GetAsync<List<RoutingModelDto>>("/api/smart-routing/models", ct) ?? []);
    }

    /// <summary>The settings with the live status (a judge being left alone, sessions holding a grade).</summary>
    public Query<SmartRoutingDto> Settings { get; }

    /// <summary>Every model id a provider serves: what a grade can be given.</summary>
    public Query<List<RoutingModelDto>> Models { get; }

    /// <summary>
    /// Saves (PUT). The answer replaces the cached settings; the virtual model joins (or leaves) the clients' model pickers and the
    /// lists of grade models, so those are read again.
    /// </summary>
    public async Task<SmartRoutingDto> SaveAsync(SmartRoutingUpdate update, CancellationToken ct = default)
    {
        var saved = await _api.PutAsync<SmartRoutingUpdate, SmartRoutingDto>("/api/smart-routing", update, ct).ConfigureAwait(false) ?? throw Empty("smart routing");
        _queries.SetData(SettingsKey, saved);
        _queries.Invalidate("client-models");
        _queries.Invalidate("routing-models");
        return saved;
    }

    /// <summary>Asks the judge about a sample text with the settings as they are being edited; saves nothing.</summary>
    public async Task<SmartRoutingTestDto> TestAsync(SmartRoutingTestRequest request, CancellationToken ct = default)
        => await _api.PostAsync<SmartRoutingTestRequest, SmartRoutingTestDto>("/api/smart-routing/test", request, ct).ConfigureAwait(false) ?? throw Empty("smart routing test");

    private static ApiException Empty(string what) => new(0, "empty " + what + " response");
}
