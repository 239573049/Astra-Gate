using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Core.Clients;
using Astra.Data;
using Astra.Data.Backup;
using Astra.Gateway.Http;
using Astra.Server.Api;
using Astra.Server.Hosting;

namespace Astra.Server.Backup;

/// <summary>
/// <c>/api/backup/*</c> (whole-installation backup, restore and the local automatic backups) and <c>/api/sync/*</c> (the sync target and multi-machine sync).
/// Everything that writes, reads a backup file or names an address Astra will contact is for this machine only (<see cref="RequestOrigin.IsLocal"/>);
/// only the status and the section counts answer a remote admin session, and a file without keys may be exported from anywhere.
/// </summary>
public static class BackupEndpoints
{
    private const int MaxItemsPerSection = 200;

    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var backup = app.MapGroup("/api/backup").AddEndpointFilter<AdminApiErrorFilter>();

        backup.MapGet("/info", async (BackupService svc, CancellationToken ct) => Results.Ok(new BackupInfoDto(await svc.CountAsync(ct), ServerOptions.Version)));

        backup.MapPost("/export", async (HttpContext http, BackupExportInput input, BackupService svc, CancellationToken ct) =>
        {
            var keys = input.IncludeKeys == true;
            if (keys) RequireLocal(http, "A backup with keys can only be made on this machine");
            if (keys && string.IsNullOrEmpty(input.Passphrase)) throw new AdminApiException(400, "A backup with keys needs a passphrase");
            var sections = BackupService.ResolveSections(input.Sections, input.IncludeUsage == true);
            var bundle = await svc.CollectAsync(sections, keys, "backup", forSync: false, ct);
            var file = BackupService.Seal(bundle, string.IsNullOrEmpty(input.Passphrase) ? null : input.Passphrase);
            var name = $"astra-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}{BackupService.FileExtension}";
            return Results.Ok(new BackupExportDto(name, Encoding.UTF8.GetString(file), !string.IsNullOrEmpty(input.Passphrase), keys, bundle.All.Count(), bundle.Warnings));
        });

        backup.MapPost("/preview", async (HttpContext http, BackupSourceInput input, BackupAccess access, CancellationToken ct) =>
        {
            RequireLocal(http, "A backup can only be read on this machine");
            return Results.Ok(await access.PreviewAsync(input, ct));
        });

        backup.MapPost("/restore", async (HttpContext http, BackupSourceInput input, BackupAccess access, CancellationToken ct) =>
        {
            RequireLocal(http, "A backup can only be restored on this machine");
            return Results.Ok(await access.RestoreAsync(input, ct));
        });

        backup.MapGet("/auto", async (AutoBackupService auto, CancellationToken ct) => Results.Ok(await AutoDtoAsync(auto, ct)));
        backup.MapPut("/auto", async (HttpContext http, AutoBackupInput input, AutoBackupService auto, ISecretProtector secrets, CancellationToken ct) =>
        {
            RequireLocal(http, "Automatic backups can only be set up on this machine");
            var s = await auto.LoadAsync(ct);
            if (input.Enabled is { } enabled) s.Enabled = enabled;
            if (input.Hours is { } hours) s.Hours = Math.Clamp(hours, 1, 24 * 30);
            if (input.Keep is { } keep) s.Keep = Math.Clamp(keep, 1, 100);
            if (input.ClearPassphrase == true) s.PassphraseProtected = null;
            if (!string.IsNullOrEmpty(input.Passphrase))
            {
                BackupEnvelope.RequirePassphrase(input.Passphrase);
                s.PassphraseProtected = secrets.Protect(input.Passphrase);
            }
            await auto.SaveAsync(s, ct);
            return Results.Ok(await AutoDtoAsync(auto, ct));
        });
        backup.MapPost("/auto/run", async (HttpContext http, AutoBackupService auto, CancellationToken ct) =>
        {
            RequireLocal(http, "Backups can only be made on this machine");
            await auto.CreateAsync(AutoBackupService.KindAuto, force: true, ct);
            return Results.Ok(await AutoDtoAsync(auto, ct));
        });
        backup.MapDelete("/auto/{name}", async (HttpContext http, string name, AutoBackupService auto, CancellationToken ct) =>
        {
            RequireLocal(http, "Backups can only be deleted on this machine");
            auto.Delete(name);
            return Results.Ok(await AutoDtoAsync(auto, ct));
        });

        var sync = app.MapGroup("/api/sync").AddEndpointFilter<AdminApiErrorFilter>();

        sync.MapGet("/status", async (SyncService svc, SyncStateStore states, CancellationToken ct) => Results.Ok(await StatusAsync(svc, states, ct)));

