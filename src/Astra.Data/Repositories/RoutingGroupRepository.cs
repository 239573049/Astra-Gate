using Dapper;
using Astra.Core.Routing;

namespace Astra.Data.Repositories;

/// <summary>
/// Routing groups (<c>routing_groups</c>, migration 0027). The gateway reads the whole (small) list on every request that
/// could name one, so <see cref="ListCachedAsync"/> goes through a <see cref="RowCache{TKey,TValue}"/> like the other per-request
/// rows; its callers only read. The admin API uses the uncached methods and gets fresh copies.
/// </summary>
public sealed class RoutingGroupRepository
{
    private const string Select = "SELECT id, slug, name, mode, stays, members_json, enabled, listed, created_at, updated_at FROM routing_groups";
    private const string AllKey = "all";

    private readonly SqliteConnectionFactory _factory;
    private readonly RowCache<string, List<RoutingGroup>> _all;

    public RoutingGroupRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
        _all = new RowCache<string, List<RoutingGroup>>(factory, capacity: 4);
    }

    /// <summary>Every group, by name. Read-only: a cached list shared between requests.</summary>
    public async Task<IReadOnlyList<RoutingGroup>> ListCachedAsync(CancellationToken ct = default) =>
        await _all.GetAsync(AllKey, async (_, token) => (await ListAsync(token)).ToList(), ct) ?? [];

    public async Task<RoutingGroup?> FindCachedAsync(string slug, CancellationToken ct = default) =>
        (await ListCachedAsync(ct)).FirstOrDefault(g => g.Slug == slug);

    public async Task<IReadOnlyList<RoutingGroup>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<RoutingGroup>($"{Select} ORDER BY name, slug")).ToList();
    }

    public async Task<RoutingGroup?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<RoutingGroup>($"{Select} WHERE id = @id", new { id });
    }

    public async Task<RoutingGroup?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<RoutingGroup>($"{Select} WHERE slug = @slug", new { slug });
    }

    public async Task InsertAsync(RoutingGroup g, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO routing_groups(id, slug, name, mode, stays, members_json, enabled, listed, created_at, updated_at)
            VALUES (@id, @slug, @name, @mode, @stays, @members_json, @enabled, @listed, @created_at, @updated_at)
            """, RoutingGroupParameters.From(g));
    }

    public async Task UpdateAsync(RoutingGroup g, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE routing_groups SET slug = @slug, name = @name, mode = @mode, stays = @stays, members_json = @members_json,
                   enabled = @enabled, listed = @listed, updated_at = @updated_at
            WHERE id = @id
            """, RoutingGroupParameters.From(g));
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM routing_groups WHERE id = @id", new { id }) == 1;
    }
}

/// <summary>Bound parameters of an insert / update (a named type: Dapper.AOT cannot see through a helper that returns <c>object</c>).</summary>
internal sealed class RoutingGroupParameters
{
    public string id { get; init; } = "";
    public string slug { get; init; } = "";
    public string name { get; init; } = "";
    public string mode { get; init; } = "";
    public string stays { get; init; } = "";
    public string members_json { get; init; } = "[]";
    public int enabled { get; init; }
    public int listed { get; init; }
    public string created_at { get; init; } = "";
    public string updated_at { get; init; } = "";

    public static RoutingGroupParameters From(RoutingGroup g) => new()
    {
        id = g.Id, slug = g.Slug, name = g.Name, mode = g.Mode, stays = g.Stays, members_json = g.MembersJson,
        enabled = g.Enabled ? 1 : 0, listed = g.Listed ? 1 : 0,
        created_at = DateTimeOffsetHandler.ToStorage(g.CreatedAt), updated_at = DateTimeOffsetHandler.ToStorage(g.UpdatedAt),
    };
}
