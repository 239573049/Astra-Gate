using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Backup;

/// <summary>
/// The rules of Settings › Backup &amp; sync that need no UI (web <c>components/backup/shared.tsx</c> and <c>SyncPanel</c>, linked update 38): passphrase
/// length, what a sync target needs per kind, the request bodies, and the texts of a plan, a restore and the sync status.
/// </summary>
public static class BackupLogic
{
    /// <summary>The shortest passphrase the server accepts.</summary>
    public const int MinPassphrase = 8;

    /// <summary>The sync interval used when none is typed.</summary>
    public const int DefaultIntervalMinutes = 15;

    /// <summary>The sync target kinds, in menu order.</summary>
    public static readonly IReadOnlyList<string> Kinds = ["folder", "webdav", "s3", "github"];

    /// <summary>Whether a typed passphrase is non-empty yet too short.</summary>
    public static bool PassphraseTooShort(string? pass) => !string.IsNullOrEmpty(pass) && pass.Length < MinPassphrase;

    /// <summary>A file with keys needs a passphrase; one without may have none, but a typed passphrase must be long enough.</summary>
    public static bool CanExport(bool includeKeys, string? pass)
        => string.IsNullOrEmpty(pass) ? !includeKeys : pass.Length >= MinPassphrase;

    /// <summary>The body of an export: no passphrase means a keyless, plain file.</summary>
    public static BackupExportInput ExportInput(bool includeKeys, string? pass)
        => new(null, includeKeys && !string.IsNullOrEmpty(pass), null, Blank(pass));

    /// <summary>A restore source that is the picked file's text.</summary>
    public static BackupSourceInput FileSource(string content, string? pass, string mode)
        => new("file", content, null, Blank(pass), null, mode);

    /// <summary>A restore source that is a file of the local automatic backup folder.</summary>
    public static BackupSourceInput AutoSource(string name, string? pass, string mode)
        => new("auto", null, name, Blank(pass), null, mode);

    /// <summary>A restore source that reads the latest backup from the configured sync target (WebDAV / S3 / GitHub / folder).</summary>
    public static BackupSourceInput RemoteSource(string? pass, string mode)
        => new("remote", null, null, Blank(pass), null, mode);

    /// <summary>The key of the first field the target still needs for <paramref name="kind"/> (the placeholder the form shows), or null when complete.</summary>
    public static string? MissingTargetField(string kind, string? url, string? folder, string? bucket) => kind switch
    {
        "folder" => Blank(folder?.Trim()) is null ? "sync.folder.folder" : null,
        "webdav" => Blank(url?.Trim()) is null ? "sync.url.webdav" : null,
        "s3" => Blank(bucket?.Trim()) is null ? "sync.bucket" : null,
        "github" => Blank(url?.Trim()) is null ? "sync.url.github" : null,
        _ => "sync.kind",
    };

    /// <summary>The target as the server takes it: only the fields the kind uses (an empty secret keeps the stored one).</summary>
    public static SyncTargetInput Target(string kind, string url, string username, string secret, string folder, string bucket, string region, bool pathStyle, string branch)
    {
        var remote = kind is "webdav" or "s3" or "github";
        return new SyncTargetInput(
            kind,
            remote ? url.Trim() : null,
            kind is "webdav" or "s3" ? username.Trim() : null,
            secret.Length == 0 ? null : secret,
            folder.Trim(),
            kind == "s3" ? bucket.Trim() : null,
            kind == "s3" ? region.Trim() : null,
            kind == "s3" ? pathStyle : null,
            kind == "github" ? branch.Trim() : null);
    }

    /// <summary>The sync config body (the section list is left to the server's default: every section but the usage log).</summary>
    public static SyncConfigInput SyncConfig(SyncTargetInput target, string pass, int interval, bool includeKeys, bool includeSignins)
        => new(target, Blank(pass), Math.Max(1, interval), null, includeKeys, includeSignins, null);

    /// <summary>The interval a typed number means: at least 1, the default for anything unreadable.</summary>
    public static int Interval(decimal? typed) => typed is { } v && v >= 1 ? (int)v : DefaultIntervalMinutes;

