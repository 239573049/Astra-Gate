namespace Astra.Clients.Library;

/// <summary>Why the library refused something; the server maps it to an HTTP status.</summary>
public enum SkillFailure
{
    /// <summary>Bad input (a name that is not a safe directory name, a source with no SKILL.md).</summary>
    Invalid,

    /// <summary>Nothing by that name.</summary>
    NotFound,

    /// <summary>Something of the user's is in the way (a same-named directory, a Skill that already exists).</summary>
    Conflict,
}

public sealed class SkillLibraryException(string message, SkillFailure failure) : Exception(message)
{
    public SkillFailure Failure { get; } = failure;
}

/// <summary>One client as the Skills page sees it.</summary>
/// <param name="Supported">False for clients Astra does not know a Skills directory for.</param>
/// <param name="Detected">The client's config directory exists.</param>
public sealed record SkillTargetInfo(string Kind, bool Supported, bool Detected, string? Dir, bool ReadsShared, IReadOnlyList<string> AlsoReads);

/// <summary>How a client sees one Skill. <see cref="State"/>: <c>own</c>, <c>via-shared</c>, <c>via-&lt;kind&gt;</c>, <c>off</c>, <c>unsupported</c>.</summary>
public sealed record SkillClientView(string Kind, string State, string? How, bool Modified);

public sealed record SkillPlacementInfo(string Path, string Target, string How, string Origin, bool Modified, bool Missing);

public sealed record SkillInfo(
    string Name, string Description, string SourceKind, string? SourceJson, DateTimeOffset UpdatedAt,
    IReadOnlyList<SkillClientView> Clients, IReadOnlyList<SkillPlacementInfo> Placements);

public sealed record FoundLocation(string Path, string Target, string Hash, DateTimeOffset Modified);

/// <summary>A Skill found in a client directory that is not (yet) Astra's. <see cref="Kind"/>: <c>unmanaged</c> or <c>external</c> (a link to elsewhere).</summary>
public sealed record FoundSkill(
    string Name, string Kind, IReadOnlyList<FoundLocation> Locations, bool Conflict, bool InLibrary, bool SkillsCli, string? LinkTarget);

public sealed record SkillLibraryState(IReadOnlyList<SkillTargetInfo> Targets, IReadOnlyList<SkillInfo> Skills, IReadOnlyList<FoundSkill> Found, string LibraryDir, string SharedDir);

/// <summary>Result of switching a client on or off. <see cref="NeedsConfirm"/> lists the other clients a shared link also serves.</summary>
public sealed record SkillSetResult(bool Done, IReadOnlyList<string> Affected)
{
    public bool NeedsConfirm => !Done;
}

/// <summary>A Skill offered by a source directory (<see cref="SkillLibrary.Probe"/>).</summary>
public sealed record SkillCandidate(string Name, string Description, string Dir, string Status, string? Reason);

public sealed record SkillOutcome(string Name, bool Ok, string? Reason);

public sealed record MigratePreviewItem(string Name, IReadOnlyList<FoundLocation> Locations, bool Conflict, bool InLibrary, string? Skip);

public sealed record SkillMigrateChoice(string Name, string? Keep);
