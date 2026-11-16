using System.Text.RegularExpressions;
using Astra.Data.Backup;
using Microsoft.Data.Sqlite;

namespace Astra.Data.Tests;

/// <summary>
/// The guard of whole-installation backup: every table and every settings key must have been classified. A migration that adds a table (or a
/// feature that adds a settings key) fails here until somebody decides whether it is configuration, history or this machine's own — so
/// nothing is silently left out of a backup, or silently put into one (and nothing machine-local leaks into sync).
/// </summary>
public sealed class BackupCatalogTests
{
    private static async Task<List<(string Table, List<(string Name, bool Pk)> Columns)>> SchemaAsync()
    {
        var db = await TestDb.InitializeAsync();
        await using var conn = await db.Factory.OpenAsync();
        var tables = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) tables.Add(r.GetString(0));
        }
        var result = new List<(string, List<(string, bool)>)>();
        foreach (var t in tables)
        {
            var cols = new List<(string, bool)>();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({t})";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) cols.Add((r.GetString(1), r.GetInt64(5) > 0));
            result.Add((t, cols));
        }
        SqliteConnection.ClearAllPools();
        return result;
    }

    [Fact]
    public async Task Every_table_in_the_schema_is_classified()
    {
        var schema = await SchemaAsync();
        var missing = schema.Select(s => s.Table).Where(t => !BackupCatalog.Tables.ContainsKey(t)).ToList();
        Assert.True(missing.Count == 0,
            "These tables are not classified in BackupCatalog.Tables (config / usage / data / machine-local / internal): " + string.Join(", ", missing)
            + ". A config table also needs a TableSpec (or a special handler) so a backup carries it.");
        var gone = BackupCatalog.Tables.Keys.Where(k => schema.All(s => s.Table != k)).ToList();
        Assert.True(gone.Count == 0, "BackupCatalog lists tables the schema does not have: " + string.Join(", ", gone));
    }

    [Fact]
    public async Task Every_config_table_is_carried_by_a_spec_or_a_handler()
    {
        _ = await SchemaAsync();
        foreach (var (table, (cls, why)) in BackupCatalog.Tables)
        {
            if (cls is not (TableClass.Config or TableClass.Usage)) continue;
            var carried = BackupCatalog.Specs.Any(s => s.Table == table) || BackupCatalog.HandledSpecially.Contains(table);
            Assert.True(carried, $"{table} ({why}) is classified as {cls} but no TableSpec carries it");
        }
        // a spec's section is a real section and a child's parent is a spec table of the same section
        foreach (var s in BackupCatalog.Specs)
        {
            Assert.True(BackupSections.IsKnown(s.Section), s.Name);
            if (s.Parent is { } p) Assert.Contains(BackupCatalog.Specs, x => x.Table == p && x.Section == s.Section && !x.IsChild);
            Assert.True(BackupCatalog.Tables[s.Table].Class is TableClass.Config or TableClass.Usage, $"{s.Table} has a spec but is not config or usage");
        }
    }

    [Fact]
    public async Task Specs_name_columns_that_exist_and_every_protected_column_is_listed_as_a_secret()
    {
        var schema = (await SchemaAsync()).ToDictionary(s => s.Table, s => s.Columns);
        foreach (var s in BackupCatalog.Specs)
        {
            var cols = schema[s.Table].Select(c => c.Name).ToHashSet();
            foreach (var c in s.Key.Concat(s.Secret ?? []).Concat(s.WithSecret ?? []).Concat(s.Skip ?? []).Concat(s.ParentColumns ?? []))
                Assert.True(cols.Contains(c), $"{s.Name}: column {c} is not in {s.Table}");
        }
        // any *_enc / *_protected column of a carried table is a DataProtection value: it must travel as a secret (decrypted, inside the encrypted bundle), never as ciphertext
        foreach (var (table, cls) in BackupCatalog.Tables.Where(t => t.Value.Class is TableClass.Config or TableClass.Usage).Select(t => (t.Key, t.Value.Class)))
        {
            if (BackupCatalog.HandledSpecially.Contains(table)) continue;
            var listed = BackupCatalog.Specs.Where(s => s.Table == table).SelectMany(s => s.Secret ?? []).ToHashSet();
            foreach (var (name, _) in schema[table].Where(c => BackupCatalog.IsSecretName(c.Name)))
                Assert.True(listed.Contains(name), $"{table}.{name} looks protected but is not a Secret column of its spec ({cls})");
        }
    }

    [Fact]
    public void Every_settings_key_the_source_names_is_classified()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Astra.sln"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var pattern = new Regex(@"const\s+string\s+\w*(?:StorageKey|SettingsKey|SettingKey|StateKey|MarkerKey|MarketKey|VersionKey)\s*=\s*""([a-z][a-z0-9_.]*)""");
        var found = new Dictionary<string, string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}Astra.Desktop") || file.Contains($"{Path.DirectorySeparatorChar}Backup{Path.DirectorySeparatorChar}")) continue;
            var text = File.ReadAllText(file);
            if (!text.Contains("Settings.") && !text.Contains("settings")) continue;
            foreach (Match m in pattern.Matches(text)) found[m.Groups[1].Value] = Path.GetFileName(file);
        }
        // Constants that are not keys of the settings table.
        var notSettings = new HashSet<string> { "key" };
        var unclassified = found.Where(f => !notSettings.Contains(f.Key) && !BackupCatalog.SettingsKeys.ContainsKey(f.Key)).Select(f => $"{f.Key} ({f.Value})").ToList();
        Assert.True(unclassified.Count == 0,
            "Settings keys not classified in BackupCatalog.SettingsKeys (a section, or null for this machine's own): " + string.Join(", ", unclassified));
    }
}
