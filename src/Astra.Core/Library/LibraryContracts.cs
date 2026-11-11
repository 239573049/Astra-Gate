namespace Astra.Core.Library;

/// <summary>Where a library Skill came from (<c>library_skills.source_kind</c>).</summary>
public static class SkillSourceKinds
{
    /// <summary>Moved in from a client's directory.</summary>
    public const string Migrated = "migrated";

    /// <summary>Copied from a local directory.</summary>
    public const string Local = "local";

    /// <summary>Fetched from a Git repository.</summary>
    public const string Git = "git";
}

/// <summary>How a placement is made (<c>library_placements.how</c>).</summary>
public static class PlacementHows
{
    public const string Symlink = "symlink";

    /// <summary>A Windows directory junction (a symlink needs a privilege a normal user lacks).</summary>
    public const string Junction = "junction";

    /// <summary>A real copy, for volumes that cannot hold links; carries <c>.astra-managed</c>.</summary>
    public const string Copy = "copy";
}

/// <summary>Who made a placement (<c>library_placements.origin</c>).</summary>
public static class PlacementOrigins
{
    /// <summary>Left at the original location when a Skill was migrated into the library.</summary>
    public const string Migrated = "migrated";

    /// <summary>Made when the user switched a client on.</summary>
    public const string Applied = "applied";
}

/// <summary>A Skill in the library (row of <c>library_skills</c>).</summary>
public sealed class LibrarySkillRecord
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string SourceKind { get; set; } = SkillSourceKinds.Local;
    public string? SourceJson { get; set; }
    public string Hash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A link (or copy) Astra put in a client's directory (row of <c>library_placements</c>): the ledger of what Astra may remove.
/// <see cref="Target"/> is a client kind, or <see cref="SkillTargets.Shared"/> for the shared <c>~/.agents/skills</c>.
/// </summary>
public sealed class LibraryPlacementRecord
{
    public string Path { get; set; } = "";
    public string SkillName { get; set; } = "";
    public string Target { get; set; } = "";
    public string How { get; set; } = PlacementHows.Symlink;
    public string? CopyHash { get; set; }
    public string Origin { get; set; } = PlacementOrigins.Applied;
    public DateTimeOffset PlacedAt { get; set; }
}

/// <summary>The pseudo target for the directory several clients read.</summary>
public static class SkillTargets
{
    public const string Shared = "shared";
}

/// <summary>The library's ledger, so the engine (<c>Astra.Clients</c>) needs no reference to the database assembly.</summary>
public interface ILibraryLedger
{
    Task<IReadOnlyList<LibrarySkillRecord>> ListSkillsAsync(CancellationToken ct = default);
    Task<LibrarySkillRecord?> GetSkillAsync(string name, CancellationToken ct = default);
    Task UpsertSkillAsync(LibrarySkillRecord skill, CancellationToken ct = default);
    Task DeleteSkillAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<LibraryPlacementRecord>> ListPlacementsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LibraryPlacementRecord>> ListPlacementsAsync(string skillName, CancellationToken ct = default);
    Task UpsertPlacementAsync(LibraryPlacementRecord placement, CancellationToken ct = default);
    Task DeletePlacementAsync(string path, CancellationToken ct = default);
}
