using Dapper;
using Astra.Core.Mcp;

namespace Astra.Data.Repositories;

/// <summary>
/// The MCP gateway's tables (plan mcp-gateway): upstream servers (<c>mcp_servers</c>), the OAuth state Astra holds for some of
/// them (<c>mcp_oauth</c>) and the tool-call log (<c>mcp_calls</c>). Nothing here is read per model request, so none of it is cached.
/// </summary>
public sealed class McpRepository
{
    private const string ServerSelect = """
        SELECT id, slug, name, transport, url, auth_type, headers_enc, enabled, timeout_seconds, status, status_message,
               protocol_version, checked_at, created_at, updated_at
        FROM mcp_servers
        """;

    private readonly SqliteConnectionFactory _factory;

    public McpRepository(SqliteConnectionFactory factory) => _factory = factory;

    // ---------------------------------------------------------------- servers

    /// <summary>All servers, by name.</summary>
    public async Task<IReadOnlyList<McpServerRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<McpServerRecord>($"{ServerSelect} ORDER BY name, id")).ToList();
    }

    public async Task<McpServerRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<McpServerRecord>($"{ServerSelect} WHERE id = @id", new { id });
    }

    public async Task<McpServerRecord?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<McpServerRecord>($"{ServerSelect} WHERE slug = @slug", new { slug });
    }

    /// <summary>The servers with these ids that exist (a bound id may name a deleted server), by slug.</summary>
    public async Task<IReadOnlyList<McpServerRecord>> ListByIdsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return [];
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        // The table is a handful of rows; filtering here keeps the query free of a variable-length IN list.
        return (await ListAsync(ct)).Where(s => wanted.Contains(s.Id)).OrderBy(s => s.Slug, StringComparer.Ordinal).ToList();
    }

    /// <summary>Whether another server already uses <paramref name="slug"/>.</summary>
    public async Task<bool> SlugTakenAsync(string slug, string? exceptId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM mcp_servers WHERE slug = @slug AND id <> @exceptId",
            new { slug, exceptId = exceptId ?? "" }) > 0;
    }

    public async Task InsertAsync(McpServerRecord s, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO mcp_servers(id, slug, name, transport, url, auth_type, headers_enc, enabled, timeout_seconds, status,
                                    status_message, protocol_version, checked_at, created_at, updated_at)
            VALUES (@id, @slug, @name, @transport, @url, @auth_type, @headers_enc, @enabled, @timeout_seconds, @status,
                    @status_message, @protocol_version, @checked_at, @created_at, @updated_at)
            """, McpServerParameters.From(s));
    }

    /// <summary>Saves a server's settings (not its status cache, see <see cref="UpdateStatusAsync"/>).</summary>
    public async Task UpdateAsync(McpServerRecord s, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE mcp_servers SET slug = @slug, name = @name, transport = @transport, url = @url, auth_type = @auth_type,
                   headers_enc = @headers_enc, enabled = @enabled, timeout_seconds = @timeout_seconds, updated_at = @updated_at
            WHERE id = @id
            """, McpServerParameters.From(s));
    }

    /// <summary>Records what the last contact showed. Does not touch <c>updated_at</c>: that marks a settings change (it keys the gateway's connection cache).</summary>
    public async Task UpdateStatusAsync(string id, string status, string? message, string? protocolVersion, DateTimeOffset at, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE mcp_servers SET status = @status, status_message = @message, protocol_version = @protocolVersion, checked_at = @at
            WHERE id = @id
            """, new { id, status, message, protocolVersion, at = DateTimeOffsetHandler.ToStorage(at) });
    }

    /// <summary>Deletes a server and, through the foreign key, its OAuth state. False = no such server.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM mcp_servers WHERE id = @id", new { id }) == 1;
    }

    // ---------------------------------------------------------------- oauth

    public async Task<McpOAuthRecord?> GetOAuthAsync(string serverId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<McpOAuthRecord>(
            "SELECT server_id, tokens_enc, issuer, scope, updated_at FROM mcp_oauth WHERE server_id = @serverId", new { serverId });
    }

    public async Task SaveOAuthAsync(McpOAuthRecord o, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO mcp_oauth(server_id, tokens_enc, issuer, scope, updated_at)
            VALUES (@server_id, @tokens_enc, @issuer, @scope, @updated_at)
            ON CONFLICT(server_id) DO UPDATE SET tokens_enc = excluded.tokens_enc, issuer = excluded.issuer,
                scope = excluded.scope, updated_at = excluded.updated_at
            """, new
        {
            server_id = o.ServerId,
            tokens_enc = o.TokensEnc,
            issuer = o.Issuer,
            scope = o.Scope,
            updated_at = DateTimeOffsetHandler.ToStorage(o.UpdatedAt),
        });
    }

    /// <summary>Forgets the tokens of a server (it needs authorizing again).</summary>
    public async Task DeleteOAuthAsync(string serverId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM mcp_oauth WHERE server_id = @serverId", new { serverId });
    }

    // ---------------------------------------------------------------- calls

    /// <summary>Appends a call. Through the untracked connection: the log is never read by the gateway's cached lookups.</summary>
    public async Task InsertCallAsync(McpCallRecord c, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenUntrackedAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO mcp_calls(at, token_id, server_id, server_slug, tool, duration_ms, ok, error_category, front_era)
            VALUES (@at, @token_id, @server_id, @server_slug, @tool, @duration_ms, @ok, @error_category, @front_era)
            """, new
        {
            at = DateTimeOffsetHandler.ToStorage(c.At),
            token_id = c.TokenId,
            server_id = c.ServerId,
            server_slug = c.ServerSlug,
            tool = c.Tool,
            duration_ms = c.DurationMs,
            ok = c.Ok ? 1 : 0,
            error_category = c.ErrorCategory,
            front_era = c.FrontEra,
        });
    }

    /// <summary>The newest calls, optionally of one token or one server.</summary>
    public async Task<IReadOnlyList<McpCallRecord>> ListCallsAsync(int limit, string? tokenId = null, string? serverId = null, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<McpCallRecord>("""
            SELECT id, at, token_id, server_id, server_slug, tool, duration_ms, ok, error_category, front_era
            FROM mcp_calls
            WHERE (@tokenId IS NULL OR token_id = @tokenId) AND (@serverId IS NULL OR server_id = @serverId)
            ORDER BY id DESC LIMIT @limit
            """, new { tokenId, serverId, limit })).ToList();
    }

    /// <summary>Drops calls older than <paramref name="before"/>; returns how many.</summary>
    public async Task<int> PruneCallsAsync(DateTimeOffset before, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenUntrackedAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM mcp_calls WHERE at < @before", new { before = DateTimeOffsetHandler.ToStorage(before) });
    }
}

/// <summary>The bound parameters of a server insert / update (a named type: Dapper.AOT cannot see through a helper that returns <c>object</c>).</summary>
internal sealed class McpServerParameters
{
    public string id { get; init; } = "";
    public string slug { get; init; } = "";
    public string name { get; init; } = "";
    public string transport { get; init; } = "";
    public string url { get; init; } = "";
    public string auth_type { get; init; } = "";
    public string? headers_enc { get; init; }
    public int enabled { get; init; }
    public int timeout_seconds { get; init; }
    public string status { get; init; } = "";
    public string? status_message { get; init; }
    public string? protocol_version { get; init; }
    public string? checked_at { get; init; }
    public string created_at { get; init; } = "";
    public string updated_at { get; init; } = "";

    public static McpServerParameters From(McpServerRecord s) => new()
    {
        id = s.Id,
        slug = s.Slug,
        name = s.Name,
        transport = s.Transport,
        url = s.Url,
        auth_type = s.AuthType,
        headers_enc = s.HeadersEnc,
        enabled = s.Enabled ? 1 : 0,
        timeout_seconds = s.TimeoutSeconds,
        status = s.Status,
        status_message = s.StatusMessage,
        protocol_version = s.ProtocolVersion,
        checked_at = s.CheckedAt is { } c ? DateTimeOffsetHandler.ToStorage(c) : null,
        created_at = DateTimeOffsetHandler.ToStorage(s.CreatedAt),
        updated_at = DateTimeOffsetHandler.ToStorage(s.UpdatedAt),
    };
}