        sync.MapPut("/config", async (HttpContext http, SyncConfigInput input, SyncService svc, SyncStateStore states, ISecretProtector secrets, CancellationToken ct) =>
        {
            RequireLocal(http, "Sync can only be set up on this machine");
            var c = await svc.LoadConfigAsync(ct);
            var resetState = false;
            if (input.Target is { } t)
            {
                var next = new SyncTargetConfig
                {
                    Kind = t.Kind, Url = t.Url ?? "", Username = t.Username ?? "", Folder = t.Folder ?? "astra", Bucket = t.Bucket ?? "", Region = t.Region ?? "",
                    PathStyle = t.PathStyle ?? false, Branch = t.Branch ?? "",
                };
                SyncTargets.Validate(next);
                var same = c.Target.Kind == next.Kind && c.Target.Url == next.Url && c.Target.Username == next.Username && c.Target.Bucket == next.Bucket
                           && c.Target.Folder == next.Folder && c.Target.Branch == next.Branch && c.Target.Region == next.Region;
                next.SecretProtected = same ? c.Target.SecretProtected : null;
                if (!string.IsNullOrEmpty(t.Secret))
                {
                    if (t.Secret.Length > 500 || t.Secret.Any(char.IsControl)) throw new AdminApiException(400, "Invalid password or token");
                    next.SecretProtected = secrets.Protect(t.Secret);
                }
                resetState = !same;
                c.Target = next;
            }
            if (!string.IsNullOrEmpty(input.Passphrase))
            {
                BackupEnvelope.RequirePassphrase(input.Passphrase);
                c.PassphraseProtected = secrets.Protect(input.Passphrase);
            }
            if (input.IntervalMinutes is { } minutes) c.IntervalMinutes = Math.Clamp(minutes, 1, 24 * 60);
            if (input.Sections is { } sections) c.Sections = sections.Count == 0 ? [] : BackupService.ResolveSections(sections);
            if (input.IncludeKeys is { } keys) c.IncludeKeys = keys;
            if (input.IncludeSignins is { } signins) c.IncludeSignins = signins;
            if (input.Enabled is { } enabled)
            {
                if (enabled && (c.Target.Kind.Length == 0 || c.PassphraseProtected is null)) throw new AdminApiException(409, "Set the target and the passphrase first");
                c.Enabled = enabled;
            }
            await svc.SaveConfigAsync(c, ct);
            if (resetState) await svc.ResetStateAsync(ct);
            return Results.Ok(await StatusAsync(svc, states, ct));
        });

        sync.MapDelete("/config", async (HttpContext http, SyncService svc, SyncStateStore states, CancellationToken ct) =>
        {
            RequireLocal(http, "Sync can only be set up on this machine");
            await svc.SaveConfigAsync(new SyncConfig(), ct);
            await svc.ResetStateAsync(ct);
            return Results.Ok(await StatusAsync(svc, states, ct));
        });

        sync.MapPost("/test", async (HttpContext http, SyncService svc, CancellationToken ct) =>
        {
            RequireLocal(http, "Sync can only be used on this machine");
            await svc.TestAsync(ct);
            return Results.Ok(new { ok = true });
        });

        sync.MapPost("/now", async (HttpContext http, SyncService svc, CancellationToken ct, bool dryRun = false) =>
        {
            RequireLocal(http, "Sync can only be used on this machine");
            var r = await svc.RunAsync(dryRun, ct);
            return Results.Ok(new SyncRunDto(r.Pushed, r.Pulled, r.DryRun, r.FirstSync, r.Conflicts.Select(Map).ToList(),
                r.Items.Select(i => new BackupItemDto(i.Section + "/" + i.Id, i.Label, i.Status)).ToList(), r.Errors));
        });

        sync.MapDelete("/conflicts", async (HttpContext http, SyncService svc, SyncStateStore states, CancellationToken ct) =>
        {
            RequireLocal(http, "Sync can only be used on this machine");
            await states.UpdateAsync(s => s.Conflicts = [], ct);
            return Results.Ok(await StatusAsync(svc, states, ct));
        });

