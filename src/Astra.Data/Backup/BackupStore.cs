using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Microsoft.Data.Sqlite;

namespace Astra.Data.Backup;

/// <summary>
/// One configuration document of a backup: a row of a spec table (with its child rows inside) or one <c>settings</c> value.
/// <see cref="Id"/> is <c>kind/key</c> (<c>providers/01J…</c>, <c>setting/smart_routing</c>) and is unique within a section.
/// Secrets travel decrypted in <c>$secrets</c> and only inside an encrypted bundle (see <see cref="BackupStore"/>).
/// </summary>
public sealed class BackupDoc
{
    public const string SecretsField = "$secrets";

    public string Section { get; set; } = "";
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public JsonObject Data { get; set; } = new();

    public string Kind => Id.Split('/', 2)[0];
    public string Key => Id.Contains('/') ? Id[(Id.IndexOf('/') + 1)..] : Id;

    /// <summary>A stable fingerprint of the content (key order does not matter). <paramref name="ignoreSecrets"/> leaves <c>$secrets</c> out.</summary>
    public string Hash(bool ignoreSecrets = false)
    {
        var sb = new StringBuilder();
        Canonical(Data, sb, ignoreSecrets, top: true);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    private static void Canonical(JsonNode? node, StringBuilder sb, bool ignoreSecrets, bool top)
    {
        switch (node)
        {
            case null: sb.Append("null"); break;
            case JsonObject o:
                sb.Append('{');
                var first = true;
                foreach (var name in o.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal))
                {
                    if (top && ignoreSecrets && name == SecretsField) continue;
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(JsonValue.Create(name)!.ToJsonString()).Append(':');
                    Canonical(o[name], sb, ignoreSecrets, top: false);
                }
                sb.Append('}');
                break;
            case JsonArray a:
                sb.Append('[');
                for (var i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Canonical(a[i], sb, ignoreSecrets, top: false);
                }
                sb.Append(']');
                break;
            default: sb.Append(node.ToJsonString()); break;
        }
    }

    public BackupDoc Clone() => new() { Section = Section, Id = Id, Label = Label, Data = (JsonObject)Data.DeepClone() };
}

/// <summary>What to read: with the secrets (decrypted into <c>$secrets</c>), and in the shape sync wants.</summary>
/// <param name="Keys">Include secrets and the columns derived from them. Without it a document has no secret at all.</param>
/// <param name="ForSync">Leave out what is one machine's own even though a backup carries it (the default token's key).</param>
public sealed record ReadOptions(bool Keys, bool ForSync = false);

/// <summary>A document to write (or, with <see cref="Data"/> null, to remove).</summary>
public sealed record DocChange(string Section, string Id, JsonObject? Data);

/// <summary>What <see cref="BackupStore.ApplyAsync"/> did.</summary>
public sealed class ApplyReport
{
    public int Written { get; set; }
    public int Removed { get; set; }

    /// <summary>Documents (or child rows) left out, with why: <c>section/id: reason</c>.</summary>
    public List<string> Skipped { get; } = [];

    /// <summary>Settings keys that changed, so their services can reload.</summary>
    public HashSet<string> SettingsKeys { get; } = [];

    /// <summary>Secrets that could not be read from this machine's protector (reported, never fatal).</summary>
    public int UnreadableSecrets { get; set; }
}

/// <summary>
/// Reads the configuration tables as <see cref="BackupDoc"/>s and writes documents back, generically from
/// <see cref="BackupCatalog.Specs"/> (every column but the skipped ones travels). The clients and the library are not
/// here: they go through their engines. Secrets are unprotected on read and protected again on write, so a document moves
/// between machines whose DataProtection keys differ; without <see cref="ReadOptions.Keys"/> no secret is read at all.
/// Ported in spirit from magpie's internal/backup (MIT): a backup is a decrypted bundle sealed as a whole.
/// </summary>
public sealed class BackupStore(SqliteConnectionFactory factory, ISecretProtector secrets)
{
    private const string JsonBytes = "$b64";

    // -------------------------------------------------------------------------------------------- read

