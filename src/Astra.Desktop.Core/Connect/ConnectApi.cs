using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Connect;

/// <summary>
/// Connecting this machine to a unified gateway (a company's Astra server), over the local server's <c>/api/connect</c>: join (the local
/// server exchanges the link's code or checks the key, and keeps the gateway as a provider), send chosen clients through it, leave.
/// The key never passes through the desktop app: it goes from the gateway to the local server and stays there, encrypted.
/// </summary>
public sealed class ConnectApi
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the facade over the shared client and cache.</summary>
    public ConnectApi(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
        Status = queries.Query<ConnectStatusDto>("connect-status", async ct => await api.GetAsync<ConnectStatusDto>("/api/connect/status", ct)
            ?? throw new ApiException(0, "empty response"),
            new QueryOptions<ConnectStatusDto> { RefetchInterval = _ => TimeSpan.FromSeconds(60) });
    }

    /// <summary>['connect-status']: whether a gateway is connected, which clients use it, and the key's limits and usage.</summary>
    public Query<ConnectStatusDto> Status { get; }

    /// <summary>POST /api/connect/join: adds (or refreshes) the gateway provider. Throws <see cref="ApiException"/>: 410 link used / expired / revoked, 404 unknown link, 401 wrong key, 403 token disabled, 502 gateway unreachable.</summary>
    public async Task<ConnectJoinResultDto> JoinAsync(JoinLink link, CancellationToken ct = default)
        => await JoinAsync(new ConnectJoinRequest(link.GatewayUrl, Token: link.Token), ct);

    /// <summary>POST /api/connect/join with the employee's own key (no link).</summary>
    public async Task<ConnectJoinResultDto> JoinWithKeyAsync(string gatewayUrl, string key, CancellationToken ct = default)
        => await JoinAsync(new ConnectJoinRequest(gatewayUrl, Key: key), ct);

    private async Task<ConnectJoinResultDto> JoinAsync(ConnectJoinRequest request, CancellationToken ct)
    {
        var result = await _api.PostAsync<ConnectJoinRequest, ConnectJoinResultDto>("/api/connect/join", request, ct) ?? throw new ApiException(0, "empty response");
        Changed();
        return result;
    }

    /// <summary>POST /api/connect/apply: sends these clients through the gateway; each client's outcome is reported on its own.</summary>
    public async Task<ConnectApplyResultDto> ApplyAsync(IEnumerable<string> clients, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<ConnectApplyRequest, ConnectApplyResultDto>("/api/connect/apply", new ConnectApplyRequest(clients.ToList()), ct)
            ?? throw new ApiException(0, "empty response");
        Changed();
        return result;
    }

    /// <summary>POST /api/connect/leave: removes the gateway; clients with other providers keep them, the rest are disabled.</summary>
    public async Task<ConnectLeaveResultDto> LeaveAsync(CancellationToken ct = default)
    {
        var result = await _api.PostAsync<ConnectLeaveResultDto>("/api/connect/leave", ct) ?? throw new ApiException(0, "empty response");
        Changed();
        return result;
    }

    /// <summary>What a failed join says to the person, by the status the local server relays (the server's own words are the gateway's, in Chinese).</summary>
    public static string JoinProblemKey(ApiException e) => e.Status switch
    {
        410 => "connect.problem.dead",
        404 => "connect.problem.unknown",
        401 => "connect.problem.key",
        403 => "connect.problem.disabled",
        502 => "connect.problem.unreachable",
        _ => "connect.problem.other",
    };

    private void Changed()
    {
        _queries.Invalidate("connect-status");
        _queries.Invalidate("clients");
        _queries.Invalidate("providers");
    }
}
