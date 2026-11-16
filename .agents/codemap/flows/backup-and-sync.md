# Backup, restore and sync

Rules and linked changes: AGENTS.md linked update 38. Decisions: memory `backup-and-sync`.

```
Astra.Data/Backup
  BackupCatalog   table classes, TableSpec list (what a document is), settings-key and app-field rules
  BackupStore     rows <-> BackupDoc (secrets in $secrets), ApplyAsync = one transaction
Astra.Server/Backup
  ConfigDocs      all documents: BackupStore + clients (ClientService.CaptureProfile/ApplyProfile)
                  + Skills (LibraryService.Engine) + plugin-auth.json; reloads settings services
  Bundle          documents grouped by section, + sync's ts / device / tombstones
  BackupEnvelope  seal / open (astra-backup, PBKDF2 600k + AES-GCM, AAD = header)
  BackupService   Collect, Seal, PlanAsync (preview + the changes)      RestoreService: safety copy, then apply
  AutoBackupService / AutoBackupWorker   backups/auto, daily + on update, prune
  SyncMerger      pure three-way per-document merge     SyncService / SyncWorker   target I/O, retries, state
  SyncTargets     FolderTarget, WebDavTarget, S3Target (+ S3Signer), GitHubTarget
  BackupEndpoints /api/backup/*, /api/sync/*  (local-origin only except status / info / keyless export)
```

Flow of a sync: read local documents (`ForSync`: no machine-local fields, the default token's key stays) -> remember when each local change was first seen -> GET the sealed
file (ETag / sha) -> `SyncMerger.Merge` (baseline = last agreed hash per document) -> conditional PUT when something is to push (retry on a lost race) -> apply the pulled changes
through `ConfigDocs.ApplyAsync` -> save the baseline from what was *actually* read back. Entry points for a new section: `BackupSections`, a `TableSpec`, `BackupSettingsKeys` / `SettingsKeys`.
