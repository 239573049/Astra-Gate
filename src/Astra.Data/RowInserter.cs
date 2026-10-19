using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Astra.Data;

/// <summary>
/// Inserts many rows with one statement, bound by ordinal on the caller's connection and transaction. Binding by
/// name scans the statement's parameter list for every value on every row (60 columns for a request). The column
/// list is the single source of both SQL and ordinals. Only the storage types used by the request log are accepted;
/// timestamps must first go through <see cref="DateTimeOffsetHandler.ToStorage"/>. No connection or statement is
/// retained across batches, and every parameter, including NULL, is rebound on every row.
/// </summary>
internal sealed class RowInserter<T>(string table, IReadOnlyList<(string Column, Func<T, object?> Value)> columns)
{
    public string Sql { get; } =
        $"INSERT INTO {table}({string.Join(", ", columns.Select(c => c.Column))}) VALUES ({string.Join(", ", columns.Select(c => "@" + c.Column))})";

    public Task InsertAsync(SqliteConnection conn, SqliteTransaction tx, IEnumerable<T> rows, CancellationToken ct)
    {
        if (tx.Connection != conn) throw new InvalidOperationException("The insert requires an active transaction on its connection.");
        ct.ThrowIfCancellationRequested();
        var handle = conn.Handle!;
        var result = raw.sqlite3_prepare_v2(handle, Sql, out var statement);
        using (statement)
        {
            SqliteException.ThrowExceptionForRC(result, handle);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                for (var i = 0; i < columns.Count; i++)
                {
                    var ordinal = i + 1;
                    result = columns[i].Value(row) switch
                    {
                        null => raw.sqlite3_bind_null(statement, ordinal),
                        string value => raw.sqlite3_bind_text(statement, ordinal, value),
                        long value => raw.sqlite3_bind_int64(statement, ordinal, value),
                        int value => raw.sqlite3_bind_int(statement, ordinal, value),
                        bool value => raw.sqlite3_bind_int(statement, ordinal, value ? 1 : 0),
                        double value => raw.sqlite3_bind_double(statement, ordinal, value),
                        _ => throw new InvalidOperationException($"Unsupported storage type for {table}.{columns[i].Column}."),
                    };
                    SqliteException.ThrowExceptionForRC(result, handle);
                }
                SqliteException.ThrowExceptionForRC(raw.sqlite3_step(statement), handle);
                SqliteException.ThrowExceptionForRC(raw.sqlite3_reset(statement), handle);
            }
        }
        return Task.CompletedTask;
    }
}
