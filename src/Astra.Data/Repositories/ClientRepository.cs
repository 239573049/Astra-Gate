using Dapper;
using Astra.Core.Clients;

namespace Astra.Data.Repositories;

/// <summary>
/// Gateway clients (table <c>clients</c>) and their provider bindings (table <c>client_bindings</c>).
/// Client rows are created lazily — by default no client is configured.
/// </summary>
public sealed class ClientRepository
{
    private const string ClientSelect = """
        SELECT kind, enabled, token_id, selected_model, extra_json, applied_at
        FROM clients
        """;

    private const string BindingSelect = "SELECT client_kind, provider_id, priority, account_id FROM client_bindings";

    private readonly SqliteConnectionFactory _factory;

    public ClientRepository(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>All configured clients, ordered by kind. Empty when none is configured.</summary>
    public async Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<ClientRecord>($"{ClientSelect} ORDER BY kind")).ToList();
    }

    public async Task<ClientRecord?> GetAsync(string kind, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ClientRecord>($"{ClientSelect} WHERE kind = @kind", new { kind });
    }

    /// <summary>Inserts or updates the client row by kind.</summary>
    public async Task UpsertAsync(ClientRecord record, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("""
            INSERT INTO clients(kind, enabled, token_id, selected_model, extra_json, applied_at)
            VALUES (@kind, @enabled, @token_id, @selected_model, @extra_json, @applied_at)
            ON CONFLICT(kind) DO UPDATE SET
                enabled = excluded.enabled,
                token_id = excluded.token_id,
                selected_model = excluded.selected_model,
                extra_json = excluded.extra_json,
                applied_at = excluded.applied_at
            """, new
        {
            kind = record.Kind,
            enabled = record.Enabled,
            token_id = record.TokenId,
            selected_model = record.SelectedModel,
            extra_json = record.ExtraJson,
            applied_at = record.AppliedAt is { } applied ? DateTimeOffsetHandler.ToStorage(applied) : null,
        });
    }

    /// <summary>Clients whose configuration uses the token (<c>token_id</c>; null rows count as the default token).</summary>
    public async Task<IReadOnlyList<ClientRecord>> ListByTokenAsync(string tokenId, bool isDefault, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var where = isDefault ? "token_id = @token_id OR token_id IS NULL" : "token_id = @token_id";
        return (await conn.QueryAsync<ClientRecord>($"{ClientSelect} WHERE {where} ORDER BY kind", new { token_id = tokenId })).ToList();
    }

    /// <summary>Moves every client from one token to another (before deleting <paramref name="fromTokenId"/>). Returns the moved kinds.</summary>
    public async Task<IReadOnlyList<string>> ReassignTokenAsync(string fromTokenId, string toTokenId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<string>(
            "UPDATE clients SET token_id = @to WHERE token_id = @from RETURNING kind",
            new { from = fromTokenId, to = toTokenId })).ToList();
    }

    /// <summary>Creates the client row when missing (not enabled) — used before writing bindings/state.</summary>
    public async Task EnsureAsync(string kind, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("INSERT OR IGNORE INTO clients(kind, enabled) VALUES (@kind, 0)", new { kind });
    }

    /// <summary>Deletes the client row; bindings cascade. Config state is untouched (use <see cref="ClientConfigStateStore"/>).</summary>
    public async Task<bool> DeleteAsync(string kind, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM clients WHERE kind = @kind", new { kind }) > 0;
    }

    // ----- bindings -----

    /// <summary>The primary binding (lowest priority), or null when unbound.</summary>
    public async Task<ClientBinding?> GetBindingAsync(string clientKind, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.QueryFirstOrDefaultAsync<ClientBinding>(
            $"{BindingSelect} WHERE client_kind = @client_kind ORDER BY priority LIMIT 1",
            new { client_kind = clientKind });
    }

    /// <summary>Sets (or replaces) the binding for one priority slot; creates the client row when missing.</summary>
    public async Task SetBindingAsync(ClientBinding binding, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync("INSERT OR IGNORE INTO clients(kind, enabled) VALUES (@kind, 0)",
            new { kind = binding.ClientKind });
        await conn.ExecuteAsync("""
            INSERT INTO client_bindings(client_kind, provider_id, priority, account_id)
            VALUES (@client_kind, @provider_id, @priority, @account_id)
            ON CONFLICT(client_kind, priority) DO UPDATE SET
                provider_id = excluded.provider_id,
                account_id = excluded.account_id
            """, new
        {
            client_kind = binding.ClientKind,
            provider_id = binding.ProviderId,
            priority = binding.Priority,
            account_id = binding.AccountId,
        });
    }

    /// <summary>All bindings of one client ordered by priority — the order the gateway tries them in for a model.</summary>
    public async Task<IReadOnlyList<ClientBinding>> ListBindingsAsync(string clientKind, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<ClientBinding>(
            $"{BindingSelect} WHERE client_kind = @client_kind ORDER BY priority",
            new { client_kind = clientKind })).ToList();
    }

    /// <summary>
    /// Replaces every binding of the client with <paramref name="bindings"/>, renumbering priorities 0..n-1 in list
    /// order (one transaction); creates the client row when missing. Callers reject duplicate providers.
    /// </summary>
    public async Task ReplaceBindingsAsync(string clientKind, IReadOnlyList<ClientBinding> bindings, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync("INSERT OR IGNORE INTO clients(kind, enabled) VALUES (@kind, 0)", new { kind = clientKind }, transaction: tx);
        await conn.ExecuteAsync("DELETE FROM client_bindings WHERE client_kind = @client_kind", new { client_kind = clientKind }, transaction: tx);
        for (var i = 0; i < bindings.Count; i++)
            await conn.ExecuteAsync("""
                INSERT INTO client_bindings(client_kind, provider_id, priority, account_id)
                VALUES (@client_kind, @provider_id, @priority, @account_id)
                """, new
            {
                client_kind = clientKind,
                provider_id = bindings[i].ProviderId,
                priority = i,
                account_id = bindings[i].AccountId,
            }, transaction: tx);
        await tx.CommitAsync(ct);
    }

    /// <summary>Bindings pointing at the provider — check before deleting a bound provider.</summary>
    public async Task<IReadOnlyList<ClientBinding>> ListBindingsByProviderAsync(string providerId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<ClientBinding>(
            $"{BindingSelect} WHERE provider_id = @provider_id ORDER BY client_kind",
            new { provider_id = providerId })).ToList();
    }

    /// <summary>Removes the binding with the given priority; true when one was removed.</summary>
    public async Task<bool> ClearBindingAsync(string clientKind, int priority = 0, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM client_bindings WHERE client_kind = @client_kind AND priority = @priority",
            new { client_kind = clientKind, priority }) > 0;
    }
}
