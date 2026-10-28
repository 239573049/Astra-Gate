using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Clients;

/// <summary>
/// The clients hooks of web/src/api/hooks.ts as queries and calls over the shared <see cref="QueryClient"/>: the keys are the web's
/// (<c>clients</c>, <c>client-models</c>, <c>backups</c>, <c>client-install-job</c>, <c>claude-direct</c>, <c>claude-direct-requests</c>),
/// and every write invalidates what the web's <c>useClientMutation</c> does (clients, providers, provider, client-models + its own keys).
/// The typed list lives under <c>['clients','page']</c> (prefix <c>clients</c>, so every clients invalidation reaches it) because it needs
/// its own refetch interval (2 s while an install runs); the shared <c>ClientsStore</c> query stays raw JSON.
/// </summary>
public sealed class ClientsApi(ApiClient api, QueryClient queries)
{
    /// <summary>The query key of <see cref="Clients"/>.</summary>
    public static readonly QueryKey ListKey = new("clients", "page");

    /// <summary>The client list; polls every 2 s while an install / update runs (web <c>useClients</c>).</summary>
    public Query<List<ClientInfoDto>> Clients()
        => queries.Query(ListKey, async ct => await api.GetAsync<List<ClientInfoDto>>("/api/clients", ct) ?? [],
            new QueryOptions<List<ClientInfoDto>> { RefetchInterval = list => list?.Any(c => c.Install?.Busy == true) == true ? TimeSpan.FromSeconds(2) : null });

    /// <summary>The models the client's bound providers serve (web <c>useClientModels</c>; no retry).</summary>
    public Query<List<string>> Models(string kind)
        => queries.Query(new QueryKey("client-models", kind), async ct => await api.GetAsync<List<string>>($"/api/clients/{ApiClient.Enc(kind)}/models", ct) ?? [],
            new QueryOptions<List<string>> { Retries = 0 });

    /// <summary>The client's config backups (web <c>useBackups</c>).</summary>
    public Query<List<ClientBackupDto>> Backups(string kind)
        => queries.Query(new QueryKey("backups", kind), async ct => await api.GetAsync<List<ClientBackupDto>>($"/api/clients/{ApiClient.Enc(kind)}/backups", ct) ?? []);

    /// <summary>The current or last install / update; polls every 800 ms while it runs (web <c>useClientInstallJob</c>; 404 = none).</summary>
    public Query<ClientInstallJobDto?> InstallJob(string kind)
        => queries.Query<ClientInstallJobDto?>(new QueryKey("client-install-job", kind),
            async ct => await api.GetAsync<ClientInstallJobDto>($"/api/clients/{ApiClient.Enc(kind)}/install", ct),
            new QueryOptions<ClientInstallJobDto?> { Retries = 0, RefetchInterval = job => job?.State == "running" ? TimeSpan.FromMilliseconds(800) : null });

    /// <summary>The launcher's direct-connection state (web <c>useClaudeDirect</c>; loopback only, no retry).</summary>
    public Query<ClaudeDirectStateDto> ClaudeDirect()
        => queries.Query(new QueryKey("claude-direct"), async ct => await api.GetAsync<ClaudeDirectStateDto>(ClaudeDirectPath, ct) ?? throw new ApiException(0, "empty response"),
            new QueryOptions<ClaudeDirectStateDto> { Retries = 0 });

    /// <summary>An account's reported requests; polls every 5 s while <paramref name="live"/> (web <c>useClaudeDirectRequests</c>).</summary>
    public Query<List<ClaudeDirectRequestDto>> ClaudeDirectRequests(string accountId, bool live)
        => queries.Query(new QueryKey("claude-direct-requests", accountId),
            async ct => await api.GetAsync<List<ClaudeDirectRequestDto>>($"{ClaudeDirectPath}/accounts/{ApiClient.Enc(accountId)}/requests", ct) ?? [],
            new QueryOptions<List<ClaudeDirectRequestDto>> { Retries = 0, RefetchInterval = _ => live ? TimeSpan.FromSeconds(5) : null });

    private const string ClaudeDirectPath = "/api/clients/claude-code/direct";

    // ---------- writes ----------

    /// <summary>Fetches the latest versions (forced or within the server's 6 h window) and stores the refreshed list (web <c>useCheckClientUpdates</c>).</summary>
    public async Task<List<ClientInfoDto>> CheckUpdatesAsync(bool force, CancellationToken ct = default)
    {
        var list = await api.PostAsync<ClientUpdateCheckRequest, List<ClientInfoDto>>("/api/clients/check-updates", new ClientUpdateCheckRequest(force), ct) ?? [];
        queries.SetData(ListKey, list);
        return list;
    }

