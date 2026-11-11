using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Clients.Config;
using Astra.Core;
using Astra.Core.Library;

namespace Astra.Clients.Library;

/// <summary>
/// The Skill library (<c>.plan/resources-library.md</c>): Skills live in <c>~/.astra/library/skills/&lt;name&gt;</c> and a client's
/// directory only holds a link to one. The ledger (<see cref="ILibraryLedger"/>) is the record of every link Astra made, and Astra
/// only ever removes what the ledger lists and is still its own: a link into the library, or a copy whose hash has not changed.
/// One change runs at a time; a failure inside one Skill is reported in its outcome and does not stop the others.
/// </summary>
public sealed partial class SkillLibrary(ClientEnvironment env, AstraPaths paths, ILibraryLedger ledger)
{
    /// <summary>A Skill larger than this is not installed.</summary>
    public const long MaxSkillBytes = 200L * 1024 * 1024;

    private const int KeepBackups = 30;

    private readonly SemaphoreSlim _gate = new(1, 1);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex NamePattern();

    public string LibraryDir => paths.LibrarySkillsDir;

    public string SharedDir => SkillPlaces.SharedDir(env);

    public string SkillDir(string name) => Path.Combine(paths.LibrarySkillsDir, name);

    /// <summary>The name is a plain directory name: no separator, no dot-only name, nothing that could leave the Skills directory.</summary>
    public static bool IsValidName(string name) => NamePattern().IsMatch(name) && !name.EndsWith('.');

    private static string RequireName(string name)
    {
        if (!IsValidName(name)) throw new SkillLibraryException($"'{name}' is not a valid Skill name", SkillFailure.Invalid);
        return name;
    }

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

    // ---------------------------------------------------------------- places

    private string DirOf(string target) =>
        target == SkillTargets.Shared ? SharedDir : SkillPlaces.For(env, target)?.SkillsDir
            ?? throw new SkillLibraryException($"'{target}' has no Skills directory", SkillFailure.Invalid);

    private string PathOf(string target, string name) => Path.Combine(DirOf(target), name);

    private bool Detected(SkillPlace place) => env.DirectoryExists(Path.GetDirectoryName(place.SkillsDir)!);