        sync.MapPost("/backup", async (HttpContext http, RemoteBackupInput input, BackupService backup, SyncService svc, ISecretProtector secrets, CancellationToken ct) =>
        {
            RequireLocal(http, "Backups can only be uploaded from this machine");
            var config = await svc.LoadConfigAsync(ct);
            var keys = input.IncludeKeys == true;
            var passphrase = input.Passphrase;
            if (string.IsNullOrEmpty(passphrase) && config.PassphraseProtected is not null) passphrase = secrets.Unprotect(config.PassphraseProtected);
            if (string.IsNullOrEmpty(passphrase)) throw new AdminApiException(400, "A backup on a server needs a passphrase");
            var bundle = await backup.CollectAsync(BackupService.ResolveSections(input.Sections, input.IncludeUsage == true), keys, "backup", forSync: false, ct);
            return Results.Ok(new RemoteBackupDto(await svc.UploadBackupAsync(BackupService.Seal(bundle, passphrase), ct)));
        });
    }

    private static void RequireLocal(HttpContext http, string message)
    {
        if (!RequestOrigin.IsLocal(http)) throw new AdminApiException(403, message);
    }

    private static SyncConflictDto Map(SyncConflict c) => new(c.Section, c.Id, c.Label, c.Winner, c.At, c.Device);

    private static async Task<AutoBackupDto> AutoDtoAsync(AutoBackupService auto, CancellationToken ct)
    {
        var s = await auto.LoadAsync(ct);
        return new AutoBackupDto(s.Enabled, s.Hours, s.Keep, !string.IsNullOrEmpty(s.PassphraseProtected), auto.Dir,
            auto.List().Select(f => new AutoBackupFileDto(f.Name, f.Kind, f.At, f.Size, f.Encrypted)).ToList());
    }

    private static async Task<SyncStatusDto> StatusAsync(SyncService svc, SyncStateStore states, CancellationToken ct)
    {
        var c = await svc.LoadConfigAsync(ct);
        var s = await states.LoadAsync(ct);
        var t = c.Target;
        return new SyncStatusDto(c.Enabled, t.Url.Length > 0 || t.Bucket.Length > 0, new SyncTargetDto(t.Kind, t.Url, t.Username, t.SecretProtected is not null, t.Folder, t.Bucket, t.Region, t.PathStyle, t.Branch),
            c.PassphraseProtected is not null, c.IntervalMinutes, SyncService.ScopeOf(c), c.IncludeKeys, c.IncludeSignins, s.DeviceId, s.LastSyncAt, s.LastError, s.LastPushed, s.LastPulled,
            svc.Running, s.Conflicts.Select(Map).ToList(), s.Baseline.Count);
    }

    internal static BackupPlanDto ToDto(RestorePlan plan)
    {
        var sections = new List<BackupSectionPlanDto>();
        foreach (var section in plan.Sections)
        {
            var items = plan.Items.Where(i => i.Section == section).ToList();
            var changed = items.Where(i => i.Status != "unchanged").ToList();
            sections.Add(new BackupSectionPlanDto(section, items.Count(i => i.Status == "added"), items.Count(i => i.Status == "changed"), items.Count(i => i.Status == "removed"),
                items.Count(i => i.Status == "unchanged"), changed.Take(MaxItemsPerSection).Select(i => new BackupItemDto(i.Id, i.Label, i.Status)).ToList(), changed.Count > MaxItemsPerSection));
        }
        return new BackupPlanDto(plan.Bundle.CreatedAt, plan.Bundle.AstraVersion, plan.Bundle.Keys, plan.Mode, plan.Bundle.Sections, sections, plan.Notes, false, null);
    }
}

/// <summary>Reads a backup from where the request says (a pasted file, a local automatic backup, the sync target) and previews or restores it; the providers-only files of earlier versions go to their own importer.</summary>
public sealed class BackupAccess(BackupService backup, RestoreService restore, AutoBackupService auto, SyncService sync, ProviderBackupService providers)
{
    private async Task<byte[]> ReadAsync(BackupSourceInput input, CancellationToken ct)
    {
        switch (input.Source ?? "file")
        {
            case "file":
                if (string.IsNullOrEmpty(input.Content)) throw new AdminApiException(400, "No backup file given");
                return Encoding.UTF8.GetBytes(input.Content);
            case "auto":
                return auto.Read(input.Name ?? "");
            case "remote":
                return await sync.FetchLatestBackupAsync(ct);
            default:
                throw new AdminApiException(400, "source must be file, auto or remote");
        }
    }

    private static JsonObject LegacyBody(byte[] file, BackupSourceInput input) =>
        new() { ["file"] = JsonNode.Parse(file), ["passphrase"] = input.Passphrase };

    public async Task<BackupPlanDto> PreviewAsync(BackupSourceInput input, CancellationToken ct)
    {
        var file = await ReadAsync(input, ct);
        if (BackupService.IsLegacyProviders(file))
        {
            var preview = await providers.PreviewAsync(LegacyBody(file, input), ct);
            return new BackupPlanDto("", "", false, RestoreModes.Merge, [BackupSections.Providers], [], [], true, preview.ToJsonString());
        }
        var bundle = BackupService.Open(file, input.Passphrase);
        return BackupEndpoints.ToDto(await backup.PlanAsync(bundle, input.Sections, input.Mode ?? RestoreModes.Merge, ct));
    }

    public async Task<BackupRestoreDto> RestoreAsync(BackupSourceInput input, CancellationToken ct)
    {
        var file = await ReadAsync(input, ct);
        if (BackupService.IsLegacyProviders(file))
        {
            var done = await providers.ImportAsync(LegacyBody(file, input), ct);
            var plan = new BackupPlanDto("", "", false, RestoreModes.Merge, [BackupSections.Providers], [], [], true, null);
            return new BackupRestoreDto(plan, (done["added"] as JsonArray)?.Count ?? 0, 0, [], [], null, done.ToJsonString());
        }
        var bundle = BackupService.Open(file, input.Passphrase);
        var outcome = await restore.RestoreAsync(bundle, input.Sections, input.Mode ?? RestoreModes.Merge, ct);
        var r = outcome.Result;
        return new BackupRestoreDto(BackupEndpoints.ToDto(outcome.Plan), r?.Written ?? 0, r?.Removed ?? 0, r?.Skipped ?? [], r?.Errors ?? [], outcome.SafetyBackup, null);
    }
}
