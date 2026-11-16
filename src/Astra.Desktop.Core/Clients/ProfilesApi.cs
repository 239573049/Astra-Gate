using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Clients;

/// <summary>
/// Profiles (web <c>useProfiles</c> and friends): named snapshots of every configured client. The list is the <c>profiles</c> query;
/// every write invalidates it and what a client switch invalidates (clients, providers, provider, client-models).
/// </summary>
public sealed class ProfilesApi(ApiClient api, QueryClient queries)
{
    /// <summary>The saved profiles (the active one marked, and whether the clients drifted from it).</summary>
    public Query<List<ProfileDto>> Profiles()
        => queries.Query(new QueryKey("profiles"), async ct => await api.GetAsync<List<ProfileDto>>("/api/profiles", ct) ?? [],
            new QueryOptions<List<ProfileDto>> { SameData = QueryData.JsonEqual });

    /// <summary>What applying the profile would do per client; writes nothing.</summary>
    public async Task<ProfilePlanDto> PreviewAsync(string id, CancellationToken ct = default)
        => await api.GetAsync<ProfilePlanDto>($"/api/profiles/{ApiClient.Enc(id)}/preview", ct) ?? throw new ApiException(0, "empty response");

    /// <summary>Switches every client the profile covers through the regular enable / disable path.</summary>
    public async Task<ProfilePlanDto> ApplyAsync(string id, CancellationToken ct = default)
    {
        var plan = await api.PostAsync<ProfilePlanDto>($"/api/profiles/{ApiClient.Enc(id)}/apply", ct) ?? throw new ApiException(0, "empty response");
        Invalidate();
        return plan;
    }

    /// <summary>Saves the clients' current state as a new profile (which becomes the active one).</summary>
    public async Task<ProfileDto> SaveAsync(string name, CancellationToken ct = default)
    {
        var saved = await api.PostAsync<ProfileInput, ProfileDto>("/api/profiles", new ProfileInput(name), ct) ?? throw new ApiException(0, "empty response");
        Invalidate();
        return saved;
    }

    /// <summary>Renames a profile and / or replaces its snapshot with the clients' current state.</summary>
    public async Task<ProfileDto> UpdateAsync(string id, string? name, bool overwrite, CancellationToken ct = default)
    {
        var saved = await api.PutAsync<ProfileInput, ProfileDto>($"/api/profiles/{ApiClient.Enc(id)}", new ProfileInput(name, overwrite ? true : null), ct)
                    ?? throw new ApiException(0, "empty response");
        Invalidate();
        return saved;
    }

    /// <summary>Deletes a profile; the clients' configs do not change.</summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await api.DeleteAsync($"/api/profiles/{ApiClient.Enc(id)}", ct);
        Invalidate();
    }

    private void Invalidate()
    {
        queries.Invalidate("profiles");
        queries.Invalidate("clients");
        queries.Invalidate("providers");
        queries.Invalidate("provider");
        queries.Invalidate("client-models");
    }
}
