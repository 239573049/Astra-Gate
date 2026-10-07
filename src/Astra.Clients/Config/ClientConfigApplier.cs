using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Clients.Editing;
using Astra.Core;
using Astra.Core.Clients;

namespace Astra.Clients.Config;

/// <summary>
/// Executes <see cref="ConfigChangePlan"/>s against real files: first-write and rolling backups,
/// state tracking (original vs applied per key), atomic writes and drift-aware restores.
/// </summary>
public sealed class ClientConfigApplier
{
    private const int MaxRollingBackups = 20;

    private readonly IClientConfigStateStore _store;
    private readonly ClientEnvironment _env;
    private readonly string _backupsRoot;

    /// <param name="store">Persistence for per-key original/applied values.</param>
    /// <param name="env">Environment (file IO, home dir).</param>
    /// <param name="clientBackupsDir">The ClientBackupsDir of <see cref="Astra.AstraPaths"/> (~/.astra/backups/client-configs).</param>
    public ClientConfigApplier(IClientConfigStateStore store, ClientEnvironment env, string clientBackupsDir)
    {
        _store = store;
        _env = env;
        _backupsRoot = clientBackupsDir;
    }

    // ---------------------------------------------------------------- apply (enable / key rotation)

    /// <summary>Simulates a combined set of changes without writing files, backups or state.</summary>
    public ConfigChangePlan Preview(string clientKind, IReadOnlyList<ConfigChange> changes)
    {
        var diffs = new List<FileDiff>();
        foreach (var group in changes.GroupBy(c => c.File))
        {
            var before = _env.ReadTextOrNull(group.Key) ?? "";
            var ops = ConfigEditorOps.Create(group.First().Format, before);
            var warnings = new List<string>();
            foreach (var change in group) ops.Apply(change, warnings);
            if (ops.Text != before) diffs.Add(new FileDiff(group.Key, DiffTool.Unified(group.Key, before, ops.Text)));
        }
        return new ConfigChangePlan(clientKind, changes, diffs);
    }

    /// <summary>Applies every change in the plan, taking first-write and rolling backups and recording state.</summary>
    public ApplyReport Apply(ConfigChangePlan plan)
    {
        var applied = new List<AppliedKey>();
        var warnings = new List<string>();
        string? rollingDir = null;

        foreach (var group in plan.ByFile)
        {
            var path = group.Key;
            var format = group.First().Format;
            var existed = _env.FileExists(path);
            var (text, bom) = _env.ReadTextWithBom(path) ?? ("", false);
            EnsureFirstWriteBackup(plan.ClientKind, path, existed);

            var ops = ConfigEditorOps.Create(format, text);
            foreach (var change in group)
            {
                var before = ops.Read(change);
                ops.Apply(change, warnings);
                var after = ops.Read(change);
                applied.Add(new AppliedKey(path, change.KeyPath, before is null,
                    ConfigValueCodec.Encode(format, before), ConfigValueCodec.Encode(format, after)));
                UpsertState(plan.ClientKind, change, before, after);
            }

            rollingDir ??= CreateRollingBackupDir(plan.ClientKind);
            AddRollingBackup(plan.ClientKind, rollingDir, path, existed);
            _env.WriteTextAtomic(path, ops.Text, bom);
        }

        return new ApplyReport { ClientKind = plan.ClientKind, Applied = applied, Warnings = warnings };
    }

    // ---------------------------------------------------------------- restore (disable / force / restore-all)

    /// <summary>
    /// Restores every key in the plan: when the current value still equals what Astra wrote, the
    /// original value (or absence) is restored; drifted keys are skipped and reported.
    /// </summary>
    public RestoreReport Disable(ConfigChangePlan plan) => Restore(plan, force: false);

    /// <summary>Like <see cref="Disable"/>, but restores even drifted keys.</summary>
    public RestoreReport ForceRestore(ConfigChangePlan plan) => Restore(plan, force: true);

