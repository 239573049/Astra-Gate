using System.Globalization;
using System.Text.Json.Nodes;
using Dapper;
using Astra.Core;
using Astra.Core.Models;

namespace Astra.Data.Repositories;

/// <summary>Provider instances (table <c>providers</c>) and their models (table <c>provider_models</c>).</summary>
public sealed class ProviderRepository
{
    private const string ProviderSelect = """
        SELECT id, template_id, price_key, template_version, template_snapshot_json, name, icon, category,
               endpoints_json, preferred_upstream_protocols_json, auth_scheme, api_key_enc, extra_headers_json,
               http_proxy, price_multiplier, adapter_id, settings_json, enabled, sort_order, notes, website,
               created_at, updated_at
        FROM providers
        """;

    private const string ProviderModelSelect = """
        SELECT id, provider_id, model_id, system_model_id, overrides_json, enabled, sort_order
        FROM provider_models
        """;

    private readonly SqliteConnectionFactory _factory;

    public ProviderRepository(SqliteConnectionFactory factory) => _factory = factory;

    // ----- providers -----

    /// <summary>All providers ordered by sort_order then name.</summary>
    public async Task<IReadOnlyList<Provider>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ProviderRow>($"{ProviderSelect} ORDER BY sort_order, name");
        return rows.Select(r => r.ToProvider()).ToList();
    }

    public async Task<Provider?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ProviderRow>($"{ProviderSelect} WHERE id = @id", new { id });
        return row?.ToProvider();
    }

    /// <summary>Inserts a new provider; fills <c>CreatedAt</c>/<c>UpdatedAt</c> when left default.</summary>
    public async Task InsertAsync(Provider provider, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (provider.CreatedAt == default) provider.CreatedAt = now;
        if (provider.UpdatedAt == default) provider.UpdatedAt = now;
        await using var conn = await _factory.OpenAsync(ct);
        await conn.ExecuteAsync(ProviderInsertSql, ProviderParams(provider));
    }

    /// <summary>Full update of every column; stamps <c>UpdatedAt</c>. Returns false when the id is unknown.</summary>
    public async Task<bool> UpdateAsync(Provider provider, CancellationToken ct = default)
    {
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(ProviderUpdateSql, ProviderParams(provider)) > 0;
    }

    /// <summary>Deletes the provider and (via cascade) its models and bindings. Returns false when absent.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM providers WHERE id = @id", new { id }) > 0;
    }

    /// <summary>Creation/duplication and its initial models commit together.</summary>
    public Task InsertWithModelsAsync(Provider provider, IReadOnlyList<ProviderModel> models, CancellationToken ct = default) =>
        SaveWithModelsAsync(provider, models, insert: true, ct);

    /// <summary>A template update and newly introduced default models commit together.</summary>
    public Task UpdateWithModelsAsync(Provider provider, IReadOnlyList<ProviderModel> models, CancellationToken ct = default) =>
        SaveWithModelsAsync(provider, models, insert: false, ct);

    private async Task SaveWithModelsAsync(Provider provider, IReadOnlyList<ProviderModel> models, bool insert, CancellationToken ct)
    {
        if (models.Any(m => m.ProviderId != provider.Id)) throw new ArgumentException("Models must belong to the provider");
        var now = DateTimeOffset.UtcNow;
        if (provider.CreatedAt == default) provider.CreatedAt = now;
        provider.UpdatedAt = now;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var changed = await conn.ExecuteAsync(new CommandDefinition(insert ? ProviderInsertSql : ProviderUpdateSql,
            ProviderParams(provider), transaction: tx, cancellationToken: ct));
        if (changed == 0) throw new InvalidOperationException("Provider no longer exists");
        foreach (var model in models)
            model.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(ModelInsertSql,
                ModelParams(model), transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>Checks bindings in the DELETE itself, so a concurrent bind cannot be silently cascaded away.</summary>
    public async Task<bool> DeleteIfUnboundAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM providers WHERE id = @id
            AND NOT EXISTS (SELECT 1 FROM client_bindings WHERE provider_id = @id)
            """, new { id }, cancellationToken: ct)) > 0;
    }

    public async Task ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var now = DateTimeOffsetHandler.ToStorage(DateTimeOffset.UtcNow);
        for (var i = 0; i < ids.Count; i++)
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE providers SET sort_order = @sort_order, updated_at = @now WHERE id = @id",
                new { sort_order = i, now, id = ids[i] }, transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    // ----- provider models -----

    /// <summary>The provider's models ordered by sort_order.</summary>
    public async Task<IReadOnlyList<ProviderModel>> ListModelsAsync(string providerId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<ProviderModelRow>(
            $"{ProviderModelSelect} WHERE provider_id = @provider_id ORDER BY sort_order, id",
            new { provider_id = providerId });
        return rows.Select(r => r.ToProviderModel()).ToList();
    }

    public async Task<ProviderModel?> GetModelAsync(string providerId, string modelId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ProviderModelRow>(
            $"{ProviderModelSelect} WHERE provider_id = @provider_id AND model_id = @model_id",
            new { provider_id = providerId, model_id = modelId });
        return row?.ToProviderModel();
    }

    public async Task<ProviderModel?> GetModelByIdAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ProviderModelRow>(
            $"{ProviderModelSelect} WHERE id = @id", new { id });
        return row?.ToProviderModel();
    }

    /// <summary>Inserts a provider model; fails on duplicate (provider_id, model_id). Fills the generated id.</summary>
    public async Task InsertModelAsync(ProviderModel model, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        model.Id = await conn.ExecuteScalarAsync<long>(ModelInsertSql, ModelParams(model));
    }

    /// <summary>A batch add is all-or-nothing (including generated row ids).</summary>
    public async Task InsertModelsAsync(IReadOnlyList<ProviderModel> models, CancellationToken ct = default)
    {
        if (models.Count == 0) return;
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var model in models)
            model.Id = await conn.ExecuteScalarAsync<long>(new CommandDefinition(ModelInsertSql,
                ModelParams(model), transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>Full update by row id. Returns false when the row does not exist.</summary>
    public async Task<bool> UpdateModelAsync(ProviderModel model, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("""
            UPDATE provider_models SET model_id = @model_id, system_model_id = @system_model_id,
                overrides_json = @overrides_json, enabled = @enabled, sort_order = @sort_order
            WHERE id = @id
            """, ModelParams(model)) > 0;
    }

    public async Task<bool> DeleteModelAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM provider_models WHERE id = @id", new { id }) > 0;
    }

    public async Task<bool> DeleteModelAsync(string providerId, string modelId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM provider_models WHERE provider_id = @provider_id AND model_id = @model_id",
            new { provider_id = providerId, model_id = modelId }) > 0;
    }

    /// <summary>Rewrites sort_order for the provider's models from the given row-id order.</summary>
    public async Task ReorderModelsAsync(string providerId, IReadOnlyList<long> orderedModelIds, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        for (var i = 0; i < orderedModelIds.Count; i++)
        {
            await conn.ExecuteAsync(
                "UPDATE provider_models SET sort_order = @sort_order WHERE id = @id AND provider_id = @provider_id",
                new { sort_order = i, id = orderedModelIds[i], provider_id = providerId }, transaction: tx);
        }
        await tx.CommitAsync(ct);
    }

    private const string ModelInsertSql = """
        INSERT INTO provider_models(provider_id, model_id, system_model_id, overrides_json, enabled, sort_order)
        VALUES (@provider_id, @model_id, @system_model_id, @overrides_json, @enabled, @sort_order)
        RETURNING id
        """;

    private const string ProviderUpdateSql = """
        UPDATE providers SET
            template_id = @template_id, price_key = @price_key, template_version = @template_version,
            template_snapshot_json = @template_snapshot_json, name = @name, icon = @icon, category = @category,
            endpoints_json = @endpoints_json, preferred_upstream_protocols_json = @preferred_upstream_protocols_json,
            auth_scheme = @auth_scheme, api_key_enc = @api_key_enc, extra_headers_json = @extra_headers_json,
            http_proxy = @http_proxy, price_multiplier = @price_multiplier, adapter_id = @adapter_id,
            settings_json = @settings_json, enabled = @enabled, sort_order = @sort_order, notes = @notes,
            website = @website, created_at = @created_at, updated_at = @updated_at
        WHERE id = @id
        """;

    private const string ProviderInsertSql = """
        INSERT INTO providers(id, template_id, price_key, template_version, template_snapshot_json, name, icon,
                              category, endpoints_json, preferred_upstream_protocols_json, auth_scheme, api_key_enc,
                              extra_headers_json, http_proxy, price_multiplier, adapter_id, settings_json, enabled,
                              sort_order, notes, website, created_at, updated_at)
        VALUES (@id, @template_id, @price_key, @template_version, @template_snapshot_json, @name, @icon,
                @category, @endpoints_json, @preferred_upstream_protocols_json, @auth_scheme, @api_key_enc,
                @extra_headers_json, @http_proxy, @price_multiplier, @adapter_id, @settings_json, @enabled,
                @sort_order, @notes, @website, @created_at, @updated_at)
        """;

    private static object ProviderParams(Provider p) => new
    {
        id = p.Id,
        template_id = p.TemplateId,
        price_key = p.PriceKey,
        template_version = p.TemplateVersion,
        template_snapshot_json = p.TemplateSnapshotJson,
        name = p.Name,
        icon = p.Icon,
        category = p.Category,
        endpoints_json = Json.Serialize(p.Endpoints),
        preferred_upstream_protocols_json = Json.Serialize(p.PreferredUpstreamProtocols),
        auth_scheme = p.AuthScheme,
        api_key_enc = p.ApiKeyEnc,
        extra_headers_json = Json.Serialize(p.ExtraHeaders),
        http_proxy = p.HttpProxy,
        price_multiplier = p.PriceMultiplier.ToString(CultureInfo.InvariantCulture),
        adapter_id = p.AdapterId,
        settings_json = Json.Serialize(p.Settings),
        enabled = p.Enabled,
        sort_order = p.SortOrder,
        notes = p.Notes,
        website = p.Website,
        created_at = DateTimeOffsetHandler.ToStorage(p.CreatedAt),
        updated_at = DateTimeOffsetHandler.ToStorage(p.UpdatedAt),
    };

    private static object ModelParams(ProviderModel m) => new
    {
        id = m.Id,
        provider_id = m.ProviderId,
        model_id = m.ModelId,
        system_model_id = m.SystemModelId,
        overrides_json = Json.Serialize(m.Overrides),
        enabled = m.Enabled,
        sort_order = m.SortOrder,
    };

    private sealed class ProviderRow
    {
        public string Id { get; set; } = "";
        public string? TemplateId { get; set; }
        public string? PriceKey { get; set; }
        public int? TemplateVersion { get; set; }
        public string? TemplateSnapshotJson { get; set; }
        public string Name { get; set; } = "";
        public string? Icon { get; set; }
        public string Category { get; set; } = "";
        public string? EndpointsJson { get; set; }
        public string? PreferredUpstreamProtocolsJson { get; set; }
        public string AuthScheme { get; set; } = "";
        public string? ApiKeyEnc { get; set; }
        public string? ExtraHeadersJson { get; set; }
        public string? HttpProxy { get; set; }

        /// <summary>Invariant decimal string (exact TEXT storage).</summary>
        public string PriceMultiplier { get; set; } = "1";

        public string? AdapterId { get; set; }
        public string? SettingsJson { get; set; }
        public bool Enabled { get; set; }
        public int SortOrder { get; set; }
        public string? Notes { get; set; }
        public string? Website { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public Provider ToProvider() => new()
        {
            Id = Id,
            TemplateId = TemplateId,
            PriceKey = PriceKey,
            TemplateVersion = TemplateVersion,
            TemplateSnapshotJson = TemplateSnapshotJson,
            Name = Name,
            Icon = Icon,
            Category = Category,
            Endpoints = Json.Deserialize<List<ProviderEndpoint>>(EndpointsJson) ?? [],
            PreferredUpstreamProtocols = Json.Deserialize<List<ApiProtocol>>(PreferredUpstreamProtocolsJson) ?? [],
            AuthScheme = AuthScheme,
            ApiKeyEnc = ApiKeyEnc,
            ExtraHeaders = Json.Deserialize<Dictionary<string, string>>(ExtraHeadersJson) ?? [],
            HttpProxy = HttpProxy,
            PriceMultiplier = decimal.TryParse(PriceMultiplier, NumberStyles.Number, CultureInfo.InvariantCulture, out var m)
                ? m
                : 1m,
            AdapterId = AdapterId,
            Settings = Json.Deserialize<JsonObject>(SettingsJson) ?? new JsonObject(),
            Enabled = Enabled,
            SortOrder = SortOrder,
            Notes = Notes,
            Website = Website,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }

    private sealed class ProviderModelRow
    {
        public long Id { get; set; }
        public string ProviderId { get; set; } = "";
        public string ModelId { get; set; } = "";
        public string? SystemModelId { get; set; }
        public string? OverridesJson { get; set; }
        public bool Enabled { get; set; }
        public int SortOrder { get; set; }

        public ProviderModel ToProviderModel() => new()
        {
            Id = Id,
            ProviderId = ProviderId,
            ModelId = ModelId,
            SystemModelId = SystemModelId,
            Overrides = Json.Deserialize<ModelOverrides>(OverridesJson) ?? new(),
            Enabled = Enabled,
            SortOrder = SortOrder,
        };
    }
}
