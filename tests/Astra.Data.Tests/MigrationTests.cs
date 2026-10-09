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
        Assert.Equal([1L, 5L, 6L, 7L, 8L, 9L], versions.Order());

        var appliedAt = await conn.ExecuteScalarAsync<string>("SELECT applied_at FROM schema_version ORDER BY version LIMIT 1");
        Assert.NotNull(appliedAt);
        DateTimeOffset.Parse(appliedAt!); // stored as parseable ISO-8601

        var tables = (await conn.QueryAsync<string>(
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name")).AsList();
        foreach (var expected in new[]
                 {
                     "schema_version", "settings", "models", "model_prices", "providers", "provider_models",
                     "clients", "client_bindings", "client_config_state", "requests", "request_usage_items",
                     "request_attempts", "provider_accounts", "tokens", "token_usage_totals",
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
        // 0007_provider_quota: the provider's balance / quota snapshot and the time of the last attempt.
        var providerColumns = (await conn.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('providers') ORDER BY name")).AsList();
        Assert.Contains("quota_json", providerColumns);
        Assert.Contains("quota_checked_at_utc", providerColumns);
        // 0008_subscription_accounts: account switching state and the account that served each request.
        var accountColumns = (await conn.QueryAsync<string>(
            "SELECT name FROM pragma_table_info('provider_accounts') ORDER BY name")).AsList();
        foreach (var column in new[] { "enabled", "is_current", "sort_order", "cooldown_until_utc", "last_error" })
            Assert.Contains(column, accountColumns);
        Assert.Contains("account_id", requestColumns);
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
            // idempotent script must re-apply as a no-op. 0001 is idempotent but 0005 through 0009
            // are not, so the pre-0005 schema is simulated by undoing what they add.
            await conn.ExecuteAsync(UndoAfterProviderQuotaMigrations);
            await conn.ExecuteAsync(UndoProviderQuotaMigration);
            await conn.ExecuteAsync(UndoTokensMigration);
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
        Assert.Equal(6, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM schema_version"));
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
            await conn.ExecuteAsync(UndoAfterProviderQuotaMigrations);
            await conn.ExecuteAsync(UndoProviderQuotaMigration);
            await conn.ExecuteAsync(UndoTokensMigration);
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
        Assert.Equal(9, await check.ExecuteScalarAsync<long>("SELECT MAX(version) FROM schema_version"));
        var record = await check.QuerySingleAsync<Astra.Core.Requests.RequestRecord>("""
            SELECT requested_model, reasoning_effort, reasoning_mode, reasoning_budget_tokens
            FROM requests WHERE id = 'r-v4'
            """);
        Assert.Equal("gpt-5", record.RequestedModel);
        Assert.Null(record.ReasoningEffort);
        Assert.Null(record.ReasoningMode);
        Assert.Null(record.ReasoningBudgetTokens);
    }

    /// <summary>Rolls a fully migrated database back to its v7 shape (what 0008 and 0009 add is removed).</summary>
    private const string UndoAfterProviderQuotaMigrations = """
        ALTER TABLE provider_models DROP COLUMN upstream_protocols_json;
        DELETE FROM schema_version WHERE version = 9;
        ALTER TABLE requests DROP COLUMN account_id;
        ALTER TABLE provider_accounts DROP COLUMN enabled;
        ALTER TABLE provider_accounts DROP COLUMN is_current;
        ALTER TABLE provider_accounts DROP COLUMN sort_order;
        ALTER TABLE provider_accounts DROP COLUMN cooldown_until_utc;
        ALTER TABLE provider_accounts DROP COLUMN last_error;
        DELETE FROM schema_version WHERE version = 8;
        """;

    /// <summary>Rolls a v7 database back to its v6 shape (what 0007_provider_quota adds is removed).</summary>
    private const string UndoProviderQuotaMigration = """
        ALTER TABLE providers DROP COLUMN quota_json;
        ALTER TABLE providers DROP COLUMN quota_checked_at_utc;
        DELETE FROM schema_version WHERE version = 7;
        """;

    /// <summary>Rolls a v6 database back to its v5 shape (what 0006_tokens adds is removed).</summary>
    private const string UndoTokensMigration = """
        DROP INDEX idx_requests_token_started;
        ALTER TABLE requests DROP COLUMN token_id;
        ALTER TABLE requests DROP COLUMN token_name;
        ALTER TABLE clients DROP COLUMN token_id;
        DROP TABLE token_usage_totals;
        DROP TABLE tokens;
        DELETE FROM settings WHERE key = 'token_migration';
        DELETE FROM schema_version WHERE version = 6;
        """;

    [Fact]
    public async Task Tokens_Migration_Moves_Clients_To_The_Default_Token_And_Backfills_Totals()
    {
        var paths = TestDb.NewPaths(out _);
        var factory = new SqliteConnectionFactory(paths);
        var runner = new MigrationRunner(factory, paths);
        await runner.MigrateAsync();

        // A fresh install gets the default token (key generated later by the server) and no rewrite marker.
        await using (var fresh = await factory.OpenAsync())
        {
            Assert.Equal(1, await fresh.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM tokens WHERE id = 'default' AND is_default = 1 AND key_enc IS NULL"));
            Assert.Equal(0, await fresh.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM settings WHERE key = 'token_migration'"));
            var created = await fresh.ExecuteScalarAsync<string>("SELECT created_at FROM tokens WHERE id = 'default'");
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", created!);
        }

        await using (var conn = await factory.OpenAsync())
        {
            await conn.ExecuteAsync(UndoAfterProviderQuotaMigrations);
            await conn.ExecuteAsync(UndoProviderQuotaMigration);
            await conn.ExecuteAsync(UndoTokensMigration);
            await conn.ExecuteAsync("""
                INSERT INTO clients(kind, enabled, local_key_enc, local_key_hash, local_key_prefix) VALUES ('codex', 1, 'enc', 'hash', 'astra-codex-AB');
                INSERT INTO clients(kind, enabled) VALUES ('opencode', 0);
                INSERT INTO requests(id, started_at_utc, client_kind, inbound_protocol, status, total_input_tokens, total_output_tokens,
                                     cache_read_tokens, cost_nanousd, generation_ms)
                VALUES ('r1', '2026-10-07T00:00:00.000Z', 'codex', 'openai-responses', 'success', 100, 50, 40, 1000, 500),
                       ('r2', '2026-10-07T00:01:00.000Z', 'codex', 'openai-responses', 'upstream_error', 10, 0, 0, 0, NULL);
                """);
        }

        await runner.MigrateAsync();

        await using var check = await factory.OpenAsync();
        Assert.Equal(["default", "default"], (await check.QueryAsync<string>("SELECT token_id FROM clients ORDER BY kind")).AsList());
        Assert.Equal(0, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM clients WHERE local_key_enc IS NOT NULL OR local_key_hash IS NOT NULL"));
        Assert.Equal(2, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM requests WHERE token_id = 'default'"));
        var totals = await check.QuerySingleAsync<Astra.Core.Tokens.TokenUsage>("SELECT * FROM token_usage_totals WHERE token_id = 'default'");
        Assert.Equal(2, totals.Requests);
        Assert.Equal(1, totals.SuccessRequests);
        Assert.Equal(1000, totals.CostNanoUsd);
        Assert.Equal(110, totals.InputTokens);
        Assert.Equal(40, totals.CacheReadTokens);
        Assert.Equal(50, totals.TpsOutputTokens);
        Assert.Equal(500, totals.TpsGenerationMs);
        Assert.Equal(100.0, totals.Tps);
        Assert.Equal(1, await check.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM settings WHERE key = 'token_migration'"));
    }

    [Fact]
    public async Task Provider_Quota_Migration_Adds_Empty_Quota_Columns_To_Existing_Providers()
    {
        var paths = TestDb.NewPaths(out _);
        var factory = new SqliteConnectionFactory(paths);
        var runner = new MigrationRunner(factory, paths);
        await runner.MigrateAsync();

        await using (var conn = await factory.OpenAsync())
        {
            await conn.ExecuteAsync(UndoAfterProviderQuotaMigrations);
            await conn.ExecuteAsync(UndoProviderQuotaMigration);
            await conn.ExecuteAsync("""
                INSERT INTO providers(id, name, created_at, updated_at)
                VALUES ('p-v6', 'Old', '2026-10-07T00:00:00.000Z', '2026-10-07T00:00:00.000Z');
                """);
        }

        await runner.MigrateAsync();

        var backup = Assert.Single(Directory.GetFiles(paths.DbBackupsDir));
        Assert.Contains("-v6", Path.GetFileName(backup));
        var db = new Astra.Data.Repositories.ProviderRepository(factory);
        var provider = await db.GetAsync("p-v6");
        Assert.NotNull(provider);
        Assert.Equal("Old", provider!.Name);
        Assert.Null(provider.Quota);
        Assert.Null(provider.QuotaCheckedAtUtc);
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
