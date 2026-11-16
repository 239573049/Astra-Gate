using Astra.Api.Contracts;
using Astra.Desktop.Core.Api;

namespace Astra.Desktop.Core.Backup;

/// <summary>
/// The backup and sync queries and writes (web/src/api/hooks.ts <c>useBackupInfo</c> / <c>useSyncStatus</c> …, linked update 38): <c>['backup-info']</c> counts per
/// section, <c>['backup-auto']</c> the schedule and the files, <c>['sync-status']</c> the target, the state and the conflicts. The same keys as the web's.
/// Everything but the status is local-origin only on the server; a 403 reaches the caller as an <see cref="ApiException"/>.
/// </summary>
public sealed class BackupApi
{
    private readonly ApiClient _api;
    private readonly QueryClient _queries;

    /// <summary>Creates the facade over the shared client and cache.</summary>
    public BackupApi(ApiClient api, QueryClient queries)
    {
        _api = api;
        _queries = queries;
        Info = queries.Query(QueryKey.Of("backup-info"), async ct => await api.GetAsync<BackupInfoDto>("/api/backup/info", ct) ?? throw new ApiException(0, "empty response"));
        Auto = queries.Query(QueryKey.Of("backup-auto"), async ct => await api.GetAsync<AutoBackupDto>("/api/backup/auto", ct) ?? throw new ApiException(0, "empty response"));
        SyncStatus = queries.Query(QueryKey.Of("sync-status"), async ct => await api.GetAsync<SyncStatusDto>("/api/sync/status", ct) ?? throw new ApiException(0, "empty response"));
    }

    /// <summary>['backup-info'].</summary>
    public Query<BackupInfoDto> Info { get; }

    /// <summary>['backup-auto'].</summary>
    public Query<AutoBackupDto> Auto { get; }

    /// <summary>['sync-status'].</summary>
    public Query<SyncStatusDto> SyncStatus { get; }

    /// <summary>POST /api/backup/export: the file's text (to be saved by the caller).</summary>
    public async Task<BackupExportDto> ExportAsync(BackupExportInput input, CancellationToken ct = default) =>
        await _api.PostAsync<BackupExportInput, BackupExportDto>("/api/backup/export", input, ct) ?? throw new ApiException(0, "empty response");

    /// <summary>POST /api/backup/preview: what restoring the source would do.</summary>
    public async Task<BackupPlanDto> PreviewAsync(BackupSourceInput input, CancellationToken ct = default) =>
        await _api.PostAsync<BackupSourceInput, BackupPlanDto>("/api/backup/preview", input, ct) ?? throw new ApiException(0, "empty response");

    /// <summary>POST /api/backup/restore.</summary>
    public async Task<BackupRestoreDto> RestoreAsync(BackupSourceInput input, CancellationToken ct = default)
    {
        var result = await _api.PostAsync<BackupSourceInput, BackupRestoreDto>("/api/backup/restore", input, ct) ?? throw new ApiException(0, "empty response");
        InvalidateAll();
        return result;
    }

    /// <summary>PUT /api/backup/auto; omitted fields stay.</summary>
    public async Task<AutoBackupDto> SetAutoAsync(AutoBackupInput input, CancellationToken ct = default)
    {
        var result = await _api.PutAsync<AutoBackupInput, AutoBackupDto>("/api/backup/auto", input, ct) ?? throw new ApiException(0, "empty response");
        _queries.Invalidate("backup-auto");
        return result;
    }

    /// <summary>POST /api/backup/auto/run: a backup now.</summary>
    public async Task<AutoBackupDto> RunAutoAsync(CancellationToken ct = default)
    {
        var result = await _api.PostAsync<AutoBackupDto>("/api/backup/auto/run", ct) ?? throw new ApiException(0, "empty response");
        _queries.Invalidate("backup-auto");
        return result;
    }

    /// <summary>DELETE /api/backup/auto/{name}.</summary>
    public async Task DeleteAutoAsync(string name, CancellationToken ct = default)
    {
        await _api.DeleteAsync("/api/backup/auto/" + ApiClient.Enc(name), ct);
        _queries.Invalidate("backup-auto");
    }

    /// <summary>PUT /api/sync/config; omitted fields stay (an omitted target secret stays while the target is unchanged).</summary>
    public async Task<SyncStatusDto> SetSyncAsync(SyncConfigInput input, CancellationToken ct = default)
    {
        var result = await _api.PutAsync<SyncConfigInput, SyncStatusDto>("/api/sync/config", input, ct) ?? throw new ApiException(0, "empty response");
        _queries.Invalidate("sync-status");
        return result;
    }

    /// <summary>DELETE /api/sync/config: forgets the target and the sync state.</summary>
    public async Task<SyncStatusDto> ForgetSyncAsync(CancellationToken ct = default)
    {
        var result = await _api.DeleteAsync<SyncStatusDto>("/api/sync/config", ct) ?? throw new ApiException(0, "empty response");
        _queries.Invalidate("sync-status");
        return result;
    }

    /// <summary>POST /api/sync/test: writes and reads a probe file on the target.</summary>
    public Task TestSyncAsync(CancellationToken ct = default) => _api.PostAsync<SyncTestDto>("/api/sync/test", ct);

    /// <summary>POST /api/sync/now (<paramref name="dryRun"/> plans without writing).</summary>
    public async Task<SyncRunDto> SyncNowAsync(bool dryRun = false, CancellationToken ct = default)
    {
        try
        {
            return await _api.PostAsync<SyncRunDto>("/api/sync/now" + (dryRun ? "?dryRun=true" : ""), ct) ?? throw new ApiException(0, "empty response");
        }
        finally
        {
            _queries.Invalidate("sync-status");
            if (!dryRun) InvalidateAll();
        }
    }

    /// <summary>DELETE /api/sync/conflicts.</summary>
    public async Task ClearConflictsAsync(CancellationToken ct = default)
    {
        await _api.DeleteAsync<SyncStatusDto>("/api/sync/conflicts", ct);
        _queries.Invalidate("sync-status");
    }

    /// <summary>POST /api/sync/backup: uploads a backup to the sync target.</summary>
    public async Task<RemoteBackupDto> UploadAsync(RemoteBackupInput input, CancellationToken ct = default) =>
        await _api.PostAsync<RemoteBackupInput, RemoteBackupDto>("/api/sync/backup", input, ct) ?? throw new ApiException(0, "empty response");

    // A restore or a sync that pulled changes can touch every page's data.
    private void InvalidateAll()
    {
        foreach (var key in new[] { "backup-info", "backup-auto", "providers", "clients", "tokens", "settings", "models", "profiles" }) _queries.Invalidate(key);
    }
}
