namespace Astra.Api.Contracts;

/// <summary>A client as the Skills page sees it (<c>GET /api/library/skills</c>).</summary>
/// <param name="Supported">False for a client Astra knows no Skills directory for.</param>
/// <param name="Detected">The client's config directory exists on this machine.</param>
/// <param name="Dir">The client's own Skills directory.</param>
/// <param name="ReadsShared">The client also reads the shared <c>~/.agents/skills</c>.</param>
/// <param name="AlsoReads">Other clients whose Skills directory it reads too.</param>
public sealed record SkillTargetDto(string Kind, bool Supported, bool Detected, string? Dir, bool ReadsShared, List<string> AlsoReads);

/// <summary>How one client sees one Skill.</summary>
/// <param name="State"><c>own</c> (a link in its own directory), <c>via-shared</c>, <c>via-&lt;kind&gt;</c> (through another client's directory) or <c>off</c>.</param>
/// <param name="How"><c>symlink</c>, <c>junction</c> or <c>copy</c> when it is on.</param>
/// <param name="Modified">A copy whose content was changed since Astra wrote it.</param>
public sealed record SkillClientDto(string Kind, string State, string? How, bool Modified);

/// <summary>A link or copy Astra made.</summary>
public sealed record SkillPlacementDto(string Path, string Target, string How, string Origin, bool Modified, bool Missing);

/// <summary>A Skill in Astra's library.</summary>
/// <param name="SourceKind"><c>migrated</c>, <c>local</c> or <c>git</c>.</param>
/// <param name="SourceJson">Where it came from (path, or url / ref / commit); never a credential.</param>
public sealed record SkillDto(
    string Name, string Description, string SourceKind, string? SourceJson, DateTimeOffset UpdatedAt,
    List<SkillClientDto> Clients, List<SkillPlacementDto> Placements);

/// <summary>One place a found Skill was seen.</summary>
public sealed record FoundSkillLocationDto(string Path, string Target, string Hash, DateTimeOffset Modified);

/// <summary>A Skill found in a client directory that Astra does not manage yet.</summary>
/// <param name="Kind"><c>unmanaged</c> (a real directory, can be migrated) or <c>external</c> (a link to somewhere else; shown only).</param>
/// <param name="Conflict">Copies with different content under one name.</param>
/// <param name="InLibrary">The library already has a Skill of this name.</param>
/// <param name="SkillsCli">Managed by the <c>skills</c> CLI (it appears in its lock file).</param>
public sealed record FoundSkillDto(
    string Name, string Kind, List<FoundSkillLocationDto> Locations, bool Conflict, bool InLibrary, bool SkillsCli, string? LinkTarget);

/// <summary>The Skills page's data.</summary>
/// <param name="CanCreate">False off this machine: installing a Skill (a local path or a repository, and the scripts in it) is local-only.</param>
public sealed record SkillLibraryDto(
    List<SkillTargetDto> Targets, List<SkillDto> Skills, List<FoundSkillDto> Found, string LibraryDir, string SharedDir, bool CanCreate);

/// <summary>Request body of switching a client on or off for a Skill.</summary>
/// <param name="Confirm">Go ahead although the shared link also serves other clients.</param>
public sealed record SkillSwitchDto(bool On, bool Confirm = false);

/// <summary>Result of a switch. When <see cref="NeedsConfirm"/> nothing was changed and <see cref="Affected"/> lists the other clients.</summary>
public sealed record SkillSwitchResultDto(bool Done, bool NeedsConfirm, List<string> Affected);

/// <summary>Outcome for one Skill of a batch (install, migrate, return).</summary>
public sealed record SkillOutcomeDto(string Name, bool Ok, string? Reason);

/// <summary>What to migrate; <see cref="Keep"/> is the path of the copy to keep when copies differ (or <c>library</c>).</summary>
public sealed record SkillMigrateItemDto(string Name, string? Keep);

public sealed record SkillMigrateRequestDto(List<SkillMigrateItemDto> Items);

public sealed record SkillMigratePreviewRequestDto(List<string>? Names);

/// <param name="Skip">Why it is not selected by default (<c>skills-cli</c>).</param>
public sealed record SkillMigratePreviewItemDto(string Name, List<FoundSkillLocationDto> Locations, bool Conflict, bool InLibrary, string? Skip);

/// <summary>Request body of looking into a source for Skills.</summary>
/// <param name="Kind"><c>git</c> or <c>local</c>.</param>
/// <param name="Source">A repository address, or a directory on this machine.</param>
/// <param name="Ref">A branch or tag (git only); the default branch when empty.</param>
public sealed record SkillProbeRequestDto(string Kind, string Source, string? Ref);

/// <param name="Status"><c>new</c>, <c>exists</c> (same content already installed), <c>differs</c> (same name, other content; not installed) or <c>invalid</c>.</param>
/// <param name="Reason">For <c>invalid</c>: <c>name</c> or <c>size</c>.</param>
public sealed record SkillCandidateDto(string Name, string Description, string Status, string? Reason);

/// <param name="ProbeId">Hands the found Skills back to <c>install</c>; held for 15 minutes.</param>
public sealed record SkillProbeResultDto(string ProbeId, List<SkillCandidateDto> Skills);

public sealed record SkillInstallRequestDto(string ProbeId, List<string> Names, List<string> Clients);
