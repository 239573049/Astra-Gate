using Dapper;
using Astra.Core.Library;

namespace Astra.Data.Repositories;

/// <summary>The resource library's tables: Skills (<c>library_skills</c>) and the placements Astra made (<c>library_placements</c>).</summary>
public sealed class LibraryRepository : Astra.Core.Library.ILibraryLedger
{
    private const string SkillSelect = "SELECT name, description, source_kind, source_json, hash, created_at, updated_at FROM library_skills";
    private const string PlacementSelect = "SELECT path, skill_name, target, how, copy_hash, origin, placed_at FROM library_placements";

    private readonly SqliteConnectionFactory _factory;

    public LibraryRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<LibrarySkillRecord>> ListSkillsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<LibrarySkillRecord>($"{SkillSelect} ORDER BY name")).ToList();
    }

    public async Task<LibrarySkillRecord?> GetSkillAsync(string name, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<LibrarySkillRecord>($"{SkillSelect} WHERE name = @name", new { name });
    }

    /// <summary>Inserts the Skill, or updates its description, source and hash when the name exists.</summary>
    public async Task UpsertSkillAsync(LibrarySkillRecord s, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO library_skills(name, description, source_kind, source_json, hash, created_at, updated_at)
            VALUES (@name, @description, @source_kind, @source_json, @hash, @created_at, @updated_at)
            ON CONFLICT(name) DO UPDATE SET description = excluded.description, source_kind = excluded.source_kind,
                source_json = excluded.source_json, hash = excluded.hash, updated_at = excluded.updated_at
            """, new
        {
            name = s.Name,
            description = s.Description,
            source_kind = s.SourceKind,
            source_json = s.SourceJson,
            hash = s.Hash,
            created_at = DateTimeOffsetHandler.ToStorage(s.CreatedAt),
            updated_at = DateTimeOffsetHandler.ToStorage(s.UpdatedAt),
        });
    }

    /// <summary>Deletes the Skill; its placements go with it (foreign key), so remove the links from disk first.</summary>
    public async Task DeleteSkillAsync(string name, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM library_skills WHERE name = @name", new { name });
    }

    public async Task<IReadOnlyList<LibraryPlacementRecord>> ListPlacementsAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<LibraryPlacementRecord>($"{PlacementSelect} ORDER BY skill_name, target")).ToList();
    }

    public async Task<IReadOnlyList<LibraryPlacementRecord>> ListPlacementsAsync(string skillName, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<LibraryPlacementRecord>($"{PlacementSelect} WHERE skill_name = @skillName ORDER BY target",
            new { skillName })).ToList();
    }

    public async Task UpsertPlacementAsync(LibraryPlacementRecord p, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO library_placements(path, skill_name, target, how, copy_hash, origin, placed_at)
            VALUES (@path, @skill_name, @target, @how, @copy_hash, @origin, @placed_at)
            ON CONFLICT(path) DO UPDATE SET skill_name = excluded.skill_name, target = excluded.target, how = excluded.how,
                copy_hash = excluded.copy_hash, origin = excluded.origin, placed_at = excluded.placed_at
            """, new
        {
            path = p.Path,
            skill_name = p.SkillName,
            target = p.Target,
            how = p.How,
            copy_hash = p.CopyHash,
            origin = p.Origin,
            placed_at = DateTimeOffsetHandler.ToStorage(p.PlacedAt),
        });
    }

    public async Task DeletePlacementAsync(string path, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM library_placements WHERE path = @path", new { path });
    }
}
