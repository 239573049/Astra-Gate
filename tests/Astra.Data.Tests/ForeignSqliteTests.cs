using Astra.Core.Import;
using Astra.Data.Foreign;
using Microsoft.Data.Sqlite;

namespace Astra.Data.Tests;

public sealed class ForeignSqliteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "astra-foreign-" + Guid.NewGuid().ToString("N"));

    public ForeignSqliteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string Create(params string[] statements)
    {
        var path = Path.Combine(_dir, "foreign.db");
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        conn.Open();
        foreach (var sql in statements)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        return path;
    }

    [Fact]
    public void Reads_Tables_Columns_Rows_And_User_Version()
    {
        var path = Create("CREATE TABLE t (id TEXT, n INTEGER, blob BLOB, empty TEXT);",
            "INSERT INTO t VALUES ('a', 5, x'00ff', NULL);", "PRAGMA user_version = 7;");
        using var db = new ForeignSqliteOpener().Open(path);
        Assert.Equal(7, db.UserVersion);
        Assert.True(db.HasTable("t"));
        Assert.False(db.HasTable("missing"));
        Assert.Equal(["id", "n", "blob", "empty"], db.Columns("t"));
        var row = Assert.Single(db.Rows("t"));
        Assert.Equal("a", row["id"]);
        Assert.Equal("5", row["n"]);
        Assert.Null(row["blob"]);
        Assert.Null(row["empty"]);
    }

    [Fact]
    public void Never_Modifies_Or_Creates_Files()
    {
        var path = Create("CREATE TABLE t (id TEXT);", "INSERT INTO t VALUES ('a');");
        var before = File.ReadAllBytes(path);
        using (var db = new ForeignSqliteOpener().Open(path)) _ = db.Rows("t");
        Assert.Equal(before, File.ReadAllBytes(path));

        var missing = Path.Combine(_dir, "nope.db");
        Assert.Throws<ForeignDbException>(() => new ForeignSqliteOpener().Open(missing));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void Rejects_Unsafe_Table_Names()
    {
        using var db = new ForeignSqliteOpener().Open(Create("CREATE TABLE t (id TEXT);"));
        Assert.Throws<ArgumentException>(() => db.Rows("t; DROP TABLE t"));
    }

    [Fact]
    public void A_File_That_Is_Not_A_Database_Is_A_Foreign_Db_Error()
    {
        var path = Path.Combine(_dir, "junk.db");
        File.WriteAllText(path, "this is not sqlite, but long enough to look like a file header to the engine....");
        Assert.Throws<ForeignDbException>(() =>
        {
            using var db = new ForeignSqliteOpener().Open(path);
            _ = db.HasTable("t");
        });
    }

    [Fact]
    public void A_Locked_Database_Reports_Busy_After_The_Wait_Instead_Of_Hanging()
    {
        var path = Create("CREATE TABLE t (id TEXT);");
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        writer.Open();
        using (var journal = writer.CreateCommand()) { journal.CommandText = "PRAGMA journal_mode=DELETE;"; journal.ExecuteNonQuery(); }
        using (var begin = writer.CreateCommand()) { begin.CommandText = "BEGIN EXCLUSIVE;"; begin.ExecuteNonQuery(); }

        var started = DateTime.UtcNow;
        Assert.Throws<ForeignDbException>(() =>
        {
            using var db = new ForeignSqliteOpener().Open(path);
            _ = db.Rows("t");
        });
        Assert.InRange(DateTime.UtcNow - started, TimeSpan.FromMilliseconds(2000), TimeSpan.FromSeconds(10));
    }
}
