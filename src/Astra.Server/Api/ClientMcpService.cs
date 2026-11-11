using Astra.Clients.Config;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Server.Hosting;

namespace Astra.Server.Api;

/// <summary>
/// Applies the MCP gateway's <c>/mcp</c> entry to Claude Code and Codex (issue #34, <c>.plan/client-mcp-apply.md</c>), through
/// <see cref="ClientMcpApplier"/>. The entry carries one gateway token's key, read back here from its encrypted copy and written
/// straight into the client's file: it never goes through the browser or back out of the API.
/// </summary>
public sealed class ClientMcpService(
    AstraDatabase db, ISecretProtector secrets, ClientEnvironment env, AstraPaths paths, ServerOptions server, SettingsService settings)
{
    private readonly ClientMcpApplier _mcp = new(env, db.ClientConfigState, new ClientConfigApplier(db.ClientConfigState, env, paths.ClientBackupsDir));
    private readonly SemaphoreSlim _writes = new(1, 1);

    public async Task<List<ClientMcpDto>> ListAsync(CancellationToken ct)
    {
        var tokens = await db.Tokens.ListAsync(ct);
        return ClientMcpApplier.SupportedKinds.Select(k => ToDto(k, tokens)).ToList();
    }

    public async Task<ClientMcpDto> StatusAsync(string kind, CancellationToken ct)
    {
        RequireSupported(kind);
        return ToDto(kind, await db.Tokens.ListAsync(ct));
    }

    public async Task<ClientMcpDto> ApplyAsync(string kind, ClientMcpApplyDto input, CancellationToken ct)
    {
        RequireSupported(kind);
        var token = await db.Tokens.GetAsync(input.TokenId, ct) ?? throw new AdminApiException(404, "Token not found");
        if (!token.Enabled) throw new AdminApiException(400, "The token is disabled");
        if (token.McpServerIds().Count == 0) throw new AdminApiException(400, "The token has no MCP servers bound; bind some on the Tokens page first");
        if (token.KeyEnc is null) throw new AdminApiException(409, "Token has no key yet");
        await _writes.WaitAsync(ct);
        try
        {
            try
            {
                _mcp.Apply(kind, server.GatewayBaseUrl, secrets.Unprotect(token.KeyEnc), input.Force);
            }
            catch (ClientMcpException e)
            {
                throw new AdminApiException(409, e.Message);
            }
        }
        finally
        {
            _writes.Release();
        }
        return ToDto(kind, await db.Tokens.ListAsync(ct));
    }

    /// <summary>Takes the entry out. A changed entry stays unless <paramref name="force"/>.</summary>
    public async Task<ClientMcpDto> RemoveAsync(string kind, bool force, CancellationToken ct)
    {
        RequireSupported(kind);
        await _writes.WaitAsync(ct);
        try
        {
            _mcp.Remove(kind, force);
        }
        finally
        {
            _writes.Release();
        }
        return ToDto(kind, await db.Tokens.ListAsync(ct));
    }

    private ClientMcpDto ToDto(string kind, IReadOnlyList<Core.Tokens.TokenRecord> tokens)
    {
        var status = _mcp.Inspect(kind);
        // which of Astra's tokens the entry carries: by the hash of its key, never by returning the key
        var key = status.Authorization is { } auth && auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth["Bearer ".Length..] : null;
        var hash = key is null ? null : GatewayTokens.Hash(key);
        var token = hash is null ? null : tokens.FirstOrDefault(t => t.KeyHash == hash);
        return new ClientMcpDto(kind, status.State.ToString().ToLowerInvariant(), status.File, status.Url, token?.Id, token?.Name,
            settings.Current.McpGateway.Enabled, server.GatewayBaseUrl.TrimEnd('/') + "/mcp", status.Error);
    }

    private static void RequireSupported(string kind)
    {
        if (!ClientMcpApplier.Supports(kind)) throw new AdminApiException(404, "This client has no MCP gateway entry");
    }
}
