using System.Data;
using System.Text.Json.Nodes;
using Dapper;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Models;
using Microsoft.Data.Sqlite;

namespace Astra.Data.Repositories;

/// <summary>
/// System models (table <c>models</c>) and their per-provider price tables (table <c>model_prices</c>).
/// JSON columns are serialized with <see cref="Json"/> storage options.
/// </summary>
public sealed class ModelRepository
{
    private const string ModelSelect = """
        SELECT id, display_name, vendor, family, aliases_json, context_window, max_output_tokens,
               capabilities_json, pricing_json, source, user_modified_fields_json, seed_version, enabled,
               created_at, updated_at
        FROM models
        """;

    private const string PriceSelect = """
        SELECT id, model_id, price_key, upstream_model_id, pricing_json, source, user_modified, updated_at
        FROM model_prices
        """;

    private readonly SqliteConnectionFactory _factory;

    public ModelRepository(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>Lists all models ordered by vendor then id; <paramref name="search"/> filters across id, display name, vendor, family and aliases.</summary>
    public async Task<IReadOnlyList<SystemModel>> ListAsync(string? search = null, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var models = (await conn.QueryAsync<ModelRow>(ModelSelect)).Select(r => r.ToModel()).ToList();
        if (string.IsNullOrWhiteSpace(search)) return models;
        var needle = search.Trim();
        return models.Where(m => Matches(m, needle)).ToList();
    }

    public async Task<SystemModel?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<ModelRow>($"{ModelSelect} WHERE id = @id", new { id });
        return row?.ToModel();
    }

    /// <summary>Inserts a new model; fills <c>CreatedAt</c>/<c>UpdatedAt</c> when left default.</summary>
    public async Task InsertAsync(SystemModel model, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (model.CreatedAt == default) model.CreatedAt = now;
        if (model.UpdatedAt == default) model.UpdatedAt = now;
        await using var conn = await _factory.OpenAsync(ct);
        await InsertCoreAsync(conn, model);
    }

    /// <summary>Full update of every column; stamps <c>UpdatedAt</c>. Returns false when the id is unknown.</summary>
    public async Task<bool> UpdateAsync(SystemModel model, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await UpdateCoreAsync(conn, model) > 0;
    }

