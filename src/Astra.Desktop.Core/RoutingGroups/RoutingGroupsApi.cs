using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.RoutingGroups;

/// <summary>
/// The Routing groups page's reads and writes (web <c>useRoutingGroups</c>, <c>useRoutingGroupStatus</c>, <c>useCreateRoutingGroup</c>,
/// <c>useUpdateRoutingGroup</c>, <c>useDeleteRoutingGroup</c>): the groups, their live status (polled every 10 s while a page shows it)
/// and the models a member can be picked from (the same <c>routing-models</c> query the Smart routing page reads).
/// </summary>
public sealed class RoutingGroupsApi
{
    /// <summary>['routing-groups']</summary>
    public static readonly QueryKey GroupsKey = "routing-groups";

    /// <summary>['routing-groups', 'status']</summary>
    public static readonly QueryKey StatusKey = QueryKey.Of("routing-groups", "status");

    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the facade over the shared client and cache.</summary>
    public RoutingGroupsApi(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
        Groups = queries.Query(GroupsKey, async ct => await api.GetAsync<List<RoutingGroupDto>>("/api/routing/groups", ct) ?? []);
        Status = queries.Polling(StatusKey, async ct => await api.GetAsync<List<RoutingGroupStatusDto>>("/api/routing/groups/status", ct) ?? [], TimeSpan.FromSeconds(10));
        Models = queries.Query(Astra.Desktop.Core.SmartRouting.SmartRoutingApi.ModelsKey, async ct => await api.GetAsync<List<RoutingModelDto>>("/api/smart-routing/models", ct) ?? []);
    }

    /// <summary>Every group.</summary>
    public Query<List<RoutingGroupDto>> Groups { get; }

    /// <summary>Every group's live state: each member's health, quota left and last use, and the group's last decision.</summary>
    public Query<List<RoutingGroupStatusDto>> Status { get; }

    /// <summary>Every model id a provider serves, with that provider: what a member can be picked from.</summary>
    public Query<List<RoutingModelDto>> Models { get; }

    /// <summary>POST: creates a group (the slug defaults to the name's).</summary>
    public async Task<RoutingGroupDto> CreateAsync(RoutingGroupWrite body, CancellationToken ct = default)
    {
        var saved = await _api.PostAsync<RoutingGroupWrite, RoutingGroupDto>("/api/routing/groups", body, ct).ConfigureAwait(false) ?? throw Empty();
        Changed();
        return saved;
    }

    /// <summary>PATCH: changes what <paramref name="body"/> names; the slug never changes.</summary>
    public async Task<RoutingGroupDto> UpdateAsync(string id, RoutingGroupWrite body, CancellationToken ct = default)
    {
        var saved = await _api.PatchAsync<RoutingGroupWrite, RoutingGroupDto>($"/api/routing/groups/{Uri.EscapeDataString(id)}", body, ct).ConfigureAwait(false) ?? throw Empty();
        Changed();
        return saved;
    }

    /// <summary>DELETE: a group another group contains is refused (409).</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await _api.DeleteAsync($"/api/routing/groups/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        Changed();
    }

    // A group joins (or leaves) the clients' model lists.
    private void Changed()
    {
        _queries.Invalidate("routing-groups");
        _queries.Invalidate("client-models");
    }

    private static ApiException Empty() => new(0, "empty routing group response");
}
