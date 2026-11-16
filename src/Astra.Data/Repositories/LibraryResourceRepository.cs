using Dapper;
using Astra.Core.Library;

namespace Astra.Data.Repositories;

/// <summary>
/// The non-Skill part of the resource library (migration 0028): the items (instructions, sub-agents, commands, hooks, environment
/// variables), what Astra wrote for them (the ownership ledger), the project directories and the skill router's clients.
/// A handful of rows, never read per request.
/// </summary>
public sealed class LibraryResourceRepository(SqliteConnectionFactory factory) : ILibraryResourceLedger
{
    private const string ItemSelect = "SELECT id, kind, name, description, body, extra_json, targets_json, enabled, created_at, updated_at FROM library_items";
    private const string WriteSelect = "SELECT id, item_id, client, project, path, mode, locator, content_hash, entry, written_at FROM library_writes";

    public async Task<IReadOnlyList<LibraryItemRecord>> ListItemsAsync(string? kind = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return kind is null
            ? (await conn.QueryAsync<LibraryItemRecord>($"{ItemSelect} ORDER BY kind, name")).ToList()
            : (await conn.QueryAsync<LibraryItemRecord>($"{ItemSelect} WHERE kind = @kind ORDER BY name", new { kind })).ToList();
    }

    public async Task<LibraryItemRecord?> GetItemAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<LibraryItemRecord>($"{ItemSelect} WHERE id = @id", new { id });
    }

    public async Task<LibraryItemRecord?> GetItemByNameAsync(string kind, string name, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<LibraryItemRecord>($"{ItemSelect} WHERE kind = @kind AND name = @name", new { kind, name });
    }

    public async Task UpsertItemAsync(LibraryItemRecord i, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO library_items(id, kind, name, description, body, extra_json, targets_json, enabled, created_at, updated_at)
            VALUES (@id, @kind, @name, @description, @body, @extra_json, @targets_json, @enabled, @created_at, @updated_at)
            ON CONFLICT(id) DO UPDATE SET name = excluded.name, description = excluded.description, body = excluded.body,
                extra_json = excluded.extra_json, targets_json = excluded.targets_json, enabled = excluded.enabled, updated_at = excluded.updated_at
            """, new
        {
            id = i.Id,
            kind = i.Kind,
            name = i.Name,
            description = i.Description,
            body = i.Body,
            extra_json = i.ExtraJson,
            targets_json = i.TargetsJson,
            enabled = i.Enabled ? 1 : 0,
            created_at = DateTimeOffsetHandler.ToStorage(i.CreatedAt),
            updated_at = DateTimeOffsetHandler.ToStorage(i.UpdatedAt),
        });
    }

    /// <summary>Deletes the item; its ledger rows go with it (foreign key), so take what it wrote out of the clients first.</summary>
    public async Task DeleteItemAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM library_items WHERE id = @id", new { id });
    }

    public async Task<IReadOnlyList<LibraryWriteRecord>> ListWritesAsync(string? itemId = null, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return itemId is null
            ? (await conn.QueryAsync<LibraryWriteRecord>($"{WriteSelect} ORDER BY path, locator")).ToList()
            : (await conn.QueryAsync<LibraryWriteRecord>($"{WriteSelect} WHERE item_id = @itemId ORDER BY path, locator", new { itemId })).ToList();
    }

    public async Task UpsertWriteAsync(LibraryWriteRecord w, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO library_writes(id, item_id, client, project, path, mode, locator, content_hash, entry, written_at)
            VALUES (@id, @item_id, @client, @project, @path, @mode, @locator, @content_hash, @entry, @written_at)
            ON CONFLICT(id) DO UPDATE SET content_hash = excluded.content_hash, entry = excluded.entry, written_at = excluded.written_at
            """, new
        {
            id = w.Id,
            item_id = w.ItemId,
            client = w.Client,
            project = w.Project,
            path = w.Path,
            mode = w.Mode,
            locator = w.Locator,
            content_hash = w.ContentHash,
            entry = w.Entry,
            written_at = DateTimeOffsetHandler.ToStorage(w.WrittenAt),
        });
    }

    public async Task DeleteWriteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM library_writes WHERE id = @id", new { id });
    }

    public async Task<IReadOnlyList<LibraryProjectRecord>> ListProjectsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<LibraryProjectRecord>("SELECT path, name, added_at FROM library_projects ORDER BY name, path")).ToList();
    }

    public async Task UpsertProjectAsync(LibraryProjectRecord p, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO library_projects(path, name, added_at) VALUES (@path, @name, @added_at)
            ON CONFLICT(path) DO UPDATE SET name = excluded.name
            """, new { path = p.Path, name = p.Name, added_at = DateTimeOffsetHandler.ToStorage(p.AddedAt) });
    }

    public async Task DeleteProjectAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM library_projects WHERE path = @path", new { path });
    }

    public async Task<IReadOnlyList<string>> ListRouterClientsAsync(CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        return (await conn.QueryAsync<string>("SELECT client FROM library_router ORDER BY client")).ToList();
    }

    public async Task SetRouterClientAsync(string client, bool on, CancellationToken ct = default)
    {
        await using var conn = await factory.OpenAsync(ct);
        if (on)
            await conn.ExecuteAsync("INSERT OR IGNORE INTO library_router(client, enabled_at) VALUES (@client, @at)",
                new { client, at = DateTimeOffsetHandler.ToStorage(DateTimeOffset.UtcNow) });
        else
            await conn.ExecuteAsync("DELETE FROM library_router WHERE client = @client", new { client });
    }
}
