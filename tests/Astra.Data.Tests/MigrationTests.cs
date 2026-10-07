using Dapper;
using Astra.Core;
using Astra.Data.Tests;

namespace Astra.Data;

public class MigrationTests
{
    [Fact]
    public async Task Migrate_Is_Idempotent_And_Records_Version()
    {
        var paths = TestDb.NewPaths(out _);
        var factory = new SqliteConnectionFactory(paths);
        var runner = new MigrationRunner(factory, paths);

        await runner.MigrateAsync();
        await runner.MigrateAsync(); // second run must be a no-op

        await using var conn = await factory.OpenAsync();
        var versions = (await conn.QueryAsync<long>("SELECT version FROM schema_version")).AsList();
        Assert.Equal([1L, 5L], versions.Order());

        var appliedAt = await conn.ExecuteScalarAsync<string>("SELECT applied_at FROM schema_version ORDER BY version LIMIT 1");
        Assert.NotNull(appliedAt);
        DateTimeOffset.Parse(appliedAt!); // stored as parseable ISO-8601

        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")).AsList();
        foreach (var expected in new[]
                 {
                     "schema_version", "settings", "models", "model_prices", "providers", "provider_models",
                     "clients", "client_bindings", "client_config_state", "requests", "request_usage_items",
                     "request_attempts", "provider_accounts",
                 })
        {
            Assert.Contains(expected, tables);
        }

        // Full init shape: privacy guard outcome, subscription account pinning, response model
        // and the token breakdown all exist from the single migration on. The reasoning
        // metadata columns come from 0005_request_reasoning.
        var requestColumns = (await conn.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('requests') ORDER BY name")).AsList();
        Assert.Contains("privacy_json", requestColumns);
        Assert.Contains("response_model", requestColumns);
        Assert.Contains("cache_read_tokens", requestColumns);
        Assert.Contains("cache_write_tokens", requestColumns);
        Assert.Contains("reasoning_tokens", requestColumns);
        Assert.Contains("reasoning_effort", requestColumns);
        Assert.Contains("reasoning_mode", requestColumns);
        Assert.Contains("reasoning_budget_tokens", requestColumns);
        var bindingColumns = (await conn.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('client_bindings') ORDER BY name")).AsList();
        Assert.Contains("account_id", bindingColumns);

        var indexes = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'idx_%'")).AsList();
        Assert.Contains("idx_requests_started_at", indexes);
        Assert.Contains("idx_requests_provider_started", indexes);
        Assert.Contains("idx_requests_client_started", indexes);
        Assert.Contains("idx_requests_model_started", indexes);
        Assert.Contains("idx_request_usage_items_request", indexes);
        Assert.Contains("idx_request_attempts_request", indexes);
        Assert.Contains("idx_provider_accounts_provider", indexes);
        Assert.Contains("idx_provider_accounts_expires", indexes);
    }

    [Fact]
    public async Task First_Migration_On_Fresh_Db_Creates_No_Backup()
    {
        var paths = TestDb.NewPaths(out _);
        await new MigrationRunner(new SqliteConnectionFactory(paths), paths).MigrateAsync();

        var backups = Directory.Exists(paths.DbBackupsDir) ? Directory.GetFiles(paths.DbBackupsDir) : [];
        Assert.Empty(backups);
    }

    [Fact]
    public async Task Migrating_Existing_Unversioned_Db_Creates_Backup_And_Preserves_Data()
    {
        var paths = TestDb.NewPaths(out _);
        var factory = new SqliteConnectionFactory(paths);
        await new MigrationRunner(factory, paths).MigrateAsync();

        await using (var conn = await factory.OpenAsync())
        {
            await conn.ExecuteAsync("INSERT INTO settings(key, value_json) VALUES ('marker', 'true')");
            // A database with tables but no version tracking (e.g. from before the runner existed):
            // fromVersion = 0 while the schema already exists, so a backup must be taken and the
            // idempotent script must re-apply as a no-op. 0001 is idempotent but 0005's ALTER TABLE
            // is not, so the pre-0005 schema is simulated by dropping the columns it adds.
            foreach (var column in new[] { "reasoning_effort", "reasoning_mode", "reasoning_budget_tokens" })
                await conn.ExecuteAsync($"ALTER TABLE requests DROP COLUMN {column}");
            await conn.ExecuteAsync("DROP TABLE schema_version");
        }

        await new MigrationRunner(factory, paths).MigrateAsync();

        var backups = Directory.GetFiles(paths.DbBackupsDir);
        var backup = Assert.Single(backups);
        Assert.Contains("-v0", Path.GetFileName(backup));
        Assert.True(new FileInfo(backup).Length > 0);

        await using var check = await factory.OpenAsync();
        Assert.Equal(2, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_version"));
        Assert.Equal("true",
            await check.ExecuteScalarAsync<string>("SELECT value_json FROM settings WHERE key = 'marker'"));
        // 0005 was re-applied after the rollback: the reasoning columns are back.
        var restoredColumns = (await check.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('requests')")).AsList();
        Assert.Contains("reasoning_effort", restoredColumns);
        Assert.Contains("reasoning_mode", restoredColumns);
        Assert.Contains("reasoning_budget_tokens", restoredColumns);
    }

    [Fact]
    public async Task Reasoning_Migration_Upgrades_V4_Without_Guessing_Historical_Settings()
    {
        var paths = TestDb.NewPaths(out _);
        var factory = new SqliteConnectionFactory(paths);
        var runner = new MigrationRunner(factory, paths);
        await runner.MigrateAsync();

        await using (var conn = await factory.OpenAsync())
        {
            await conn.ExecuteAsync("""
                INSERT INTO requests(id, started_at_utc, inbound_protocol, status, requested_model)
                VALUES ('r-v4', '2026-10-07T00:00:00.000Z', 'openai-responses', 'success', 'gpt-5');
                ALTER TABLE requests DROP COLUMN reasoning_effort;
                ALTER TABLE requests DROP COLUMN reasoning_mode;
                ALTER TABLE requests DROP COLUMN reasoning_budget_tokens;
                DELETE FROM schema_version WHERE version = 5;
                INSERT INTO schema_version(version, applied_at) VALUES (4, '2026-10-07T00:00:00.000Z');
                """);
        }

        await runner.MigrateAsync();
        await runner.MigrateAsync();

        var backup = Assert.Single(Directory.GetFiles(paths.DbBackupsDir));
        Assert.Contains("-v4", Path.GetFileName(backup));
        await using var check = await factory.OpenAsync();
        Assert.Equal(5, await check.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_version"));
        var record = await check.QuerySingleAsync<Astra.Core.Requests.RequestRecord>("""
            SELECT requested_model, reasoning_effort, reasoning_mode, reasoning_budget_tokens
            FROM requests WHERE id = 'r-v4'
            """);
        Assert.Equal("gpt-5", record.RequestedModel);
        Assert.Null(record.ReasoningEffort);
        Assert.Null(record.ReasoningMode);
        Assert.Null(record.ReasoningBudgetTokens);
    }

    [Fact]
    public async Task DateTimeOffset_Is_Stored_As_Iso8601_Utc_Text()
    {
        var db = await TestDb.InitializeAsync();
        var at = new DateTimeOffset(2026, 10, 6, 12, 34, 56, 789, TimeSpan.Zero);
        var model = new Astra.Core.Models.SystemModel { Id = "m-dt", DisplayName = "DT", CreatedAt = at, UpdatedAt = at };
        await db.Models.InsertAsync(model);

        await using var conn = await db.Factory.OpenAsync();
        var stored = await conn.ExecuteScalarAsync<string>("SELECT created_at FROM models WHERE id = 'm-dt'");
        Assert.Equal("2026-10-06T12:34:56.789Z", stored);

        var fetched = await db.Models.GetAsync("m-dt");
        Assert.Equal(at, fetched!.CreatedAt);
    }
}