    /// <summary>The target a client's link goes to: the shared directory when the client reads it, its own otherwise.</summary>
    private string TargetFor(SkillPlace place) => place.ReadsShared ? SkillTargets.Shared : place.Kind;

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path) || FsLinks.IsLink(path);

    // ---------------------------------------------------------------- state of a placement

    private enum Held { Ours, OurCopy, ModifiedCopy, Gone, Foreign }

    private Held Inspect(LibraryPlacementRecord p)
    {
        if (!Exists(p.Path)) return Held.Gone;
        if (FsLinks.IsLink(p.Path)) return FsLinks.IsLinkTo(p.Path, SkillDir(p.SkillName)) ? Held.Ours : Held.Foreign;
        if (p.How != PlacementHows.Copy || !File.Exists(Path.Combine(p.Path, FsLinks.ManagedMarker))) return Held.Foreign;
        return FsLinks.HashDirectory(p.Path) == p.CopyHash ? Held.OurCopy : Held.ModifiedCopy;
    }

    // ---------------------------------------------------------------- state

    public Task<SkillLibraryState> StateAsync(CancellationToken ct = default) => LockedAsync(async () =>
    {
        var skills = await ledger.ListSkillsAsync(ct);
        var placements = await ledger.ListPlacementsAsync(ct);
        var kinds = SkillPlaces.SupportedKinds;
        var targets = kinds.Select(k =>
        {
            var place = SkillPlaces.For(env, k)!;
            return new SkillTargetInfo(k, true, Detected(place), place.SkillsDir, place.ReadsShared, place.AlsoReads);
        }).ToList();

        var infos = new List<SkillInfo>();
        foreach (var skill in skills)
        {
            var mine = placements.Where(p => p.SkillName == skill.Name).Select(p => (Record: p, Held: Inspect(p))).ToList();
            var clients = kinds.Select(k => ClientView(k, mine)).ToList();
            infos.Add(new SkillInfo(skill.Name, skill.Description, skill.SourceKind, skill.SourceJson, skill.UpdatedAt, clients,
                mine.Select(m => new SkillPlacementInfo(m.Record.Path, m.Record.Target, m.Record.How, m.Record.Origin,
                    m.Held == Held.ModifiedCopy, m.Held is Held.Gone)).ToList()));
        }
        return new SkillLibraryState(targets, infos, Found(skills), LibraryDir, SharedDir);
    }, ct);

    private SkillClientView ClientView(string kind, List<(LibraryPlacementRecord Record, Held Held)> mine)
    {
        var place = SkillPlaces.For(env, kind)!;
        bool Live((LibraryPlacementRecord Record, Held Held) m) => m.Held is Held.Ours or Held.OurCopy or Held.ModifiedCopy;
        if (mine.FirstOrDefault(m => m.Record.Target == kind && Live(m)) is { Record: not null } own && own.Record is { } ownRecord)
            return new SkillClientView(kind, "own", ownRecord.How, own.Held == Held.ModifiedCopy);
        if (place.ReadsShared && mine.FirstOrDefault(m => m.Record.Target == SkillTargets.Shared && Live(m)) is { Record: not null } shared)
            return new SkillClientView(kind, "via-shared", shared.Record.How, shared.Held == Held.ModifiedCopy);
        foreach (var other in place.AlsoReads)
            if (mine.FirstOrDefault(m => m.Record.Target == other && Live(m)) is { Record: not null } via)
                return new SkillClientView(kind, "via-" + other, via.Record.How, via.Held == Held.ModifiedCopy);
        return new SkillClientView(kind, "off", null, false);
    }

    // ---------------------------------------------------------------- discovery

    private IEnumerable<(string Target, string Dir)> ScanDirs()
    {
        yield return (SkillTargets.Shared, SharedDir);
        foreach (var kind in SkillPlaces.SupportedKinds)
            if (SkillPlaces.For(env, kind) is { } place && Detected(place)) yield return (kind, place.SkillsDir);
    }

    private HashSet<string> SkillsCliNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<string> { env.Combine(".agents", ".skill-lock.json") };
        if (env.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state) candidates.Add(Path.Combine(state, "skills", ".skill-lock.json"));
        foreach (var file in candidates)
        {
            try
            {
                if (env.ReadTextOrNull(file) is { } text && JsonNode.Parse(text) is JsonObject root && root["skills"] is JsonObject skills)
                    foreach (var (name, _) in skills) names.Add(name);
            }
            catch (System.Text.Json.JsonException)
            {
                // A lock file Astra cannot read just means nothing is flagged.
            }
        }
        return names;
    }

    private List<FoundSkill> Found(IReadOnlyList<LibrarySkillRecord> library)
    {
        var inLibrary = library.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var cli = SkillsCliNames();
        var unmanaged = new Dictionary<string, List<FoundLocation>>(StringComparer.Ordinal);
        var external = new Dictionary<string, (string Path, string Target, string Link)>(StringComparer.Ordinal);
        foreach (var (target, dir) in ScanDirs())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var entry in new DirectoryInfo(dir).EnumerateDirectories())
            {
                if (entry.Name.StartsWith('.')) continue;
                if (FsLinks.LinkTargetOf(entry.FullName) is { } link)
                {
                    if (!FsLinks.IsLinkTo(entry.FullName, SkillDir(entry.Name)) && File.Exists(Path.Combine(entry.FullName, "SKILL.md")))
                        external.TryAdd(entry.Name + "\0" + target, (entry.FullName, target, link));
                    continue;
                }
                if (!File.Exists(Path.Combine(entry.FullName, "SKILL.md")) || !IsValidName(entry.Name)) continue;
                if (!unmanaged.TryGetValue(entry.Name, out var list)) unmanaged[entry.Name] = list = [];
                list.Add(new FoundLocation(entry.FullName, target, FsLinks.HashDirectory(entry.FullName), entry.LastWriteTimeUtc));
            }
        }

        var found = new List<FoundSkill>();
        foreach (var (name, locations) in unmanaged.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var hashes = locations.Select(l => l.Hash).Distinct().Count();
            found.Add(new FoundSkill(name, "unmanaged", locations, Conflict: hashes > 1, inLibrary.Contains(name), cli.Contains(name), null));
        }
        foreach (var (key, e) in external.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            found.Add(new FoundSkill(key.Split('\0')[0], "external",
                [new FoundLocation(e.Path, e.Target, "", Directory.GetLastWriteTimeUtc(e.Path))], false, false, false, e.Link));
        return found;
    }

    // ---------------------------------------------------------------- switching a client on or off

    /// <summary>
    /// Switches a client on or off for a Skill. A client that reads the shared directory gets its link there (so every reader
    /// sees it); switching it off removes that link, which needs <paramref name="confirm"/> when other clients read it too.
    /// </summary>
    public Task<SkillSetResult> SetAsync(string name, string client, bool on, bool confirm, CancellationToken ct = default) =>
        LockedAsync(async () =>
        {
            RequireName(name);
            var place = SkillPlaces.For(env, client) ?? throw new SkillLibraryException($"'{client}' is not supported", SkillFailure.Invalid);
            if (await ledger.GetSkillAsync(name, ct) is null || !Directory.Exists(SkillDir(name)))
                throw new SkillLibraryException($"No Skill named '{name}'", SkillFailure.NotFound);
            return on ? await ApplyAsync(name, place, ct) : await UnapplyAsync(name, place, confirm, ct);
        }, ct);

    private async Task<SkillSetResult> ApplyAsync(string name, SkillPlace place, CancellationToken ct)
    {
        var existing = await ledger.ListPlacementsAsync(name, ct);
        var target = TargetFor(place);
        var path = PathOf(target, name);
        if (existing.FirstOrDefault(p => p.Path == path) is { } have && Inspect(have) is Held.Ours or Held.OurCopy or Held.ModifiedCopy)
            return new SkillSetResult(true, []);
        if (Exists(path))
            throw new SkillLibraryException($"{path} already exists and is not managed by Astra; move or remove it first", SkillFailure.Conflict);
        var how = FsLinks.Create(path, SkillDir(name));
        await ledger.UpsertPlacementAsync(new LibraryPlacementRecord
        {
            Path = path, SkillName = name, Target = target, How = how,
            CopyHash = how == PlacementHows.Copy ? FsLinks.HashDirectory(path) : null,
            Origin = PlacementOrigins.Applied, PlacedAt = DateTimeOffset.UtcNow,
        }, ct);
        return new SkillSetResult(true, []);
    }

    private async Task<SkillSetResult> UnapplyAsync(string name, SkillPlace place, bool confirm, CancellationToken ct)
    {
        var existing = await ledger.ListPlacementsAsync(name, ct);
        var own = existing.FirstOrDefault(p => p.Target == place.Kind);
        var shared = place.ReadsShared ? existing.FirstOrDefault(p => p.Target == SkillTargets.Shared) : null;

        if (own is null && shared is null)
        {
            if (place.AlsoReads.Any(o => existing.Any(p => p.Target == o)))
                throw new SkillLibraryException($"{place.Kind} sees this Skill through another client's directory; switch that client off instead", SkillFailure.Conflict);
            return new SkillSetResult(true, []);
        }
        if (shared is not null)
        {
            var affected = SkillPlaces.SupportedKinds
                .Where(k => k != place.Kind && SkillPlaces.For(env, k) is { ReadsShared: true } p && Detected(p)).ToList();
            if (affected.Count > 0 && !confirm) return new SkillSetResult(false, affected);
        }
        if (own is not null) await RemovePlacementAsync(own, backup: true, ct);
        if (shared is not null) await RemovePlacementAsync(shared, backup: true, ct);
        return new SkillSetResult(true, []);
    }

    /// <summary>Removes one placement from disk and the ledger, touching only what is still Astra's. A modified copy is backed up first.</summary>
    private async Task RemovePlacementAsync(LibraryPlacementRecord p, bool backup, CancellationToken ct)
    {
        switch (Inspect(p))
        {
            case Held.Ours:
                FsLinks.RemoveLink(p.Path);
                break;
            case Held.OurCopy:
                FsLinks.DeleteTree(p.Path);
                break;
            case Held.ModifiedCopy:
                if (backup) BackupDirectory(p.Path, p.Target, p.SkillName);
                FsLinks.DeleteTree(p.Path);
                break;
            case Held.Gone or Held.Foreign:
                // Gone already, or the user put something of their own there: forget the record, leave the path alone.
                break;
        }
        await ledger.DeletePlacementAsync(p.Path, ct);
    }

    // ---------------------------------------------------------------- backups

    private string? _backupRoot;

    private string NewBackupRoot()
    {
        Directory.CreateDirectory(paths.LibraryBackupsDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(paths.LibraryBackupsDir, stamp);
        for (var n = 2; Directory.Exists(dir); n++) dir = Path.Combine(paths.LibraryBackupsDir, $"{stamp}-{n}");
        Directory.CreateDirectory(dir);
        Prune();
        return dir;
    }

    private void Prune()
    {
        try
        {
            var all = new DirectoryInfo(paths.LibraryBackupsDir).EnumerateDirectories().OrderByDescending(d => d.Name, StringComparer.Ordinal).Skip(KeepBackups);
            foreach (var old in all) old.Delete(recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Pruning is housekeeping; never let it fail a change.
        }
    }

    /// <summary>Copies a directory into this change's backup folder (<c>&lt;target&gt;/&lt;name&gt;</c>). Returns the copy's path.</summary>
    private string BackupDirectory(string source, string target, string name)
    {
        _backupRoot ??= NewBackupRoot();
        var dest = Path.Combine(_backupRoot, target, name);
        for (var n = 2; Directory.Exists(dest); n++) dest = Path.Combine(_backupRoot, target, $"{name}-{n}");
        FsLinks.CopyDirectory(source, dest);
        return dest;
    }

    /// <summary>Starts a new backup folder for the next change.</summary>
    private void EndChange() => _backupRoot = null;

    // ---------------------------------------------------------------- SKILL.md

    /// <summary>The <c>description</c> of a SKILL.md's frontmatter (a plain, quoted or folded <c>&gt;</c> / <c>|</c> value), or "".</summary>
    public static string ReadDescription(string skillDir)
    {
        try
        {
            var file = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(file)) return "";
            var lines = File.ReadAllLines(file);
            if (lines.Length == 0 || lines[0].Trim() != "---") return "";
            for (var i = 1; i < lines.Length && lines[i].Trim() != "---"; i++)
            {
                if (!lines[i].StartsWith("description:", StringComparison.Ordinal)) continue;
                var value = lines[i]["description:".Length..].Trim();
                if (value is ">" or "|" or ">-" or "|-" or "")
                {
                    var parts = new List<string>();
                    for (var j = i + 1; j < lines.Length && lines[j].Trim() != "---" && (lines[j].StartsWith(' ') || lines[j].StartsWith('\t')); j++)
                        parts.Add(lines[j].Trim());
                    return string.Join(' ', parts);
                }
                if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
                return value;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable SKILL.md has no description.
        }
        return "";
    }

    private static long SizeOf(string dir) => FsLinks.Files(dir).Sum(f => new FileInfo(Path.Combine(dir, f)).Length);

    // ---------------------------------------------------------------- install

    /// <summary>
    /// Skills a source directory offers: the directory itself when it holds a SKILL.md, else each subdirectory (at any depth) that does.
    /// The status is <c>new</c>, <c>exists</c> (same content already in the library) or <c>differs</c> (same name, other content; skipped).
    /// </summary>
    public async Task<IReadOnlyList<SkillCandidate>> ProbeAsync(string sourceDir, CancellationToken ct = default)
    {
        if (!Directory.Exists(sourceDir)) throw new SkillLibraryException("The source is not a directory", SkillFailure.Invalid);
        var dirs = new List<string>();
        if (File.Exists(Path.Combine(sourceDir, "SKILL.md"))) dirs.Add(sourceDir);
        else
        {
            foreach (var file in Directory.EnumerateFiles(sourceDir, "SKILL.md", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                var dir = Path.GetDirectoryName(file)!;
                if (dir.Split(Path.DirectorySeparatorChar).Contains(".git")) continue;
                dirs.Add(dir);
            }
        }
        var result = new List<SkillCandidate>();
        foreach (var dir in dirs.OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar));
            if (!IsValidName(name)) { result.Add(new SkillCandidate(name, "", dir, "invalid", "name")); continue; }
            var description = ReadDescription(dir);
            if (SizeOf(dir) > MaxSkillBytes) { result.Add(new SkillCandidate(name, description, dir, "invalid", "size")); continue; }
            var status = "new";
            if (await ledger.GetSkillAsync(name, ct) is { } have)
                status = Directory.Exists(SkillDir(name)) && FsLinks.HashDirectory(SkillDir(name)) == FsLinks.HashDirectory(dir) ? "exists" : "differs";
            result.Add(new SkillCandidate(name, description, dir, status, null));
        }
        return result;
    }

    /// <summary>Copies the chosen Skills of <paramref name="candidates"/> into the library and switches them on for <paramref name="clients"/>.</summary>
    public Task<IReadOnlyList<SkillOutcome>> InstallAsync(IReadOnlyList<SkillCandidate> candidates, string sourceKind, string? sourceJson,
        IReadOnlyList<string> clients, CancellationToken ct = default) =>
        LockedAsync<IReadOnlyList<SkillOutcome>>(async () =>
        {
            var outcomes = new List<SkillOutcome>();
            foreach (var c in candidates)
            {
                try
                {
                    RequireName(c.Name);
                    if (c.Status is "invalid") { outcomes.Add(new SkillOutcome(c.Name, false, c.Reason)); continue; }
                    if (c.Status == "differs") { outcomes.Add(new SkillOutcome(c.Name, false, "differs")); continue; }
                    if (c.Status == "new")
                    {
                        if (Directory.Exists(SkillDir(c.Name)) || Exists(SkillDir(c.Name)))
                            throw new SkillLibraryException($"{SkillDir(c.Name)} already exists", SkillFailure.Conflict);
                        FsLinks.CopyDirectory(c.Dir, SkillDir(c.Name));
                        var now = DateTimeOffset.UtcNow;
                        await ledger.UpsertSkillAsync(new LibrarySkillRecord
                        {
                            Name = c.Name, Description = ReadDescription(SkillDir(c.Name)), SourceKind = sourceKind, SourceJson = sourceJson,
                            Hash = FsLinks.HashDirectory(SkillDir(c.Name)), CreatedAt = now, UpdatedAt = now,
                        }, ct);
                    }
                    foreach (var client in clients)
                    {
                        var place = SkillPlaces.For(env, client);
                        if (place is not null) await ApplyAsync(c.Name, place, ct);
                    }
                    outcomes.Add(new SkillOutcome(c.Name, true, c.Status == "exists" ? "exists" : null));
                }
                catch (Exception e) when (e is SkillLibraryException or IOException or UnauthorizedAccessException)
                {
                    outcomes.Add(new SkillOutcome(c.Name, false, e.Message));
                }
            }
            return outcomes;
        }, ct);

    // ---------------------------------------------------------------- migrate

    public async Task<IReadOnlyList<MigratePreviewItem>> MigratePreviewAsync(IReadOnlyCollection<string>? names, CancellationToken ct = default)
    {
        var library = await ledger.ListSkillsAsync(ct);
        return Found(library).Where(f => f.Kind == "unmanaged" && (names is null || names.Contains(f.Name)))
            .Select(f => new MigratePreviewItem(f.Name, f.Locations, f.Conflict && !f.InLibrary, f.InLibrary,
                f.SkillsCli ? "skills-cli" : null)).ToList();
    }

    /// <summary>
    /// Moves each named Skill into the library and links every place it was found back to it. Copies with different content need
    /// <c>Keep</c> (the path of the one to keep, or "library" for the library's own); the others are backed up first.
    /// </summary>
    public Task<IReadOnlyList<SkillOutcome>> MigrateAsync(IReadOnlyList<SkillMigrateChoice> choices, CancellationToken ct = default) =>
        LockedAsync<IReadOnlyList<SkillOutcome>>(async () =>
        {
            var outcomes = new List<SkillOutcome>();
            try
            {
                var library = await ledger.ListSkillsAsync(ct);
                var found = Found(library).Where(f => f.Kind == "unmanaged").ToDictionary(f => f.Name, StringComparer.Ordinal);
                foreach (var choice in choices)
                {
                    try
                    {
                        RequireName(choice.Name);
                        if (!found.TryGetValue(choice.Name, out var f)) throw new SkillLibraryException("Nothing to migrate", SkillFailure.NotFound);
                        await MigrateOneAsync(f, choice.Keep, library.FirstOrDefault(s => s.Name == choice.Name), ct);
                        outcomes.Add(new SkillOutcome(choice.Name, true, null));
                    }
                    catch (Exception e) when (e is SkillLibraryException or IOException or UnauthorizedAccessException)
                    {
                        outcomes.Add(new SkillOutcome(choice.Name, false, e.Message));
                    }
                }
            }
            finally
            {
                EndChange();
            }
            return outcomes;
        }, ct);

    private async Task MigrateOneAsync(FoundSkill f, string? keep, LibrarySkillRecord? existing, CancellationToken ct)
    {
        var libraryDir = SkillDir(f.Name);
        string? chosen = null;
        if (existing is not null)
        {
            if (keep is not null && keep != "library" && f.Locations.Any(l => l.Path == keep))
                throw new SkillLibraryException("A Skill with this name is already in the library; keep the library's version", SkillFailure.Conflict);
            var libraryHash = FsLinks.HashDirectory(libraryDir);
            if (keep != "library" && f.Locations.Any(l => l.Hash != libraryHash))
                throw new SkillLibraryException("The content differs from the library's; choose which version to keep", SkillFailure.Conflict);
        }
        else
        {
            if (f.Conflict)
            {
                chosen = f.Locations.FirstOrDefault(l => l.Path == keep)?.Path
                    ?? throw new SkillLibraryException("The copies differ; choose which one to keep", SkillFailure.Conflict);
            }
            else chosen = f.Locations[0].Path;
            if (SizeOf(chosen) > MaxSkillBytes) throw new SkillLibraryException("The Skill is larger than 200 MB", SkillFailure.Invalid);
        }

        foreach (var loc in f.Locations) BackupDirectory(loc.Path, loc.Target, f.Name);

        if (chosen is not null)
        {
            Directory.CreateDirectory(paths.LibrarySkillsDir);
            FsLinks.CopyDirectory(chosen, libraryDir);
            if (FsLinks.HashDirectory(libraryDir) != FsLinks.HashDirectory(chosen))
            {
                FsLinks.DeleteTree(libraryDir);
                throw new SkillLibraryException("Copying into the library did not verify; nothing was changed", SkillFailure.Invalid);
            }
            var now = DateTimeOffset.UtcNow;
            await ledger.UpsertSkillAsync(new LibrarySkillRecord
            {
                Name = f.Name, Description = ReadDescription(libraryDir), SourceKind = SkillSourceKinds.Migrated,
                SourceJson = new JsonObject { ["from"] = chosen }.ToJsonString(), Hash = FsLinks.HashDirectory(libraryDir), CreatedAt = now, UpdatedAt = now,
            }, ct);
        }

        foreach (var loc in f.Locations)
        {
            FsLinks.DeleteTree(loc.Path);
            var how = FsLinks.Create(loc.Path, libraryDir);
            await ledger.UpsertPlacementAsync(new LibraryPlacementRecord
            {
                Path = loc.Path, SkillName = f.Name, Target = loc.Target, How = how,
                CopyHash = how == PlacementHows.Copy ? FsLinks.HashDirectory(loc.Path) : null,
                Origin = PlacementOrigins.Migrated, PlacedAt = DateTimeOffset.UtcNow,
            }, ct);
        }
    }

    // ---------------------------------------------------------------- return, delete, copies

    /// <summary>
    /// Gives a Skill back: each link Astra made becomes a real directory with the same content, and the record is dropped. A copy only
    /// loses its marker. <paramref name="name"/> null = every Skill.
    /// </summary>
    public Task<IReadOnlyList<SkillOutcome>> ReturnAsync(string? name, CancellationToken ct = default) =>
        LockedAsync<IReadOnlyList<SkillOutcome>>(async () =>
        {
            var outcomes = new List<SkillOutcome>();
            var skills = (await ledger.ListSkillsAsync(ct)).Where(s => name is null || s.Name == name).ToList();
            if (name is not null && skills.Count == 0) throw new SkillLibraryException($"No Skill named '{name}'", SkillFailure.NotFound);
            foreach (var skill in skills)
            {
                try
                {
                    foreach (var p in await ledger.ListPlacementsAsync(skill.Name, ct)) await GiveBackAsync(p, ct);
                    outcomes.Add(new SkillOutcome(skill.Name, true, null));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    outcomes.Add(new SkillOutcome(skill.Name, false, e.Message));
                }
            }
            return outcomes;
        }, ct);

    private async Task GiveBackAsync(LibraryPlacementRecord p, CancellationToken ct)
    {
        switch (Inspect(p))
        {
            case Held.Ours:
                var temp = p.Path + ".astra-return";
                FsLinks.DeleteTree(temp);
                FsLinks.CopyDirectory(SkillDir(p.SkillName), temp);
                FsLinks.RemoveLink(p.Path);
                Directory.Move(temp, p.Path);
                break;
            case Held.OurCopy or Held.ModifiedCopy:
                File.Delete(Path.Combine(p.Path, FsLinks.ManagedMarker));
                break;
        }
        await ledger.DeletePlacementAsync(p.Path, ct);
    }

    /// <summary>Removes a Skill: every placement that is still Astra's, then the library directory (saved to the backups first).</summary>
    public Task DeleteAsync(string name, CancellationToken ct = default) => LockedAsync<bool>(async () =>
    {
        RequireName(name);
        if (await ledger.GetSkillAsync(name, ct) is null) throw new SkillLibraryException($"No Skill named '{name}'", SkillFailure.NotFound);
        try
        {
            foreach (var p in await ledger.ListPlacementsAsync(name, ct)) await RemovePlacementAsync(p, backup: true, ct);
            if (Directory.Exists(SkillDir(name)))
            {
                BackupDirectory(SkillDir(name), "library", name);
                FsLinks.DeleteTree(SkillDir(name));
            }
            await ledger.DeleteSkillAsync(name, ct);
        }
        finally
        {
            EndChange();
        }
        return true;
    }, ct);

    /// <summary>A copy that was edited: make its content the library's version (the old one is backed up) and refresh the copy's record.</summary>
    public Task TakeEditsAsync(string name, CancellationToken ct = default) => LockedAsync<bool>(async () =>
    {
        var (skill, copy) = await ModifiedCopyAsync(name, ct);
        try
        {
            BackupDirectory(SkillDir(name), "library", name);
            FsLinks.DeleteTree(SkillDir(name));
            FsLinks.CopyDirectory(copy.Path, SkillDir(name));
        }
        finally
        {
            EndChange();
        }
        skill.Hash = FsLinks.HashDirectory(SkillDir(name));
        skill.Description = ReadDescription(SkillDir(name));
        skill.UpdatedAt = DateTimeOffset.UtcNow;
        await ledger.UpsertSkillAsync(skill, ct);
        foreach (var p in (await ledger.ListPlacementsAsync(name, ct)).Where(p => p.How == PlacementHows.Copy))
        {
            p.CopyHash = FsLinks.HashDirectory(p.Path);
            await ledger.UpsertPlacementAsync(p, ct);
        }
        return true;
    }, ct);

    /// <summary>A copy that was edited: replace it with the library's version (the edited one is backed up).</summary>
    public Task OverwriteCopyAsync(string name, CancellationToken ct = default) => LockedAsync<bool>(async () =>
    {
        var (_, copy) = await ModifiedCopyAsync(name, ct);
        try
        {
            BackupDirectory(copy.Path, copy.Target, name);
        }
        finally
        {
            EndChange();
        }
        FsLinks.DeleteTree(copy.Path);
        FsLinks.CopyDirectory(SkillDir(name), copy.Path);
        File.WriteAllText(Path.Combine(copy.Path, FsLinks.ManagedMarker), $"{{\"hash\":\"{FsLinks.HashDirectory(SkillDir(name))}\"}}");
        copy.CopyHash = FsLinks.HashDirectory(copy.Path);
        await ledger.UpsertPlacementAsync(copy, ct);
        return true;
    }, ct);

    private async Task<(LibrarySkillRecord Skill, LibraryPlacementRecord Copy)> ModifiedCopyAsync(string name, CancellationToken ct)
    {
        RequireName(name);
        var skill = await ledger.GetSkillAsync(name, ct) ?? throw new SkillLibraryException($"No Skill named '{name}'", SkillFailure.NotFound);
        var copy = (await ledger.ListPlacementsAsync(name, ct)).FirstOrDefault(p => Inspect(p) == Held.ModifiedCopy)
            ?? throw new SkillLibraryException("No edited copy of this Skill", SkillFailure.NotFound);
        return (skill, copy);
    }
}
