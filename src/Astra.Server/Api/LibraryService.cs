using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Clients.Library;
using Astra.Core;
using Astra.Core.Library;
using Astra.Data;

namespace Astra.Server.Api;

/// <summary>The admin side of the Skill library (<c>.plan/resources-library.md</c>): maps the engine's results to DTOs and holds the short-lived probes of an install.</summary>
public sealed class LibraryService(AstraDatabase db, ClientEnvironment env, AstraPaths paths)
{
    private static readonly TimeSpan ProbeLifetime = TimeSpan.FromMinutes(15);

    private sealed record Probe(string Dir, bool Temporary, string SourceKind, string? SourceJson, List<SkillCandidate> Skills, DateTimeOffset Created);

    private readonly SkillLibrary _skills = new(env, paths, db.Library);
    private readonly ConcurrentDictionary<string, Probe> _probes = new();

    public async Task<SkillLibraryDto> StateAsync(bool canCreate, CancellationToken ct)
    {
        var s = await _skills.StateAsync(ct);
        return new SkillLibraryDto(
            s.Targets.Select(t => new SkillTargetDto(t.Kind, t.Supported, t.Detected, t.Dir, t.ReadsShared, t.AlsoReads.ToList())).ToList(),
            s.Skills.Select(k => new SkillDto(k.Name, k.Description, k.SourceKind, k.SourceJson, k.UpdatedAt,
                k.Clients.Select(c => new SkillClientDto(c.Kind, c.State, c.How, c.Modified)).ToList(),
                k.Placements.Select(p => new SkillPlacementDto(p.Path, p.Target, p.How, p.Origin, p.Modified, p.Missing)).ToList())).ToList(),
            s.Found.Select(Map).ToList(), s.LibraryDir, s.SharedDir, canCreate);
    }

    private static FoundSkillDto Map(FoundSkill f) =>
        new(f.Name, f.Kind, f.Locations.Select(Map).ToList(), f.Conflict, f.InLibrary, f.SkillsCli, f.LinkTarget);

    private static FoundSkillLocationDto Map(FoundLocation l) => new(l.Path, l.Target, l.Hash, l.Modified);

    private static List<SkillOutcomeDto> Map(IReadOnlyList<SkillOutcome> o) => o.Select(x => new SkillOutcomeDto(x.Name, x.Ok, x.Reason)).ToList();

    private static async Task<T> GuardAsync<T>(Func<Task<T>> run)
    {
        try
        {
            return await run();
        }
        catch (SkillLibraryException e)
        {
            throw new AdminApiException(e.Failure switch { SkillFailure.NotFound => 404, SkillFailure.Conflict => 409, _ => 400 }, e.Message);
        }
    }

    public Task<SkillSwitchResultDto> SwitchAsync(string name, string client, SkillSwitchDto body, CancellationToken ct) => GuardAsync(async () =>
    {
        var r = await _skills.SetAsync(name, client, body.On, body.Confirm, ct);
        return new SkillSwitchResultDto(r.Done, r.NeedsConfirm, r.Affected.ToList());
    });

    public Task<List<SkillMigratePreviewItemDto>> MigratePreviewAsync(List<string>? names, CancellationToken ct) => GuardAsync(async () =>
        (await _skills.MigratePreviewAsync(names, ct)).Select(i => new SkillMigratePreviewItemDto(i.Name, i.Locations.Select(Map).ToList(), i.Conflict, i.InLibrary, i.Skip)).ToList());

    public Task<List<SkillOutcomeDto>> MigrateAsync(SkillMigrateRequestDto body, CancellationToken ct) => GuardAsync(async () =>
        Map(await _skills.MigrateAsync(body.Items.Select(i => new SkillMigrateChoice(i.Name, i.Keep)).ToList(), ct)));

    public Task<List<SkillOutcomeDto>> ReturnAsync(string? name, CancellationToken ct) => GuardAsync(async () => Map(await _skills.ReturnAsync(name, ct)));

    public Task DeleteAsync(string name, CancellationToken ct) => GuardAsync(async () => { await _skills.DeleteAsync(name, ct); return true; });

    public Task TakeEditsAsync(string name, CancellationToken ct) => GuardAsync(async () => { await _skills.TakeEditsAsync(name, ct); return true; });

    public Task OverwriteCopyAsync(string name, CancellationToken ct) => GuardAsync(async () => { await _skills.OverwriteCopyAsync(name, ct); return true; });

    // ---------------------------------------------------------------- install

    public Task<SkillProbeResultDto> ProbeAsync(SkillProbeRequestDto body, CancellationToken ct) => GuardAsync(async () =>
    {
        Expire();
        var id = Guid.NewGuid().ToString("N");
        string dir;
        bool temporary;
        string? sourceJson;
        if (body.Kind == "git")
        {
            dir = Path.Combine(Path.GetTempPath(), "astra-skill-probe", id);
            temporary = true;
            try
            {
                var commit = await SkillGit.FetchAsync(env, body.Source, body.Ref, dir, ct);
                sourceJson = new JsonObject { ["url"] = body.Source.Trim(), ["ref"] = string.IsNullOrEmpty(body.Ref) ? null : body.Ref, ["commit"] = commit }.ToJsonString();
            }
            catch
            {
                Discard(dir);
                throw;
            }
        }
        else if (body.Kind == "local")
        {
            if (!Path.IsPathRooted(body.Source)) throw new AdminApiException(400, "Give the directory's full path");
            dir = Path.GetFullPath(body.Source);
            temporary = false;
            sourceJson = new JsonObject { ["path"] = dir }.ToJsonString();
        }
        else throw new AdminApiException(400, "The source kind must be git or local");

        List<SkillCandidate> found;
        try
        {
            found = (await _skills.ProbeAsync(dir, ct)).ToList();
        }
        catch
        {
            if (temporary) Discard(dir);
            throw;
        }
        if (found.Count == 0)
        {
            if (temporary) Discard(dir);
            throw new AdminApiException(404, "No Skill (a directory with a SKILL.md) was found there");
        }
        _probes[id] = new Probe(dir, temporary, body.Kind == "git" ? SkillSourceKinds.Git : SkillSourceKinds.Local, sourceJson, found, DateTimeOffset.UtcNow);
        return new SkillProbeResultDto(id, found.Select(c => new SkillCandidateDto(c.Name, c.Description, c.Status, c.Reason)).ToList());
    });

    public Task<List<SkillOutcomeDto>> InstallAsync(SkillInstallRequestDto body, CancellationToken ct) => GuardAsync(async () =>
    {
        Expire();
        if (!_probes.TryGetValue(body.ProbeId, out var probe)) throw new AdminApiException(404, "The search expired; look into the source again");
        var wanted = body.Names.ToHashSet(StringComparer.Ordinal);
        var chosen = probe.Skills.Where(s => wanted.Contains(s.Name)).ToList();
        var outcome = await _skills.InstallAsync(chosen, probe.SourceKind, probe.SourceJson, body.Clients, ct);
        if (_probes.TryRemove(body.ProbeId, out var done) && done.Temporary) Discard(done.Dir);
        return Map(outcome);
    });

    private void Expire()
    {
        foreach (var (id, p) in _probes.ToList())
        {
            if (DateTimeOffset.UtcNow - p.Created < ProbeLifetime) continue;
            if (_probes.TryRemove(id, out var gone) && gone.Temporary) Discard(gone.Dir);
        }
    }

    private static void Discard(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A temporary directory left behind is harmless.
        }
    }
}
