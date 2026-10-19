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
}
