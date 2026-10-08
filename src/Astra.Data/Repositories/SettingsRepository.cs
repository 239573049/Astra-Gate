using Dapper;
using Astra.Core;

namespace Astra.Data.Repositories;

/// <summary>Typed JSON values stored by key (table <c>settings</c>).</summary>
public sealed class SettingsRepository
{
    private readonly SqliteConnectionFactory _factory;

    public SettingsRepository(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>Deserializes the JSON stored under <paramref name="key"/>; null when the key is absent.</summary>
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var json = await conn.ExecuteScalarAsync<string?>(
            "SELECT value_json FROM settings WHERE key = @key", new { key });
        return Json.Deserialize<T>(json);
    }

    /// <summary>Serializes <paramref name="value"/> as JSON and upserts it under <paramref name="key"/>.</summary>
    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        var json = Json.Serialize(value);
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO settings(key, value_json) VALUES (@key, @json)
            ON CONFLICT(key) DO UPDATE SET value_json = excluded.value_json
            """, new { key, json });
    }

    /// <summary>Removes the key; true when a row was deleted.</summary>
    public async Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM settings WHERE key = @key", new { key }) > 0;
    }

    /// <summary>All settings as raw JSON strings keyed by setting key.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<SettingRow>(
            "SELECT key AS Key, value_json AS ValueJson FROM settings ORDER BY key");
        return rows.ToDictionary(r => r.Key, r => r.ValueJson);
    }

}

// Dapper.AOT only materializes rows into types it can see from outside the repository class; nested
// private types are silently left on vanilla Dapper, which dies under Native AOT (see its FAQ).
internal sealed class SettingRow
{
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "";
}