    /// <summary>
    /// Offline restore for the "restore-all [--purge]" subcommand: disables every listed client
    /// (with purge plans when <paramref name="purge"/> is set).
    /// </summary>
    public IReadOnlyList<RestoreReport> RestoreAll(IReadOnlyList<IClientAdapter> adapters, bool purge)
    {
        var reports = new List<RestoreReport>();
        foreach (var adapter in adapters)
        {
            var plan = purge ? adapter.PlanPurge() : adapter.PlanDisable();
            reports.Add(Disable(plan));
        }
        return reports;
    }

    private RestoreReport Restore(ConfigChangePlan plan, bool force)
    {
        var keys = new List<RestoredKey>();
        var warnings = new List<string>();
        var deletedFiles = new List<string>();

        foreach (var group in plan.ByFile)
        {
            var path = group.Key;
            var format = group.First().Format;
            var firstWriteEntry = ReadFirstWriteEntry(plan.ClientKind, path);
            var existedOriginally = firstWriteEntry?.Existed ?? _env.FileExists(path);

            var (text, bom) = _env.ReadTextWithBom(path) ?? ("", false);
            var ops = ConfigEditorOps.Create(format, text);
            var fileChanged = false;
            var fileHasDrift = false;
            OriginalJsonDocument? originalDocument = null;

            foreach (var change in group)
            {
                var state = _store.Get(plan.ClientKind, path, change.KeyPath);
                if (state is null)
                {
                    keys.Add(new RestoredKey(path, change.KeyPath, RestoreKeyOutcome.NotTracked));
                    continue;
                }

                if (!_env.FileExists(path))
                {
                    if (state.OriginalAbsent)
                    {
                        _store.Delete(plan.ClientKind, path, change.KeyPath);
                        keys.Add(new RestoredKey(path, change.KeyPath, RestoreKeyOutcome.Restored, "file no longer exists"));
                    }
                    else
                    {
                        fileHasDrift = true;
                        keys.Add(new RestoredKey(path, change.KeyPath, RestoreKeyOutcome.Drifted, "file no longer exists"));
                    }
                    continue;
                }

                var current = ops.Read(change);
                var drifted = change.Kind == ConfigChangeKind.Table
                    ? !string.Equals(current, "true", StringComparison.Ordinal)
                    : !ConfigValueCodec.EqualsValue(format, current, state.AppliedValueJson);

                if (drifted && !force)
                {
                    fileHasDrift = true;
                    keys.Add(new RestoredKey(path, change.KeyPath, RestoreKeyOutcome.Drifted,
                        "current value differs from what Astra wrote"));
                    continue;
                }

                if (state.OriginalAbsent)
                {
                    ops.Remove(change);
                    if (format == ConfigFileFormat.Json)
                        PruneEmptyParents(ops, change, originalDocument ??= OriginalJson(plan.ClientKind, path, firstWriteEntry));
                }
                else if (change.Kind != ConfigChangeKind.Table)
                {
                    var originalSource = ConfigValueCodec.DecodeToSource(format, state.OriginalValueJson);
                    if (originalSource is null)
                    {
                        warnings.Add($"State entry for '{change.KeyPath}' has no original value; skipped.");
                        continue;
                    }
                    ops.RestoreSource(change, originalSource);
                }
                _store.Delete(plan.ClientKind, path, change.KeyPath);
                keys.Add(new RestoredKey(path, change.KeyPath, RestoreKeyOutcome.Restored));
                fileChanged = true;
            }

            if (fileChanged || ops.Text != text)
            {
                // A file we created ourselves that no longer holds any key/value content is deleted.
                if (!existedOriginally && HasNoUserContent(format, ops.Text))
                {
                    _env.DeleteFile(path);
                    deletedFiles.Add(path);
                    continue;
                }
                _env.WriteTextAtomic(path, ops.Text, bom);
            }

            if (!fileHasDrift && fileChanged && existedOriginally)
                ReconcileToOriginal(plan.ClientKind, path, format);
        }

        return new RestoreReport { ClientKind = plan.ClientKind, Keys = keys, DeletedFiles = deletedFiles, Warnings = warnings };
    }

