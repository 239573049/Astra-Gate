namespace Astra.Api.Contracts;

/// <summary>Where a resource goes: a client, user-wide (<see cref="Project"/> empty) or inside a project directory.</summary>
public sealed record ResourceTargetDto(string Client, string Project = "");

/// <summary>
/// The state of a resource on one target. <see cref="State"/>: <c>applied</c>, <c>drifted</c> (the user changed what Astra wrote; never overwritten without force),
/// <c>missing</c> (removed since), <c>conflict</c> (something of the user's is in the way), <c>project-missing</c> (the project directory is gone),
/// <c>unsupported</c> (<see cref="Message"/> says why), <c>pending</c> or <c>off</c>.
/// </summary>
public sealed record ResourceTargetStateDto(string Client, string Project, string State, string? Path, string? Message);

/// <summary>An instruction, sub-agent, slash command, hook or environment variable of the library, with where it stands in each target.</summary>
/// <param name="Kind"><c>instruction</c>, <c>agent</c>, <c>command</c>, <c>hook</c> or <c>env</c>.</param>
/// <param name="Body">The text, prompt, command or value. An environment variable's value is returned (it is the user's own setting, not a credential store).</param>
/// <param name="Event">A hook's event: portable (<c>turn-end</c>, <c>after-tool</c>, <c>before-tool</c>, <c>prompt</c>, <c>session-start</c>) or a client's own (<c>claude-code:PermissionRequest</c>).</param>
/// <param name="Matcher">A hook's narrowing: <c>shell</c> or <c>edit</c> for a tool event, some of <c>startup|resume|clear|compact|fork</c> for a session start.</param>
/// <param name="Model">A sub-agent's model (written into Codex's role file only).</param>
/// <param name="LastError">Why the last write into a target failed, keyed <c>client</c> or <c>client@project</c> (kept in memory; empty after a restart).</param>
public sealed record ResourceItemDto(
    string Id, string Kind, string Name, string Description, string Body, string? Event, string? Matcher, int Timeout, string? Model, bool Enabled,
    List<ResourceTargetStateDto> Targets, DateTimeOffset UpdatedAt, Dictionary<string, string>? LastError = null);

/// <param name="Detected">The client's directory exists on this machine.</param>
/// <param name="Path">The instruction file, folder or settings file Astra writes for this client.</param>
/// <param name="Project">The client can also be given this kind of resource inside a project directory.</param>
public sealed record ResourceClientDto(string Kind, bool Detected, string? Path, bool Project);

public sealed record ResourceProjectDto(string Path, string Name, bool Exists);

/// <summary>A resource page's data (<c>GET /api/library/{instructions|agents|commands|hooks|env}</c>).</summary>
/// <param name="CanWrite">False off this machine: writing client files is local-only.</param>
public sealed record ResourceLibraryDto(string Kind, List<ResourceClientDto> Clients, List<ResourceItemDto> Items, List<ResourceProjectDto> Projects, bool CanWrite);

/// <summary>Create / update body of a resource. Fields a kind does not use are ignored.</summary>
public sealed record ResourceInputDto(
    string Name, string? Description, string? Body, string? Event, string? Matcher, int? Timeout, string? Model, bool? Enabled, List<ResourceTargetDto>? Targets);

public sealed record ProjectInputDto(string Path, string? Name);

/// <summary>Switches a Skill on or off inside a project: <see cref="Dir"/> is a project Skills directory from <see cref="SkillLibraryDto.ProjectDirs"/>.</summary>
public sealed record SkillProjectSwitchDto(string Project, string Dir, bool On);

/// <summary>A directory, relative to a project, that these clients read Skills from.</summary>
public sealed record SkillProjectDirDto(string Dir, List<string> Clients);

/// <summary>Whether a Git-installed Skill has a newer commit upstream.</summary>
public sealed record SkillUpdateDto(string Name, string Url, string? Ref, string? Current, string? Latest, bool Behind, bool Edited, string? Error);

/// <summary>A Skill the market lists. <see cref="Source"/> is the <c>owner/repo</c> it comes from (the page groups by it).</summary>
public sealed record SkillMarketEntryDto(string Source, string SkillId, string Name, int Installs, bool Installed);

/// <summary>What the market has for a search; <see cref="Error"/> is set when the registry could not be reached.</summary>
public sealed record SkillMarketResultDto(List<SkillMarketEntryDto> Skills, string Registry, string? Error);

public sealed record SkillMarketInstallDto(string Source, string SkillId, List<string> Clients);

/// <summary>The registry the Skills market reads (<see cref="Default"/> is skills.sh, a third-party service; the setting lets a company point it at its own).</summary>
public sealed record SkillMarketSettingsDto(string Url, string Default);

public sealed record SkillMarketSettingsInputDto(string? Url);

/// <summary>One client as the skill router sees it. <see cref="Why"/> is <c>shared</c> or <c>own</c> when the router cannot be turned on.</summary>
public sealed record SkillRouterClientDto(string Kind, bool Supported, bool Enabled, string? Why, int Listed, string? Entry);

public sealed record SkillRouterSwitchDto(bool On);
