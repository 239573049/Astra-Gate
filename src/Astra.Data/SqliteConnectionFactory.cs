using System.Data;
using System.Globalization;
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
/// Opens SQLite connections for a given database file. Every opened connection gets
/// <c>foreign_keys=ON</c>, <c>busy_timeout=5000</c> and WAL journal mode. Row mapping and the
/// DateTimeOffset handler come from Dapper.AOT's generated factories (module attributes above);
/// the vanilla-Dapper globals this used to set are gone — they root vanilla Dapper's reflection
/// machinery into the Native AOT closure (IL2104/IL3053) and are only needed by the test
/// assemblies that opt out of interception (see their VanillaDapper module initializers).
/// </summary>
public sealed class SqliteConnectionFactory
{
    public SqliteConnectionFactory(string dbPath)
    {
        DbPath = Path.GetFullPath(dbPath);
        ConnectionString = new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();
    }

    public SqliteConnectionFactory(AstraPaths paths) : this(paths.Database)
    {
    }

    /// <summary>Absolute path of the database file.</summary>
    public string DbPath { get; }

    public string ConnectionString { get; }

    /// <summary>Opens a configured connection (synchronous callers, e.g. <c>IClientConfigStateStore</c>).</summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        ConfigureConnection(conn);
        return conn;
    }

    /// <summary>Opens a configured connection for async callers (API/gateway).</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync(ct);
        await ConfigureConnectionAsync(conn);
        return conn;
    }

    private static void ConfigureConnection(SqliteConnection conn)
    {
        conn.Execute("PRAGMA busy_timeout=5000;");
        conn.Execute("PRAGMA foreign_keys=ON;");
        conn.ExecuteScalar<string?>("PRAGMA journal_mode=WAL;");
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection conn)
    {
        await conn.ExecuteAsync("PRAGMA busy_timeout=5000;");
        await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");
        await conn.ExecuteScalarAsync<string?>("PRAGMA journal_mode=WAL;");
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