    /// <summary>Saving is allowed with a long-enough new passphrase, or with none when one is already stored.</summary>
    public static bool CanSaveSync(string pass, bool hasStoredPassphrase) => pass.Length == 0 ? hasStoredPassphrase : pass.Length >= MinPassphrase;

    /// <summary>The label of a section id (the id itself when the catalog does not know it).</summary>
    public static string SectionLabel(string id) => Loc.Has("backup.all.section." + id) ? Loc.T("backup.all.section." + id) : id;

    /// <summary>"Providers: added 1 · changed 0 · removed 0 · unchanged 4".</summary>
    public static string SectionLine(BackupSectionPlanDto s) => SectionLabel(s.Section) + ": " + Loc.T("backup.all.counts",
        ("added", s.Added), ("changed", s.Changed), ("removed", s.Removed), ("unchanged", s.Unchanged));

    /// <summary>The preview's lines: when and what the file is, the sections that would change, the server's notes.</summary>
    public static List<string> PlanLines(BackupPlanDto plan)
    {
        var lines = new List<string>
        {
            Loc.T("backup.all.restore.created", ("time", plan.CreatedAt), ("version", plan.AstraVersion)) + " · "
                + Loc.T(plan.HasKeys ? "backup.all.restore.hasKeys" : "backup.all.restore.noKeys"),
        };
        if (plan.Legacy) lines.Add(Loc.T("backup.all.restore.legacy"));
        lines.AddRange(plan.Sections.Where(s => s.Added + s.Changed + s.Removed > 0).Select(SectionLine));
        lines.AddRange(plan.Notes);
        return lines;
    }

    /// <summary>The result of a restore: what was written and removed, the safety copy, and how many were skipped or failed.</summary>
    public static List<string> RestoreLines(BackupRestoreDto r)
    {
        var lines = new List<string> { Loc.T("backup.all.restore.done", ("written", r.Written), ("removed", r.Removed)) };
        if (!string.IsNullOrEmpty(r.SafetyBackup)) lines.Add(Loc.T("backup.all.restore.safety", ("name", r.SafetyBackup)));
        if (r.Skipped.Count > 0) lines.Add(Loc.T("backup.all.restore.skipped", ("count", r.Skipped.Count)));
        if (r.Errors.Count > 0) lines.Add(Loc.T("backup.all.restore.errors", ("count", r.Errors.Count)));
        return lines;
    }

    /// <summary>The toast of a sync run (a dry run says what it would do).</summary>
    public static string RunText(SyncRunDto run)
        => Loc.T(run.DryRun ? "sync.run.preview" : "sync.run.done", ("pushed", run.Pushed), ("pulled", run.Pulled)) + (run.FirstSync ? " " + Loc.T("sync.run.first") : "");

    /// <summary>"Last sync: …" with the never-synced fallback.</summary>
    public static string LastSyncText(SyncStatusDto s)
        => Loc.T("sync.status.last") + ": " + (s.Running ? Loc.T("sync.status.running") : s.LastSyncAt is { Length: > 0 } at ? FormatTime(at) : Loc.T("sync.status.never"));

    /// <summary>The sync counters line.</summary>
    public static string CountsText(SyncStatusDto s) => Loc.T("sync.status.counts", ("pushed", s.LastPushed), ("pulled", s.LastPulled), ("tracked", s.Tracked));

    /// <summary>One conflict: the document and which side was kept.</summary>
    public static string ConflictLine(SyncConflictDto c) => $"{SectionLabel(c.Section)} · {c.Label} · {Loc.T("sync.winner." + c.Winner)}";

    /// <summary>A timestamp in the viewer's local time (the text itself when it is not a date).</summary>
    public static string FormatTime(string? iso) => DateTimeOffset.TryParse(iso, out var t) ? t.ToLocalTime().ToString("g") : iso ?? "";

    /// <summary>A file size as B / KB / MB.</summary>
    public static string FormatSize(long n) => n < 1024 ? $"{n} B" : n < 1024 * 1024 ? $"{n / 1024.0:0.0} KB" : $"{n / 1024.0 / 1024.0:0.0} MB";

    private static string? Blank(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
