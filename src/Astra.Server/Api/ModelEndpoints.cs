using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Models;
using Astra.Core.Seed;
using Astra.Data;
using Astra.Gateway.Pipeline;

namespace Astra.Server.Api;

/// <summary>/api/models (system catalog + per-provider prices) and /api/billing/simulate.</summary>
public static class ModelEndpoints
{
    // API field names (camelCase, web ModelField) ↔ storage names kept in user_modified_fields_json.
    private static readonly Dictionary<string, string> FieldToStorage = new(StringComparer.Ordinal)
    {
        ["displayName"] = "display_name",
        ["family"] = "family",
        ["aliases"] = "aliases",
        ["contextWindow"] = "context_window",
        ["maxOutputTokens"] = "max_output_tokens",
        ["capabilities"] = "capabilities",
        ["pricing"] = "pricing",
    };

    private static readonly Dictionary<string, string> StorageToField = FieldToStorage.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static void MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/models");

        g.MapGet("", async (string? search, string? vendor, AstraDatabase db, CancellationToken ct) =>
        {
            var models = await db.Models.ListAsync(search, ct);
            var keys = await db.Models.GetPriceKeysByModelAsync(ct);
            return Results.Ok(models
                .Where(m => string.IsNullOrEmpty(vendor) || string.Equals(m.Vendor, vendor, StringComparison.OrdinalIgnoreCase))
                .Select(m => ToDto(m, keys.GetValueOrDefault(m.Id) ?? [])));
        });

        g.MapGet("/{id}", async (string id, AstraDatabase db, CancellationToken ct) =>
            await db.Models.GetAsync(id, ct) is { } m ? Results.Ok(await DetailAsync(db, m, ct)) : NotFound(id));

        g.MapPost("", async (ModelInput input, AstraDatabase db, CancellationToken ct) =>
        {
            var id = input.Id?.Trim();
            if (string.IsNullOrEmpty(id)) return Bad("id is required");
            if (await db.Models.GetAsync(id, ct) is not null) return Results.Json(new ErrorBody($"Model '{id}' already exists"), statusCode: 409);
            var model = new SystemModel { Id = id, Source = "user", Enabled = input.Enabled ?? true };
            if (Apply(model, input, trackChanges: false) is { } error) return Bad(error);
            await db.Models.InsertAsync(model, ct);
            return Results.Ok(ToDto(model, []));
        });

        g.MapPut("/{id}", async (string id, ModelInput input, AstraDatabase db, CancellationToken ct) =>
        {
            if (await db.Models.GetAsync(id, ct) is not { } model) return NotFound(id);
            if (Apply(model, input, trackChanges: model.Source != "user") is { } error) return Bad(error);
            await db.Models.UpdateAsync(model, ct);
            var keys = await db.Models.GetPriceKeysByModelAsync(ct);
            return Results.Ok(ToDto(model, keys.GetValueOrDefault(id) ?? []));
        });

        g.MapDelete("/{id}", async (string id, AstraDatabase db, CancellationToken ct) =>
        {
            if (!await db.Models.DeleteAsync(id, ct)) return NotFound(id);
            // provider_models.system_model_id has no FK: unlink dangling references.
            foreach (var p in await db.Providers.ListAsync(ct))
            {
                foreach (var pm in (await db.Providers.ListModelsAsync(p.Id, ct)).Where(pm => pm.SystemModelId == id))
                {
                    pm.SystemModelId = null;
                    await db.Providers.UpdateModelAsync(pm, ct);
                }
            }
            return Results.NoContent();
        });

        g.MapPost("/{id}/reset-field", async (string id, ResetFieldInput input, AstraDatabase db, CancellationToken ct) =>
        {
            if (await db.Models.GetAsync(id, ct) is not { } model) return NotFound(id);
            if (!FieldToStorage.TryGetValue(input.Field, out var storage)) return Bad($"Unknown field '{input.Field}'");
            var seed = SeedCatalog.Current.Models.FirstOrDefault(s => s.Id == id);
            if (seed is null) return Bad("This model has no seed value to restore");
            var fresh = SeedCatalog.ToSystemModel(seed, SeedCatalog.Current.SeedVersion, DateTimeOffset.UtcNow);
            switch (input.Field)
            {
                case "displayName": model.DisplayName = fresh.DisplayName; break;
                case "family": model.Family = fresh.Family; break;
                case "aliases": model.Aliases = fresh.Aliases; break;
                case "contextWindow": model.ContextWindow = fresh.ContextWindow; break;
                case "maxOutputTokens": model.MaxOutputTokens = fresh.MaxOutputTokens; break;
                case "capabilities": model.Capabilities = fresh.Capabilities; break;
                case "pricing": model.Pricing = fresh.Pricing; break;
            }
            model.UserModifiedFields.Remove(storage);
            await db.Models.UpdateAsync(model, ct);
            var keys = await db.Models.GetPriceKeysByModelAsync(ct);
            return Results.Ok(ToDto(model, keys.GetValueOrDefault(id) ?? []));
        });

