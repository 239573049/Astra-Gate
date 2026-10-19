using SQLitePCL;

namespace Astra.Data;

/// <summary>
/// Process-wide SQLite settings, applied once before the first connection initializes the library. Memory statistics
/// are turned off: with them on (the e_sqlite3 default) every sqlite3_malloc and free takes one global mutex, and
/// concurrent gateway requests, each preparing several statements, queued on it. Nothing in Astra reads
/// <c>sqlite3_memory_used</c> or sets a soft heap limit, the two features that need the statistics.
/// </summary>
internal static class SqliteSetup
{
    static SqliteSetup()
    {
        Batteries_V2.Init();
        // SQLITE_MISUSE when the library is already initialized (a connection was opened first): the default stays.
        raw.sqlite3_config(raw.SQLITE_CONFIG_MEMSTATUS, 0);
    }

    /// <summary>Runs the static constructor; call before opening a connection.</summary>
    public static void Ensure()
    {
    }
}
