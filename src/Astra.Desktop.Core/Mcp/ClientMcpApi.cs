using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Mcp;

/// <summary>
/// What the MCP page writes into AI clients (issue #34; the web's <c>useClientMcp</c> / <c>useLocalMcp*</c> hooks): the gateway's
/// <c>/mcp</c> entry (<c>/api/clients/{kind}/mcp</c>) and the user's own MCP servers (<c>/api/mcp/local</c>). Every write is refused
/// off this machine by the server. Token keys, environment values and header values never come back: only names and states.
/// </summary>
public sealed class ClientMcpApi
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the facade over the shared client and cache.</summary>
    public ClientMcpApi(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
        Entries = queries.Query<List<ClientMcpDto>>("client-mcp", async ct => await api.GetAsync<List<ClientMcpDto>>("/api/clients/mcp", ct) ?? []);
        Servers = queries.Query<List<LocalMcpServerDto>>("local-mcp", async ct => await api.GetAsync<List<LocalMcpServerDto>>("/api/mcp/local", ct) ?? []);
    }

    /// <summary>['client-mcp']: where Claude Code and Codex stand with the gateway entry.</summary>
    public Query<List<ClientMcpDto>> Entries { get; }

    /// <summary>['local-mcp']: the user's own MCP servers with their placement in each client.</summary>
    public Query<List<LocalMcpServerDto>> Servers { get; }

    /// <summary>POST /api/clients/{kind}/mcp: writes the entry carrying <paramref name="tokenId"/>'s key (409 for a foreign or changed entry).</summary>
    public async Task<ClientMcpDto> ApplyAsync(string kind, string tokenId, bool force, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<ClientMcpApplyDto, ClientMcpDto>($"/api/clients/{Uri.EscapeDataString(kind)}/mcp", new ClientMcpApplyDto(tokenId, force), ct)
            ?? throw new ApiException(0, "empty response");
        _queries.Invalidate("client-mcp");
        return result;
    }

    /// <summary>DELETE /api/clients/{kind}/mcp: takes the entry out (a changed one stays unless <paramref name="force"/>).</summary>
    public async Task<ClientMcpDto> RemoveAsync(string kind, bool force, CancellationToken ct = default)
    {
        var path = $"/api/clients/{Uri.EscapeDataString(kind)}/mcp{(force ? "?force=true" : "")}";
        var result = await _api.DeleteAsync<ClientMcpDto>(path, ct) ?? throw new ApiException(0, "empty response");
        _queries.Invalidate("client-mcp");
        return result;
    }

    /// <summary>POST /api/mcp/local: creates a definition and writes it into the chosen clients.</summary>
    public async Task<LocalMcpServerDto> CreateAsync(LocalMcpServerInputDto body, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<LocalMcpServerInputDto, LocalMcpServerDto>("/api/mcp/local", body, ct) ?? throw new ApiException(0, "empty response");
        ServersChanged();
        return result;
    }

    /// <summary>PUT /api/mcp/local/{id}: <c>Env</c> / <c>Headers</c> left null keep the stored values.</summary>
    public async Task<LocalMcpServerDto> UpdateAsync(string id, LocalMcpServerInputDto body, bool force = false, CancellationToken ct = default)
    {
        var result = await _api.PutAsync<LocalMcpServerInputDto, LocalMcpServerDto>($"/api/mcp/local/{Uri.EscapeDataString(id)}{Force(force)}", body, ct)
            ?? throw new ApiException(0, "empty response");
        ServersChanged();
        return result;
    }

    /// <summary>POST /api/mcp/local/{id}/apply: writes the definition into the wanted clients again.</summary>
    public async Task<LocalMcpServerDto> RewriteAsync(string id, bool force, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<LocalMcpServerDto>($"/api/mcp/local/{Uri.EscapeDataString(id)}/apply{Force(force)}", ct) ?? throw new ApiException(0, "empty response");
        ServersChanged();
        return result;
    }

    /// <summary>DELETE /api/mcp/local/{id}: removes the entries from the clients, then the definition.</summary>
    public async Task DeleteAsync(string id, bool force = false, CancellationToken ct = default)
    {
        await _api.DeleteAsync($"/api/mcp/local/{Uri.EscapeDataString(id)}{Force(force)}", ct);
        ServersChanged();
    }

    /// <summary>GET /api/mcp/local/import: the entries the clients already have that Astra did not write.</summary>
    public async Task<List<LocalMcpCandidateDto>> CandidatesAsync(CancellationToken ct = default)
        => await _api.GetAsync<List<LocalMcpCandidateDto>>("/api/mcp/local/import", ct) ?? [];

    /// <summary>POST /api/mcp/local/import: takes the chosen entries over.</summary>
    public async Task<LocalMcpImportResultDto> ImportAsync(IEnumerable<LocalMcpCandidateDto> items, CancellationToken ct = default)
    {
        var body = new LocalMcpImportDto([.. items.Select(i => new LocalMcpImportItemDto(i.Kind, i.Name))]);
        var result = await _api.PostAsync<LocalMcpImportDto, LocalMcpImportResultDto>("/api/mcp/local/import", body, ct) ?? throw new ApiException(0, "empty response");
        ServersChanged();
        return result;
    }

    private static string Force(bool force) => force ? "?force=true" : "";

    private void ServersChanged() => _queries.Invalidate("local-mcp");
}