        g.MapPut("/{id}/prices/{priceKey}", async (string id, string priceKey, ModelPriceInput input, AstraDatabase db, CancellationToken ct) =>
        {
            if (await db.Models.GetAsync(id, ct) is null) return NotFound(id);
            if (string.IsNullOrWhiteSpace(priceKey)) return Bad("priceKey is required");
            if (ParsePricing(input.Pricing, out var pricing) is { } error) return Bad(error);
            var existing = await db.Models.GetPriceAsync(id, priceKey, ct);
            var price = new ModelPrice
            {
                ModelId = id,
                PriceKey = priceKey,
                UpstreamModelId = string.IsNullOrWhiteSpace(input.UpstreamModelId) ? null : input.UpstreamModelId.Trim(),
                Pricing = pricing!,
                Source = existing?.Source ?? "user",
                UserModified = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await db.Models.UpsertPriceAsync(price, ct);
            return Results.Ok(ToPriceDto(price));
        });

        g.MapDelete("/{id}/prices/{priceKey}", async (string id, string priceKey, AstraDatabase db, CancellationToken ct) =>
            await db.Models.DeletePriceAsync(id, priceKey, ct) ? Results.NoContent() : NotFound($"{id}@{priceKey}"));

        app.MapPost("/api/billing/simulate", SimulateAsync);
    }

    private static async Task<IResult> SimulateAsync(BillingSimulateRequest req, AstraDatabase db,
        EffectiveModelResolver resolver, CancellationToken ct)
    {
        var locale = req.Locale is "en" ? "en" : "zh";
        ResolvedPricing resolved;
        string label = req.ModelId ?? "simulated";
        string? providerLabel = null;

        if (req.Pricing is not null)
        {
            if (ParsePricing(req.Pricing, out var schedule) is { } error) return Bad(error);
            resolved = new ResolvedPricing(schedule, PricingSource.SystemDefault, null, req.ProviderMultiplier ?? 1m);
        }
        else if (!string.IsNullOrEmpty(req.ProviderId) && !string.IsNullOrEmpty(req.ModelId))
        {
            if (await db.Providers.GetAsync(req.ProviderId, ct) is not { } provider) return NotFound(req.ProviderId);
            var (_, effective) = await resolver.ResolveByModelIdAsync(provider, req.ModelId, ct);
            resolved = effective.Pricing;
            if (req.ProviderMultiplier is { } m && resolved.Source != PricingSource.ProviderOverride) resolved = resolved with { Multiplier = m };
            providerLabel = provider.Name;
        }
        else if (!string.IsNullOrEmpty(req.ModelId))
        {
            if (await db.Models.GetAsync(req.ModelId, ct) is not { } model) return NotFound(req.ModelId);
            resolved = PricingResolver.Resolve(null, null, null, model.Pricing, req.ProviderMultiplier ?? 1m);
        }
        else
        {
            return Bad("Provide pricing, or modelId (optionally with providerId)");
        }

        var ctx = BillingContext.From(resolved, req.RequestTimeUtc ?? DateTimeOffset.UtcNow, label, providerLabel, locale);
        var result = BillingEngine.Calculate(req.Usage.ToNormalized(), resolved.Schedule, ctx);
        return Results.Ok(BillingResultDto.From(result, resolved));
    }

    // ---------- mapping ----------

    internal static ModelDto ToDto(SystemModel m, List<string> priceKeys) => new()
    {
        Id = m.Id,
        DisplayName = m.DisplayName,
        Vendor = m.Vendor,
        Family = m.Family,
        Aliases = m.Aliases,
        ContextWindow = m.ContextWindow,
        MaxOutputTokens = m.MaxOutputTokens,
        Capabilities = m.Capabilities,
        Pricing = PricingJson.ToNode(m.Pricing),
        Source = m.Source,
        UserModifiedFields = ApiFields(m),
        Enabled = m.Enabled,
        ProviderPriceKeys = priceKeys,
        UpdatedAt = m.UpdatedAt,
    };

