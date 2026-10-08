using Dapper;
using Astra.Core.Clients;

namespace Astra.Data.Repositories;

/// <summary>
/// Dapper implementation of <see cref="IClientConfigStateStore"/> (synchronous per the interface):
/// records the original and applied value of every key Astra writes into client config files.
/// </summary>
public sealed class ClientConfigStateStore : IClientConfigStateStore
{
    private const string Select = """
        SELECT client_kind, file_path, key_path, original_absent, original_value_json, applied_value_json, applied_at
        FROM client_config_state
        """;

    private readonly SqliteConnectionFactory _factory;

    public ClientConfigStateStore(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<ClientConfigStateEntry> List(string clientKind)
    {
        using var conn = _factory.Open();
        // List<T> ctor, not .AsList()/.ToList(): AsList is vanilla Dapper (SqlMapper.AsList) and roots
        // SqlMapper's static state into the Native AOT closure (IL3050/IL2070); DAP028 dislikes ToList.
        return new List<ClientConfigStateEntry>(conn.Query<ClientConfigStateEntry>(
            $"{Select} WHERE client_kind = @client_kind ORDER BY file_path, key_path",
            new { client_kind = clientKind }));
    }

    public ClientConfigStateEntry? Get(string clientKind, string filePath, string keyPath)
    {
        using var conn = _factory.Open();
        return conn.QuerySingleOrDefault<ClientConfigStateEntry>(
            $"{Select} WHERE client_kind = @client_kind AND file_path = @file_path AND key_path = @key_path",
            new { client_kind = clientKind, file_path = filePath, key_path = keyPath });
    }

    public void Upsert(ClientConfigStateEntry entry)
    {
        if (entry.AppliedAt == default) entry.AppliedAt = DateTimeOffset.UtcNow;
        using var conn = _factory.Open();
        conn.Execute("""
            INSERT INTO client_config_state(client_kind, file_path, key_path, original_absent, original_value_json, applied_value_json, applied_at)
            VALUES (@client_kind, @file_path, @key_path, @original_absent, @original_value_json, @applied_value_json, @applied_at)
            ON CONFLICT(client_kind, file_path, key_path) DO UPDATE SET
                original_absent = excluded.original_absent,
                original_value_json = excluded.original_value_json,
                applied_value_json = excluded.applied_value_json,
                applied_at = excluded.applied_at
            """, new
        {
            client_kind = entry.ClientKind,
            file_path = entry.FilePath,
            key_path = entry.KeyPath,
            original_absent = entry.OriginalAbsent,
            original_value_json = entry.OriginalValueJson,
            applied_value_json = entry.AppliedValueJson,
            applied_at = DateTimeOffsetHandler.ToStorage(entry.AppliedAt),
        });
    }

    public void Delete(string clientKind, string filePath, string keyPath)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "DELETE FROM client_config_state WHERE client_kind = @client_kind AND file_path = @file_path AND key_path = @key_path",
            new { client_kind = clientKind, file_path = filePath, key_path = keyPath });
    }

    public void DeleteAll(string clientKind)
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM client_config_state WHERE client_kind = @client_kind",
            new { client_kind = clientKind });
    }
}
