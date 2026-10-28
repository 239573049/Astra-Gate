using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Settings;

/// <summary>
/// The queries and writes of the Settings page (web/src/api/hooks.ts: <c>useSettings</c>, <c>useVersion</c>, <c>useAuthStatus</c>,
/// <c>useListen</c>, <c>useAutostart</c>, <c>useTunnel</c>, <c>useUpdateStatus</c>, the webhook and alert hooks). The queries share the
/// app's cache by key (the same keys as the web's <c>keys</c>), so two pages asking for ['tunnel'] share one request; the writes
/// update or invalidate them like the hooks' <c>onSuccess</c> do. A page owns one instance over the app context.
/// </summary>
public sealed class SettingsApi
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;
    private readonly ProvidersStore _providers;
    private readonly TokensStore _tokens;

    /// <summary>Creates the facade over the shared client, cache and stores.</summary>
    public SettingsApi(ApiClient api, QueryClient queries, SettingsStore settings, ProvidersStore providers, TokensStore tokens)
    {
        _api = api;
        _queries = queries;
        _providers = providers;
        _tokens = tokens;
        Settings = settings.Settings;
        Version = queries.Query<VersionDto>("version", async ct => await api.GetAsync<VersionDto>("/api/version", ct) ?? throw Empty(),
            new QueryOptions<VersionDto> { StaleTime = TimeSpan.MaxValue });
        Auth = queries.Query<AuthStatusDto>("auth", async ct => await api.GetAsync<AuthStatusDto>("/api/auth/status", ct) ?? throw Empty(),
            new QueryOptions<AuthStatusDto> { Retries = 0 });
        Listen = queries.Query<ListenDto>("listen", async ct => await api.GetAsync<ListenDto>("/api/server/listen", ct) ?? throw Empty());
        Autostart = queries.Query<AutostartDto>("autostart", async ct => await api.GetAsync<AutostartDto>("/api/server/autostart", ct) ?? throw Empty());
        Tunnel = queries.Query<TunnelStatusDto>("tunnel", async ct => await api.GetAsync<TunnelStatusDto>("/api/tunnel", ct) ?? throw Empty(),
            new QueryOptions<TunnelStatusDto> { RefetchInterval = TunnelLogic.PollInterval });
        Update = queries.Query<UpdateStatusDto>("update", async ct => await api.GetAsync<UpdateStatusDto>("/api/update/status", ct) ?? throw Empty(),
            new QueryOptions<UpdateStatusDto> { RefetchInterval = _ => TimeSpan.FromMinutes(5) });
        Webhooks = queries.Query<List<WebhookDto>>("webhooks", async ct => await api.GetAsync<List<WebhookDto>>("/api/webhooks", ct) ?? []);
        AlertEvents = queries.Query<List<AlertEventDto>>("alert-events", async ct => await api.GetAsync<List<AlertEventDto>>("/api/alerts/events", ct) ?? [],
            new QueryOptions<List<AlertEventDto>> { StaleTime = TimeSpan.MaxValue });
        AlertRules = queries.Query<List<AlertRuleDto>>("alert-rules", async ct => await api.GetAsync<List<AlertRuleDto>>("/api/alerts/rules", ct) ?? []);
        FiringAlerts = queries.Polling<List<AlertStateDto>>(QueryKey.Of("alert-states", "true"),
            async ct => await api.GetAsync<List<AlertStateDto>>("/api/alerts/states?firing=true", ct) ?? [], TimeSpan.FromSeconds(60));
    }

    private static ApiException Empty() => new(0, "empty response");

    /// <summary>['settings'] (shared with the shell's sidebar).</summary>
    public Query<SettingsDto> Settings { get; }

    /// <summary>['version'].</summary>
    public Query<VersionDto> Version { get; }

    /// <summary>['auth']: whether this request needs a session (it comes from another machine or through the tunnel).</summary>
    public Query<AuthStatusDto> Auth { get; }

    /// <summary>['listen'].</summary>
    public Query<ListenDto> Listen { get; }

    /// <summary>['autostart'].</summary>
    public Query<AutostartDto> Autostart { get; }

    /// <summary>['tunnel'], polled at <see cref="TunnelLogic.PollInterval"/>.</summary>
    public Query<TunnelStatusDto> Tunnel { get; }

    /// <summary>['update'].</summary>
    public Query<UpdateStatusDto> Update { get; }

    /// <summary>['webhooks'].</summary>
    public Query<List<WebhookDto>> Webhooks { get; }

    /// <summary>['alert-events']: the event descriptors.</summary>
    public Query<List<AlertEventDto>> AlertEvents { get; }

    /// <summary>['alert-rules'].</summary>
    public Query<List<AlertRuleDto>> AlertRules { get; }

    /// <summary>['alert-states', true]: what is firing now.</summary>
    public Query<List<AlertStateDto>> FiringAlerts { get; }

    /// <summary>The providers list, raw (rule subjects pick from it).</summary>
    public Query<JsonArray> Providers => _providers.Providers;

    /// <summary>The tokens list.</summary>
    public Query<List<TokenDto>> Tokens => _tokens.Tokens;

    /// <summary>['webhook-deliveries', id].</summary>
    public Query<List<WebhookDeliveryDto>> Deliveries(string webhookId)
        => _queries.Query<List<WebhookDeliveryDto>>(QueryKey.Of("webhook-deliveries", webhookId),
            async ct => await _api.GetAsync<List<WebhookDeliveryDto>>($"/api/webhooks/{ApiClient.Enc(webhookId)}/deliveries", ct) ?? []);

    /// <summary>['provider-accounts', id]: the accounts of a subscription provider.</summary>
    public Query<List<ProviderAccountDto>> Accounts(string providerId)
        => _queries.Query<List<ProviderAccountDto>>(QueryKey.Of("provider-accounts", providerId),
            async ct => await _api.GetAsync<List<ProviderAccountDto>>($"/api/providers/{ApiClient.Enc(providerId)}/accounts", ct) ?? []);

    // ---------- writes ----------

    /// <summary>PATCH /api/settings; the answer replaces the cached settings.</summary>
    public async Task<SettingsDto> PatchSettingsAsync(JsonObject patch, CancellationToken ct = default)
    {
        var saved = await _api.PatchAsync<JsonObject, SettingsDto>("/api/settings", patch, ct) ?? throw Empty();
        Settings.SetData(saved);
        return saved;
    }

    /// <summary>POST /api/auth/logout.</summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await _api.PostAsync("/api/auth/logout", ct);
        _queries.Invalidate("auth");
    }

    /// <summary>POST /api/auth/password: sets the admin password (only from this machine); the tunnel and listen checks depend on it.</summary>
    public async Task SetAdminPasswordAsync(string password, CancellationToken ct = default)
    {
        await _api.PostAsync("/api/auth/password", new SetPasswordRequest(password), ct);
        _queries.Invalidate("tunnel");
        _queries.Invalidate("listen");
    }

    /// <summary>PUT /api/server/listen: saves the address for the next start.</summary>
    public async Task<ListenDto> PutListenAsync(string host, CancellationToken ct = default)
    {
        var saved = await _api.PutAsync<ListenRequest, ListenDto>("/api/server/listen", new ListenRequest(host), ct) ?? throw Empty();
        Listen.SetData(saved);
        return saved;
    }

    /// <summary>PUT /api/server/autostart.</summary>
    public async Task<AutostartDto> PutAutostartAsync(bool enabled, CancellationToken ct = default)
    {
        var saved = await _api.PutAsync<AutostartRequest, AutostartDto>("/api/server/autostart", new AutostartRequest(enabled), ct) ?? throw Empty();
        Autostart.SetData(saved);
        return saved;
    }

    /// <summary>PUT /api/tunnel.</summary>
    public async Task<TunnelStatusDto> PutTunnelAsync(TunnelConfigRequest config, CancellationToken ct = default)
    {
        var saved = await _api.PutAsync<TunnelConfigRequest, TunnelStatusDto>("/api/tunnel", config, ct) ?? throw Empty();
        Tunnel.SetData(saved);
        return saved;
    }

    /// <summary>POST /api/tunnel/start | stop.</summary>
    public async Task<TunnelStatusDto> ToggleTunnelAsync(bool on, CancellationToken ct = default)
    {
        var saved = await _api.PostAsync<TunnelStatusDto>(on ? "/api/tunnel/start" : "/api/tunnel/stop", ct) ?? throw Empty();
        Tunnel.SetData(saved);
        return saved;
    }

    /// <summary>POST /api/tunnel/cloudflared/detect: looks for a cloudflared on PATH; saves nothing.</summary>
    public async Task<DetectedCloudflaredDto> DetectCloudflaredAsync(CancellationToken ct = default)
        => await _api.PostAsync<DetectedCloudflaredDto>("/api/tunnel/cloudflared/detect", ct) ?? new DetectedCloudflaredDto(null, null);

    /// <summary>POST /api/update/check: the server checks the feed now; the answer replaces the cached status.</summary>
    public async Task<UpdateStatusDto> CheckUpdateAsync(CancellationToken ct = default)
    {
        var status = await _api.PostAsync<UpdateStatusDto>("/api/update/check", ct) ?? throw Empty();
        Update.SetData(status);
        return status;
    }

    private void RefreshAlerts()
    {
        _queries.Invalidate("webhooks");
        _queries.Invalidate("alert-rules");
        _queries.Invalidate("alert-states");
        _queries.Invalidate("webhook-deliveries");
    }

    /// <summary>POST /api/webhooks: creates one; the secret (generic format) is in the answer, once.</summary>
    public async Task<WebhookSecretDto> CreateWebhookAsync(JsonObject body, CancellationToken ct = default)
    {
        var r = await _api.PostAsync<JsonObject, WebhookSecretDto>("/api/webhooks", body, ct) ?? throw Empty();
        RefreshAlerts();
        return r;
    }

    /// <summary>PATCH /api/webhooks/{id}.</summary>
    public async Task<WebhookDto> UpdateWebhookAsync(string id, JsonObject body, CancellationToken ct = default)
    {
        var r = await _api.PatchAsync<JsonObject, WebhookDto>($"/api/webhooks/{ApiClient.Enc(id)}", body, ct) ?? throw Empty();
        RefreshAlerts();
        return r;
    }

    /// <summary>DELETE /api/webhooks/{id}.</summary>
    public async Task DeleteWebhookAsync(string id, CancellationToken ct = default)
    {
        await _api.DeleteAsync($"/api/webhooks/{ApiClient.Enc(id)}", ct);
        RefreshAlerts();
    }

    /// <summary>POST /api/webhooks/{id}/secret: a new secret, shown once.</summary>
    public async Task<WebhookSecretDto> ResetWebhookSecretAsync(string id, CancellationToken ct = default)
    {
        var r = await _api.PostAsync<WebhookSecretDto>($"/api/webhooks/{ApiClient.Enc(id)}/secret", ct) ?? throw Empty();
        RefreshAlerts();
        return r;
    }

    /// <summary>POST /api/webhooks/{id}/test: delivers a test event.</summary>
    public async Task<WebhookDeliveryDto> TestWebhookAsync(string id, CancellationToken ct = default)
    {
        var r = await _api.PostAsync<WebhookDeliveryDto>($"/api/webhooks/{ApiClient.Enc(id)}/test", ct) ?? throw Empty();
        RefreshAlerts();
        return r;
    }

    /// <summary>POST /api/webhook-deliveries/{id}/redeliver.</summary>
    public async Task<WebhookDeliveryDto> RedeliverAsync(string deliveryId, CancellationToken ct = default)
    {
        var r = await _api.PostAsync<WebhookDeliveryDto>($"/api/webhook-deliveries/{ApiClient.Enc(deliveryId)}/redeliver", ct) ?? throw Empty();
        RefreshAlerts();
        return r;
    }

    /// <summary>POST /api/alerts/rules.</summary>
    public async Task<AlertRuleDto> CreateRuleAsync(JsonObject body, CancellationToken ct = default)
    {
        var r = await _api.PostAsync<JsonObject, AlertRuleDto>("/api/alerts/rules", body, ct) ?? throw Empty();
        RefreshAlerts();
        return r;
    }

    /// <summary>PATCH /api/alerts/rules/{id}.</summary>
    public async Task<AlertRuleDto> UpdateRuleAsync(string id, JsonObject body, CancellationToken ct = default)
    {
        var r = await _api.PatchAsync<JsonObject, AlertRuleDto>($"/api/alerts/rules/{ApiClient.Enc(id)}", body, ct) ?? throw Empty();
        RefreshAlerts();
        return r;
    }

    /// <summary>DELETE /api/alerts/rules/{id}.</summary>
    public async Task DeleteRuleAsync(string id, CancellationToken ct = default)
    {
        await _api.DeleteAsync($"/api/alerts/rules/{ApiClient.Enc(id)}", ct);
        RefreshAlerts();
    }

    /// <summary>The providers to refresh after the quota interval changed (they show their effective interval).</summary>
    public void RefreshProviders() => _queries.Invalidate("providers");
}
