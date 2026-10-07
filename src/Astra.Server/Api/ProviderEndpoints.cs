using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;
using Astra.Gateway.Pipeline;
using Astra.Providers.Templates;

namespace Astra.Server.Api;

public sealed record ProviderDto(
    string Id, string? TemplateId, int? TemplateVersion, bool TemplateUpdateAvailable, string Name, string? Icon,
    string Category, List<ProviderEndpoint> Endpoints, List<ApiProtocol> PreferredUpstreamProtocols, string AuthScheme,
    bool HasApiKey, string? ApiKeyMasked, Dictionary<string, string> ExtraHeaders, string? HttpProxy, decimal PriceMultiplier,
    string? PriceKey, string? AdapterId, JsonObject Settings, bool Enabled, int SortOrder, string? Notes, string? Website,
    int ModelCount, IReadOnlyList<string> BoundClients, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ModelOverridesDto(string? DisplayName, long? ContextWindow, long? MaxOutputTokens, ModelCapabilities? Capabilities, JsonNode? Pricing);
public sealed record EffectiveModelDto(string DisplayName, long? ContextWindow, long? MaxOutputTokens, ModelCapabilities Capabilities);
public sealed record ProviderModelDto(long Id, string ProviderId, string ModelId, string? SystemModelId, bool Enabled, int SortOrder,
    ModelOverridesDto Overrides, EffectiveModelDto Effective, Dictionary<string, string> Origins, string PricingSource,
    string? PriceKey, decimal Multiplier, JsonNode? EffectivePricing);

public static class ProviderEndpoints
{
    public static void MapProviderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/provider-templates", (ProviderTemplateCatalog catalog) => Results.Ok(catalog.All));
        app.MapGet("/api/price-keys", async (ProviderTemplateCatalog catalog, AstraDatabase db, CancellationToken ct) =>
        {
            var keys = new Dictionary<string, PriceKeyInfo>(StringComparer.Ordinal);
            foreach (var t in catalog.All)
            {
                Add(t.PriceKey ?? t.Id, t.Name, t.Id);
                foreach (var v in t.Variants ?? []) if (v.PriceKey is { } key) Add(key, $"{t.Name} · {v.Name}", t.Id);
            }
            foreach (var key in await db.Models.ListPriceKeysAsync(ct))
                if (!keys.ContainsKey(key)) keys[key] = new PriceKeyInfo(key, key, []);
            return Results.Ok(keys.Values.OrderBy(k => k.Label));

            void Add(string key, string label, string templateId)
            {
                if (keys.TryGetValue(key, out var old))
                { if (!old.TemplateIds.Contains(templateId)) old.TemplateIds.Add(templateId); }
                else keys[key] = new PriceKeyInfo(key, label, [templateId]);
            }
        });

        var group = app.MapGroup("/api/providers").AddEndpointFilter<AdminApiErrorFilter>()
            .AddEndpointFilter(new ProviderWriteFilter());
        group.MapGet("", async (AstraDatabase db, ProviderTemplateCatalog catalog, ISecretProtector secrets, CancellationToken ct) =>
        {
            var result = new List<ProviderDto>();
            foreach (var p in await db.Providers.ListAsync(ct)) result.Add(await ToDtoAsync(p, db, catalog, secrets, ct));
            return Results.Ok(result);
        });
        group.MapGet("/{id}", async (string id, AstraDatabase db, ProviderTemplateCatalog catalog, ISecretProtector secrets, CancellationToken ct) =>
            Results.Ok(await ToDtoAsync(await RequireAsync(db, id, ct), db, catalog, secrets, ct)));

        group.MapPost("", async (JsonObject body, AstraDatabase db, ProviderTemplateCatalog catalog, ISecretProtector secrets,
            CancellationToken ct) =>
        {
            var templateId = String(body, "templateId");
            var variantId = String(body, "variantId");
            if (templateId is null && variantId is not null) throw new AdminApiException(400, "variantId requires a template");
            var template = templateId is null ? null : catalog.Get(templateId)?.WithVariant(variantId)
                ?? throw new AdminApiException(404, "Provider template not found");
            // 订阅类模版是单实例（plan §5.4）：多个账号登录到同一个提供商内部，而不是建多个提供商。
            if (template is { Category: "subscription" })
            {
                var existing = (await db.Providers.ListAsync(ct)).FirstOrDefault(p => p.TemplateId == template.Id);
                if (existing is not null)
                    throw new AdminApiException(409, "订阅类模版只能创建一个提供商，请在已有提供商内添加登录账号",
                        new { existingProviderId = existing.Id, existingProviderName = existing.Name });
            }
            var provider = new Provider
            {
                Id = Ulid.NewUlid(), TemplateId = template?.Id, TemplateVersion = template?.Version,
                TemplateSnapshotJson = template is null ? null : Snapshot(template, variantId),
                Name = String(body, "name") ?? template?.Name ?? "Custom provider", Icon = template?.Icon,
                Category = template?.Category ?? "custom", Endpoints = template?.Endpoints ?? [],
                PreferredUpstreamProtocols = template?.PreferredUpstreamProtocols ?? [],
                AuthScheme = String(body, "authScheme") ?? template?.Auth.Scheme ?? AuthSchemes.Bearer,
                PriceKey = body["priceKey"] is null ? template?.PriceKey ?? template?.Id : Read<string>(body["priceKey"]).Trim(),
                PriceMultiplier = template?.DefaultPriceMultiplier ?? 1m, AdapterId = template?.Adapter,
                ExtraHeaders = template?.DefaultHeaders ?? [], Settings = template?.Settings ?? new JsonObject(), Website = template?.Website,
                SortOrder = (await db.Providers.ListAsync(ct)).Select(p => p.SortOrder).DefaultIfEmpty(-1).Max() + 1,
            };
            if (body["endpoints"] is not null) provider.Endpoints = Read<List<ProviderEndpoint>>(body["endpoints"]!);
            Validate(provider);
            if (provider.PreferredUpstreamProtocols.Count == 0) provider.PreferredUpstreamProtocols = provider.Endpoints.Select(e => e.Protocol).Distinct().ToList();
            var key = String(body, "apiKey");
            if (key?.Any(char.IsControl) == true) throw new AdminApiException(400, "apiKey must not contain control characters");
            if (template is { RequiresApiKey: true } && provider.AuthScheme is not AuthSchemes.None and not AuthSchemes.OAuthSubscription && key is null)
                throw new AdminApiException(400, "API key is required");
            provider.ApiKeyEnc = key is null ? null : secrets.Protect(key);
            var modelIds = body["models"] is null ? template?.Models ?? [] : Read<List<string>>(body["models"]!);
            ValidateModelIds(modelIds);
            var initialModels = await PlanModelsAsync(db, provider, modelIds, ct);
            await db.Providers.InsertWithModelsAsync(provider, initialModels, ct);
            return Results.Ok(await ToDtoAsync(provider, db, catalog, secrets, ct));
        });

        group.MapPatch("/{id}", async (string id, JsonObject body, AstraDatabase db, ProviderTemplateCatalog catalog,
            ISecretProtector secrets, CancellationToken ct) =>
        {
            var p = await RequireAsync(db, id, ct);
            if (body.ContainsKey("name")) p.Name = String(body, "name") ?? "";
            if (body.ContainsKey("endpoints")) p.Endpoints = Read<List<ProviderEndpoint>>(body["endpoints"]);
            if (body.ContainsKey("preferredUpstreamProtocols")) p.PreferredUpstreamProtocols = Read<List<ApiProtocol>>(body["preferredUpstreamProtocols"]);
            if (body.ContainsKey("authScheme")) p.AuthScheme = String(body, "authScheme") ?? "";
            if (body.ContainsKey("extraHeaders")) p.ExtraHeaders = Read<Dictionary<string, string>>(body["extraHeaders"]);
            if (body.ContainsKey("httpProxy")) p.HttpProxy = String(body, "httpProxy");
            if (body.ContainsKey("priceMultiplier")) p.PriceMultiplier = Read<decimal>(body["priceMultiplier"]);
            if (body.ContainsKey("priceKey")) p.PriceKey = body["priceKey"] is null ? null : Read<string>(body["priceKey"]).Trim();
            if (body.ContainsKey("settings")) p.Settings = Read<JsonObject>(body["settings"]);
            if (body.ContainsKey("enabled")) p.Enabled = Read<bool>(body["enabled"]);
            if (body.ContainsKey("notes")) p.Notes = String(body, "notes");
            Validate(p);
            if (body.ContainsKey("apiKey"))
            {
                if (body["apiKey"] is null) throw new AdminApiException(400, "apiKey must be a string (empty clears it)");
                var key = Read<string>(body["apiKey"]).Trim();
                if (key.Any(char.IsControl)) throw new AdminApiException(400, "apiKey must not contain control characters");
                p.ApiKeyEnc = key.Length == 0 ? null : secrets.Protect(key);
            }
            await db.Providers.UpdateAsync(p, ct);
            return Results.Ok(await ToDtoAsync(p, db, catalog, secrets, ct));
        });

        group.MapDelete("/{id}", async (string id, AstraDatabase db, CancellationToken ct) =>
        {
            await RequireAsync(db, id, ct);
            if (!await db.Providers.DeleteIfUnboundAsync(id, ct))
            {
                var bound = (await db.Clients.ListBindingsByProviderAsync(id, ct)).Select(b => b.ClientKind).Distinct().ToList();
                throw new AdminApiException(409, "Provider is bound to clients", new { boundClients = bound });
            }
            return Results.NoContent();
        });
        group.MapPost("/{id}/duplicate", async (string id, AstraDatabase db, ProviderTemplateCatalog catalog, ISecretProtector secrets, CancellationToken ct) =>
        {
            var p = await RequireAsync(db, id, ct);
            // 复制等于第二个实例：订阅类提供商只允许一个，账号在原提供商内添加。
            if (catalog.Get(p.TemplateId ?? "") is { Category: "subscription" })
                throw new AdminApiException(409, "订阅类提供商不支持复制，请在提供商内添加登录账号");
            var oldModels = await db.Providers.ListModelsAsync(id, ct);
            p.Id = Ulid.NewUlid(); p.Name += " (copy)"; p.CreatedAt = p.UpdatedAt = DateTimeOffset.UtcNow;
            p.SortOrder = (await db.Providers.ListAsync(ct)).Select(x => x.SortOrder).DefaultIfEmpty(-1).Max() + 1;
            foreach (var m in oldModels) { m.Id = 0; m.ProviderId = p.Id; }
            await db.Providers.InsertWithModelsAsync(p, oldModels, ct);
            return Results.Ok(await ToDtoAsync(p, db, catalog, secrets, ct));
        });
        group.MapPut("/order", async (JsonObject body, AstraDatabase db, CancellationToken ct) =>
        {
            var ids = Read<List<string>>(body["ids"]);
            var providers = await db.Providers.ListAsync(ct);
            if (ids.Count != providers.Count || ids.Distinct().Count() != ids.Count || ids.Any(id => providers.All(p => p.Id != id)))
                throw new AdminApiException(400, "ids must contain each provider exactly once");
            await db.Providers.ReorderAsync(ids, ct);
            return Results.NoContent();
        });

        group.MapGet("/{id}/models", async (string id, AstraDatabase db, EffectiveModelResolver resolver, CancellationToken ct) =>
        {
            var p = await RequireAsync(db, id, ct);
            var result = new List<ProviderModelDto>();
            foreach (var m in await db.Providers.ListModelsAsync(id, ct)) result.Add(await ToModelDtoAsync(p, m, resolver, ct));
            return Results.Ok(result);
        });
        group.MapPost("/{id}/models", async (string id, JsonObject body, AstraDatabase db, EffectiveModelResolver resolver, CancellationToken ct) =>
        {
            var p = await RequireAsync(db, id, ct);
            var ids = Read<List<string>>(body["modelIds"]);
            ValidateModelIds(ids);
            var added = await PlanModelsAsync(db, p, ids, ct);
            await db.Providers.InsertModelsAsync(added, ct);
            var result = new List<ProviderModelDto>();
            foreach (var m in added) result.Add(await ToModelDtoAsync(p, m, resolver, ct));
            return Results.Ok(result);
        });
        group.MapPatch("/{id}/models/{pmId:long}", async (string id, long pmId, JsonObject body, AstraDatabase db,
            EffectiveModelResolver resolver, CancellationToken ct) =>
        {
            var p = await RequireAsync(db, id, ct);
            var m = await db.Providers.GetModelByIdAsync(pmId, ct);
            if (m is null || m.ProviderId != id) throw new AdminApiException(404, "Provider model not found");
            if (body.ContainsKey("systemModelId"))
            {
                m.SystemModelId = String(body, "systemModelId");
                if (m.SystemModelId is { } linked && await db.Models.GetAsync(linked, ct) is null)
                    throw new AdminApiException(400, "Linked system model not found");
            }
            if (body.ContainsKey("enabled")) m.Enabled = Read<bool>(body["enabled"]);
            if (body.ContainsKey("overrides"))
            {
                if (body["overrides"] is not JsonObject o) throw new AdminApiException(400, "overrides must be an object");
                if (o.ContainsKey("displayName")) m.Overrides.DisplayName = String(o, "displayName");
                if (o.ContainsKey("contextWindow")) m.Overrides.ContextWindow = NullableLong(o["contextWindow"]);
                if (o.ContainsKey("maxOutputTokens")) m.Overrides.MaxOutputTokens = NullableLong(o["maxOutputTokens"]);
                if (o.ContainsKey("capabilities")) m.Overrides.Capabilities = o["capabilities"] is null ? null : Read<ModelCapabilities>(o["capabilities"]);
                if (o.ContainsKey("pricing"))
                {
                    if (o["pricing"] is null) m.Overrides.Pricing = null;
                    else if (ModelEndpoints.ParsePricing(o["pricing"], out var price) is { } error) throw new AdminApiException(400, error);
                    else m.Overrides.Pricing = price;
                }
            }
            await db.Providers.UpdateModelAsync(m, ct);
            return Results.Ok(await ToModelDtoAsync(p, m, resolver, ct));
        });
        group.MapDelete("/{id}/models/{pmId:long}", async (string id, long pmId, AstraDatabase db, CancellationToken ct) =>
        {
            var m = await db.Providers.GetModelByIdAsync(pmId, ct);
            if (m is null || m.ProviderId != id) throw new AdminApiException(404, "Provider model not found");
            await db.Providers.DeleteModelAsync(pmId, ct);
            return Results.NoContent();
        });

        group.MapGet("/{id}/template-update", async (string id, AstraDatabase db, ProviderTemplateCatalog catalog, CancellationToken ct) =>
        {
            var p = await RequireAsync(db, id, ct);
            var (old, latest, _) = Templates(p, catalog);
            return Results.Ok(new { currentVersion = p.TemplateVersion ?? 0, latestVersion = latest?.Version ?? 0,
                changes = latest is not null && latest.Version > (p.TemplateVersion ?? 0)
                    ? TemplateChanges(old, latest) : [] });
        });
        group.MapPost("/{id}/template-update", async (string id, AstraDatabase db, ProviderTemplateCatalog catalog, ISecretProtector secrets, CancellationToken ct) =>
        {
            var p = await RequireAsync(db, id, ct);
            var (old, latest, variantId) = Templates(p, catalog);
            if (latest is null) throw new AdminApiException(400, "Provider has no current template");
            if (latest.Version > (p.TemplateVersion ?? 0))
            {
                // Three-way merge: a value changed by the user never gets overwritten by a new default.
                if (old is not null)
                {
                    if (p.Name == old.Name) p.Name = latest.Name;
                    if (p.Category == old.Category) p.Category = latest.Category;
                    if (Same(p.Endpoints, old.Endpoints)) p.Endpoints = latest.Clone().Endpoints;
                    if (Same(p.PreferredUpstreamProtocols, old.PreferredUpstreamProtocols)) p.PreferredUpstreamProtocols = latest.Clone().PreferredUpstreamProtocols;
                    if (p.AuthScheme == old.Auth.Scheme) p.AuthScheme = latest.Auth.Scheme;
                    if (p.PriceKey == (old.PriceKey ?? old.Id)) p.PriceKey = latest.PriceKey ?? latest.Id;
                    if (p.PriceMultiplier == old.DefaultPriceMultiplier) p.PriceMultiplier = latest.DefaultPriceMultiplier;
                    if (p.AdapterId == old.Adapter) p.AdapterId = latest.Adapter;
                    if (p.Icon == old.Icon) p.Icon = latest.Icon;
                    if (p.Website == old.Website) p.Website = latest.Website;
                    MergeHeaders(p.ExtraHeaders, old.DefaultHeaders, latest.DefaultHeaders);
                    MergeSettings(p.Settings, old.Settings, latest.Settings);
                }
                var newIds = old is null ? [] : latest.Models.Except(old.Models, StringComparer.Ordinal).ToList();
                var added = await PlanModelsAsync(db, p, newIds, ct);
                p.TemplateVersion = latest.Version;
                p.TemplateSnapshotJson = Snapshot(latest, variantId);
                Validate(p);
                await db.Providers.UpdateWithModelsAsync(p, added, ct);
            }
            return Results.Ok(await ToDtoAsync(p, db, catalog, secrets, ct));
        });
        group.MapPost("/{id}/test", async (string id, JsonObject? body, ProviderProbe probe, CancellationToken ct) =>
            Results.Ok(await probe.TestAsync(id, body is null ? null : String(body, "modelId"), ct)));
        group.MapGet("/{id}/remote-models", async (string id, ProviderProbe probe, CancellationToken ct) =>
            Results.Ok(await probe.ModelsAsync(id, ct)));
    }

    internal static async Task<Provider> RequireAsync(AstraDatabase db, string id, CancellationToken ct) =>
        await db.Providers.GetAsync(id, ct) ?? throw new AdminApiException(404, "Provider not found");

    private static async Task<ProviderDto> ToDtoAsync(Provider p, AstraDatabase db, ProviderTemplateCatalog catalog, ISecretProtector secrets, CancellationToken ct)
    {
        string? mask = null;
        if (p.ApiKeyEnc is not null)
        {
            try { var key = secrets.Unprotect(p.ApiKeyEnc); mask = key.Length >= 12 ? key[..3] + "…" + key[^4..] : "••••"; }
            catch (CryptographicException) { mask = "••••"; }
        }
        var bound = (await db.Clients.ListBindingsByProviderAsync(p.Id, ct)).Select(b => b.ClientKind).Distinct().ToList();
        return new ProviderDto(p.Id, p.TemplateId, p.TemplateVersion,
            p.TemplateId is not null && catalog.Get(p.TemplateId) is { } t && t.Version > (p.TemplateVersion ?? 0),
            p.Name, p.Icon, p.Category, p.Endpoints, p.PreferredUpstreamProtocols, p.AuthScheme, p.ApiKeyEnc is not null, mask,
            p.ExtraHeaders, p.HttpProxy, p.PriceMultiplier, p.PriceKey, p.AdapterId, p.Settings, p.Enabled, p.SortOrder, p.Notes,
            p.Website, (await db.Providers.ListModelsAsync(p.Id, ct)).Count, bound, p.CreatedAt, p.UpdatedAt);
    }

    private static async Task<ProviderModelDto> ToModelDtoAsync(Provider p, ProviderModel m, EffectiveModelResolver resolver, CancellationToken ct)
    {
        var e = await resolver.ResolveAsync(p, m, ct);
        return new ProviderModelDto(m.Id, m.ProviderId, m.ModelId, m.SystemModelId, m.Enabled, m.SortOrder,
            new ModelOverridesDto(m.Overrides.DisplayName, m.Overrides.ContextWindow, m.Overrides.MaxOutputTokens, m.Overrides.Capabilities, PricingJson.ToNode(m.Overrides.Pricing)),
            new EffectiveModelDto(e.DisplayName, e.ContextWindow, e.MaxOutputTokens, e.Capabilities), e.Origins,
            BillingResultDto.SourceId(e.Pricing.Source), e.Pricing.PriceKey, e.Pricing.Multiplier, PricingJson.ToNode(e.Pricing.Schedule));
    }

    private static async Task<List<ProviderModel>> PlanModelsAsync(AstraDatabase db, Provider p, IReadOnlyList<string> ids, CancellationToken ct)
    {
        ValidateModelIds(ids);
        var existing = (await db.Providers.ListModelsAsync(p.Id, ct)).ToDictionary(m => m.ModelId, StringComparer.Ordinal);
        var order = existing.Values.Select(m => m.SortOrder).DefaultIfEmpty(-1).Max() + 1;
        var candidates = await db.Models.GetMatcherCandidatesAsync(ct);
        var priceKey = p.PriceKey ?? p.TemplateId;
        var index = priceKey is null ? null : await db.Models.GetUpstreamIdIndexAsync(priceKey, ct);
        var result = new List<ProviderModel>();
        foreach (var id in ids.Select(id => id.Trim()).Distinct(StringComparer.Ordinal))
        {
            if (existing.ContainsKey(id)) continue;
            var m = new ProviderModel { ProviderId = p.Id, ModelId = id, SystemModelId = ModelIdMatcher.Match(id, candidates, index), SortOrder = order++ };
            existing[id] = m; result.Add(m);
        }
        return result;
    }

    private static string Snapshot(ProviderTemplate template, string? variantId)
    {
        var node = JsonNode.Parse(Json.Serialize(template))!.AsObject();
        if (variantId is not null) node["variant_id"] = variantId;
        return node.ToJsonString();
    }

    private static (ProviderTemplate? Old, ProviderTemplate? Latest, string? VariantId) Templates(Provider p, ProviderTemplateCatalog catalog)
    {
        var old = Json.Deserialize<ProviderTemplate>(p.TemplateSnapshotJson);
        var latest = p.TemplateId is null ? null : catalog.Get(p.TemplateId);
        var variantId = p.TemplateSnapshotJson is null ? null : JsonNode.Parse(p.TemplateSnapshotJson)?["variant_id"]?.GetValue<string>();
        // Older snapshots stored the applied variant without its id; recover it from the endpoint defaults.
        variantId ??= old?.Variants?.FirstOrDefault(v => v.EndpointsOverride is not null && Same(old.Endpoints, v.EndpointsOverride))?.Id;
        if (variantId is not null && latest is not null && latest.Variants?.Any(v => v.Id == variantId) != true)
            throw new AdminApiException(409, "The selected template variant was removed; review the provider endpoints first");
        return (old, latest?.WithVariant(variantId), variantId);
    }

    private static List<string> TemplateChanges(ProviderTemplate? old, ProviderTemplate latest)
    {
        if (old is null) return ["No previous snapshot: preserve all configuration and record the current template version."];
        var changes = new List<string>();
        void Compare<T>(string field, T before, T after)
        {
            if (!Same(before, after)) changes.Add($"{field}: updated defaults (custom values are preserved)");
        }
        Compare("name", old.Name, latest.Name);
        Compare("category", old.Category, latest.Category);
        Compare("endpoints", old.Endpoints, latest.Endpoints);
        Compare("preferredUpstreamProtocols", old.PreferredUpstreamProtocols, latest.PreferredUpstreamProtocols);
        Compare("authScheme", old.Auth.Scheme, latest.Auth.Scheme);
        Compare("priceKey", old.PriceKey, latest.PriceKey);
        Compare("priceMultiplier", old.DefaultPriceMultiplier, latest.DefaultPriceMultiplier);
        Compare("adapter", old.Adapter, latest.Adapter);
        Compare("headers", old.DefaultHeaders, latest.DefaultHeaders);
        Compare("settings", old.Settings, latest.Settings);
        Compare("models", old.Models, latest.Models);
        Compare("icon", old.Icon, latest.Icon);
        Compare("website", old.Website, latest.Website);
        if (changes.Count == 0) changes.Add("Record the current template version; provider configuration is unchanged.");
        return changes;
    }

    private static void MergeHeaders(Dictionary<string, string> current, Dictionary<string, string> old, Dictionary<string, string> latest)
    {
        var before = new Dictionary<string, string>(old, StringComparer.OrdinalIgnoreCase);
        var after = new Dictionary<string, string>(latest, StringComparer.OrdinalIgnoreCase);
        foreach (var key in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var currentKey = current.Keys.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
            var hadDefault = before.TryGetValue(key, out var oldValue);
            if ((currentKey is null && !hadDefault) || (currentKey is not null && hadDefault && current[currentKey] == oldValue))
            {
                if (after.TryGetValue(key, out var value)) current[currentKey ?? key] = value;
                else if (currentKey is not null) current.Remove(currentKey);
            }
        }
    }

    private static void MergeSettings(JsonObject current, JsonObject old, JsonObject latest)
    {
        foreach (var key in old.Select(kv => kv.Key).Union(latest.Select(kv => kv.Key)))
        {
            var hasCurrent = current.TryGetPropertyValue(key, out var value);
            var hadDefault = old.TryGetPropertyValue(key, out var oldValue);
            if ((!hasCurrent && !hadDefault) || (hasCurrent && hadDefault && JsonNode.DeepEquals(value, oldValue)))
            {
                if (latest.TryGetPropertyValue(key, out var next)) current[key] = next?.DeepClone();
                else current.Remove(key);
            }
        }
    }

    /// <summary>Serialize read/modify/write operations, but never hold the lock during an upstream probe.</summary>
    private sealed class ProviderWriteFilter : IEndpointFilter
    {
        private readonly SemaphoreSlim _writes = new(1, 1);

        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var request = context.HttpContext.Request;
            if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || request.Path.Value?.EndsWith("/test", StringComparison.Ordinal) == true)
                return await next(context);
            await _writes.WaitAsync(context.HttpContext.RequestAborted);
            object? result;
            try { result = await next(context); }
            finally { _writes.Release(); }

            // Plan §7.6: an enabled OpenCode lists the bound provider's models in its config; keep that list in step
            // with whatever this write changed (models added/removed/renamed, provider enabled/disabled).
            if (context.HttpContext.GetRouteValue("id") is string id && result is not IStatusCodeHttpResult { StatusCode: >= 400 })
                await context.HttpContext.RequestServices.GetRequiredService<ClientService>()
                    .SyncOpenCodeModelsAsync(id, context.HttpContext.RequestAborted);
            return result;
        }
    }

    private static void Validate(Provider p)
    {
        if (string.IsNullOrWhiteSpace(p.Name)) throw new AdminApiException(400, "Provider name is required");
        if (p.Endpoints.Count == 0 || p.Endpoints.Any(e => e is null) || p.Endpoints.Select(e => e.Protocol).Distinct().Count() != p.Endpoints.Count)
            throw new AdminApiException(400, "Configure one endpoint per protocol");
        foreach (var e in p.Endpoints)
            if (!ApiProtocols.All.Contains(e.Protocol) || !Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var u) ||
                u.Scheme is not ("http" or "https") || u.UserInfo.Length != 0 || u.Fragment.Length != 0 || e.BaseUrl.Any(char.IsControl))
                throw new AdminApiException(400, "Endpoints must be http(s) URLs without embedded credentials or fragments");
        if (!AuthSchemes.All.Contains(p.AuthScheme)) throw new AdminApiException(400, "Unknown authentication scheme");
        if (p.PriceMultiplier < 0) throw new AdminApiException(400, "Price multiplier must be non-negative");
        if (p.HttpProxy is not null && (!Uri.TryCreate(p.HttpProxy, UriKind.Absolute, out var proxy) || proxy.Scheme is not ("http" or "https" or "socks5")))
            throw new AdminApiException(400, "Invalid HTTP/SOCKS proxy URL");
        if (p.ExtraHeaders.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.ExtraHeaders.Count)
            throw new AdminApiException(400, "Extra header names must be unique (case-insensitive)");
        foreach (var (key, value) in p.ExtraHeaders)
            if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[!#$%&'*+.^_`|~0-9A-Za-z-]+$") || value is null || value.Any(char.IsControl) ||
                key.Equals("Host", StringComparison.OrdinalIgnoreCase) || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("Connection", StringComparison.OrdinalIgnoreCase) || key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("X-Astra-Admin", StringComparison.OrdinalIgnoreCase) || key.Equals("X-Astra-Runtime-Token", StringComparison.OrdinalIgnoreCase))
                throw new AdminApiException(400, "Invalid or reserved extra header");
    }

    private static void ValidateModelIds(IReadOnlyList<string> ids)
    {
        if (ids.Count > 2000 || ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 512 || id.Any(char.IsControl)))
            throw new AdminApiException(400, "Invalid model IDs (up to 2000 non-empty IDs, max 512 characters each)");
    }

    private static T Read<T>(JsonNode? value)
    {
        if (value is null) throw new AdminApiException(400, "Required value is missing");
        try { return value.Deserialize<T>(Json.Api) ?? throw new AdminApiException(400, "Invalid JSON value"); }
        catch (InvalidOperationException) { throw new AdminApiException(400, "Invalid JSON value"); }
    }
    private static string? String(JsonObject o, string key) => o[key] is null ? null :
        Read<string>(o[key]).Trim() is { Length: > 0 } value ? value : null;
    private static long? NullableLong(JsonNode? value)
    {
        if (value is null) return null;
        var n = Read<long>(value);
        return n > 0 ? n : throw new AdminApiException(400, "Token limit must be positive");
    }
    private static bool Same<T>(T a, T b) => JsonNode.DeepEquals(JsonNode.Parse(Json.Serialize(a)), JsonNode.Parse(Json.Serialize(b)));
}
