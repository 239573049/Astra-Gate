using Microsoft.Data.Sqlite;

namespace Astra.Data;

/// <summary>What a <see cref="SqliteException"/> says about the database file rather than about one statement.</summary>
public static class SqliteFailures
{
    /// <summary>SQLITE_IOERR: the primary code of every "disk I/O error", whatever the extended code adds.</summary>
    public const int IoError = 10;

    /// <summary>Whether <paramref name="e"/> is a disk I/O error (SQLITE_IOERR or one of its extended codes).</summary>
    public static bool IsIoError(SqliteException e) => e.SqliteErrorCode == IoError;

    /// <summary>
    /// The error with both codes, for a log line: the message alone ("disk I/O error") does not say whether the shared-memory
    /// file could not be opened (3850), a read or write failed, or a lock could not be taken — and that is what points at a cause.
    /// </summary>
    public static string Describe(SqliteException e) =>
        $"SQLite error {e.SqliteErrorCode} (extended {e.SqliteExtendedErrorCode}): {e.Message}";
}
