namespace Astra.Api.Contracts;

// Whole-installation backup, restore and multi-machine sync (/api/backup/*, /api/sync/*; .agents/memory/backup-and-sync.md).
// The backup file itself travels as text (BackupExportDto.Content): it is opaque to the console, which only saves and uploads it.

/// <summary>What a backup holds: the documents per section on this machine, and the sections' ids.</summary>
public sealed record BackupInfoDto(IReadOnlyDictionary<string, int> Sections, string Version);

/// <summary>Body of <c>POST /api/backup/export</c>. No passphrase means a file without any secret; keys need a passphrase and a request from this machine.</summary>
public sealed record BackupExportInput(List<string>? Sections, bool? IncludeKeys, bool? IncludeUsage, string? Passphrase);

/// <summary>The exported file: <see cref="Content"/> is its text, <see cref="Name"/> a suggested file name.</summary>
public sealed record BackupExportDto(string Name, string Content, bool Encrypted, bool HasKeys, int Documents, IReadOnlyList<string> Warnings);

/// <summary>
/// Which backup a preview or restore reads: <c>file</c> (<see cref="Content"/> is the file text), <c>auto</c> (<see cref="Name"/> a file of the local backup folder)
/// or <c>remote</c> (the latest manual backup on the sync target). Local requests only.
/// </summary>
public sealed record BackupSourceInput(string? Source, string? Content, string? Name, string? Passphrase, List<string>? Sections, string? Mode);

/// <summary>One document in a preview: <c>added</c>, <c>changed</c>, <c>removed</c> or <c>unchanged</c>.</summary>
public sealed record BackupItemDto(string Id, string Label, string Status);

/// <summary>One section of a preview: the counts, and the documents that would change (capped; <see cref="Truncated"/> says when).</summary>
public sealed record BackupSectionPlanDto(string Section, int Added, int Changed, int Removed, int Unchanged, IReadOnlyList<BackupItemDto> Items, bool Truncated);

/// <summary>What restoring a backup would do. <see cref="Legacy"/> is a providers-only file (<see cref="LegacyJson"/> is the providers preview the older importer made).</summary>
public sealed record BackupPlanDto(
    string CreatedAt, string AstraVersion, bool HasKeys, string Mode, IReadOnlyList<string> Available, IReadOnlyList<BackupSectionPlanDto> Sections,
    IReadOnlyList<string> Notes, bool Legacy, string? LegacyJson);

/// <summary>The result of a restore: the plan that was carried out, what was written, and the copy of the previous setup that was kept first.</summary>
public sealed record BackupRestoreDto(
    BackupPlanDto Plan, int Written, int Removed, IReadOnlyList<string> Skipped, IReadOnlyList<string> Errors, string? SafetyBackup, string? LegacyJson);

/// <summary>A file in the automatic backup folder.</summary>
public sealed record AutoBackupFileDto(string Name, string Kind, DateTimeOffset At, long Size, bool Encrypted);

/// <summary>The schedule of automatic backups and what is kept.</summary>
public sealed record AutoBackupDto(bool Enabled, int Hours, int Keep, bool HasPassphrase, string Dir, IReadOnlyList<AutoBackupFileDto> Files);

/// <summary>Body of <c>PUT /api/backup/auto</c>; omitted fields stay. A passphrase makes the files encrypted and keyful; <see cref="ClearPassphrase"/> goes back to plain keyless files.</summary>
public sealed record AutoBackupInput(bool? Enabled, int? Hours, int? Keep, string? Passphrase, bool? ClearPassphrase);

/// <summary>The sync target as the console sees it (the secret is never returned).</summary>
public sealed record SyncTargetDto(string Kind, string Url, string Username, bool HasSecret, string Folder, string Bucket, string Region, bool PathStyle, string Branch);

/// <summary>A document two machines changed differently: which side was kept.</summary>
public sealed record SyncConflictDto(string Section, string Id, string Label, string Winner, string At, string Device);

public sealed record SyncStatusDto(
    bool Enabled, bool Configured, SyncTargetDto Target, bool HasPassphrase, int IntervalMinutes, IReadOnlyList<string> Sections, bool IncludeKeys, bool IncludeSignins,
    string DeviceId, string? LastSyncAt, string? LastError, int LastPushed, int LastPulled, bool Running, IReadOnlyList<SyncConflictDto> Conflicts, int Tracked);

/// <summary>The target in <see cref="SyncConfigInput"/>. An omitted <see cref="Secret"/> keeps the stored one while the rest of the target is unchanged.</summary>
public sealed record SyncTargetInput(
    string Kind, string? Url, string? Username, string? Secret, string? Folder, string? Bucket, string? Region, bool? PathStyle, string? Branch);

/// <summary>Body of <c>PUT /api/sync/config</c>; omitted fields stay. The passphrase must be the same on every machine and cannot be recovered.</summary>
public sealed record SyncConfigInput(
    SyncTargetInput? Target, string? Passphrase, int? IntervalMinutes, List<string>? Sections, bool? IncludeKeys, bool? IncludeSignins, bool? Enabled);

/// <summary>What a sync run did (<c>POST /api/sync/now</c>, or with <c>?dryRun=true</c> would do): documents pushed, pulled, the conflicts it settled and what it brought in.</summary>
public sealed record SyncRunDto(
    int Pushed, int Pulled, bool DryRun, bool FirstSync, IReadOnlyList<SyncConflictDto> Conflicts, IReadOnlyList<BackupItemDto> Items, IReadOnlyList<string> Errors);

/// <summary>Body of <c>POST /api/sync/backup</c>: uploads a whole-installation backup to the sync target (<c>passphrase</c> falls back to the sync one).</summary>
public sealed record RemoteBackupInput(List<string>? Sections, bool? IncludeKeys, bool? IncludeUsage, string? Passphrase);

/// <summary>The name a manual remote backup was stored under.</summary>
public sealed record RemoteBackupDto(string Name);

/// <summary>Answer of <c>POST /api/sync/test</c> (a failure is an error response instead).</summary>
public sealed record SyncTestDto(bool Ok);
