using Dapper;
using Astra.Data.Tests;
using Microsoft.Data.Sqlite;

namespace Astra.Data;

public class SqliteConnectionFactoryTests
{
    [Fact]
    public async Task A_reused_pooled_connection_keeps_its_pragmas()
    {
        var factory = new SqliteConnectionFactory(TestDb.NewPaths(out _));
        SQLitePCL.sqlite3? first;
        await using (var conn = await factory.OpenAsync())
        {
            first = conn.Handle;
            await AssertConfiguredAsync(conn);
        }

        // The pool hands back the same physical connection, whose PRAGMAs were set on its first open only.
        await using (var conn = await factory.OpenAsync())
        {
            Assert.Same(first, conn.Handle);
            await AssertConfiguredAsync(conn);
        }
        using (var conn = factory.Open()) await AssertConfiguredAsync(conn);
    }

    [Fact]
    public async Task A_new_physical_connection_is_configured_after_the_pool_is_cleared()
    {
        var factory = new SqliteConnectionFactory(TestDb.NewPaths(out _));
        await using (var conn = await factory.OpenAsync()) await AssertConfiguredAsync(conn);

        SqliteConnection.ClearAllPools();
        await using (var conn = await factory.OpenAsync()) await AssertConfiguredAsync(conn);
    }

    private static async Task AssertConfiguredAsync(SqliteConnection conn)
    {
        Assert.Equal(5000L, await conn.ExecuteScalarAsync<long>("PRAGMA busy_timeout;"));
        Assert.Equal(1L, await conn.ExecuteScalarAsync<long>("PRAGMA foreign_keys;"));
        Assert.Equal("wal", await conn.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task A_probe_of_a_healthy_database_finds_nothing_and_a_reset_leaves_it_usable()
    {
        var factory = new SqliteConnectionFactory(TestDb.NewPaths(out _));
        await using (var conn = await factory.OpenAsync())
            await conn.ExecuteAsync("CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT);");
        Assert.Null(await factory.ProbeAsync());

        SqliteConnectionFactory.ResetConnections();
        Assert.Null(await factory.ProbeAsync());
    }

    [Fact]
    public async Task A_probe_reports_a_sqlite_failure_instead_of_throwing()
    {
        var factory = new SqliteConnectionFactory(TestDb.NewPaths(out _)); // no settings table: SQLITE_ERROR
        var failure = await factory.ProbeAsync();
        Assert.NotNull(failure);
        Assert.False(SqliteFailures.IsIoError(failure));
    }

    [Fact]
    public void Disk_io_errors_are_recognised_by_their_primary_code_and_described_with_both_codes()
    {
        const int shmOpen = 10 | (15 << 8); // SQLITE_IOERR_SHMOPEN
        var e = new SqliteException("disk I/O error", 10, shmOpen);
        Assert.True(SqliteFailures.IsIoError(e));
        Assert.Contains("10", SqliteFailures.Describe(e));
        Assert.Contains(shmOpen.ToString(), SqliteFailures.Describe(e));
        Assert.False(SqliteFailures.IsIoError(new SqliteException("busy", 5)));
    }
}
