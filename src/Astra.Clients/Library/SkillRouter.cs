using System.Text;
using Astra.Clients.Config;
using Astra.Core;
using Astra.Core.Library;

namespace Astra.Clients.Library;

/// <summary>One client as the router sees it. <see cref="Why"/>: <c>shared</c> (it reads the shared directory other clients read too), <c>unsupported</c>, <c>own</c> (a Skill named astra-skills of the user's is there), or null.</summary>
public sealed record SkillRouterClient(string Kind, bool Supported, bool Enabled, string? Why, int Listed, string? Entry);

/// <summary>
/// The skill router (opt-in per client; the idea is magpie's <c>library/skill_router.go</c>, MIT): instead of linking every Skill into a
/// client, the client gets one Skill, <c>astra-skills</c>, whose SKILL.md lists the library's Skills with their descriptions and the path of each
/// SKILL.md; the client opens one only when a task fits it, so its prompt carries one description however many Skills there are.
/// Astra's variant is additive: the router lists the library's Skills the client has <i>no link for</i> (so the user keeps a Skill they type as
/// <c>/name</c> by leaving it linked, and routes the rest by switching their links off). The entry is a link into
/// <c>~/.astra/library/router/&lt;client&gt;/</c>, which holds the generated SKILL.md and, under the hidden <c>.skills/</c>, a link to each listed
/// Skill. Ledger rows (<see cref="WriteModes.Router"/>) record the entry; Astra removes only a link that still points at its router directory.
/// A client that reads the shared <c>~/.agents/skills</c> is refused (the entry would appear for every reader); a Skill whose SKILL.md sets
/// <c>disable-model-invocation</c> is never listed. Claude Code's Read permission rules for the listed files are not written (deferred).
/// </summary>
public sealed class SkillRouter(ClientEnvironment env, AstraPaths paths, ILibraryLedger skills, ILibraryResourceLedger resources)
{
    public const string EntryName = "astra-skills";
    private const int DescriptionMax = 1024;   // Codex refuses a longer one

    private readonly SemaphoreSlim _gate = new(1, 1);

    private string RouterDir(string client) => Path.Combine(paths.LibraryDir, "router", client);

    private static SkillPlace? PlaceOf(ClientEnvironment e, string client) => SkillPlaces.For(e, client);

