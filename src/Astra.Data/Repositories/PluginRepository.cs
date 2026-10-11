using Dapper;
using Astra.Core.Plugins;

namespace Astra.Data.Repositories;

/// <summary>
/// The installed plugins (table <c>plugins</c>) and the plugin picked for each shared provider id
/// (table <c>plugin_provider_prefs</c>) — what magpie keeps in plugins.json.
/// </summary>
public sealed class PluginRepository : IPluginListStore
{
    private const string Select = """
        SELECT spec, name, enabled, sort_order, options_json, source, installed_version, added_at_utc, updated_at_utc
        FROM plugins
        """;

    private readonly SqliteConnectionFactory _factory;

    public PluginRepository(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<PluginEntry>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<PluginEntry>($"{Select} ORDER BY sort_order, rowid")).ToList();
    }

    public async Task<PluginEntry?> GetAsync(string spec, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PluginEntry>($"{Select} WHERE spec = @spec", new { spec });
    }

    public async Task AddAsync(PluginEntry entry, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var now = Now();
        await conn.ExecuteAsync("""
            INSERT INTO plugins(spec, name, enabled, sort_order, options_json, source, installed_version, added_at_utc, updated_at_utc)
            VALUES (@spec, @name, @enabled, (SELECT COALESCE(MAX(sort_order), -1) + 1 FROM plugins), @options_json,
                    @source, @installed_version, @now, @now)
            ON CONFLICT(spec) DO UPDATE SET
                name = excluded.name,
                enabled = excluded.enabled,
                options_json = excluded.options_json,
                source = excluded.source,
                installed_version = excluded.installed_version,
                updated_at_utc = excluded.updated_at_utc
            """, new
        {
            spec = entry.Spec,
            name = entry.Name,
            enabled = entry.Enabled,
            options_json = entry.OptionsJson,
            source = entry.Source,
            installed_version = entry.InstalledVersion,
            now,
        });
    }

    public async Task ReplaceAsync(string oldSpec, PluginEntry entry, CancellationToken ct = default)
    {
        if (oldSpec == entry.Spec)
        {
            await AddAsync(entry, ct);
            return;
        }
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = Now();
        // the new spec takes the old one's place: its order, its added time and (below) its picks
        await conn.ExecuteAsync("DELETE FROM plugins WHERE spec = @spec", new { spec = entry.Spec }, tx);
        await conn.ExecuteAsync("""
            INSERT INTO plugins(spec, name, enabled, sort_order, options_json, source, installed_version, added_at_utc, updated_at_utc)
            SELECT @spec, @name, @enabled, sort_order, @options_json, @source, @installed_version, added_at_utc, @now
            FROM plugins WHERE spec = @old_spec
            """, new
        {
            spec = entry.Spec,
            name = entry.Name,
            enabled = entry.Enabled,
            options_json = entry.OptionsJson,
            source = entry.Source,
            installed_version = entry.InstalledVersion,
            now,
            old_spec = oldSpec,
        }, tx);
        await conn.ExecuteAsync("UPDATE plugin_provider_prefs SET spec = @spec WHERE spec = @old_spec",
            new { spec = entry.Spec, old_spec = oldSpec }, tx);
        await conn.ExecuteAsync("DELETE FROM plugins WHERE spec = @old_spec", new { old_spec = oldSpec }, tx);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> SetEnabledAsync(string spec, bool enabled, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("UPDATE plugins SET enabled = @enabled, updated_at_utc = @now WHERE spec = @spec",
            new { enabled, spec, now = Now() }) > 0;
    }

    public async Task<bool> SetOptionsAsync(string spec, string? optionsJson, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("UPDATE plugins SET options_json = @options_json, updated_at_utc = @now WHERE spec = @spec",
            new { options_json = optionsJson, spec, now = Now() }) > 0;
    }

    public async Task<bool> SetInstalledVersionAsync(string spec, string? version, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("UPDATE plugins SET installed_version = @version, updated_at_utc = @now WHERE spec = @spec",
            new { version, spec, now = Now() }) > 0;
    }

    public async Task<bool> RemoveAsync(string spec, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM plugins WHERE spec = @spec", new { spec }) > 0;
    }

    public async Task ReorderAsync(IReadOnlyList<string> specs, CancellationToken ct = default)
    {
        var current = await ListAsync(ct);
        var order = specs.Where(s => current.Any(p => p.Spec == s)).Distinct().ToList();
        order.AddRange(current.Select(p => p.Spec).Where(s => !order.Contains(s)));
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        for (var i = 0; i < order.Count; i++)
            await conn.ExecuteAsync("UPDATE plugins SET sort_order = @sort_order WHERE spec = @spec",
                new { sort_order = i, spec = order[i] }, tx);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> ListPreferencesAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PluginPreferenceRow>("SELECT provider_id, spec FROM plugin_provider_prefs");
        return rows.ToDictionary(r => r.ProviderId, r => r.Spec);
    }

    public async Task SetPreferenceAsync(string providerId, string spec, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO plugin_provider_prefs(provider_id, spec) VALUES (@provider_id, @spec)
            ON CONFLICT(provider_id) DO UPDATE SET spec = excluded.spec
            """, new { provider_id = providerId, spec });
    }

    private static string Now() => DateTimeOffsetHandler.ToStorage(DateTimeOffset.UtcNow);
}

internal sealed class PluginPreferenceRow
{
    public string ProviderId { get; set; } = "";
    public string Spec { get; set; } = "";
}
