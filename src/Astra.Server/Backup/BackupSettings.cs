using System.Text.Json.Serialization;
using Astra.Data.Backup;

namespace Astra.Server.Backup;

/// <summary>The schedule of local automatic backups, under the settings key <see cref="BackupSettingsKeys.Auto"/> (this machine's own: never exported or synced).</summary>
public sealed class AutoBackupSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>A backup is due this many hours after the newest automatic one.</summary>
    public int Hours { get; set; } = 24;

    /// <summary>How many files of each kind (auto, restore, update) are kept.</summary>
    public int Keep { get; set; } = 10;

    /// <summary>Set: the files are encrypted and hold the keys. Empty: plain files without any secret. Sealed by <see cref="Astra.Core.Clients.ISecretProtector"/>; never returned.</summary>
    public string? PassphraseProtected { get; set; }

    /// <summary>The Astra version the last start saw: a different one means an update happened, which earns a backup of its own.</summary>
    public string? LastVersion { get; set; }
}

/// <summary>Where sync keeps its file. <see cref="Kind"/> is one of <see cref="SyncTargetKinds"/>.</summary>
public sealed class SyncTargetConfig
{
    public string Kind { get; set; } = SyncTargetKinds.Folder;

    /// <summary>folder: the directory; webdav: the server address; s3: the endpoint (empty = AWS); github: <c>owner/repo</c>.</summary>
    public string Url { get; set; } = "";

    /// <summary>webdav: user; s3: access key id.</summary>
    public string Username { get; set; } = "";

    /// <summary>The password / S3 secret key / GitHub token, sealed by the secret protector. Never returned.</summary>
    public string? SecretProtected { get; set; }

    /// <summary>webdav: a folder under the address; s3: the key prefix; github: the folder in the repository.</summary>
    public string Folder { get; set; } = "astra";

    public string Bucket { get; set; } = "";
    public string Region { get; set; } = "";
    public bool PathStyle { get; set; }
    public string Branch { get; set; } = "";
}

public static class SyncTargetKinds
{
    public const string Folder = "folder";
    public const string WebDav = "webdav";
    public const string S3 = "s3";
    public const string GitHub = "github";
    public static readonly IReadOnlyList<string> All = [Folder, WebDav, S3, GitHub];
}

/// <summary>Multi-machine sync's configuration, under <see cref="BackupSettingsKeys.Sync"/> (machine-local).</summary>
public sealed class SyncConfig
{
    public bool Enabled { get; set; }
    public SyncTargetConfig Target { get; set; } = new();
    public int IntervalMinutes { get; set; } = 3;

    /// <summary>The sections that sync; empty = <see cref="BackupSections.Default"/> without sign-ins and usage.</summary>
    public List<string> Sections { get; set; } = [];

    /// <summary>Provider keys, tokens and other secrets travel (always inside the encrypted file).</summary>
    public bool IncludeKeys { get; set; } = true;

    /// <summary>Plugin / provider sign-ins (refresh tokens) travel too. Off by default: a token two machines refresh in turn gets revoked.</summary>
    public bool IncludeSignins { get; set; }

    /// <summary>The sync passphrase (the same on every machine), sealed. Lost passphrase = unreadable remote file.</summary>
    public string? PassphraseProtected { get; set; }
}

/// <summary>What the last sync saw of one document: its fingerprint and when it last changed. The common ancestor of the next three-way merge.</summary>
public sealed class SyncBaseline
{
    public string Hash { get; set; } = "";
    public long Ts { get; set; }
    public bool Deleted { get; set; }
}

public sealed class SyncConflict
{
    public string Section { get; set; } = "";
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";

    /// <summary>Which side was kept: <c>local</c> or <c>remote</c> (the other was overwritten; the later change wins).</summary>
    public string Winner { get; set; } = "";
    public string At { get; set; } = "";
    public string Device { get; set; } = "";
}

/// <summary>Sync's bookkeeping, under <see cref="BackupSettingsKeys.SyncState"/> (machine-local).</summary>
public sealed class SyncState
{
    public string DeviceId { get; set; } = "";
    public Dictionary<string, SyncBaseline> Baseline { get; set; } = [];

    /// <summary>The remote file's version (ETag / sha) this machine last read or wrote.</summary>
    public string? RemoteVersion { get; set; }

    public string? LastSyncAt { get; set; }

    /// <summary>The start of the last run, successful or not: the interval counts from it.</summary>
    public string? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
    public int LastPushed { get; set; }
    public int LastPulled { get; set; }
    public List<SyncConflict> Conflicts { get; set; } = [];

    /// <summary>When a local change to a document was first seen (Unix ms), kept until it is synced: the "when" of a last-writer-wins decision after being offline.</summary>
    public Dictionary<string, long> Pending { get; set; } = [];

    /// <summary>Tombstones are forgotten after this many days.</summary>
    [JsonIgnore]
    public static int TombstoneDays => 90;
}