    /// <summary>Every document of the given sections that have a spec or are settings (not clients, not library).</summary>
    public async Task<List<BackupDoc>> ReadAsync(IEnumerable<string> sections, ReadOptions options, ReadReport? report = null, CancellationToken ct = default)
    {
        var wanted = sections.ToHashSet();
        var docs = new List<BackupDoc>();
        await using var conn = await factory.OpenUntrackedAsync(ct);
        foreach (var section in BackupSections.All.Where(wanted.Contains))
        {
            foreach (var spec in BackupCatalog.TopLevel(section))
                docs.AddRange(await ReadSpecAsync(conn, spec, options, report, ct));
            docs.AddRange(await ReadSettingsAsync(conn, section, options, report, ct));
        }
        return docs;
    }

    private async Task<List<BackupDoc>> ReadSpecAsync(SqliteConnection conn, TableSpec spec, ReadOptions options, ReadReport? report, CancellationToken ct)
    {
        var rows = await QueryRowsAsync(conn, spec, null, ct);
        var children = BackupCatalog.ChildrenOf(spec).ToList();
        var docs = new List<BackupDoc>();
        foreach (var row in rows)
        {
            if (!ShapeRow(row, spec, options, report, out var doc)) continue;
            foreach (var child in children)
            {
                var parentKey = spec.Key.Select(k => row[k]).ToList();
                var childRows = await QueryRowsAsync(conn, child, (child.ParentColumns!, parentKey), ct);
                var array = new JsonArray();
                foreach (var cr in childRows)
                    if (ShapeRow(cr, child, options, report, out var cd)) array.Add((JsonNode)cd);
                doc[child.Name] = array;
            }
            docs.Add(new BackupDoc { Section = spec.Section, Id = spec.Name + "/" + KeyOf(row, spec), Label = LabelOf(row, spec), Data = doc });
        }
        return docs.OrderBy(d => d.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>The row as a document body: skipped columns dropped, secrets moved to <c>$secrets</c> (or dropped without keys).</summary>
    private bool ShapeRow(Dictionary<string, object?> row, TableSpec spec, ReadOptions options, ReadReport? report, out JsonObject doc)
    {
        doc = new JsonObject();
        if (spec.RequiresSecret && !options.Keys) return false;
        var skip = (spec.Skip ?? []).ToHashSet(StringComparer.Ordinal);
        var secretCols = (spec.Secret ?? []).ToHashSet(StringComparer.Ordinal);
        var derived = (spec.WithSecret ?? []).ToHashSet(StringComparer.Ordinal);
        var sealedBox = new JsonObject();
        var dropSecretsOfThisRow = options.ForSync && spec.Name == "tokens" && row.TryGetValue("is_default", out var d) && d is long and not 0;
        foreach (var (col, value) in row)
        {
            if (skip.Contains(col)) continue;
            if (secretCols.Contains(col))
            {
                if (!options.Keys || dropSecretsOfThisRow || value is not string text || text.Length == 0) continue;
                string? plain = null;
                try { plain = secrets.Unprotect(text); }
                catch (CryptographicException) { report?.Unreadable.Add($"{spec.Table}.{col}"); }
                catch (FormatException) { report?.Unreadable.Add($"{spec.Table}.{col}"); }
                if (plain is not null) sealedBox[col] = plain;
                continue;
            }
            if (derived.Contains(col) && (!options.Keys || dropSecretsOfThisRow)) continue;
            doc[col] = ToJson(value);
        }
        if (sealedBox.Count > 0) doc[BackupDoc.SecretsField] = sealedBox;
        if (spec.RequiresSecret && sealedBox.Count == 0) return false;
        return true;
    }

    private async Task<List<BackupDoc>> ReadSettingsAsync(SqliteConnection conn, string section, ReadOptions options, ReadReport? report, CancellationToken ct)
    {
        var docs = new List<BackupDoc>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT key, value_json FROM settings ORDER BY key";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var key = reader.GetString(0);
            if (BackupCatalog.SectionOfSetting(key) != section) continue;
            JsonNode? value;
            try { value = JsonNode.Parse(reader.GetString(1)); }
            catch (System.Text.Json.JsonException) { report?.Unreadable.Add("settings." + key); continue; }
            if (value is null) continue;
            value = BackupCatalog.StripMachineFields(key, value);
            var box = new JsonObject();
            ExtractSecrets(value, "", box, options.Keys, report);
            var data = new JsonObject { ["value"] = value };
            if (box.Count > 0) data[BackupDoc.SecretsField] = box;
            docs.Add(new BackupDoc { Section = section, Id = "setting/" + key, Label = key, Data = data });
        }
        return docs;
    }

    /// <summary>Takes every <c>*_enc</c> / <c>*_protected</c> string out of the value; with keys it lands, decrypted, in <paramref name="box"/> under its path.</summary>
    private void ExtractSecrets(JsonNode? node, string path, JsonObject box, bool keys, ReadReport? report)
    {
        if (node is JsonObject o)
        {
            foreach (var name in o.Select(p => p.Key).ToList())
            {
                var child = o[name];
                if (BackupCatalog.IsSecretName(name) && child is JsonValue v && v.TryGetValue<string>(out var text))
                {
                    o.Remove(name);
                    if (!keys || text.Length == 0) continue;
                    try { box[path + "/" + name.Replace("~", "~0").Replace("/", "~1")] = secrets.Unprotect(text); }
                    catch (CryptographicException) { report?.Unreadable.Add(path + "/" + name); }
                    catch (FormatException) { report?.Unreadable.Add(path + "/" + name); }
                }
                else ExtractSecrets(child, path + "/" + name.Replace("~", "~0").Replace("/", "~1"), box, keys, report);
            }
        }
        else if (node is JsonArray a)
            for (var i = 0; i < a.Count; i++) ExtractSecrets(a[i], path + "/" + i.ToString(CultureInfo.InvariantCulture), box, keys, report);
    }

    private static string KeyOf(Dictionary<string, object?> row, TableSpec spec) =>
        string.Join('|', spec.Key.Select(k => Convert.ToString(row[k], CultureInfo.InvariantCulture)));

    private static string LabelOf(Dictionary<string, object?> row, TableSpec spec)
    {
        foreach (var col in new[] { "name", "display_name", "slug", "plugin_id", "spec" })
            if (row.TryGetValue(col, out var v) && v is string { Length: > 0 } s) return s;
        return KeyOf(row, spec);
    }

    private static async Task<List<Dictionary<string, object?>>> QueryRowsAsync(SqliteConnection conn, TableSpec spec, (string[] Columns, IReadOnlyList<object?> Values)? parent, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (spec.Where is { } w) where.Add("(" + w + ")");
        if (parent is { } p)
            for (var i = 0; i < p.Columns.Length; i++)
            {
                where.Add($"{p.Columns[i]} = $p{i}");
                cmd.Parameters.AddWithValue($"$p{i}", p.Values[i] ?? DBNull.Value);
            }
        cmd.CommandText = $"SELECT * FROM {spec.Table}" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "") + " ORDER BY " + string.Join(", ", spec.Key);
        var rows = new List<Dictionary<string, object?>>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        // A top-level spec that has a parent's filter (models' Where) applies to the parent only; a child sees all its rows of that parent.
        return rows;
    }

    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create((long)i),
        double d => JsonValue.Create(d),
        byte[] b => new JsonObject { [JsonBytes] = Convert.ToBase64String(b) },
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static object FromJson(JsonNode? node)
    {
        switch (node)
        {
            case null: return DBNull.Value;
            case JsonObject o when o[JsonBytes] is JsonValue b && b.TryGetValue<string>(out var text): return Convert.FromBase64String(text);
            case JsonValue v:
                if (v.TryGetValue<string>(out var s)) return s;
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<double>(out var d)) return d;
                if (v.TryGetValue<bool>(out var bo)) return bo ? 1L : 0L;
                return DBNull.Value;
            default: return node.ToJsonString();
        }
    }

    // -------------------------------------------------------------------------------------------- write

    /// <summary>
    /// Applies the changes in one transaction (all of them or none): upserts in section order, then removals. A document whose
    /// reference points at something that is not here has that reference cleared (nullable) or is skipped (required), and says so in the report.
    /// </summary>
    public async Task<ApplyReport> ApplyAsync(IReadOnlyList<DocChange> changes, CancellationToken ct = default)
    {
        var report = new ApplyReport();
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var schema = new SchemaCache(conn, tx);
        var order = BackupSections.All.ToList();
        var specOrder = BackupCatalog.Specs.ToList();

        IEnumerable<DocChange> Ordered(IEnumerable<DocChange> src) => src
            .OrderBy(c => order.IndexOf(c.Section))
            .ThenBy(c => c.Id.StartsWith("setting/", StringComparison.Ordinal) ? 0 : 1 + specOrder.FindIndex(s => s.Name == c.Id.Split('/', 2)[0]))
            .ThenBy(c => c.Id, StringComparer.Ordinal);

        foreach (var change in Ordered(changes.Where(c => c.Data is not null)))
        {
            ct.ThrowIfCancellationRequested();
            var kind = change.Id.Split('/', 2)[0];
            if (kind == "setting") await UpsertSettingAsync(conn, tx, change, report, ct);
            else if (BackupCatalog.Specs.FirstOrDefault(s => !s.IsChild && s.Name == kind && s.Section == change.Section) is { } spec)
                await UpsertSpecAsync(conn, tx, schema, spec, change, report, ct);
            else report.Skipped.Add($"{change.Section}/{change.Id}: unknown document kind");
        }
        foreach (var change in Ordered(changes.Where(c => c.Data is null)).Reverse())
        {
            ct.ThrowIfCancellationRequested();
            var kind = change.Id.Split('/', 2)[0];
            var key = change.Id.Contains('/') ? change.Id[(change.Id.IndexOf('/') + 1)..] : change.Id;
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            if (kind == "setting")
            {
                cmd.CommandText = "DELETE FROM settings WHERE key = $k";
                cmd.Parameters.AddWithValue("$k", key);
                report.SettingsKeys.Add(key);
            }
            else if (BackupCatalog.Specs.FirstOrDefault(s => !s.IsChild && s.Name == kind && s.Section == change.Section) is { } spec)
            {
                var parts = key.Split('|');
                if (parts.Length != spec.Key.Length) continue;
                cmd.CommandText = $"DELETE FROM {spec.Table} WHERE " + string.Join(" AND ", spec.Key.Select((k, i) => $"{k} = $k{i}")) + (spec.Where is { } w ? $" AND ({w})" : "");
                for (var i = 0; i < parts.Length; i++) cmd.Parameters.AddWithValue($"$k{i}", parts[i]);
            }
            else continue;
            if (await cmd.ExecuteNonQueryAsync(ct) > 0) report.Removed++;
        }
        await tx.CommitAsync(ct);
        return report;
    }

    private async Task UpsertSettingAsync(SqliteConnection conn, SqliteTransaction tx, DocChange change, ApplyReport report, CancellationToken ct)
    {
        var key = change.Id["setting/".Length..];
        var data = change.Data!;
        if (data["value"]?.DeepClone() is not { } value) return;
        JsonNode? local = null;
        await using (var read = conn.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT value_json FROM settings WHERE key = $k";
            read.Parameters.AddWithValue("$k", key);
            if (await read.ExecuteScalarAsync(ct) is string json)
            {
                try { local = JsonNode.Parse(json); } catch (System.Text.Json.JsonException) { }
            }
        }
        // Put back this machine's own: its fields of the app document, and every secret the incoming value brings none of.
        if (key == "app" && value is JsonObject incoming && local is JsonObject mine)
            foreach (var f in BackupCatalog.AppMachineFields)
                if (mine[f] is { } own) incoming[f] = own.DeepClone();
        if (data[BackupDoc.SecretsField] is JsonObject box)
            foreach (var (path, plain) in box)
                if (plain is JsonValue pv && pv.TryGetValue<string>(out var text)) SetAtPath(value, path, secrets.Protect(text));
        if (local is not null) CopyMissingSecrets(local, value);

        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO settings(key, value_json) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value.ToJsonString());
        await cmd.ExecuteNonQueryAsync(ct);
        report.Written++;
        report.SettingsKeys.Add(key);
    }

    private static void SetAtPath(JsonNode root, string path, string value)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Replace("~1", "/").Replace("~0", "~")).ToList();
        var node = root;
        for (var i = 0; i < parts.Count - 1 && node is not null; i++)
            node = node switch
            {
                JsonObject o => o[parts[i]],
                JsonArray a when int.TryParse(parts[i], CultureInfo.InvariantCulture, out var ix) && ix >= 0 && ix < a.Count => a[ix],
                _ => null,
            };
        if (node is JsonObject target && parts.Count > 0) target[parts[^1]] = value;
    }

    /// <summary>Copies this machine's protected values into the incoming value wherever the incoming one has none (a backup without keys keeps the keys here).</summary>
    private static void CopyMissingSecrets(JsonNode local, JsonNode incoming)
    {
        if (local is JsonObject lo && incoming is JsonObject io)
        {
            foreach (var (name, lv) in lo)
            {
                if (lv is null) continue;
                if (BackupCatalog.IsSecretName(name)) { if (!io.ContainsKey(name)) io[name] = lv.DeepClone(); }
                else if (io[name] is { } iv) CopyMissingSecrets(lv, iv);
            }
        }
        else if (local is JsonArray la && incoming is JsonArray ia && la.Count == ia.Count)
            for (var i = 0; i < la.Count; i++)
                if (la[i] is { } l && ia[i] is { } n) CopyMissingSecrets(l, n);
    }

    private async Task UpsertSpecAsync(SqliteConnection conn, SqliteTransaction tx, SchemaCache schema, TableSpec spec, DocChange change, ApplyReport report, CancellationToken ct)
    {
        var data = change.Data!;
        var where = $"{change.Section}/{change.Id}";
        if (!await UpsertRowAsync(conn, tx, schema, spec, data, where, report, ct)) return;
        report.Written++;
        var keyValues = spec.Key.Select(k => FromJson(data[k])).ToList();
        foreach (var child in BackupCatalog.ChildrenOf(spec))
        {
            if (data[child.Name] is not JsonArray rows) continue;
            var kept = new List<List<object>>();
            foreach (var row in rows.OfType<JsonObject>())
            {
                var copy = (JsonObject)row.DeepClone();
                for (var i = 0; i < child.ParentColumns!.Length; i++) copy[child.ParentColumns[i]] = JsonValueOf(keyValues[i]);
                if (await UpsertRowAsync(conn, tx, schema, child, copy, $"{where}#{child.Name}", report, ct))
                    kept.Add(child.Key.Select(k => FromJson(copy[k])).ToList());
            }
            if (child.RequiresSecret) continue; // rows this backup has no secret for are not its to remove
            // Replace semantics inside the document: child rows the document does not list are removed.
            var existing = await QueryRowsAsync(conn, child, (child.ParentColumns!, keyValues!), ct);
            foreach (var row in existing)
            {
                var key = child.Key.Select(k => row[k] ?? DBNull.Value).ToList();
                if (kept.Any(k => k.SequenceEqual(key, ObjectComparer.Instance))) continue;
                await using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = $"DELETE FROM {child.Table} WHERE " + string.Join(" AND ", child.Key.Select((k, i) => $"{k} = $k{i}"));
                for (var i = 0; i < key.Count; i++) del.Parameters.AddWithValue($"$k{i}", key[i]);
                await del.ExecuteNonQueryAsync(ct);
            }
        }
    }

    private static JsonNode? JsonValueOf(object value) => value switch
    {
        string s => JsonValue.Create(s),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        _ => null,
    };

    private async Task<bool> UpsertRowAsync(SqliteConnection conn, SqliteTransaction tx, SchemaCache schema, TableSpec spec, JsonObject data,
        string where, ApplyReport report, CancellationToken ct)
    {
        var table = schema.Table(spec.Table);
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        var skip = (spec.Skip ?? []).ToHashSet(StringComparer.Ordinal);
        var secretCols = (spec.Secret ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var (col, node) in data)
        {
            if (col == BackupDoc.SecretsField || skip.Contains(col) || secretCols.Contains(col)) continue;
            if (node is JsonArray) continue; // a child's rows
            if (!table.Columns.ContainsKey(col)) continue;
            values[col] = FromJson(node);
        }
        foreach (var k in spec.Key)
            if (!values.ContainsKey(k)) { report.Skipped.Add($"{where}: no value for {k}"); return false; }

        if (spec.RequiresSecret && data[BackupDoc.SecretsField] is not JsonObject { Count: > 0 })
        {
            report.Skipped.Add($"{where}: the backup has no secret for it");
            return false;
        }
        if (data[BackupDoc.SecretsField] is JsonObject box)
            foreach (var col in secretCols)
                if (box[col] is JsonValue sv && sv.TryGetValue<string>(out var plain) && table.Columns.ContainsKey(col)) values[col] = secrets.Protect(plain);

        // References: a value that points at a row that is not here is cleared (nullable) or the row is left out.
        foreach (var fk in schema.ForeignKeys(spec.Table))
        {
            if (!values.TryGetValue(fk.From, out var v) || v is DBNull) continue;
            await using var probe = conn.CreateCommand();
            probe.Transaction = tx;
            probe.CommandText = $"SELECT 1 FROM {fk.Table} WHERE {fk.To} = $v LIMIT 1";
            probe.Parameters.AddWithValue("$v", v);
            if (await probe.ExecuteScalarAsync(ct) is not null) continue;
            if (table.Columns[fk.From].NotNull)
            {
                report.Skipped.Add($"{where}: {fk.From} points at {fk.Table}, which is not here");
                return false;
            }
            values[fk.From] = DBNull.Value;
            report.Skipped.Add($"{where}: {fk.From} cleared ({fk.Table} is not here)");
        }

        if (spec.UpdateOnly)
        {
            await using var exists = conn.CreateCommand();
            exists.Transaction = tx;
            exists.CommandText = $"SELECT 1 FROM {spec.Table} WHERE " + string.Join(" AND ", spec.Key.Select((k, i) => $"{k} = $k{i}"));
            for (var i = 0; i < spec.Key.Length; i++) exists.Parameters.AddWithValue($"$k{i}", values[spec.Key[i]]);
            if (await exists.ExecuteScalarAsync(ct) is null)
            {
                report.Skipped.Add($"{where}: not installed here");
                return false;
            }
        }

        var cols = values.Keys.ToList();
        var update = cols.Where(c => !spec.Key.Contains(c)).ToList();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"INSERT INTO {spec.Table} ({string.Join(", ", cols)}) VALUES ({string.Join(", ", cols.Select((_, i) => "$v" + i))}) "
            + $"ON CONFLICT({string.Join(", ", spec.Key)}) " + (update.Count == 0 ? "DO NOTHING" : "DO UPDATE SET " + string.Join(", ", update.Select(c => $"{c} = excluded.{c}")));
        for (var i = 0; i < cols.Count; i++) cmd.Parameters.AddWithValue("$v" + i, values[cols[i]]);
        await cmd.ExecuteNonQueryAsync(ct);
        return true;
    }

    private sealed class ObjectComparer : IEqualityComparer<object>
    {
        public static readonly ObjectComparer Instance = new();
        public new bool Equals(object? x, object? y) => Convert.ToString(x, CultureInfo.InvariantCulture) == Convert.ToString(y, CultureInfo.InvariantCulture);
        public int GetHashCode(object obj) => Convert.ToString(obj, CultureInfo.InvariantCulture)?.GetHashCode() ?? 0;
    }

    private sealed record ColumnInfo(bool NotNull);
    private sealed record TableInfo(Dictionary<string, ColumnInfo> Columns);
    private sealed record ForeignKey(string From, string Table, string To);

    /// <summary>Column and foreign-key lists of the tables touched in one apply, read once from the database itself.</summary>
    private sealed class SchemaCache(SqliteConnection conn, SqliteTransaction tx)
    {
        private readonly Dictionary<string, TableInfo> _tables = [];
        private readonly Dictionary<string, List<ForeignKey>> _fks = [];

        public TableInfo Table(string name)
        {
            if (_tables.TryGetValue(name, out var t)) return t;
            var columns = new Dictionary<string, ColumnInfo>(StringComparer.Ordinal);
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"PRAGMA table_info({name})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) columns[r.GetString(1)] = new ColumnInfo(r.GetInt64(3) != 0);
            return _tables[name] = new TableInfo(columns);
        }

        public List<ForeignKey> ForeignKeys(string name)
        {
            if (_fks.TryGetValue(name, out var f)) return f;
            var list = new List<ForeignKey>();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"PRAGMA foreign_key_list({name})";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(new ForeignKey(r.GetString(3), r.GetString(2), r.GetString(4)));
            return _fks[name] = list;
        }
    }
}

/// <summary>Things <see cref="BackupStore.ReadAsync"/> met that it could not read (a secret this machine cannot decrypt, a malformed setting).</summary>
public sealed class ReadReport
{
    public List<string> Unreadable { get; } = [];
}
