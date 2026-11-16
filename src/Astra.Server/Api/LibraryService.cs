using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Clients.Library;
using Astra.Core;
using Astra.Core.Library;
using Astra.Data;

namespace Astra.Server.Api;

/// <summary>The admin side of the Skill library (<c>.plan/resources-library.md</c>): maps the engine's results to DTOs and holds the short-lived probes of an install.</summary>
public sealed class LibraryService(AstraDatabase db, ClientEnvironment env, AstraPaths paths, IHttpClientFactory http)
{
    private const string MarketKey = "library_market";
    private static readonly TimeSpan ProbeLifetime = TimeSpan.FromMinutes(15);

    private sealed record Probe(string Dir, bool Temporary, string SourceKind, string? SourceJson, List<SkillCandidate> Skills, DateTimeOffset Created);

    private readonly SkillLibrary _skills = new(env, paths, db.Library);
    private readonly SkillRouter _router = new(env, paths, db.Library, db.LibraryResources);
    private readonly ConcurrentDictionary<string, Probe> _probes = new();

    public async Task<SkillLibraryDto> StateAsync(bool canCreate, CancellationToken ct)
    {
        var s = await _skills.StateAsync(ct);
        return new SkillLibraryDto(
            s.Targets.Select(t => new SkillTargetDto(t.Kind, t.Supported, t.Detected, t.Dir, t.ReadsShared, t.AlsoReads.ToList())).ToList(),
            s.Skills.Select(k => new SkillDto(k.Name, k.Description, k.SourceKind, k.SourceJson, k.UpdatedAt,
                k.Clients.Select(c => new SkillClientDto(c.Kind, c.State, c.How, c.Modified)).ToList(),
                k.Placements.Select(p => new SkillPlacementDto(p.Path, p.Target, p.How, p.Origin, p.Modified, p.Missing, p.Project, p.ProjectMissing)).ToList())).ToList(),
            s.Found.Select(Map).ToList(), s.LibraryDir, s.SharedDir, canCreate, s.ProjectDirs.Select(d => new SkillProjectDirDto(d.Dir, d.Clients.ToList())).ToList());
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
        await _router.RefreshAsync(ct);
        return new SkillSwitchResultDto(r.Done, r.NeedsConfirm, r.Affected.ToList());
    });

    public Task<SkillSwitchResultDto> SwitchProjectAsync(string name, SkillProjectSwitchDto body, CancellationToken ct) => GuardAsync(async () =>
    {
        var r = await _skills.SetProjectAsync(name, body.Project, body.Dir, body.On, ct);
        return new SkillSwitchResultDto(r.Done, r.NeedsConfirm, r.Affected.ToList());
    });

    // ---------------------------------------------------------------- updates

    public Task<List<SkillUpdateDto>> CheckUpdatesAsync(CancellationToken ct) => GuardAsync(async () =>
        (await _skills.CheckUpdatesAsync(ct)).Select(u => new SkillUpdateDto(u.Name, u.Url, u.Ref, u.Current, u.Latest, u.Behind, u.Edited, u.Error)).ToList());

    public Task<SkillOutcomeDto> UpdateSkillAsync(string name, bool force, CancellationToken ct) => GuardAsync(async () =>
    {
        var o = await _skills.UpdateAsync(name, force, ct);
        await _router.RefreshAsync(ct);
        return new SkillOutcomeDto(o.Name, o.Ok, o.Reason);
    });

    // ---------------------------------------------------------------- router

    public Task<List<SkillRouterClientDto>> RouterAsync(CancellationToken ct) => GuardAsync(async () =>
        (await _router.StateAsync(ct)).Select(c => new SkillRouterClientDto(c.Kind, c.Supported, c.Enabled, c.Why, c.Listed, c.Entry)).ToList());

    public Task<List<SkillRouterClientDto>> SetRouterAsync(string client, bool on, CancellationToken ct) => GuardAsync(async () =>
    {
        if (on) await _router.EnableAsync(client, ct);
        else await _router.DisableAsync(client, ct);
        return (await _router.StateAsync(ct)).Select(c => new SkillRouterClientDto(c.Kind, c.Supported, c.Enabled, c.Why, c.Listed, c.Entry)).ToList();
    });

    // ---------------------------------------------------------------- market

    private async Task<string> RegistryAsync(CancellationToken ct) =>
        (await db.Settings.GetAsync<SkillMarketSettingsInputDto>(MarketKey, ct))?.Url is { Length: > 0 } url ? url : SkillMarket.DefaultUrl;

    public async Task<SkillMarketSettingsDto> MarketSettingsAsync(CancellationToken ct) => new(await RegistryAsync(ct), SkillMarket.DefaultUrl);

    public Task<SkillMarketSettingsDto> SetMarketSettingsAsync(SkillMarketSettingsInputDto body, CancellationToken ct) => GuardAsync(async () =>
    {
        var url = body.Url?.Trim() ?? "";
        if (url.Length == 0) await db.Settings.DeleteAsync(MarketKey, ct);
        else
        {
            if (SkillMarket.CheckUrl(url) is { } why) throw new AdminApiException(400, why);
            await db.Settings.SetAsync(MarketKey, new SkillMarketSettingsInputDto(url), ct);
        }
        return await MarketSettingsAsync(ct);
    });

    public async Task<SkillMarketResultDto> MarketSearchAsync(string? query, CancellationToken ct)
    {
        var registry = await RegistryAsync(ct);
        try
        {
            var found = await SkillMarket.SearchAsync(http.CreateClient(), registry, query ?? "", ct);
            var have = (await db.Library.ListSkillsAsync(ct)).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            return new SkillMarketResultDto([.. found.Select(f => new SkillMarketEntryDto(f.Source, f.SkillId, f.Name, f.Installs, have.Contains(f.SkillId)))], registry, null);
        }
        catch (SkillLibraryException e)
        {
            return new SkillMarketResultDto([], registry, e.Message);
        }
    }

    /// <summary>Installs one market Skill from its GitHub repository (a Git source like any other: the address and branch checks of <see cref="SkillGit"/> apply).</summary>
    public Task<List<SkillOutcomeDto>> MarketInstallAsync(SkillMarketInstallDto body, CancellationToken ct) => GuardAsync(async () =>
    {
        if (!SkillMarket.IsSource(body.Source)) throw new AdminApiException(400, "The repository is not an owner/repo");
        if (!SkillLibrary.IsValidName(body.SkillId)) throw new AdminApiException(400, "The Skill name is not valid");
        var temp = Path.Combine(Path.GetTempPath(), "astra-skill-probe", Guid.NewGuid().ToString("N"));
        try
        {
            var url = SkillMarket.RepoUrl(body.Source);
            var commit = await SkillGit.FetchAsync(env, url, null, temp, ct);
            var candidates = (await _skills.ProbeAsync(temp, ct)).Where(c => c.Name == body.SkillId).ToList();
            if (candidates.Count == 0) throw new AdminApiException(404, "The repository has no Skill of that name");
            var sourceJson = new JsonObject { ["url"] = url, ["ref"] = null, ["commit"] = commit }.ToJsonString();
            var outcome = await _skills.InstallAsync(candidates, SkillSourceKinds.Git, sourceJson, body.Clients, ct);
            await _router.RefreshAsync(ct);
            return Map(outcome);
        }
        finally
        {
            Discard(temp);
        }
    });

    /// <summary>The offline <c>restore-all</c> step for the skill router (links Astra put in client directories).</summary>
    public Task<IReadOnlyList<string>> RemoveRoutersAsync(CancellationToken ct) => _router.RemoveAllAsync(ct);

    public Task<List<SkillMigratePreviewItemDto>> MigratePreviewAsync(List<string>? names, CancellationToken ct) => GuardAsync(async () =>
        (await _skills.MigratePreviewAsync(names, ct)).Select(i => new SkillMigratePreviewItemDto(i.Name, i.Locations.Select(Map).ToList(), i.Conflict, i.InLibrary, i.Skip)).ToList());

    public Task<List<SkillOutcomeDto>> MigrateAsync(SkillMigrateRequestDto body, CancellationToken ct) => GuardAsync(async () =>
        Map(await _skills.MigrateAsync(body.Items.Select(i => new SkillMigrateChoice(i.Name, i.Keep)).ToList(), ct)));

    public Task<List<SkillOutcomeDto>> ReturnAsync(string? name, CancellationToken ct) => GuardAsync(async () => Map(await _skills.ReturnAsync(name, ct)));

    public Task DeleteAsync(string name, CancellationToken ct) => GuardAsync(async () => { await _skills.DeleteAsync(name, ct); await _router.RefreshAsync(ct); return true; });

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
        await _router.RefreshAsync(ct);
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
