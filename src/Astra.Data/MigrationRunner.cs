using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Astra.Core;
using Microsoft.Data.Sqlite;

namespace Astra.Data;

/// <summary>
/// Applies embedded <c>Migrations/NNNN_name.sql</c> scripts in numeric order inside a transaction and
/// records each in <c>schema_version</c>. Before applying pending migrations to an existing database,
/// a backup copy is written to <c>backups/db/astra-&lt;timestamp&gt;-v&lt;fromVersion&gt;.db</c>.
/// Idempotent: with no pending migrations nothing is written.
/// </summary>
public sealed partial class MigrationRunner
{
    private readonly SqliteConnectionFactory _factory;
    private readonly AstraPaths _paths;

    public MigrationRunner(SqliteConnectionFactory factory, AstraPaths paths)
    {
        _factory = factory;
        _paths = paths;
    }

    /// <summary>Applies all pending migrations. Safe to call on every start.</summary>
    public async Task MigrateAsync(CancellationToken ct = default)
    {
        var migrations = LoadEmbedded();
        if (migrations.Count == 0) return;

        await using var conn = await _factory.OpenAsync(ct);
        var fromVersion = await ReadCurrentVersionAsync(conn);

        var pending = migrations.Where(m => m.Version > fromVersion).OrderBy(m => m.Version).ToList();
        if (pending.Count == 0) return;

        if (await HasExistingSchemaAsync(conn)) BackupDatabase(conn, fromVersion);

        foreach (var migration in pending)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await conn.ExecuteAsync(migration.Sql, transaction: tx);
            await conn.ExecuteAsync(
                "INSERT INTO schema_version(version, applied_at) VALUES (@version, @applied_at)",
                new { version = migration.Version, applied_at = DateTimeOffsetHandler.ToStorage(DateTimeOffset.UtcNow) },
                transaction: tx);
            await tx.CommitAsync(ct);
        }
    }

    private static async Task<long> ReadCurrentVersionAsync(SqliteConnection conn)
    {
        var table = await conn.ExecuteScalarAsync<long?>(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'schema_version'");
        if (table is null) return 0;
        return await conn.ExecuteScalarAsync<long?>("SELECT MAX(version) FROM schema_version") ?? 0;
    }

    /// <summary>True when the file contains any table, i.e. it is more than an empty freshly-created db.</summary>
    private static async Task<bool> HasExistingSchemaAsync(SqliteConnection conn) =>
        await conn.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'") > 0;

    private void BackupDatabase(SqliteConnection conn, long fromVersion)
    {
        if (!File.Exists(_factory.DbPath)) return;
        // Fold the WAL into the main file so the plain copy is complete (best effort when busy).
        try
        {
            conn.Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (SqliteException)
        {
        }
        Directory.CreateDirectory(_paths.DbBackupsDir);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(_paths.DbBackupsDir, $"astra-{stamp}-v{fromVersion}.db");
        for (var i = 1; File.Exists(target); i++)
            target = Path.Combine(_paths.DbBackupsDir, $"astra-{stamp}-{i}-v{fromVersion}.db");
        File.Copy(_factory.DbPath, target);
    }

    private static List<EmbeddedMigration> LoadEmbedded()
    {
        var asm = typeof(MigrationRunner).Assembly;
        var list = new List<EmbeddedMigration>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            var match = MigrationFileRegex().Match(name);
            if (!match.Success) continue;
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add(new EmbeddedMigration(
                int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                reader.ReadToEnd()));
        }
        return list;
    }

    private readonly record struct EmbeddedMigration(int Version, string Sql);

    [GeneratedRegex(@"\.Migrations\.(\d+)_[A-Za-z0-9_]+\.sql$")]
    private static partial Regex MigrationFileRegex();
}
