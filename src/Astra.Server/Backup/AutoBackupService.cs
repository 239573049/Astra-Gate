using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Data;
using Astra.Data.Backup;
using Astra.Server.Api;
using Astra.Server.Hosting;
using Microsoft.Data.Sqlite;

namespace Astra.Server.Backup;

/// <summary>Sync's bookkeeping document, read and written one change at a time (a sync and the device id must not overwrite each other).</summary>
public sealed class SyncStateStore(AstraDatabase db)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<SyncState> LoadAsync(CancellationToken ct)
    {
        var state = await db.Settings.GetAsync<SyncState>(BackupSettingsKeys.SyncState, ct) ?? new SyncState();
        if (string.IsNullOrEmpty(state.DeviceId)) state = await UpdateAsync(s => { }, ct);
        return state;
    }

    /// <summary>Applies <paramref name="change"/> to the stored state (creating the device id on first use) and saves it.</summary>
    public async Task<SyncState> UpdateAsync(Action<SyncState> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var state = await db.Settings.GetAsync<SyncState>(BackupSettingsKeys.SyncState, ct) ?? new SyncState();
            if (string.IsNullOrEmpty(state.DeviceId)) state.DeviceId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
            change(state);
            await db.Settings.SetAsync(BackupSettingsKeys.SyncState, state, ct);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>This machine's id in sync and in the bundles it writes.</summary>
public sealed class DeviceIdentity(SyncStateStore store)
{
    private string? _id;

    public async Task<string> GetAsync(CancellationToken ct) => _id ??= (await store.LoadAsync(ct)).DeviceId;
}

/// <summary>A file in the automatic backup folder.</summary>
public sealed record AutoBackupFile(string Name, string Kind, DateTimeOffset At, long Size, bool Encrypted);

/// <summary>
/// Automatic local backups (magpie's <c>auto.go</c>, MIT): <c>astra-YYYYMMDD-HHMMSS-{auto,restore,update}.astra-backup</c> in
/// <see cref="AstraPaths.AutoBackupsDir"/>, mode 0600, written to a temp file and renamed. A scheduled one is due <c>Hours</c> after the newest
/// and is skipped when the setup has not changed; the oldest of each kind past <c>Keep</c> are removed, and nothing else in the folder is ever touched.
/// Without a passphrase a file holds no secret at all; with one it is encrypted and holds the keys. A restore also keeps a copy of the
/// database itself (<c>backups/db/pre-restore-*.db</c>), which is the one place the machine-bound secrets survive as they were.
/// </summary>
public sealed partial class AutoBackupService(AstraDatabase db, BackupService backup, ISecretProtector secrets, ILogger<AutoBackupService> log)
{
    public const string KindAuto = "auto", KindRestore = "restore", KindUpdate = "update";
    private const int KeepDbCopies = 5;

    [GeneratedRegex(@"^astra-(\d{8})-(\d{6})(?:-\d+)?-(auto|restore|update)\.astra-backup$")]
    private static partial Regex NamePattern();

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Dir => db.Paths.AutoBackupsDir;

    public async Task<AutoBackupSettings> LoadAsync(CancellationToken ct) =>
        await db.Settings.GetAsync<AutoBackupSettings>(BackupSettingsKeys.Auto, ct) ?? new AutoBackupSettings();

    public Task SaveAsync(AutoBackupSettings s, CancellationToken ct) => db.Settings.SetAsync(BackupSettingsKeys.Auto, s, ct);

    public IReadOnlyList<AutoBackupFile> List()
    {
        if (!Directory.Exists(Dir)) return [];
        var files = new List<AutoBackupFile>();
        foreach (var path in Directory.EnumerateFiles(Dir, "astra-*.astra-backup"))
        {
            var name = Path.GetFileName(path);
            if (NamePattern().Match(name) is not { Success: true } m) continue;
            if (!DateTimeOffset.TryParseExact(m.Groups[1].Value + m.Groups[2].Value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) continue;
            var info = new FileInfo(path);
            files.Add(new AutoBackupFile(name, m.Groups[3].Value, at, info.Length, IsEncrypted(path)));
        }
        return [.. files.OrderByDescending(f => f.At).ThenByDescending(f => f.Name, StringComparer.Ordinal)];
    }

    private static bool IsEncrypted(string path)
    {
        try
        {
            using var s = File.OpenRead(path);
            var head = new byte[512];
            var n = s.Read(head, 0, head.Length);
            return System.Text.Encoding.UTF8.GetString(head, 0, n).Contains("\"kdf\":\"pbkdf2-sha256\"", StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
    }

    /// <summary>The bytes of a listed file (the name is matched against the list, never used as a path).</summary>
    public byte[] Read(string name)
    {
        var file = List().FirstOrDefault(f => f.Name == name) ?? throw new AdminApiException(404, "No such backup");
        return File.ReadAllBytes(Path.Combine(Dir, file.Name));
    }

    public void Delete(string name)
    {
        var file = List().FirstOrDefault(f => f.Name == name) ?? throw new AdminApiException(404, "No such backup");
        File.Delete(Path.Combine(Dir, file.Name));
    }

    /// <summary>
    /// Writes a backup of the configuration now. A scheduled one (<paramref name="force"/> false) is skipped when it equals the newest file.
    /// </summary>
    public async Task<AutoBackupFile?> CreateAsync(string kind, bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var s = await LoadAsync(ct);
            string? passphrase = null;
            if (!string.IsNullOrEmpty(s.PassphraseProtected))
            {
                try { passphrase = secrets.Unprotect(s.PassphraseProtected); }
                catch (CryptographicException) { log.LogWarning("The automatic backup passphrase cannot be read on this machine; writing a backup without keys"); }
            }
            var bundle = await backup.CollectAsync(BackupSections.Default, keys: passphrase is not null, "backup", forSync: false, ct);
            var content = BackupService.ContentHash(bundle);
            var newest = List().FirstOrDefault(f => f.Kind == kind);
            if (!force && newest is not null && ContentOf(Path.Combine(Dir, newest.Name)) == content) return null;

            Directory.CreateDirectory(Dir);
            AstraPaths.RestrictToOwner(Dir);
            var bytes = BackupService.Seal(bundle, passphrase);
            var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var name = $"astra-{stamp}-{kind}{BackupService.FileExtension}";
            for (var n = 2; File.Exists(Path.Combine(Dir, name)); n++) name = $"astra-{stamp}-{n}-{kind}{BackupService.FileExtension}";
            var tmp = Path.Combine(Dir, ".astra-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            AstraPaths.RestrictToOwner(tmp);
            File.Move(tmp, Path.Combine(Dir, name));
            Prune(kind, Math.Max(1, s.Keep));
            log.LogInformation("Backup written: {Name} ({Bytes} bytes)", name, bytes.Length);
            return List().FirstOrDefault(f => f.Name == name);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? ContentOf(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllBytes(path)) is JsonObject o && o["content"] is JsonValue v && v.TryGetValue<string>(out var c) ? c : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return null; }
    }

    private void Prune(string kind, int keep)
    {
        foreach (var old in List().Where(f => f.Kind == kind).Skip(keep))
        {
            try { File.Delete(Path.Combine(Dir, old.Name)); } catch (IOException) { }
        }
        if (Directory.Exists(Dir))
            foreach (var tmp in Directory.EnumerateFiles(Dir, ".astra-backup-*.tmp"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(tmp) > TimeSpan.FromHours(1))
                {
                    try { File.Delete(tmp); } catch (IOException) { }
                }
    }

    /// <summary>Called every few minutes: writes the scheduled backup when it is due, and the one an update earns.</summary>
    public async Task TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var s = await LoadAsync(ct);
        if (s.LastVersion != ServerOptions.Version)
        {
            var first = s.LastVersion is null;
            s.LastVersion = ServerOptions.Version;
            await SaveAsync(s, ct);
            // The first start has no "before"; a later one means an update (the database was copied before its migrations).
            if (!first && s.Enabled) await CreateAsync(KindUpdate, force: true, ct);
        }
        if (!s.Enabled) return;
        var newest = List().FirstOrDefault(f => f.Kind == KindAuto);
        if (newest is null || now - newest.At >= TimeSpan.FromHours(Math.Max(1, s.Hours))) await CreateAsync(KindAuto, force: false, ct);
    }

    /// <summary>Copies the database to <c>backups/db/pre-restore-*.db</c> (every row, secrets as protected on this machine).</summary>
    public async Task<string> SnapshotDatabaseAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(db.Paths.DbBackupsDir);
        var path = Path.Combine(db.Paths.DbBackupsDir, $"pre-restore-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.db");
        await using (var conn = await db.Factory.OpenUntrackedAsync(ct))
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "VACUUM INTO $path";
            cmd.Parameters.AddWithValue("$path", path);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        AstraPaths.RestrictToOwner(path);
        foreach (var old in Directory.EnumerateFiles(db.Paths.DbBackupsDir, "pre-restore-*.db").OrderByDescending(f => f, StringComparer.Ordinal).Skip(KeepDbCopies))
        {
            try { File.Delete(old); } catch (IOException) { }
        }
        return path;
    }
}

/// <summary>Runs <see cref="AutoBackupService.TickAsync"/> a minute after start and every ten minutes after.</summary>
public sealed class AutoBackupWorker(AutoBackupService auto, ILogger<AutoBackupWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
            do
            {
                try { await auto.TickAsync(DateTimeOffset.UtcNow, stoppingToken); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Automatic backup failed"); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) { }
    }
}

/// <summary>What a restore did.</summary>
public sealed class RestoreOutcome
{
    public required RestorePlan Plan { get; init; }
    public ConfigApplyResult? Result { get; init; }
    public string? SafetyBackup { get; init; }
}

/// <summary>Restores a bundle: keeps the current setup first (a bundle file and a copy of the database), then applies the planned changes.</summary>
public sealed class RestoreService(BackupService backup, AutoBackupService auto, ConfigDocs docs, ILogger<RestoreService> log)
{
    public async Task<RestoreOutcome> RestoreAsync(Bundle bundle, IEnumerable<string>? sections, string mode, CancellationToken ct)
    {
        var plan = await backup.PlanAsync(bundle, sections, mode, ct);
        if (plan.Changes.Count == 0) return new RestoreOutcome { Plan = plan };
        string? safety;
        try
        {
            safety = (await auto.CreateAsync(AutoBackupService.KindRestore, force: true, ct))?.Name;
            await auto.SnapshotDatabaseAsync(ct);
        }
        catch (Exception e) when (e is IOException or SqliteException or UnauthorizedAccessException)
        {
            throw new AdminApiException(500, "The current setup could not be backed up first, so nothing was restored: " + e.Message);
        }
        var result = await docs.ApplyAsync(plan.Changes, ct);
        log.LogInformation("Restore ({Mode}): {Written} written, {Removed} removed, kept {Safety}", mode, result.Written, result.Removed, safety);
        return new RestoreOutcome { Plan = plan, Result = result, SafetyBackup = safety };
    }
}
