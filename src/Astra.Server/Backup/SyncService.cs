using System.Globalization;
using System.Security.Cryptography;
using Astra.Core.Clients;
using Astra.Data;
using Astra.Data.Backup;
using Astra.Server.Api;

namespace Astra.Server.Backup;

/// <summary>What one sync run did (or, for a dry run, would do).</summary>
public sealed class SyncRunResult
{
    public int Pushed { get; set; }
    public int Pulled { get; set; }
    public bool DryRun { get; set; }
    public bool FirstSync { get; set; }
    public List<SyncConflict> Conflicts { get; } = [];
    public List<PlanItem> Items { get; } = [];
    public List<string> Errors { get; } = [];
}

/// <summary>
/// Multi-machine sync: every few minutes (3 by default) each machine reads the shared file from its target, merges it with its own configuration
/// document by document (<see cref="SyncMerger"/>), writes its changes back with a conditional write and applies the others' changes through
/// <see cref="ConfigDocs"/> — so clients go through the enable flow and the library through its engine. The file is sealed on this machine with the
/// sync passphrase (the same on every machine): the target only ever holds ciphertext. A write that lost a race is merged again, up to three times.
/// Never synced: what <see cref="BackupCatalog"/> marks machine-local (listen address, tunnel, proxy, autostart, paths, the server's own keys, logs).
/// After magpie's davsync (MIT), with a per-document merge instead of per-part.
/// </summary>
public sealed class SyncService(
    AstraDatabase db, ConfigDocs docs, AutoBackupService auto, SyncStateStore states, DeviceIdentity device,
    ISecretProtector secrets, IHttpClientFactory http, ILogger<SyncService> log)
{
    public const string SyncFile = "astra-sync.astra-backup";
    public const string LatestBackupFile = "astra-backup-latest.astra-backup";
    private const int Retries = 3;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _running;

    public bool Running => _running;

    // ---------------------------------------------------------------- configuration

    public async Task<SyncConfig> LoadConfigAsync(CancellationToken ct) =>
        await db.Settings.GetAsync<SyncConfig>(BackupSettingsKeys.Sync, ct) ?? new SyncConfig();

    public Task SaveConfigAsync(SyncConfig c, CancellationToken ct) => db.Settings.SetAsync(BackupSettingsKeys.Sync, c, ct);

    /// <summary>The sections a sync covers: the configured ones, else everything but usage (and sign-ins unless asked).</summary>
    public static List<string> ScopeOf(SyncConfig c)
    {
        var list = c.Sections.Count > 0 ? BackupService.ResolveSections(c.Sections) : [.. BackupSections.Default.Where(s => s != BackupSections.Signins)];
        list.Remove(BackupSections.Usage);
        if (c.IncludeSignins && !list.Contains(BackupSections.Signins)) list.Add(BackupSections.Signins);
        if (!c.IncludeSignins) list.Remove(BackupSections.Signins);
        return [.. BackupSections.All.Where(list.Contains)];
    }

    private string? Plain(string? sealedValue, string what)
    {
        if (string.IsNullOrEmpty(sealedValue)) return null;
        try { return secrets.Unprotect(sealedValue); }
        catch (CryptographicException) { throw new AdminApiException(409, $"The stored {what} cannot be read on this machine; enter it again"); }
    }

    public ISyncTarget CreateTarget(SyncConfig c)
    {
        var secret = Plain(c.Target.SecretProtected, "password or token");
        return SyncTargets.Create(c.Target, secret, () => http.CreateClient(SyncTargets.HttpClientName));
    }

    private string Passphrase(SyncConfig c) =>
        Plain(c.PassphraseProtected, "sync passphrase") is { Length: > 0 } p ? p : throw new AdminApiException(409, "Sync has no passphrase yet");

    // ---------------------------------------------------------------- a sync

    /// <summary>Runs one sync (or only plans it with <paramref name="dryRun"/>). One at a time; a second caller waits.</summary>
    public async Task<SyncRunResult> RunAsync(bool dryRun, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        _running = true;
        try
        {
            var config = await LoadConfigAsync(ct);
            if (config.Target.Kind.Length == 0 || (config.Target.Url.Length == 0 && config.Target.Kind != SyncTargetKinds.S3)) throw new AdminApiException(409, "Sync is not set up yet");
            var passphrase = Passphrase(config);
            var target = CreateTarget(config);
            if (!dryRun) await states.UpdateAsync(s => s.LastAttemptAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), ct);
            try
            {
                var result = await SyncOnceAsync(config, target, passphrase, dryRun, ct);
                if (!dryRun)
                    await states.UpdateAsync(s =>
                    {
                        s.LastError = null;
                        s.LastSyncAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                        s.LastPushed = result.Pushed;
                        s.LastPulled = result.Pulled;
                    }, ct);
                return result;
            }
            catch (Exception e) when (e is AdminApiException && !dryRun)
            {
                await states.UpdateAsync(s => s.LastError = e.Message, ct);
                throw;
            }
        }
        finally
        {
            _running = false;
            _gate.Release();
        }
    }

    private async Task<SyncRunResult> SyncOnceAsync(SyncConfig config, ISyncTarget target, string passphrase, bool dryRun, CancellationToken ct)
    {
        var scope = ScopeOf(config);
        var scopeSet = scope.ToHashSet();
        var me = await device.GetAsync(ct);
        for (var attempt = 0; ; attempt++)
        {
            var state = await states.LoadAsync(ct);
            var result = new SyncRunResult { DryRun = dryRun, FirstSync = state.Baseline.Count == 0 };
            var report = new ReadReport();
            var local = await docs.ReadAsync(scope, new ReadOptions(config.IncludeKeys, ForSync: true), report, ct);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            // When a local change was first seen, kept even if this sync fails: it is the "when" of a later conflict decision.
            if (!dryRun)
            {
                var firstSeen = new Dictionary<string, long>();
                foreach (var d in local)
                {
                    var key = SyncMerger.KeyOf(d.Section, d.Id);
                    var changed = !state.Baseline.TryGetValue(key, out var bl) || bl.Deleted || bl.Hash != d.Hash();
                    if (changed && !state.Pending.ContainsKey(key)) firstSeen[key] = now;
                }
                foreach (var (key, bl) in state.Baseline)
                    if (!bl.Deleted && scopeSet.Contains(key.Split('\n')[0]) && !local.Any(d => SyncMerger.KeyOf(d.Section, d.Id) == key) && !state.Pending.ContainsKey(key)) firstSeen[key] = now;
                if (firstSeen.Count > 0) state = await states.UpdateAsync(s => { foreach (var (k, v) in firstSeen) s.Pending.TryAdd(k, v); }, ct);
            }

            var remote = await target.GetAsync(SyncFile, ct);
            var entries = new List<BundleEntry>();
            if (remote is not null)
            {
                var bundle = Bundle.FromJson(BackupEnvelope.Open(remote.Bytes, passphrase));
                if (bundle.Keys != config.IncludeKeys && bundle.All.Any())
                    throw new AdminApiException(409, "The other machines sync " + (bundle.Keys ? "with" : "without") + " keys; use the same \"include keys\" setting everywhere");
                entries = bundle.All.ToList();
            }

            var merge = SyncMerger.Merge(local, state.Baseline, entries, scopeSet, state.Pending, me, now);
            result.Pushed = merge.Pushed;
            result.Pulled = merge.Pull.Count;
            result.Conflicts.AddRange(merge.Conflicts);
            result.Items.AddRange(merge.PullItems);
            if (dryRun) return result;

            string? version = remote?.Version;
            if (merge.RemoteChanged || remote is null && merge.Remote.Count > 0)
            {
                var outBundle = new Bundle { Kind = "sync", AstraVersion = Hosting.ServerOptions.Version, Device = me, Keys = config.IncludeKeys, Sections = scope };
                foreach (var e in merge.Remote)
                {
                    if (!outBundle.Entries.TryGetValue(e.Doc.Section, out var list)) outBundle.Entries[e.Doc.Section] = list = [];
                    list.Add(e);
                }
                try { version = await target.PutAsync(SyncFile, BackupService.Seal(outBundle, passphrase), remote?.Version, ct); }
                catch (SyncPreconditionException) when (attempt + 1 < Retries) { continue; }
                catch (SyncPreconditionException) { throw new AdminApiException(409, "Another machine kept writing at the same time; the next sync will merge again"); }
            }

            if (merge.Pull.Count > 0)
            {
                if (result.FirstSync) await auto.CreateAsync(AutoBackupService.KindRestore, force: true, ct); // joining keeps what this machine had
                var applied = await docs.ApplyAsync(merge.Pull, ct);
                result.Errors.AddRange(applied.Errors);
            }

            // What this machine holds now is what was agreed on: re-read the documents that were pulled (a skipped or adjusted one must not look like a local edit next time).
            var pulledKeys = merge.Pull.Select(c => SyncMerger.KeyOf(c.Section, c.Id)).ToHashSet();
            var after = new Dictionary<string, BackupDoc>();
            if (pulledKeys.Count > 0)
                foreach (var d in await docs.ReadAsync(merge.Pull.Select(c => c.Section).Distinct(), new ReadOptions(config.IncludeKeys, ForSync: true), null, ct))
                    if (pulledKeys.Contains(SyncMerger.KeyOf(d.Section, d.Id))) after[SyncMerger.KeyOf(d.Section, d.Id)] = d;
            await states.UpdateAsync(s =>
            {
                foreach (var (key, bl) in merge.Baseline)
                {
                    if (pulledKeys.Contains(key) && !bl.Deleted)
                    {
                        if (after.TryGetValue(key, out var d)) s.Baseline[key] = new SyncBaseline { Hash = d.Hash(), Ts = bl.Ts };
                        else s.Baseline.Remove(key);
                    }
                    else s.Baseline[key] = bl;
                }
                foreach (var key in s.Pending.Keys.Where(k => scopeSet.Contains(k.Split('\n')[0])).ToList()) s.Pending.Remove(key);
                s.RemoteVersion = version;
                s.Conflicts = [.. merge.Conflicts.Concat(s.Conflicts).Take(50)];
            }, ct);
            log.LogInformation("Sync: pushed {Pushed}, pulled {Pulled}, {Conflicts} conflict(s)", result.Pushed, result.Pulled, result.Conflicts.Count);
            return result;
        }
    }

    // ---------------------------------------------------------------- manual remote backup

    /// <summary>Uploads a whole-installation backup file to the target (a timestamped copy and the "latest" one).</summary>
    public async Task<string> UploadBackupAsync(byte[] file, CancellationToken ct)
    {
        var config = await LoadConfigAsync(ct);
        var target = CreateTarget(config);
        var name = $"astra-backup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}{BackupService.FileExtension}";
        await target.PutAsync(name, file, null, ct);
        var latest = await target.GetAsync(LatestBackupFile, ct);
        try { await target.PutAsync(LatestBackupFile, file, latest?.Version, ct); }
        catch (SyncPreconditionException) { throw new AdminApiException(409, "The latest backup on the server changed while it was uploaded; try again"); }
        return name;
    }

    /// <summary>The latest backup file on the target, for the ordinary preview and restore.</summary>
    public async Task<byte[]> FetchLatestBackupAsync(CancellationToken ct)
    {
        var config = await LoadConfigAsync(ct);
        var file = await CreateTarget(config).GetAsync(LatestBackupFile, ct) ?? throw new AdminApiException(404, "There is no backup on the target yet");
        return file.Bytes;
    }

    public async Task TestAsync(CancellationToken ct) => await CreateTarget(await LoadConfigAsync(ct)).TestAsync(ct);

    /// <summary>Forgets what the last syncs agreed on (the next one is a join: the remote setup comes here, this machine's is kept first).</summary>
    public Task ResetStateAsync(CancellationToken ct) => states.UpdateAsync(s =>
    {
        s.Baseline = [];
        s.Pending = [];
        s.RemoteVersion = null;
        s.Conflicts = [];
        s.LastError = null;
    }, ct);
}

/// <summary>Runs <see cref="SyncService.RunAsync"/> on the configured interval while sync is enabled.</summary>
public sealed class SyncWorker(SyncService sync, SyncStateStore states, ILogger<SyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                try
                {
                    var config = await sync.LoadConfigAsync(stoppingToken);
                    if (!config.Enabled) continue;
                    var state = await states.LoadAsync(stoppingToken);
                    var hasLast = DateTimeOffset.TryParse(state.LastAttemptAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var last);
                    if (hasLast && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(Math.Max(1, config.IntervalMinutes))) continue;
                    await sync.RunAsync(false, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning("Sync failed: {Message}", e is AdminApiException ? e.Message : e.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}
