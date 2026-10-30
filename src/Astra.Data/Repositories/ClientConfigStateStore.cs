using System.Collections.Concurrent;
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

    /// <summary>
    /// What identifies a kind's rows without reading their values: count, keys, <c>applied_at</c> and value lengths. Every
    /// applier write stamps <c>applied_at</c>, so another process's write (<c>restore-all</c>) changes it.
    /// </summary>
    private const string SignatureSelect = """
        SELECT count(*) || '|' || coalesce(group_concat(file_path || char(31) || key_path || char(31) || applied_at || char(31)
            || original_absent || char(31) || coalesce(length(original_value_json), -1) || char(31)
            || coalesce(length(applied_value_json), -1), char(30)), '')
        FROM client_config_state WHERE client_kind = @client_kind
        """;

    private readonly SqliteConnectionFactory _factory;

    /// <summary>
    /// The last <see cref="List"/> of each kind with its signature. The status card lists a kind's rows on every
    /// <c>GET /api/clients</c> (the tray polls it), and a recorded value can be megabytes (Codex's model catalog), so the
    /// rows are read again only when the signature changed; this store's own writes drop the kind.
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Signature, List<ClientConfigStateEntry> Entries)> _lists = new(StringComparer.Ordinal);

    public ClientConfigStateStore(SqliteConnectionFactory factory) => _factory = factory;

    public IReadOnlyList<ClientConfigStateEntry> List(string clientKind)
    {
        using var conn = _factory.Open();
        var signature = conn.ExecuteScalar<string>(SignatureSelect, new { client_kind = clientKind }) ?? "";
        if (!_lists.TryGetValue(clientKind, out var cached) || cached.Signature != signature)
        {
            // List<T> ctor, not .AsList()/.ToList(): AsList is vanilla Dapper (SqlMapper.AsList) and roots
            // SqlMapper's static state into the Native AOT closure (IL3050/IL2070); DAP028 dislikes ToList.
            cached = (signature, new List<ClientConfigStateEntry>(conn.Query<ClientConfigStateEntry>(
                $"{Select} WHERE client_kind = @client_kind ORDER BY file_path, key_path",
                new { client_kind = clientKind })));
            _lists[clientKind] = cached;
        }
        // Copies: callers may change an entry before they upsert it. The strings (the large part) are shared.
        return cached.Entries.Select(Copy).ToList();
    }

    private static ClientConfigStateEntry Copy(ClientConfigStateEntry e) => new()
    {
        ClientKind = e.ClientKind, FilePath = e.FilePath, KeyPath = e.KeyPath, OriginalAbsent = e.OriginalAbsent,
        OriginalValueJson = e.OriginalValueJson, AppliedValueJson = e.AppliedValueJson, AppliedAt = e.AppliedAt,
    };

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
        _lists.TryRemove(entry.ClientKind, out _);
    }

    public void Delete(string clientKind, string filePath, string keyPath)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "DELETE FROM client_config_state WHERE client_kind = @client_kind AND file_path = @file_path AND key_path = @key_path",
            new { client_kind = clientKind, file_path = filePath, key_path = keyPath });
        _lists.TryRemove(clientKind, out _);
    }

    public void DeleteAll(string clientKind)
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM client_config_state WHERE client_kind = @client_kind",
            new { client_kind = clientKind });
        _lists.TryRemove(clientKind, out _);
    }
}