    private static async Task<ModelDetailDto> DetailAsync(AstraDatabase db, SystemModel m, CancellationToken ct)
    {
        var prices = await db.Models.ListPricesAsync(m.Id, ct);
        var overrides = new List<ProviderOverrideRefDto>();
        foreach (var p in await db.Providers.ListAsync(ct))
        {
            foreach (var pm in await db.Providers.ListModelsAsync(p.Id, ct))
            {
                if (pm.SystemModelId == m.Id && pm.Overrides.Pricing is not null)
                    overrides.Add(new ProviderOverrideRefDto(p.Id, p.Name, pm.Id, pm.ModelId, PricingJson.ToNode(pm.Overrides.Pricing)));
            }
        }
        return new ModelDetailDto
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            Vendor = m.Vendor,
            Family = m.Family,
            Aliases = m.Aliases,
            ContextWindow = m.ContextWindow,
            MaxOutputTokens = m.MaxOutputTokens,
            Capabilities = m.Capabilities,
            Pricing = PricingJson.ToNode(m.Pricing),
            Source = m.Source,
            UserModifiedFields = ApiFields(m),
            Enabled = m.Enabled,
            ProviderPriceKeys = prices.Select(p => p.PriceKey).ToList(),
            UpdatedAt = m.UpdatedAt,
            ProviderPrices = prices.Select(ToPriceDto).ToList(),
            ProviderOverrides = overrides,
        };
    }

    internal static ModelPriceDto ToPriceDto(ModelPrice p) =>
        new(p.PriceKey, p.UpstreamModelId, PricingJson.ToNode(p.Pricing), p.Source, p.UserModified, p.UpdatedAt);

    private static List<string> ApiFields(SystemModel m) =>
        m.UserModifiedFields.Select(f => StorageToField.GetValueOrDefault(f, f)).Distinct().ToList();

    /// <summary>Applies the input onto the model; for seed/sync models every changed field is marked user-modified.</summary>
    private static string? Apply(SystemModel model, ModelInput input, bool trackChanges)
    {
        if (string.IsNullOrWhiteSpace(input.DisplayName)) return "displayName is required";
        if (string.IsNullOrWhiteSpace(input.Vendor)) return "vendor is required";
        PricingSchedule? pricing = null;
        if (input.Pricing is not null && ParsePricing(input.Pricing, out pricing) is { } error) return error;

        void Track(string field, bool changed)
        {
            if (trackChanges && changed && !model.UserModifiedFields.Contains(FieldToStorage[field]))
                model.UserModifiedFields.Add(FieldToStorage[field]);
        }

        var aliases = input.Aliases?.Select(a => a.Trim()).Where(a => a.Length > 0).Distinct().ToList() ?? model.Aliases;
        Track("displayName", model.DisplayName != input.DisplayName);
        Track("family", model.Family != input.Family);
        Track("aliases", !aliases.SequenceEqual(model.Aliases));
        Track("contextWindow", model.ContextWindow != input.ContextWindow);
        Track("maxOutputTokens", model.MaxOutputTokens != input.MaxOutputTokens);
        if (input.Capabilities is not null) Track("capabilities", Json.Serialize(input.Capabilities) != Json.Serialize(model.Capabilities));
        Track("pricing", (pricing is null ? null : Json.Serialize(pricing)) != (model.Pricing is null ? null : Json.Serialize(model.Pricing)));

        model.DisplayName = input.DisplayName.Trim();
        model.Vendor = input.Vendor.Trim();
        model.Family = input.Family;
        model.Aliases = aliases;
        model.ContextWindow = input.ContextWindow;
        model.MaxOutputTokens = input.MaxOutputTokens;
        if (input.Capabilities is not null) model.Capabilities = input.Capabilities;
        model.Pricing = pricing;
        if (input.Enabled is { } enabled) model.Enabled = enabled;
        return null;
    }

    internal static string? ParsePricing(JsonNode? node, out PricingSchedule? pricing)
    {
        pricing = null;
        if (node is null) return "pricing is required";
        try
        {
            pricing = PricingJson.FromNode(node);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return $"Invalid pricing JSON: {ex.Message}";
        }
        if (pricing is null) return "pricing is required";
        var problems = pricing.Validate();
        return problems.Count == 0 ? null : "Invalid pricing: " + string.Join("; ", problems);
    }

    internal static IResult NotFound(string what) => Results.Json(new ErrorBody($"Not found: {what}"), statusCode: 404);

    internal static IResult Bad(string message) => Results.Json(new ErrorBody(message), statusCode: 400);
}
