using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Runtime.CompilerServices;
using Dapper;
using Astra.Core;
using Microsoft.Data.Sqlite;

// Dapper.AOT: enable build-time row mapping for this project, and route DateTimeOffset columns through
// <see cref="DateTimeOffsetHandler"/> declaratively — the AOT-safe replacement for SqlMapper.AddTypeHandler,
// which throws under Native AOT. Requires a public parameterless handler ctor (DAP054).
// (Module-scoped attributes must precede every other element in the file.)
[module: DapperAot]
[module: TypeHandler(typeof(DateTimeOffset), typeof(global::Astra.Data.DateTimeOffsetHandler))]

namespace Astra.Data;

/// <summary>
/// Opens SQLite connections for a given database file. Every connection runs with
/// <c>foreign_keys=ON</c>, <c>busy_timeout=5000</c> and WAL journal mode, set once per physical (pooled)
/// connection rather than on every open: re-running the three PRAGMAs tripled the statements each query
/// prepared. Row mapping and the
/// DateTimeOffset handler come from Dapper.AOT's generated factories (module attributes above);
/// the vanilla-Dapper globals this used to set are gone — they root vanilla Dapper's reflection
/// machinery into the Native AOT closure (IL2104/IL3053) and are only needed by the test
/// assemblies that opt out of interception (see their VanillaDapper module initializers).
/// </summary>
public sealed class SqliteConnectionFactory
{
    // Physical connections (SQLite handles, reused by the pool) whose PRAGMAs are already set. A handle the pool
    // closes is collected, and its replacement is configured again.
    private static readonly ConditionalWeakTable<SQLitePCL.sqlite3, object> Configured = new();
    private static readonly object Marker = new();

    public SqliteConnectionFactory(string dbPath)
    {
        SqliteSetup.Ensure();
        DbPath = Path.GetFullPath(dbPath);
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();
        _generation = Generations.GetOrAdd(DbPath, _ => new StrongBox<long>());
    }

    public SqliteConnectionFactory(AstraPaths paths) : this(paths.Database)
    {
    }

    /// <summary>Absolute path of the database file.</summary>
    public string DbPath { get; }

    public string ConnectionString { get; }

    // One counter per database file, shared by every factory of this process that opens it (restore-all builds its own
    // AstraDatabase over the file a running host already serves).
    private static readonly ConcurrentDictionary<string, StrongBox<long>> Generations = new(StringComparer.Ordinal);
    private readonly StrongBox<long> _generation;

    /// <summary>
    /// Advances after every connection from <see cref="Open"/> / <see cref="OpenAsync"/> that changed rows is closed
    /// (so after its commit), whichever factory of this process opened it. <see cref="RowCache{TKey,TValue}"/> entries
    /// read under an older value are not served. Writes through <see cref="OpenUntrackedAsync"/> do not advance it, nor
    /// do other processes' writes (RowCache entries expire for those).
    /// </summary>
    public long Generation => Volatile.Read(ref _generation.Value);

    /// <summary>Opens a configured connection (synchronous callers, e.g. <c>IClientConfigStateStore</c>).</summary>
    public SqliteConnection Open()
    {
        var conn = new TrackedConnection(ConnectionString, this);
        conn.Open();
        ConfigureConnection(conn);
        conn.StartTracking();
        return conn;
    }

    /// <summary>Opens a configured connection for async callers (API/gateway).</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new TrackedConnection(ConnectionString, this);
        await conn.OpenAsync(ct);
        await ConfigureConnectionAsync(conn);
        conn.StartTracking();
        return conn;
    }

    /// <summary>
    /// Opens a configured connection whose writes leave <see cref="Generation"/> alone — for the request log, which
    /// commits many times a second and touches no cached row except <c>tokens.last_used_at</c> (see
    /// <see cref="Repositories.TokenRepository.FindByPrefixAsync"/>).
    /// </summary>
    public async Task<SqliteConnection> OpenUntrackedAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await ConfigureConnectionAsync(conn);
        return conn;
    }

    private void Changed() => Interlocked.Increment(ref _generation.Value);

    /// <summary>A connection that, when closed after changing rows, advances its factory's <see cref="Generation"/>.</summary>
    private sealed class TrackedConnection(string connectionString, SqliteConnectionFactory factory) : SqliteConnection(connectionString)
    {
        // sqlite3_total_changes counts rows changed over the physical connection's life, which spans pool leases.
        private int _changesAtOpen = -1;

        public void StartTracking() => _changesAtOpen = Handle is { } h ? SQLitePCL.raw.sqlite3_total_changes(h) : -1;

        public override void Close()
        {
            var changed = _changesAtOpen >= 0 && Handle is { } h && SQLitePCL.raw.sqlite3_total_changes(h) != _changesAtOpen;
            _changesAtOpen = -1;
            base.Close();
            if (changed) factory.Changed();
        }
    }

    private static void ConfigureConnection(SqliteConnection conn)
    {
        if (conn.Handle is { } handle && Configured.TryGetValue(handle, out _)) return;
        conn.Execute("PRAGMA busy_timeout=5000;");
        conn.Execute("PRAGMA foreign_keys=ON;");
        conn.ExecuteScalar<string?>("PRAGMA journal_mode=WAL;");
        MarkConfigured(conn);
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection conn)
    {
        if (conn.Handle is { } handle && Configured.TryGetValue(handle, out _)) return;
        await conn.ExecuteAsync("PRAGMA busy_timeout=5000;");
        await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");
        await conn.ExecuteScalarAsync<string?>("PRAGMA journal_mode=WAL;");
        MarkConfigured(conn);
    }

    private static void MarkConfigured(SqliteConnection conn)
    {
        if (conn.Handle is { } handle) Configured.AddOrUpdate(handle, Marker);
    }
}

/// <summary>Stores DateTimeOffset as ISO-8601 UTC text with millisecond precision ("2026-10-06T12:00:00.000Z").</summary>
public sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
{
    public static readonly DateTimeOffsetHandler Instance = new();

    public const string StorageFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Public parameterless ctor is required for the <c>[module: TypeHandler]</c> declaration (DAP054); <see cref="Instance"/> remains the shared handler.</summary>
    public DateTimeOffsetHandler()
    {
    }

    /// <summary>The canonical storage text for a timestamp (used when binding parameters explicitly).</summary>
    public static string ToStorage(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(StorageFormat, CultureInfo.InvariantCulture);

    public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) =>
        parameter.Value = ToStorage(value);

    public override DateTimeOffset Parse(object value) => value switch
    {
        null => default,
        DateTimeOffset dto => dto.ToUniversalTime(),
        DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), TimeSpan.Zero).ToUniversalTime(),
        string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
                             .ToUniversalTime(),
        _ => throw new InvalidCastException($"Cannot convert {value.GetType().Name} to DateTimeOffset."),
    };
}
