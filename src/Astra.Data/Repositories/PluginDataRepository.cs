using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Plugins;
using Dapper;

namespace Astra.Data.Repositories;

/// <summary>A v2 plugin's own data (migration 0024): storage, protected secrets, grants, activity, dismissed successors.</summary>
public sealed class PluginDataRepository : IPluginDataStore
{
    private readonly SqliteConnectionFactory _factory;

    public PluginDataRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<string?> GetAsync(string pluginId, string key, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<string>("SELECT value_json FROM plugin_kv WHERE plugin_id = @plugin_id AND key = @key",
            new { plugin_id = pluginId, key });
    }

    public async Task SetAsync(string pluginId, string key, string valueJson, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO plugin_kv(plugin_id, key, value_json, updated_at_utc) VALUES (@plugin_id, @key, @value_json, @now)
            ON CONFLICT(plugin_id, key) DO UPDATE SET value_json = excluded.value_json, updated_at_utc = excluded.updated_at_utc
            """, new { plugin_id = pluginId, key, value_json = valueJson, now = Now() });
    }

    public async Task<bool> DeleteAsync(string pluginId, string key, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM plugin_kv WHERE plugin_id = @plugin_id AND key = @key", new { plugin_id = pluginId, key }) > 0;
    }

    public async Task<IReadOnlyList<PluginKvItem>> ListAsync(string pluginId, string prefix, int limit, string? after, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        // a prefix range rather than LIKE: keys may hold % and _
        var rows = await conn.QueryAsync<PluginKvRow>("""
            SELECT key, value_json, updated_at_utc FROM plugin_kv
            WHERE plugin_id = @plugin_id AND substr(key, 1, length(@prefix)) = @prefix AND (@after IS NULL OR key > @after)
            ORDER BY key LIMIT @limit
            """, new { plugin_id = pluginId, prefix, after, limit });
        return rows.Select(r => new PluginKvItem(r.Key, r.ValueJson, r.UpdatedAtUtc)).ToList();
    }

    public async Task<long> SizeAsync(string pluginId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleAsync<long>(
            "SELECT COALESCE(SUM(length(key) + length(value_json)), 0) FROM plugin_kv WHERE plugin_id = @plugin_id", new { plugin_id = pluginId });
    }

    public async Task ClearAsync(string pluginId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync("DELETE FROM plugin_kv WHERE plugin_id = @plugin_id", new { plugin_id = pluginId }, tx);
        await conn.ExecuteAsync("DELETE FROM plugin_secrets WHERE plugin_id = @plugin_id", new { plugin_id = pluginId }, tx);
        await tx.CommitAsync(ct);
    }

    public async Task<string?> GetSecretAsync(string pluginId, string name, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT value_protected FROM plugin_secrets WHERE plugin_id = @plugin_id AND name = @name", new { plugin_id = pluginId, name });
    }

    public async Task SetSecretAsync(string pluginId, string name, string protectedValue, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO plugin_secrets(plugin_id, name, value_protected, updated_at_utc) VALUES (@plugin_id, @name, @value, @now)
            ON CONFLICT(plugin_id, name) DO UPDATE SET value_protected = excluded.value_protected, updated_at_utc = excluded.updated_at_utc
            """, new { plugin_id = pluginId, name, value = protectedValue, now = Now() });
    }

    public async Task<bool> DeleteSecretAsync(string pluginId, string name, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM plugin_secrets WHERE plugin_id = @plugin_id AND name = @name",
            new { plugin_id = pluginId, name }) > 0;
    }

    public async Task<IReadOnlyList<string>> SecretNamesAsync(string pluginId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<string>("SELECT name FROM plugin_secrets WHERE plugin_id = @plugin_id ORDER BY name",
            new { plugin_id = pluginId })).ToList();
    }

    public async Task<IReadOnlyList<string>> GrantsAsync(string pluginId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var json = await conn.QuerySingleOrDefaultAsync<string>("SELECT permissions_json FROM plugin_grants WHERE plugin_id = @plugin_id",
            new { plugin_id = pluginId });
        if (json is null) return [];
        try
        {
            return JsonNode.Parse(json) is JsonArray a
                ? a.Select(n => n is JsonValue v && v.TryGetValue<string>(out var p) ? p : null).OfType<string>().ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task SetGrantsAsync(string pluginId, IReadOnlyList<string> permissions, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO plugin_grants(plugin_id, permissions_json, updated_at_utc) VALUES (@plugin_id, @json, @now)
            ON CONFLICT(plugin_id) DO UPDATE SET permissions_json = excluded.permissions_json, updated_at_utc = excluded.updated_at_utc
            """, new { plugin_id = pluginId, json = new JsonArray([.. permissions.Select(p => (JsonNode?)JsonValue.Create(p))]).ToJsonString(), now = Now() });
    }

    public async Task AddActivityAsync(PluginActivity activity, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO plugin_activity(plugin_id, at_utc, call, target, outcome) VALUES (@plugin_id, @at, @call, @target, @outcome)
            """, new
        {
            plugin_id = activity.PluginId,
            at = DateTimeOffsetHandler.ToStorage(activity.AtUtc),
            call = activity.Call,
            target = activity.Target,
            outcome = activity.Outcome,
        });
        // keep the newest 1000 lines a plugin has
        await conn.ExecuteAsync("""
            DELETE FROM plugin_activity WHERE plugin_id = @plugin_id AND id <= (
                SELECT id FROM plugin_activity WHERE plugin_id = @plugin_id ORDER BY id DESC LIMIT 1 OFFSET 1000)
            """, new { plugin_id = activity.PluginId });
    }

    public async Task<IReadOnlyList<PluginActivity>> ActivityAsync(string pluginId, int limit, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PluginActivityRow>("""
            SELECT plugin_id, at_utc, call, target, outcome FROM plugin_activity WHERE plugin_id = @plugin_id ORDER BY id DESC LIMIT @limit
            """, new { plugin_id = pluginId, limit });
        return rows.Select(r => new PluginActivity(r.PluginId, r.AtUtc, r.Call, r.Target, r.Outcome)).ToList();
    }

    public async Task<string?> DismissedSuccessorAsync(string spec, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<string>("SELECT version FROM plugin_successor_dismissals WHERE spec = @spec", new { spec });
    }

    public async Task SetDismissedSuccessorAsync(string spec, string? version, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        if (version is null)
            await conn.ExecuteAsync("DELETE FROM plugin_successor_dismissals WHERE spec = @spec", new { spec });
        else
            await conn.ExecuteAsync("""
                INSERT INTO plugin_successor_dismissals(spec, version) VALUES (@spec, @version)
                ON CONFLICT(spec) DO UPDATE SET version = excluded.version
                """, new { spec, version });
    }

    private static string Now() => DateTimeOffsetHandler.ToStorage(DateTimeOffset.UtcNow);
}

internal sealed class PluginKvRow
{
    public string Key { get; set; } = "";
    public string ValueJson { get; set; } = "";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class PluginActivityRow
{
    public string PluginId { get; set; } = "";
    public DateTimeOffset AtUtc { get; set; }
    public string Call { get; set; } = "";
    public string? Target { get; set; }
    public string Outcome { get; set; } = "";
}
