using System.Text.Json.Nodes;
using Astra.Core.Library;

namespace Astra.Clients.Library;

/// <summary>Whether a Git-installed Skill has a newer commit upstream.</summary>
/// <param name="Current">The commit it was installed or last updated at.</param>
/// <param name="Latest">The commit upstream is at now, or null when it could not be asked.</param>
/// <param name="Edited">The library's copy no longer hashes to what was installed: updating replaces the user's edits.</param>
public sealed record SkillUpdateInfo(string Name, string Url, string? Ref, string? Current, string? Latest, bool Behind, bool Edited, string? Error);

public sealed partial class SkillLibrary
{
    private static (string Url, string? Ref, string? Commit)? GitSource(LibrarySkillRecord skill)
    {
        if (skill.SourceKind != SkillSourceKinds.Git || skill.SourceJson is null) return null;
        try
        {
            if (JsonNode.Parse(skill.SourceJson) is not JsonObject o || o["url"] is not JsonValue u || !u.TryGetValue<string>(out var url)) return null;
            return (url, o["ref"] is JsonValue r && r.TryGetValue<string>(out var gitRef) ? gitRef : null, o["commit"] is JsonValue c && c.TryGetValue<string>(out var commit) ? commit : null);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks each Git-installed Skill's repository (<c>git ls-remote</c>, no download) whether its branch moved since the Skill was
    /// installed. A repository that cannot be reached is reported in <see cref="SkillUpdateInfo.Error"/>, not thrown.
    /// </summary>
    public async Task<IReadOnlyList<SkillUpdateInfo>> CheckUpdatesAsync(CancellationToken ct = default)
    {
        var result = new List<SkillUpdateInfo>();
        foreach (var skill in await ledger.ListSkillsAsync(ct))
        {
            if (GitSource(skill) is not { } src) continue;
            var edited = Directory.Exists(SkillDir(skill.Name)) && FsLinks.HashDirectory(SkillDir(skill.Name)) != skill.Hash;
            try
            {
                var latest = await SkillGit.RemoteCommitAsync(env, src.Url, src.Ref, ct);
                result.Add(new SkillUpdateInfo(skill.Name, src.Url, src.Ref, src.Commit, latest, latest is not null && latest != src.Commit, edited, null));
            }
            catch (SkillLibraryException e)
            {
                result.Add(new SkillUpdateInfo(skill.Name, src.Url, src.Ref, src.Commit, null, false, edited, e.Message));
            }
        }
        return result;
    }

    /// <summary>
    /// Updates a Git-installed Skill to its branch's newest commit: fetches it, replaces the library's directory (the old one is saved to the
    /// backups first) and keeps every link, which points at that directory. A Skill edited since it was installed is refused unless <paramref name="force"/>.
    /// </summary>
    public Task<SkillOutcome> UpdateAsync(string name, bool force, CancellationToken ct = default) => LockedAsync(async () =>
    {
        RequireName(name);
        var skill = await ledger.GetSkillAsync(name, ct) ?? throw new SkillLibraryException($"No Skill named '{name}'", SkillFailure.NotFound);
        var src = GitSource(skill) ?? throw new SkillLibraryException("This Skill was not installed from a Git repository", SkillFailure.Invalid);
        if (!Directory.Exists(SkillDir(name))) throw new SkillLibraryException("The Skill's directory is missing", SkillFailure.NotFound);
        if (!force && FsLinks.HashDirectory(SkillDir(name)) != skill.Hash)
            throw new SkillLibraryException("The Skill was edited here since it was installed; updating would replace the edits", SkillFailure.Conflict);

        var temp = Path.Combine(Path.GetTempPath(), "astra-skill-update", Guid.NewGuid().ToString("N"));
        try
        {
            var commit = await SkillGit.FetchAsync(env, src.Url, src.Ref, temp, ct);
            var found = (await ProbeAsync(temp, ct)).FirstOrDefault(c => c.Name == name && c.Status != "invalid")
                ?? throw new SkillLibraryException("The repository no longer has this Skill", SkillFailure.NotFound);
            if (commit == src.Commit && FsLinks.HashDirectory(found.Dir) == skill.Hash) return new SkillOutcome(name, true, "current");
            try
            {
                BackupDirectory(SkillDir(name), "library", name);
                FsLinks.DeleteTree(SkillDir(name));
                FsLinks.CopyDirectory(found.Dir, SkillDir(name));
            }
            finally
            {
                EndChange();
            }
            skill.Hash = FsLinks.HashDirectory(SkillDir(name));
            skill.Description = ReadDescription(SkillDir(name));
            skill.UpdatedAt = DateTimeOffset.UtcNow;
            skill.SourceJson = new JsonObject { ["url"] = src.Url, ["ref"] = src.Ref, ["commit"] = commit }.ToJsonString();
            await ledger.UpsertSkillAsync(skill, ct);
            foreach (var p in (await ledger.ListPlacementsAsync(name, ct)).Where(p => p.How == PlacementHows.Copy && Directory.Exists(p.Path)))
            {
                // a copy that the user had not edited follows the new version; an edited one is left for the user to settle
                if (FsLinks.HashDirectory(p.Path) != p.CopyHash) continue;
                FsLinks.DeleteTree(p.Path);
                FsLinks.CopyDirectory(SkillDir(name), p.Path);
                File.WriteAllText(Path.Combine(p.Path, FsLinks.ManagedMarker), $"{{\"hash\":\"{FsLinks.HashDirectory(SkillDir(name))}\"}}");
                p.CopyHash = FsLinks.HashDirectory(p.Path);
                await ledger.UpsertPlacementAsync(p, ct);
            }
            return new SkillOutcome(name, true, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp)) FsLinks.DeleteTree(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a temporary directory left behind is harmless
            }
        }
    }, ct);
}
