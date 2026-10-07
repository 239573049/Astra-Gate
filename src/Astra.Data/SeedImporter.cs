using Astra.Core;
using Astra.Core.Models;
using Astra.Core.Seed;
using Astra.Data.Repositories;

namespace Astra.Data;

/// <summary>Summary of one seed import pass.</summary>
public sealed record SeedImportResult(
    int InsertedModels,
    int UpdatedModels,
    int SkippedModels,
    int UpsertedPrices,
    int SkippedPrices,
    int SeedVersion,

    /// <summary>True when the import was skipped entirely because the stored seed version is unchanged.</summary>
    bool Skipped);

/// <summary>
/// Imports <see cref="SeedCatalog.Current"/> into <c>models</c> and <c>model_prices</c>.
/// Seed models are upserted (fields listed in the model's <c>user_modified_fields</c> keep their stored
/// values; models with source=user/sync are never touched). Seed prices are upserted with source=seed
/// unless the row is user-modified or sync-owned. The applied version is tracked in settings key "seed_version";
/// a run with unchanged version (and no force) does no work.
/// </summary>
public sealed class SeedImporter
{
    /// <summary>Settings key holding the last imported seed version.</summary>
    public const string SeedVersionKey = "seed_version";

    private readonly SqliteConnectionFactory _factory;

    public SeedImporter(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<SeedImportResult> ImportAsync(bool force = false, CancellationToken ct = default)
    {
        var seed = SeedCatalog.Current;
        var settings = new SettingsRepository(_factory);
        var stored = await settings.GetAsync<int?>(SeedVersionKey, ct);
        if (!force && stored == seed.SeedVersion)
            return new SeedImportResult(0, 0, seed.Models.Count, 0, 0, seed.SeedVersion, Skipped: true);

        var models = new ModelRepository(_factory);
        var now = DateTimeOffset.UtcNow;
        int inserted = 0, updated = 0, skippedModels = 0, upsertedPrices = 0, skippedPrices = 0;

        foreach (var s in seed.Models)
        {
            var existing = await models.GetAsync(s.Id, ct);
            var importPrices = true;

            if (existing is null)
            {
                await models.InsertAsync(SeedCatalog.ToSystemModel(s, seed.SeedVersion, now), ct);
                inserted++;
            }
            else if (string.Equals(existing.Source, "seed", StringComparison.Ordinal))
            {
                await models.UpdateAsync(MergeSeed(existing, s, seed.SeedVersion, now), ct);
                updated++;
            }
            else
            {
                // Never touch models the user (or a sync) owns — and don't add seed prices under them either.
                skippedModels++;
                importPrices = false;
            }

            if (!importPrices) continue;
            foreach (var (priceKey, seedPrice) in s.ProviderPricing)
            {
                var current = await models.GetPriceAsync(s.Id, priceKey, ct);
                if (current is { UserModified: true } or { Source: "sync" })
                {
                    skippedPrices++;
                    continue;
                }
                await models.UpsertPriceAsync(new ModelPrice
                {
                    ModelId = s.Id,
                    PriceKey = priceKey,
                    UpstreamModelId = seedPrice.UpstreamModelId,
                    Pricing = seedPrice.Pricing.Clone(),
                    Source = "seed",
                    UserModified = false,
                    UpdatedAt = now,
                }, ct);
                upsertedPrices++;
            }
        }

        await settings.SetAsync(SeedVersionKey, seed.SeedVersion, ct);
        return new SeedImportResult(inserted, updated, skippedModels, upsertedPrices, skippedPrices, seed.SeedVersion, Skipped: false);
    }

    /// <summary>Seed values applied over the stored model, except the fields the user modified.</summary>
    private static SystemModel MergeSeed(SystemModel existing, SeedModel s, int seedVersion, DateTimeOffset now)
    {
        var merged = SeedCatalog.ToSystemModel(s, seedVersion, now);
        merged.CreatedAt = existing.CreatedAt;
        merged.Enabled = existing.Enabled;
        merged.Source = "seed";
        merged.UserModifiedFields = existing.UserModifiedFields; // protect user-modified fields from future seeds
        foreach (var field in existing.UserModifiedFields)
        {
            switch (field)
            {
                case "display_name": merged.DisplayName = existing.DisplayName; break;
                case "family": merged.Family = existing.Family; break;
                case "aliases": merged.Aliases = existing.Aliases; break;
                case "context_window": merged.ContextWindow = existing.ContextWindow; break;
                case "max_output_tokens": merged.MaxOutputTokens = existing.MaxOutputTokens; break;
                case "capabilities": merged.Capabilities = existing.Capabilities; break;
                case "pricing": merged.Pricing = existing.Pricing; break;
            }
        }
        return merged;
    }
}
