using System.Globalization;
using System.Text.RegularExpressions;
using Astra.Core.Import;
using Microsoft.Data.Sqlite;

namespace Astra.Data.Foreign;

/// <summary>
/// Opens another application's SQLite database strictly read-only (<c>mode=ro</c>, <c>query_only</c>), with raw
/// ADO.NET: no Dapper (its AOT interception does not cover other apps' schemas) and no pooling, so no handle
/// outlives the import. A busy database is waited for up to 3 s, then reported as an error.
/// </summary>
public sealed class ForeignSqliteOpener : IForeignDbOpener
{
    public IForeignDb Open(string path)
    {
        if (!File.Exists(path)) throw new ForeignDbException($"Database not found: {path}");
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3,
        }.ToString());
        try
        {
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout=3000; PRAGMA query_only=ON;";
                pragma.ExecuteNonQuery();
            }
            return new ForeignSqlite(conn);
        }
        catch (SqliteException e)
        {
            conn.Dispose();
            throw new ForeignDbException($"Cannot read {path}: {e.Message}", e);
        }
    }
}

internal sealed partial class ForeignSqlite(SqliteConnection conn) : IForeignDb
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    public long UserVersion => Scalar("PRAGMA user_version;") is { } v ? long.Parse(v, CultureInfo.InvariantCulture) : 0;

    public bool HasTable(string table) =>
        Run("SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n;", ("$n", table)).Count > 0;

    public IReadOnlyList<string> Columns(string table) =>
        Run($"SELECT name FROM pragma_table_info('{Safe(table)}');").Select(r => r["name"] ?? "").ToList();

    public IReadOnlyList<IReadOnlyDictionary<string, string?>> Rows(string table) => Run($"SELECT * FROM \"{Safe(table)}\";");

    public void Dispose() => conn.Dispose();

    private static string Safe(string table) =>
        Identifier().IsMatch(table) ? table : throw new ArgumentException("Invalid table name", nameof(table));

    private string? Scalar(string sql) => Run(sql).FirstOrDefault()?.Values.FirstOrDefault();

    private List<IReadOnlyDictionary<string, string?>> Run(string sql, params (string Name, string Value)[] args)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
            using var reader = cmd.ExecuteReader();
            var rows = new List<IReadOnlyDictionary<string, string?>>();
            while (reader.Read())
            {
                var row = new Dictionary<string, string?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) || reader.GetFieldType(i) == typeof(byte[])
                        ? null
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }
                rows.Add(row);
            }
            return rows;
        }
        catch (SqliteException e)
        {
            throw new ForeignDbException(e.Message, e);
        }
    }
}