    private async Task<T> LockedAsync<T>(Func<Task<T>> run, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await run();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<IReadOnlyList<SkillRouterClient>> StateAsync(CancellationToken ct = default) => LockedAsync<IReadOnlyList<SkillRouterClient>>(async () =>
    {
        var enabled = (await resources.ListRouterClientsAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var result = new List<SkillRouterClient>();
        foreach (var kind in SkillPlaces.SupportedKinds)
        {
            var place = SkillPlaces.For(env, kind)!;
            string? why = place.ReadsShared ? "shared" : null;
            var entry = Path.Combine(place.SkillsDir, EntryName);
            var on = enabled.Contains(kind);
            if (why is null && !on && Exists(entry)) why = "own";
            var listed = on ? (await Listed(kind, ct)).Count : 0;
            result.Add(new SkillRouterClient(kind, why is null || on, on, why, listed, on ? entry : null));
        }
        return result;
    }, ct);

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path) || FsLinks.IsLink(path);

    /// <summary>The Skills the router lists for a client: in the library, not linked for the client (own, shared or through another client), not manual-only.</summary>
    private async Task<List<LibrarySkillRecord>> Listed(string client, CancellationToken ct)
    {
        var place = SkillPlaces.For(env, client)!;
        var placements = await skills.ListPlacementsAsync(ct);
        bool Linked(string name) => placements.Any(p => p.SkillName == name && p.Project.Length == 0
            && (p.Target == client || (place.ReadsShared && p.Target == SkillTargets.Shared) || place.AlsoReads.Contains(p.Target)) && (FsLinks.IsLink(p.Path) || Directory.Exists(p.Path)));
        var list = new List<LibrarySkillRecord>();
        foreach (var skill in await skills.ListSkillsAsync(ct))
        {
            var dir = Path.Combine(paths.LibrarySkillsDir, skill.Name);
            if (!Directory.Exists(dir) || Linked(skill.Name) || ManualOnly(dir)) continue;
            list.Add(skill);
        }
        return list;
    }

    private static bool ManualOnly(string skillDir)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(skillDir, "SKILL.md"));
            if (!text.StartsWith("---", StringComparison.Ordinal)) return false;
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            var front = end < 0 ? text : text[..end];
            return front.Split('\n').Any(l => l.Trim().Replace(" ", "", StringComparison.Ordinal).Equals("disable-model-invocation:true", StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Turns the router on for a client: writes its directory and the entry link. A refusal says why in the exception.</summary>
    public Task EnableAsync(string client, CancellationToken ct = default) => LockedAsync<bool>(async () =>
    {
        var place = PlaceOf(env, client) ?? throw new SkillLibraryException($"'{client}' is not supported", SkillFailure.Invalid);
        if (place.ReadsShared)
            throw new SkillLibraryException($"{client} reads the shared Skills directory other clients read too; the router would appear for all of them", SkillFailure.Invalid);
        var entry = Path.Combine(place.SkillsDir, EntryName);
        var writes = (await resources.ListWritesAsync(null, ct)).Where(w => w.Mode == WriteModes.Router && w.Client == client).ToList();
        if (writes.Count == 0 && Exists(entry))
            throw new SkillLibraryException($"{entry} already exists and is not Astra's; move or remove it first", SkillFailure.Conflict);
        await WriteRouterAsync(client, place, ct);
        if (writes.Count == 0)
        {
            var how = FsLinks.Create(entry, RouterDir(client));
            if (how == PlacementHows.Copy)
            {
                FsLinks.DeleteTree(entry);
                throw new SkillLibraryException("This volume cannot hold links, so the router cannot be placed", SkillFailure.Invalid);
            }
            await resources.UpsertWriteAsync(new LibraryWriteRecord
            {
                Id = Ulid.NewUlid(), ItemId = null, Client = client, Path = entry, Mode = WriteModes.Router, Locator = EntryName, ContentHash = how, WrittenAt = DateTimeOffset.UtcNow,
            }, ct);
        }
        await resources.SetRouterClientAsync(client, true, ct);
        return true;
    }, ct);

    /// <summary>Rewrites the router's SKILL.md and links for every client it is on for (after a Skill was installed, removed, updated or switched).</summary>
    public Task RefreshAsync(CancellationToken ct = default) => LockedAsync<bool>(async () =>
    {
        foreach (var client in await resources.ListRouterClientsAsync(ct))
            if (PlaceOf(env, client) is { } place) await WriteRouterAsync(client, place, ct);
        return true;
    }, ct);

    /// <summary>Turns the router off: removes the entry link (only if it still points at Astra's router directory) and the router's directory.</summary>
    public Task DisableAsync(string client, CancellationToken ct = default) => LockedAsync<bool>(async () =>
    {
        await RemoveAsync(client, ct);
        await resources.SetRouterClientAsync(client, false, ct);
        return true;
    }, ct);

    /// <summary>The offline <c>restore-all</c> step: takes every router out. Returns the entries it could not remove.</summary>
    public Task<IReadOnlyList<string>> RemoveAllAsync(CancellationToken ct = default) => LockedAsync<IReadOnlyList<string>>(async () =>
    {
        var left = new List<string>();
        foreach (var w in (await resources.ListWritesAsync(null, ct)).Where(w => w.Mode == WriteModes.Router).ToList())
        {
            try
            {
                await RemoveAsync(w.Client, ct);
                await resources.SetRouterClientAsync(w.Client, false, ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                left.Add($"{w.Path}: {e.Message}");
            }
        }
        return left;
    }, ct);

    private async Task RemoveAsync(string client, CancellationToken ct)
    {
        foreach (var w in (await resources.ListWritesAsync(null, ct)).Where(w => w.Mode == WriteModes.Router && w.Client == client).ToList())
        {
            // only a link that still leads to Astra's router directory is Astra's; anything else is the user's now
            if (FsLinks.IsLink(w.Path) && FsLinks.IsLinkTo(w.Path, RouterDir(client))) FsLinks.RemoveLink(w.Path);
            await resources.DeleteWriteAsync(w.Id, ct);
        }
        var dir = RouterDir(client);
        if (Directory.Exists(dir)) FsLinks.DeleteTree(dir);
    }

    private async Task WriteRouterAsync(string client, SkillPlace place, CancellationToken ct)
    {
        var dir = RouterDir(client);
        var listed = await Listed(client, ct);
        var links = Path.Combine(dir, ".skills");
        if (Directory.Exists(links)) FsLinks.DeleteTree(links);
        Directory.CreateDirectory(links);
        foreach (var skill in listed) FsLinks.Create(Path.Combine(links, skill.Name), Path.Combine(paths.LibrarySkillsDir, skill.Name));
        var inClient = Path.Combine(place.SkillsDir, EntryName, ".skills");
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), Render(listed, inClient));
    }

    /// <summary>The router's SKILL.md: a description (at most 1024 bytes) naming the Skills, and a body with each Skill's description and SKILL.md path.</summary>
    public static string Render(IReadOnlyList<LibrarySkillRecord> listed, string skillsPath)
    {
        var sb = new StringBuilder();
        string Path(LibrarySkillRecord s) => System.IO.Path.Combine(skillsPath, s.Name, "SKILL.md");
        var intro = "An index of more Skills. When a task fits one of them, read its SKILL.md at the path given and follow it: ";
        var desc = new StringBuilder(intro);
        foreach (var s in listed)
        {
            var one = s.Name + (s.Description.Length > 0 ? " (" + Squash(s.Description) + ")" : "") + "; ";
            if (Encoding.UTF8.GetByteCount(desc.ToString()) + Encoding.UTF8.GetByteCount(one) > DescriptionMax - 3) break;
            desc.Append(one);
        }
        var text = desc.ToString().TrimEnd(' ', ';');
        if (listed.Count == 0) text = "An index of more Skills (none right now).";
        text = CutBytes(text, DescriptionMax);
        sb.Append("---\nname: ").Append(EntryName).Append("\ndescription: ").Append(ResourceText.Quote(text)).Append("\n---\n\n");
        sb.Append("# Skills index\n\nThese Skills are not loaded into the prompt. When a task fits one, read its SKILL.md at the path given, then follow it; a file it names is in the same folder.\n\n");
        foreach (var s in listed)
        {
            sb.Append("## ").Append(s.Name).Append("\n\n");
            if (s.Description.Length > 0) sb.Append(Squash(s.Description)).Append("\n\n");
            sb.Append("Read: `").Append(Path(s)).Append("`\n\n");
        }
        return sb.ToString();
    }

    private static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Replace("\"", "'", StringComparison.Ordinal);

    private static string CutBytes(string s, int max)
    {
        if (Encoding.UTF8.GetByteCount(s) <= max) return s;
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (bytes + n > max) break;
            sb.Append(rune.ToString());
            bytes += n;
        }
        return sb.ToString();
    }
}