    /// <summary>The JSON document as it was before Astra's first write; <see cref="Known"/> is false when that cannot be told.</summary>
    private sealed record OriginalJsonDocument(bool Known, JsonNode? Root);

    private OriginalJsonDocument OriginalJson(string clientKind, string path, BackupFileEntry? firstWrite)
    {
        if (firstWrite is null) return new OriginalJsonDocument(false, null);
        if (!firstWrite.Existed) return new OriginalJsonDocument(true, new JsonObject()); // we created the file
        if (firstWrite.BackupFile is null) return new OriginalJsonDocument(false, null);
        var bytes = _env.ReadAllBytesOrNull(FirstWriteFile(clientKind, firstWrite.BackupFile));
        if (bytes is null) return new OriginalJsonDocument(false, null);
        try
        {
            return new OriginalJsonDocument(true, ParseTolerantJson(Encoding.UTF8.GetString(StripBom(bytes))));
        }
        catch (JsonException)
        {
            return new OriginalJsonDocument(false, null);
        }
    }

    /// <summary>
    /// After removing a key Astra added, also removes the objects Astra had to create to hold it (e.g. "provider" for
    /// "provider.astra") once they are empty — but only when they did not exist before Astra's first write.
    /// </summary>
    private static void PruneEmptyParents(ConfigEditorOps ops, ConfigChange change, OriginalJsonDocument original)
    {
        if (!original.Known) return;
        var segments = change.KeyPath.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var depth = segments.Length - 1; depth >= 1; depth--)
        {
            var parent = new ConfigChange(change.File, change.Format, string.Join('.', segments[..depth]), null, null);
            if (ops.Read(parent) is not { } text || JsonNode.Parse(text) is not JsonObject { Count: 0 }) return;
            if (ExistsIn(original.Root, segments[..depth])) return;
            ops.Remove(parent);
        }
    }

    private static bool ExistsIn(JsonNode? root, string[] segments)
    {
        var node = root;
        foreach (var segment in segments)
        {
            if (node is not JsonObject o || !o.TryGetPropertyValue(segment, out node)) return false;
        }
        return true;
    }

    /// <summary>
    /// After a clean restore the file is semantically what it was, but our splices can leave cosmetic
    /// differences (a comma kept before '}', indentation of a re-inserted block, trailing newlines).
    /// When the current content still parses to the same document as the original, restore the original
    /// bytes wholesale — byte-identical output for untouched user keys.
    /// </summary>
    private void ReconcileToOriginal(string clientKind, string path, ConfigFileFormat format)
    {
        var entry = ReadFirstWriteEntry(clientKind, path);
        if (entry?.BackupFile is null) return;
        var originalBytes = _env.ReadAllBytesOrNull(FirstWriteFile(clientKind, entry.BackupFile));
        var currentBytes = _env.ReadAllBytesOrNull(path);
        if (originalBytes is null || currentBytes is null) return;
        var originalText = Encoding.UTF8.GetString(StripBom(originalBytes));
        var currentText = Encoding.UTF8.GetString(StripBom(currentBytes));
        if (string.Equals(currentText, originalText, StringComparison.Ordinal)) return;
        if (SemanticallyEqual(format, currentText, originalText)
            || OnlyTrailingNewlinesDiffer(currentText, originalText))
        {
            _env.WriteAllBytes(path, originalBytes);
        }
    }

    /// <summary>True when both texts parse to the same document (format-aware).</summary>
    private static bool SemanticallyEqual(ConfigFileFormat format, string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        try
        {
            return format switch
            {
                ConfigFileFormat.Json => JsonNode.DeepEquals(ParseTolerantJson(a), ParseTolerantJson(b)),
                ConfigFileFormat.Toml => TomlCanonical(a) == TomlCanonical(b),
                _ => false,
            };
        }
        catch (Exception e) when (e is JsonException or Tomlyn.TomlException or EditorException)
        {
            return false;
        }
    }

    private static JsonNode? ParseTolerantJson(string text) => JsonNode.Parse(text, nodeOptions: new JsonNodeOptions(),
        documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

    private static string TomlCanonical(string text)
    {
        var table = Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(text, Tomlyn.TomlSerializerOptions.Default)
            ?? throw new EditorException("TOML document deserialized to null.");
        return Tomlyn.TomlSerializer.Serialize(table, Tomlyn.TomlSerializerOptions.Default);
    }

    private static bool OnlyTrailingNewlinesDiffer(string current, string original) =>
        string.Equals(current.TrimEnd('\r', '\n'), original.TrimEnd('\r', '\n'), StringComparison.Ordinal);

    /// <summary>
    /// True when the text has no key/value content left — only empty scaffolding such as
    /// <c>[models]</c> table headers or nested empty JSON objects.
    /// </summary>
    private static bool HasNoUserContent(ConfigFileFormat format, string text)
    {
        if (TextTool.IsBlank(text)) return true;
        try
        {
            return format switch
            {
                ConfigFileFormat.Toml => TomlHasNoPairs(text),
                ConfigFileFormat.Json => JsonIsEmpty(ParseTolerantJson(text)),
                _ => false,
            };
        }
        catch (Exception e) when (e is JsonException or Tomlyn.TomlException or EditorException)
        {
            return false;
        }
    }

    private static bool TomlHasNoPairs(string text)
    {
        var doc = Tomlyn.Parsing.SyntaxParser.Parse(text);
        if (doc.HasErrors) throw new EditorException("Invalid TOML document.");
        if (doc.KeyValues.ChildrenCount > 0) return false;
        foreach (var table in doc.Tables)
            if (table.Items.ChildrenCount > 0)
                return false;
        return true;
    }

    private static bool JsonIsEmpty(JsonNode? node) => node switch
    {
        JsonObject o => o.Count == 0 || o.All(p => JsonIsEmpty(p.Value)),
        JsonArray a => a.Count == 0,
        null => true,
        _ => false,
    };

    private static byte[] StripBom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? bytes[3..] : bytes;

    // ---------------------------------------------------------------- backups

    /// <summary>Lists the backups of one client, oldest first ("first-write" plus up to 20 rolling backups).</summary>
    public IReadOnlyList<BackupInfo> ListBackups(string clientKind)
    {
        var result = new List<BackupInfo>();
        var clientDir = Path.Combine(_backupsRoot, clientKind);
        if (!_env.DirectoryExists(clientDir)) return result;
        foreach (var dir in Directory.GetDirectories(clientDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var manifest = ReadManifest(dir);
            if (manifest is null) continue;
            var id = Path.GetFileName(dir);
            result.Add(new BackupInfo(clientKind, id, manifest.CreatedAt, id == "first-write", manifest.Entries.ToList()));
        }
        return result;
    }

    /// <summary>Restores every file of one backup to its original location (deleting files that did not exist).</summary>
    public void RestoreFromBackup(string clientKind, string backupId)
    {
        var dir = Path.Combine(_backupsRoot, clientKind, backupId);
        var manifest = ReadManifest(dir) ?? throw new FileNotFoundException($"Backup '{clientKind}/{backupId}' not found.");
        foreach (var e in manifest.Entries)
        {
            if (!e.Existed || e.BackupFile is null)
            {
                _env.DeleteFile(e.OriginalPath);
                continue;
            }
            var bytes = _env.ReadAllBytesOrNull(Path.Combine(dir, e.BackupFile));
            if (bytes is null)
            {
                _env.DeleteFile(e.OriginalPath);
                continue;
            }
            _env.WriteAllBytes(e.OriginalPath, bytes);
        }
    }

    private BackupFileEntry? ReadFirstWriteEntry(string clientKind, string path) =>
        ReadManifest(FirstWriteDir(clientKind))?.Entries
            .FirstOrDefault(e => string.Equals(e.OriginalPath, path, StringComparison.Ordinal));

    private string FirstWriteDir(string clientKind) => Path.Combine(_backupsRoot, clientKind, "first-write");

    private string FirstWriteFile(string clientKind, string file) => Path.Combine(FirstWriteDir(clientKind), file);

    private BackupManifest? ReadManifest(string dir)
    {
        var path = Path.Combine(dir, "manifest.json");
        if (!_env.FileExists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<BackupManifest>(_env.ReadTextOrNull(path) ?? "", Json.Storage);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Keeps, forever, one full copy of each file before Astra's very first write to it.</summary>
    private void EnsureFirstWriteBackup(string clientKind, string path, bool existed)
    {
        var dir = FirstWriteDir(clientKind);
        var manifest = ReadManifest(dir) ?? new BackupManifest { CreatedAt = DateTimeOffset.UtcNow, Entries = [] };
        if (manifest.Entries.Any(e => string.Equals(e.OriginalPath, path, StringComparison.Ordinal))) return;

        string? backupFile = null;
        if (existed)
        {
            var bytes = _env.ReadAllBytesOrNull(path) ?? [];
            backupFile = UniqueFileName(dir, SanitizeFileName(Path.GetFileName(path)), bytes);
        }
        manifest.Entries.Add(new BackupFileEntry(path, existed, backupFile));
        WriteManifest(dir, manifest);
    }

    private string CreateRollingBackupDir(string clientKind)
    {
        var clientDir = Path.Combine(_backupsRoot, clientKind);
        Directory.CreateDirectory(clientDir);
        var baseName = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var dir = Path.Combine(clientDir, baseName);
        for (var i = 0; Directory.Exists(dir); i++) dir = Path.Combine(clientDir, $"{baseName}-{i}");
        Directory.CreateDirectory(dir);
        PruneRollingBackups(clientDir);
        return dir;
    }

    private void AddRollingBackup(string clientKind, string dir, string path, bool existed)
    {
        var manifest = ReadManifest(dir) ?? new BackupManifest { CreatedAt = DateTimeOffset.UtcNow, Entries = [] };
        string? backupFile = null;
        if (existed)
        {
            var bytes = _env.ReadAllBytesOrNull(path) ?? [];
            backupFile = UniqueFileName(dir, SanitizeFileName(Path.GetFileName(path)), bytes);
        }
        manifest.Entries.Add(new BackupFileEntry(path, existed, backupFile));
        WriteManifest(dir, manifest);
    }

    private void PruneRollingBackups(string clientDir)
    {
        var dirs = Directory.GetDirectories(clientDir)
            .Where(d => Path.GetFileName(d) != "first-write")
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)
            .ToList();
        for (var i = MaxRollingBackups; i < dirs.Count; i++)
        {
            try { Directory.Delete(dirs[i], recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    private void WriteManifest(string dir, BackupManifest manifest)
    {
        Directory.CreateDirectory(dir);
        _env.WriteTextAtomic(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, Json.Storage), bom: false);
    }

    /// <summary>Writes the backup copy and returns the unique file name used.</summary>
    private string UniqueFileName(string dir, string fileName, byte[] bytes)
    {
        var candidate = fileName;
        var i = 0;
        while (File.Exists(Path.Combine(dir, candidate)))
        {
            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            candidate = $"{stem}_{++i}{ext}";
        }
        _env.WriteAllBytes(Path.Combine(dir, candidate), bytes);
        return candidate;
    }

    private static string SanitizeFileName(string name) =>
        new(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').ToArray());

    private void UpsertState(string clientKind, ConfigChange change, string? before, string? after)
    {
        var entry = _store.Get(clientKind, change.File, change.KeyPath);
        var isNew = entry is null;
        entry ??= new ClientConfigStateEntry
        {
            ClientKind = clientKind,
            FilePath = change.File,
            KeyPath = change.KeyPath,
        };
        if (isNew)
        {
            entry.OriginalAbsent = before is null;
            entry.OriginalValueJson = ConfigValueCodec.Encode(change.Format, before);
        }
        entry.AppliedValueJson = ConfigValueCodec.Encode(change.Format, after);
        entry.AppliedAt = DateTimeOffset.UtcNow;
        _store.Upsert(entry);
    }
}

/// <summary>manifest.json of a backup directory.</summary>
public sealed class BackupManifest
{
    public DateTimeOffset CreatedAt { get; set; }
    public List<BackupFileEntry> Entries { get; set; } = [];
}
