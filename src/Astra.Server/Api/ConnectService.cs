using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;
using Astra.Gateway.Pipeline;

namespace Astra.Server.Api;

/// <summary>
/// Connecting this machine to a unified gateway (a company's Astra server): the gateway becomes a local provider holding the
/// employee's key (encrypted like any provider key), and the chosen clients are bound to it through the usual enable flow. A client's
/// own config therefore carries only a local token; the key never reaches it. Leaving removes the provider and unbinds the clients.
/// </summary>
public sealed class ConnectService(AstraDatabase db, ISecretProtector secrets, ClientService clients, IHttpClientFactory http, SettingsService settings)
{
    /// <summary>The <c>Provider.Settings</c> key that marks the unified gateway provider (its value: gateway address and token name).</summary>
    public const string SettingsKey = "unified_gateway";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>The gateway address as the provider's base: http(s) only, no trailing slash, no trailing <c>/v1</c>.</summary>
    internal static string NormalizeUrl(string? raw)
    {
        if (!Uri.TryCreate(raw?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new AdminApiException(400, "The gateway address must be an http(s) URL");
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) path = path[..^3];
        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    /// <summary>Plain http to a host that is not this machine: the key would cross the network in the clear.</summary>
    internal static bool IsInsecure(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out var u) && !u.IsLoopback;

    private async Task<Provider?> FindAsync(CancellationToken ct) =>
        (await db.Providers.ListAsync(ct)).FirstOrDefault(p => p.Settings[SettingsKey] is JsonObject);

    public async Task<ConnectJoinResultDto> JoinAsync(ConnectJoinRequest request, CancellationToken ct)
    {
        var url = NormalizeUrl(request.GatewayUrl);
        var key = request.Key?.Trim();
        if (string.IsNullOrEmpty(request.Token) == string.IsNullOrEmpty(key))
            throw new AdminApiException(400, "Provide either a join token or a key");
        if (key?.Any(char.IsControl) == true) throw new AdminApiException(400, "The key must not contain control characters");

        using var client = http.CreateClient();
        client.Timeout = Timeout;
        string tokenName;
        if (!string.IsNullOrEmpty(request.Token))
        {
            var exchanged = await GatewayJsonAsync(client, HttpMethod.Post, $"{url}/v1/astra/join/exchange", null, new JsonObject { ["token"] = request.Token }, ct);
            key = exchanged["key"]?.GetValue<string>() ?? throw new AdminApiException(502, "The gateway answered without a key");
            tokenName = exchanged["name"]?.GetValue<string>() ?? "";
        }
        else
        {
            var info = await GatewayJsonAsync(client, HttpMethod.Get, $"{url}/v1/astra/key", key, null, ct);
            tokenName = info["name"]?.GetValue<string>() ?? "";
        }
        var context = await GatewayJsonAsync(client, HttpMethod.Get, $"{url}/v1/astra/connect-context", key, null, ct);
        var models = (context["models"] as JsonArray ?? []).Select(m => m?["id"]?.GetValue<string>()).OfType<string>()
            .Where(id => id.Length is > 0 and <= 512).Distinct(StringComparer.Ordinal).ToList();

        var existing = await FindAsync(ct);
        var provider = existing ?? new Provider
        {
            Id = Ulid.NewUlid(), Category = "custom", AuthScheme = AuthSchemes.Bearer,
            Name = settings.Current.Locale == "en" ? "Unified gateway" : "统一网关",
            SortOrder = (await db.Providers.ListAsync(ct)).Select(p => p.SortOrder).DefaultIfEmpty(-1).Max() + 1,
        };
        // The gateway speaks every wire protocol, so each one goes through as it is, without translation. It takes the key as a bearer.
        provider.Endpoints =
        [
            new() { Protocol = ApiProtocol.OpenAIChat, BaseUrl = $"{url}/v1" },
            new() { Protocol = ApiProtocol.OpenAIResponses, BaseUrl = $"{url}/v1" },
            new() { Protocol = ApiProtocol.Anthropic, BaseUrl = $"{url}/v1" },
            new() { Protocol = ApiProtocol.Gemini, BaseUrl = $"{url}/v1beta" },
        ];
        provider.PreferredUpstreamProtocols = provider.Endpoints.Select(e => e.Protocol).ToList();
        provider.ApiKeyEnc = secrets.Protect(key!);
        provider.Enabled = true;
        provider.Settings[SettingsKey] = new JsonObject { ["url"] = url, ["token_name"] = tokenName };
        ProviderEndpoints.Validate(provider);
        var added = await ProviderEndpoints.PlanModelsAsync(db, provider, models, ct);
        if (existing is null) await db.Providers.InsertWithModelsAsync(provider, added, ct);
        else await db.Providers.UpdateWithModelsAsync(provider, added, ct);
        return new ConnectJoinResultDto(provider.Id, tokenName, url, models, IsInsecure(url));
    }

    public async Task<ConnectStatusDto> StatusAsync(CancellationToken ct)
    {
        if (await FindAsync(ct) is not { } provider) return new ConnectStatusDto(false, null, null, null, [], null, null);
        var marker = provider.Settings[SettingsKey] as JsonObject;
        var url = marker?["url"]?.GetValue<string>();
        var bound = (await db.Clients.ListBindingsByProviderAsync(provider.Id, ct)).Select(b => b.ClientKind).Distinct().Order().ToList();
        JsonObject? info = null;
        string? error = null;
        try
        {
            using var client = http.CreateClient();
            client.Timeout = Timeout;
            info = await GatewayJsonAsync(client, HttpMethod.Get, $"{url}/v1/astra/key", secrets.Unprotect(provider.ApiKeyEnc!), null, ct);
        }
        catch (AdminApiException e)
        {
            error = e.Message;
        }
        return new ConnectStatusDto(true, provider.Id, url, marker?["token_name"]?.GetValue<string>(), bound, info, error);
    }

    /// <summary>Binds each client to the gateway provider as its primary (the providers it had stay behind it, so they still serve what the gateway cannot).</summary>
    public async Task<ConnectApplyResultDto> ApplyAsync(ConnectApplyRequest request, CancellationToken ct)
    {
        var provider = await FindAsync(ct) ?? throw new AdminApiException(409, "Not connected to a unified gateway");
        var results = new List<ConnectClientResultDto>();
        foreach (var kind in request.Clients.Distinct())
        {
            try
            {
                // Enabling makes the gateway the primary and drops the old primary; put the providers the client had back behind it.
                var before = await db.Clients.ListBindingsAsync(kind, ct);
                await clients.EnableAsync(kind, new JsonObject { ["providerId"] = provider.Id }, ct);
                var behind = before.Where(b => b.ProviderId != provider.Id).Select(b => new ClientBindingDto(b.ProviderId, b.AccountId));
                if (behind.Any()) await clients.SetBindingsAsync(kind, [new ClientBindingDto(provider.Id, null), .. behind], ct);
                results.Add(new ConnectClientResultDto(kind, true, null));
            }
            catch (Exception e) when (e is AdminApiException or IOException or FormatException or InvalidOperationException)
            {
                results.Add(new ConnectClientResultDto(kind, false, e.Message));
            }
        }
        return new ConnectApplyResultDto(results);
    }

    /// <summary>Removes the gateway: clients that had other providers keep them, those with nothing else are disabled (their config restored), then the provider goes.</summary>
    public async Task<ConnectLeaveResultDto> LeaveAsync(CancellationToken ct)
    {
        var provider = await FindAsync(ct) ?? throw new AdminApiException(409, "Not connected to a unified gateway");
        var (unbound, disabled) = await clients.UnbindProviderAsync(provider.Id, ct);
        if (!await db.Providers.DeleteIfUnboundAsync(provider.Id, ct))
            throw new AdminApiException(409, "The unified gateway is still bound to a client");
        return new ConnectLeaveResultDto(unbound, disabled);
    }

    /// <summary>One call to the gateway; every failure becomes an <see cref="AdminApiException"/> with a status the console can tell apart (401 key, 410 dead link, 502 unreachable).</summary>
    private static async Task<JsonObject> GatewayJsonAsync(HttpClient client, HttpMethod method, string url, string? key, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (key is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AdminApiException(502, "Cannot reach the gateway: " + (e is TaskCanceledException ? "timed out" : e.Message));
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                return JsonNode.Parse(text) as JsonObject ?? throw new AdminApiException(502, "The gateway answered with something that is not an Astra gateway");
            var message = TryMessage(text) ?? $"The gateway answered {(int)response.StatusCode}";
            // Join-link failures keep their status (404 unknown, 410 dead, 403 disabled); a rejected key is 401; anything else is the gateway's fault.
            var status = (int)response.StatusCode is 401 or 403 or 404 or 410 ? (int)response.StatusCode : 502;
            throw new AdminApiException(status, message);
        }
    }

    private static string? TryMessage(string text)
    {
        try
        {
            return JsonNode.Parse(text)?["error"] is { } e ? e is JsonObject o ? o["message"]?.GetValue<string>() : e.GetValue<string>() : null;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