    /// <summary>Deletes the model and (via cascade) its per-provider prices. Returns false when absent.</summary>
    public async Task<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync("DELETE FROM models WHERE id = @id", new { id }) > 0;
    }

    // ----- model_prices (per-provider system prices) -----

    public async Task<IReadOnlyList<ModelPrice>> ListPricesAsync(string modelId, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<PriceRow>($"{PriceSelect} WHERE model_id = @model_id ORDER BY price_key",
            new { model_id = modelId });
        return rows.Select(r => r.ToPrice()).ToList();
    }

    public async Task<ModelPrice?> GetPriceAsync(string modelId, string priceKey, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<PriceRow>(
            $"{PriceSelect} WHERE model_id = @model_id AND price_key = @price_key",
            new { model_id = modelId, price_key = priceKey });
        return row?.ToPrice();
    }

    /// <summary>Inserts or updates by (model_id, price_key); stamps <c>UpdatedAt</c> and fills the generated id.</summary>
    public async Task UpsertPriceAsync(ModelPrice price, CancellationToken ct = default)
    {
        if (price.UpdatedAt == default) price.UpdatedAt = DateTimeOffset.UtcNow;
        await using var conn = await _factory.OpenAsync(ct);
        await UpsertPriceCoreAsync(conn, price);
    }

    /// <summary>
    /// Writes a model-sync batch atomically: new models are inserted before the price rows that reference
    /// them (FK model_prices.model_id → models.id), then field updates and price upserts run on the same
    /// connection. Either every row commits or none does.
    /// </summary>
    public async Task<bool> ApplySyncBatchAsync(IReadOnlyList<SystemModel> newModels,
        IReadOnlyList<SystemModel> updatedModels, IReadOnlyList<ModelPrice> upsertPrices,
        IReadOnlyDictionary<string, SystemModel?> expectedModels,
        IReadOnlyDictionary<(string ModelId, string PriceKey), ModelPrice?> expectedPrices, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        ct.ThrowIfCancellationRequested();
        // Acquire the writer lock before reading expectations; do not upgrade a stale WAL read snapshot.
        await using var tx = conn.BeginTransaction(deferred: false);
        foreach (var (id, expected) in expectedModels)
        {
            var row = await conn.QuerySingleOrDefaultAsync<ModelRow>(new CommandDefinition(
                $"{ModelSelect} WHERE id = @id", new { id }, transaction: tx, cancellationToken: ct));
            if (!SameSnapshot(row?.ToModel(), expected)) return false;
        }
        foreach (var (key, expected) in expectedPrices)
        {
            var row = await conn.QuerySingleOrDefaultAsync<PriceRow>(new CommandDefinition(
                $"{PriceSelect} WHERE model_id = @model_id AND price_key = @price_key",
                new { model_id = key.ModelId, price_key = key.PriceKey }, transaction: tx, cancellationToken: ct));
            if (!SameSnapshot(row?.ToPrice(), expected)) return false;
        }
        foreach (var model in newModels) { ct.ThrowIfCancellationRequested(); await InsertCoreAsync(conn, model, tx); }
        foreach (var model in updatedModels) { ct.ThrowIfCancellationRequested(); await UpdateCoreAsync(conn, model, tx); }
        foreach (var price in upsertPrices) { ct.ThrowIfCancellationRequested(); await UpsertPriceCoreAsync(conn, price, tx); }
        await tx.CommitAsync(ct);
        return true;
    }

    private static bool SameSnapshot<T>(T? actual, T? expected) =>
        JsonNode.DeepEquals(JsonNode.Parse(Json.Serialize(actual)), JsonNode.Parse(Json.Serialize(expected)));

    private static Task<int> InsertCoreAsync(SqliteConnection conn, SystemModel model, IDbTransaction? tx = null) =>
        conn.ExecuteAsync("""
            INSERT INTO models(id, display_name, vendor, family, aliases_json, context_window, max_output_tokens,
                               capabilities_json, pricing_json, source, user_modified_fields_json, seed_version,
                               enabled, created_at, updated_at)
            VALUES (@id, @display_name, @vendor, @family, @aliases_json, @context_window, @max_output_tokens,
                    @capabilities_json, @pricing_json, @source, @user_modified_fields_json, @seed_version,
                    @enabled, @created_at, @updated_at)
            """, Params(model), tx);

    private static Task<int> UpdateCoreAsync(SqliteConnection conn, SystemModel model, IDbTransaction? tx = null)
    {
        model.UpdatedAt = DateTimeOffset.UtcNow;
        return conn.ExecuteAsync("""
            UPDATE models SET display_name = @display_name, vendor = @vendor, family = @family,
                aliases_json = @aliases_json, context_window = @context_window, max_output_tokens = @max_output_tokens,
                capabilities_json = @capabilities_json, pricing_json = @pricing_json, source = @source,
                user_modified_fields_json = @user_modified_fields_json, seed_version = @seed_version,
                enabled = @enabled, created_at = @created_at, updated_at = @updated_at
            WHERE id = @id
            """, Params(model), tx);
    }

    private static async Task UpsertPriceCoreAsync(SqliteConnection conn, ModelPrice price, IDbTransaction? tx = null)
    {
        if (price.UpdatedAt == default) price.UpdatedAt = DateTimeOffset.UtcNow;
        price.Id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO model_prices(model_id, price_key, upstream_model_id, pricing_json, source, user_modified, updated_at)
            VALUES (@model_id, @price_key, @upstream_model_id, @pricing_json, @source, @user_modified, @updated_at)
            ON CONFLICT(model_id, price_key) DO UPDATE SET
                upstream_model_id = excluded.upstream_model_id,
                pricing_json = excluded.pricing_json,
                source = excluded.source,
                user_modified = excluded.user_modified,
                updated_at = excluded.updated_at
            RETURNING id
            """, new
        {
            model_id = price.ModelId,
            price_key = price.PriceKey,
            upstream_model_id = price.UpstreamModelId,
            pricing_json = Json.Serialize(price.Pricing),
            source = price.Source,
            user_modified = price.UserModified,
            updated_at = DateTimeOffsetHandler.ToStorage(price.UpdatedAt),
        }, tx);
    }

    public async Task<bool> DeletePriceAsync(string modelId, string priceKey, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return await conn.ExecuteAsync(
            "DELETE FROM model_prices WHERE model_id = @model_id AND price_key = @price_key",
            new { model_id = modelId, price_key = priceKey }) > 0;
    }

    /// <summary>The model's price table keyed by price key (provider type) — input to <see cref="PricingResolver"/>.</summary>
    public async Task<Dictionary<string, PricingSchedule>> GetPriceTableAsync(string modelId, CancellationToken ct = default)
    {
        var prices = await ListPricesAsync(modelId, ct);
        return prices.ToDictionary(p => p.PriceKey, p => p.Pricing);
    }

    /// <summary>upstream_model_id → model_id for one price key (used by <see cref="ModelIdMatcher"/> auto-linking).</summary>
    public async Task<Dictionary<string, string>> GetUpstreamIdIndexAsync(string priceKey, CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<UpstreamRow>("""
            SELECT upstream_model_id AS UpstreamModelId, model_id AS ModelId
            FROM model_prices
            WHERE price_key = @price_key AND upstream_model_id IS NOT NULL
            """, new { price_key = priceKey });
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.UpstreamModelId)) index[row.UpstreamModelId] = row.ModelId;
        }
        return index;
    }

    /// <summary>Matcher candidates (system model id + aliases) for all enabled models.</summary>
    public async Task<IReadOnlyList<ModelIdMatcher.Candidate>> GetMatcherCandidatesAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<CandidateRow>(
            "SELECT id AS SystemModelId, aliases_json AS AliasesJson FROM models WHERE enabled = 1 ORDER BY id");
        return rows.Select(r => new ModelIdMatcher.Candidate(
            r.SystemModelId,
            Json.Deserialize<List<string>>(r.AliasesJson) ?? [])).ToList();
    }

    /// <summary>model_id → price keys that have a provider-specific price (for model lists).</summary>
    public async Task<Dictionary<string, List<string>>> GetPriceKeysByModelAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        var rows = await conn.QueryAsync<(string ModelId, string PriceKey)>(
            "SELECT model_id, price_key FROM model_prices ORDER BY model_id, price_key");
        return rows.GroupBy(r => r.ModelId).ToDictionary(g => g.Key, g => g.Select(r => r.PriceKey).ToList());
    }

    /// <summary>Every distinct price key used by any model price.</summary>
    public async Task<IReadOnlyList<string>> ListPriceKeysAsync(CancellationToken ct = default)
    {
        await using var conn = await _factory.OpenAsync(ct);
        return (await conn.QueryAsync<string>("SELECT DISTINCT price_key FROM model_prices ORDER BY price_key")).AsList();
    }

    private static bool Matches(SystemModel m, string needle) =>
        m.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || m.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || m.Vendor.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || (m.Family?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
        || m.Aliases.Any(a => a.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static object Params(SystemModel m) => new
    {
        id = m.Id,
        display_name = m.DisplayName,
        vendor = m.Vendor,
        family = m.Family,
        aliases_json = Json.Serialize(m.Aliases),
        context_window = m.ContextWindow,
        max_output_tokens = m.MaxOutputTokens,
        capabilities_json = Json.Serialize(m.Capabilities),
        pricing_json = m.Pricing is null ? null : Json.Serialize(m.Pricing),
        source = m.Source,
        user_modified_fields_json = Json.Serialize(m.UserModifiedFields),
        seed_version = m.SeedVersion,
        enabled = m.Enabled,
        created_at = DateTimeOffsetHandler.ToStorage(m.CreatedAt),
        updated_at = DateTimeOffsetHandler.ToStorage(m.UpdatedAt),
    };

    private sealed class ModelRow
    {
        public string Id { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string Vendor { get; set; } = "";
        public string? Family { get; set; }
        public string? AliasesJson { get; set; }
        public long? ContextWindow { get; set; }
        public long? MaxOutputTokens { get; set; }
        public string? CapabilitiesJson { get; set; }
        public string? PricingJson { get; set; }
        public string Source { get; set; } = "user";
        public string? UserModifiedFieldsJson { get; set; }
        public int? SeedVersion { get; set; }
        public bool Enabled { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public SystemModel ToModel() => new()
        {
            Id = Id,
            DisplayName = DisplayName,
            Vendor = Vendor,
            Family = Family,
            Aliases = Json.Deserialize<List<string>>(AliasesJson) ?? [],
            ContextWindow = ContextWindow,
            MaxOutputTokens = MaxOutputTokens,
            Capabilities = Json.Deserialize<ModelCapabilities>(CapabilitiesJson) ?? new(),
            Pricing = Json.Deserialize<PricingSchedule>(PricingJson),
            Source = Source,
            UserModifiedFields = Json.Deserialize<List<string>>(UserModifiedFieldsJson) ?? [],
            SeedVersion = SeedVersion,
            Enabled = Enabled,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
        };
    }

    private sealed class PriceRow
    {
        public long Id { get; set; }
        public string ModelId { get; set; } = "";
        public string PriceKey { get; set; } = "";
        public string? UpstreamModelId { get; set; }
        public string? PricingJson { get; set; }
        public string Source { get; set; } = "user";
        public bool UserModified { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public ModelPrice ToPrice() => new()
        {
            Id = Id,
            ModelId = ModelId,
            PriceKey = PriceKey,
            UpstreamModelId = UpstreamModelId,
            Pricing = Json.Deserialize<PricingSchedule>(PricingJson) ?? new(),
            Source = Source,
            UserModified = UserModified,
            UpdatedAt = UpdatedAt,
        };
    }

    private sealed class UpstreamRow
    {
        public string? UpstreamModelId { get; set; }
        public string ModelId { get; set; } = "";
    }

    private sealed class CandidateRow
    {
        public string SystemModelId { get; set; } = "";
        public string? AliasesJson { get; set; }
    }
}