    /// <summary>Pins (or clears, with "") the subscription account of the primary binding.</summary>
    public Task SetBindingAsync(string kind, string providerId, string? accountId, CancellationToken ct = default)
        => Mutate(() => api.PutAsync($"/api/clients/{ApiClient.Enc(kind)}/binding", new BindingInput(providerId, accountId), ct), new QueryKey("client-models", kind));

    /// <summary>Replaces the provider list; the order is the routing order (first = primary).</summary>
    public Task SetBindingsAsync(string kind, IReadOnlyList<ClientBindingDto> bindings, CancellationToken ct = default)
        => Mutate(() => api.PutAsync($"/api/clients/{ApiClient.Enc(kind)}/bindings", new BindingsInput([.. bindings]), ct), new QueryKey("client-models", kind));

    /// <summary>The config edits enabling would make; writes nothing.</summary>
    public async Task<ClientPreviewDto> PreviewEnableAsync(string kind, JsonObject body, CancellationToken ct = default)
        => await api.PostAsync<JsonObject, ClientPreviewDto>($"/api/clients/{ApiClient.Enc(kind)}/preview-enable", body, ct) ?? throw new ApiException(0, "empty response");

    /// <summary>Enables (or re-applies) the client.</summary>
    public async Task<ClientInfoDto> EnableAsync(string kind, JsonObject body, CancellationToken ct = default)
    {
        var result = await api.PostAsync<JsonObject, ClientInfoDto>($"/api/clients/{ApiClient.Enc(kind)}/enable", body, ct) ?? throw new ApiException(0, "empty response");
        Invalidate(new QueryKey("backups", kind));
        return result;
    }

    /// <summary>Disables and restores the original values (drifted ones are reported, not restored).</summary>
    public async Task<ClientDisableDto> DisableAsync(string kind, bool force, CancellationToken ct = default)
    {
        var result = await api.PostAsync<ClientDisableDto>($"/api/clients/{ApiClient.Enc(kind)}/{(force ? "force-restore" : "disable")}", ct) ?? throw new ApiException(0, "empty response");
        Invalidate();
        return result;
    }

    /// <summary>Fixes the config files the client cannot read as they are (backed up first).</summary>
    public Task RepairAsync(string kind, CancellationToken ct = default)
        => Mutate(() => api.PostAsync($"/api/clients/{ApiClient.Enc(kind)}/repair", ct), new QueryKey("backups", kind));

    /// <summary>Restores one backup.</summary>
    public Task RestoreBackupAsync(string kind, string id, CancellationToken ct = default)
        => Mutate(() => api.PostAsync($"/api/clients/{ApiClient.Enc(kind)}/backups/{ApiClient.Enc(id)}/restore", ct), new QueryKey("backups", kind));

    /// <summary>Starts an install / update; the job is stored as the install-job query's data (web <c>useStartClientInstall</c>).</summary>
    public async Task<ClientInstallJobDto> StartInstallAsync(string kind, string action, bool elevated, CancellationToken ct = default)
    {
        var job = await api.PostAsync<ClientInstallRequest, ClientInstallJobDto>($"/api/clients/{ApiClient.Enc(kind)}/install", new ClientInstallRequest(action, elevated), ct)
                  ?? throw new ApiException(0, "empty response");
        InstallJob(job.Kind).SetData(job);
        queries.Invalidate("clients");
        return job;
    }

    /// <summary>Stops a running install / update.</summary>
    public async Task<ClientInstallJobDto> CancelInstallAsync(string kind, CancellationToken ct = default)
    {
        var job = await api.PostAsync<JsonObject, ClientInstallJobDto>($"/api/clients/{ApiClient.Enc(kind)}/install/cancel", new JsonObject(), ct)
                  ?? throw new ApiException(0, "empty response");
        InstallJob(job.Kind).SetData(job);
        return job;
    }

    /// <summary>Switches the launcher's mode / account or the statistics consent; stores the answer (web <c>useSelectClaudeDirect</c>).</summary>
    public async Task<ClaudeDirectStateDto> SelectClaudeDirectAsync(ClaudeDirectSelectInput body, CancellationToken ct = default)
    {
        var state = await api.PutAsync<ClaudeDirectSelectInput, ClaudeDirectStateDto>(ClaudeDirectPath, body, ct) ?? throw new ApiException(0, "empty response");
        ClaudeDirect().SetData(state);
        // Consent off stops ingestion immediately: drop any cached statistics as well.
        queries.Invalidate("claude-direct-requests");
        return state;
    }

    private async Task Mutate(Func<Task> call, params QueryKey[] extra)
    {
        await call();
        Invalidate(extra);
    }

    /// <summary>What every clients write invalidates (web <c>useClientMutation</c>), plus <paramref name="extra"/>.</summary>
    private void Invalidate(params QueryKey[] extra)
    {
        queries.Invalidate("clients");
        queries.Invalidate("providers");
        queries.Invalidate("provider");
        queries.Invalidate("client-models");
        foreach (var k in extra) queries.Invalidate(k);
    }
}
