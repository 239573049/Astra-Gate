using Dapper;
using Astra.Core.Mcp;

namespace Astra.Data.Repositories;

/// <summary>The user's own MCP server definitions (stdio or remote) (<c>mcp_local_servers</c>). A handful of rows, never read per request.</summary>
public sealed class McpLocalRepository(SqliteConnectionFactory factory)
{
    private const string Select = "SELECT id, name, transport, command, args_json, env_enc, url, headers_enc, enabled, clients_json, created_at, updated_at FROM mcp_local_servers";

    public async Task<IReadOnlyList<LocalMcpServer>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<LocalMcpServer>($"{Select} ORDER BY name")).ToList();
    }

    public async Task<LocalMcpServer?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<LocalMcpServer>($"{Select} WHERE id = @id", new { id });
    }

    public async Task<LocalMcpServer?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<LocalMcpServer>($"{Select} WHERE name = @name", new { name });
    }

    public async Task InsertAsync(LocalMcpServer s, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO mcp_local_servers(id, name, transport, command, args_json, env_enc, url, headers_enc, enabled, clients_json, created_at, updated_at)
            VALUES (@id, @name, @transport, @command, @args_json, @env_enc, @url, @headers_enc, @enabled, @clients_json, @created_at, @updated_at)
            """, LocalMcpParameters.From(s));
    }

    public async Task UpdateAsync(LocalMcpServer s, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE mcp_local_servers SET name = @name, transport = @transport, command = @command, args_json = @args_json, env_enc = @env_enc,
                   url = @url, headers_enc = @headers_enc, enabled = @enabled, clients_json = @clients_json, updated_at = @updated_at
            WHERE id = @id
            """, LocalMcpParameters.From(s));
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM mcp_local_servers WHERE id = @id", new { id }) == 1;
    }
}

/// <summary>Bound parameters of an insert / update (a named type: Dapper.AOT cannot see through a helper that returns <c>object</c>).</summary>
internal sealed class LocalMcpParameters
{
    public string id { get; init; } = "";
    public string name { get; init; } = "";
    public string transport { get; init; } = "stdio";
    public string command { get; init; } = "";
    public string url { get; init; } = "";
    public string? headers_enc { get; init; }
    public string args_json { get; init; } = "[]";
    public string? env_enc { get; init; }
    public int enabled { get; init; }
    public string clients_json { get; init; } = "[]";
    public string created_at { get; init; } = "";
    public string updated_at { get; init; } = "";

    public static LocalMcpParameters From(LocalMcpServer s) => new()
    {
        id = s.Id, name = s.Name, transport = s.Transport, command = s.Command, url = s.Url, headers_enc = s.HeadersEnc, args_json = s.ArgsJson, env_enc = s.EnvEnc, enabled = s.Enabled ? 1 : 0,
        clients_json = s.ClientsJson,
        created_at = DateTimeOffsetHandler.ToStorage(s.CreatedAt), updated_at = DateTimeOffsetHandler.ToStorage(s.UpdatedAt),
    };
}
