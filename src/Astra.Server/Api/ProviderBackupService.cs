using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Astra.Clients.Config;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Core.Models;
using Astra.Data;
using Astra.Providers.Templates;

namespace Astra.Server.Api;

/// <summary>
/// Provider backup (issue #33, <c>.plan/provider-backup.md</c>): exports providers and their models as one JSON file and
/// imports such a file again. The file is a <see cref="JsonObject"/> end to end, so no wire type needs registering.
/// API keys are left out unless asked for: <c>passphrase</c> seals each key with <see cref="BackupCrypto"/>, <c>plaintext</c>
/// writes it as it is. Anything that includes keys is refused off this machine (the endpoint checks). Not exported: OAuth
/// accounts, client bindings, tokens, MCP servers, plugin providers and the unified-gateway provider.
/// Importing is two calls with the same file: <see cref="PreviewAsync"/> (masks only, nothing written) and
/// <see cref="ImportAsync"/> (creates the providers; a provider already there under the same key and address is skipped).
/// </summary>
public sealed class ProviderBackupService(AstraDatabase db, ProviderTemplateCatalog catalog, ISecretProtector secrets, ILogger<ProviderBackupService> log)
{
    public const string FormatId = "astra-providers";
    public const int FormatVersion = 1;
    public const int MaxProviders = 500;
    public const int MaxModels = 2000;

    public static class Modes
    {
        public const string None = "none";
        public const string Passphrase = "passphrase";
        public const string Plaintext = "plaintext";
    }

    // ------------------------------------------------------------------ export

    public async Task<JsonObject> ExportAsync(JsonObject body, bool local, CancellationToken ct)
    {
        var mode = Text(body["secrets"]) ?? Modes.None;
        if (mode is not (Modes.None or Modes.Passphrase or Modes.Plaintext)) throw new AdminApiException(400, "secrets must be none, passphrase or plaintext");
        if (mode != Modes.None && !local) throw new AdminApiException(403, "A backup with API keys can only be made on this machine");

        var file = new JsonObject
        {
            ["format"] = FormatId, ["version"] = FormatVersion, ["exported_at"] = DateTimeOffset.UtcNow.ToString("O"), ["secrets"] = mode,
        };
        BackupCrypto? crypto = null;
        if (mode == Modes.Passphrase)
        {
            crypto = BackupCrypto.Create(Text(body["passphrase"]) ?? "", out var salt);
            file["kdf"] = new JsonObject
            {
                ["alg"] = "pbkdf2-sha256", ["iterations"] = BackupCrypto.DefaultIterations, ["salt"] = Convert.ToBase64String(salt),
            };
            file["check"] = crypto.Check();
        }

        HashSet<string>? only = null;
        if (body["ids"] is JsonArray idList) only = idList.Select(Text).Where(s => s is not null).Select(s => s!).ToHashSet(StringComparer.Ordinal);
        var providers = new JsonArray();
        foreach (var p in (await db.Providers.ListAsync(ct)).OrderBy(p => p.SortOrder))
        {
            if (!Exportable(p) || (only is not null && !only.Contains(p.Id))) continue;
            var item = new JsonObject
            {
                ["name"] = p.Name, ["template_id"] = p.TemplateId, ["category"] = p.Category, ["icon"] = p.Icon,
                ["endpoints"] = JsonNode.Parse(Json.Serialize(p.Endpoints)),
                ["preferred_upstream_protocols"] = JsonNode.Parse(Json.Serialize(p.PreferredUpstreamProtocols)),
                ["auth_scheme"] = p.AuthScheme, ["extra_headers"] = JsonNode.Parse(Json.Serialize(p.ExtraHeaders)),
                ["http_proxy"] = p.HttpProxy, ["price_multiplier"] = p.PriceMultiplier, ["price_key"] = p.PriceKey,
                ["settings"] = p.Settings.DeepClone(), ["notes"] = p.Notes, ["website"] = p.Website, ["enabled"] = p.Enabled,
            };
            if (mode != Modes.None && p.ApiKeyEnc is not null && Unprotect(p.ApiKeyEnc) is { } key)
            {
                if (crypto is null) item["api_key"] = key;
                else item["api_key_sealed"] = crypto.Seal(key);
            }
            var models = new JsonArray();
            foreach (var m in (await db.Providers.ListModelsAsync(p.Id, ct)).OrderBy(m => m.SortOrder))
                Push(models, new JsonObject
                {
                    ["model_id"] = m.ModelId, ["system_model_id"] = m.SystemModelId, ["overrides"] = JsonNode.Parse(Json.Serialize(m.Overrides)),
                    ["enabled"] = m.Enabled, ["upstream_protocols"] = JsonNode.Parse(Json.Serialize(m.UpstreamProtocols)),
                });
            item["models"] = models;
            Push(providers, item);
        }
        file["providers"] = providers;
        log.LogInformation("Provider backup exported: {Count} provider(s), secrets={Mode}", providers.Count, mode);
        return file;
    }

    /// <summary>Providers whose config is a machine's own (plugin sign-ins, the unified gateway) are not portable.</summary>
    private static bool Exportable(Provider p) =>
        p.AuthScheme != AuthSchemes.Plugin && !(p.Settings["unified_gateway"] is JsonValue v && v.TryGetValue<bool>(out var on) && on);

    private string? Unprotect(string enc)
    {
        try { return secrets.Unprotect(enc); }
        catch (CryptographicException) { return null; }
    }

    // ------------------------------------------------------------------ preview

    public async Task<JsonObject> PreviewAsync(JsonObject body, CancellationToken ct)
    {
        var (mode, planned) = Plan(body);
        var existing = await LoadExistingAsync(ct);
        var items = new JsonArray();
        foreach (var plan in planned)
        {
            var status = Status(plan, existing, out var shown);
            var item = new JsonObject { ["index"] = plan.Index, ["name"] = plan.Name, ["status"] = status, ["modelCount"] = plan.ModelIds.Count };
            if (plan.Provider is { } p)
            {
                item["endpoints"] = new JsonArray(p.Endpoints.Select(e => (JsonNode)new JsonObject { ["protocol"] = e.Protocol.ToId(), ["baseUrl"] = e.BaseUrl }).ToArray());
                item["authScheme"] = p.AuthScheme;
            }
            item["hasKey"] = plan.Key is not null;
            item["keyMasked"] = ImportService.Mask(plan.Key);
            if (shown is not null) item["existing"] = new JsonObject { ["id"] = shown.Id, ["name"] = shown.Name };
            if (plan.Error is not null) item["error"] = plan.Error;
            item["ignored"] = new JsonArray(plan.Ignored.Select(s => (JsonNode)s).ToArray());
            Push(items, item);
        }
        return new JsonObject { ["secrets"] = mode, ["items"] = items };
    }

    // ------------------------------------------------------------------ commit

    /// <summary>Creates the providers of the file (or only <c>indexes</c>). Skips the ones already there and the invalid ones.</summary>
    public async Task<JsonObject> ImportAsync(JsonObject body, CancellationToken ct)
    {
        var (_, planned) = Plan(body);
        HashSet<int>? only = null;
        if (body["indexes"] is JsonArray list)
            only = list.Select(n => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1).ToHashSet();

        var existing = await LoadExistingAsync(ct);
        var all = await db.Providers.ListAsync(ct);
        var names = new HashSet<string>(all.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var order = all.Select(p => p.SortOrder).DefaultIfEmpty(-1).Max() + 1;
        var added = new JsonArray();
        var skipped = new JsonArray();
        foreach (var plan in planned.Where(p => only is null || only.Contains(p.Index)))
        {
            if (plan.Provider is null || plan.Error is not null) { Push(skipped, Skip(plan.Index, plan.Error ?? "invalid")); continue; }
            if (Status(plan, existing, out _) == "same") { Push(skipped, Skip(plan.Index, "same")); continue; }

            var provider = plan.Provider;
            provider.Id = Ulid.NewUlid();
            provider.Name = ImportService.UniqueName(plan.Name, names);
            provider.SortOrder = order++;
            provider.ApiKeyEnc = plan.Key is null ? null : secrets.Protect(plan.Key);
            var models = await ProviderEndpoints.PlanModelsAsync(db, provider, plan.ModelIds, ct);
            Overlay(models, plan.ModelEntries);
            await db.Providers.InsertWithModelsAsync(provider, models, ct);
            existing.Add(new ExistingProvider(provider.Id, provider.Name, ImportService.KeyHash(plan.Key), Urls(provider)));
            Push(added, new JsonObject { ["id"] = provider.Id, ["name"] = provider.Name });
        }
        log.LogInformation("Provider backup imported: {Added} created, {Skipped} skipped", added.Count, skipped.Count);
        return new JsonObject { ["added"] = added, ["skipped"] = skipped };
    }

    /// <summary>Adds through the non-generic overload: <c>JsonArray.Add&lt;T&gt;</c> is not AOT-safe and wins overload resolution for a <see cref="JsonObject"/>.</summary>
    private static void Push(JsonArray array, JsonNode node) => array.Add(node);

    private static JsonObject Skip(int index, string reason) => new() { ["index"] = index, ["reason"] = reason };

    /// <summary>The file's own per-model settings win over what the matcher and the family defaults chose.</summary>
    private static void Overlay(List<ProviderModel> models, Dictionary<string, JsonObject> entries)
    {
        var order = 0;
        foreach (var m in models.OrderBy(m => m.SortOrder))
        {
            m.SortOrder = order++;
            if (!entries.TryGetValue(m.ModelId, out var entry)) continue;
            if (entry["overrides"] is JsonObject o && Json.Deserialize<ModelOverrides>(o.ToJsonString()) is { } overrides) m.Overrides = overrides;
            if (entry["enabled"] is JsonValue e && e.TryGetValue<bool>(out var enabled)) m.Enabled = enabled;
            if (entry["upstream_protocols"] is JsonArray u && Json.Deserialize<List<ApiProtocol>>(u.ToJsonString()) is { } protocols) m.UpstreamProtocols = protocols;
        }
    }

    // ------------------------------------------------------------------ reading the file

    private sealed record Planned(
        int Index, string Name, Provider? Provider, string? Key, List<string> ModelIds, Dictionary<string, JsonObject> ModelEntries,
        List<string> Ignored, string? Error);

    private (string Mode, List<Planned> Items) Plan(JsonObject body)
    {
        if (body["file"] is not JsonObject file || Text(file["format"]) != FormatId) throw new AdminApiException(400, "This is not an Astra provider backup file");
        if (file["version"] is not JsonValue ver || !ver.TryGetValue<int>(out var version) || version < 1) throw new AdminApiException(400, "The backup file has no valid version");
        if (version > FormatVersion) throw new AdminApiException(400, "The backup file comes from a newer Astra; update Astra to read it");
        var mode = Text(file["secrets"]) ?? Modes.None;
        if (mode is not (Modes.None or Modes.Passphrase or Modes.Plaintext)) throw new AdminApiException(400, "The backup file's secrets mode is not valid");
        if (file["providers"] is not JsonArray providers) throw new AdminApiException(400, "The backup file has no providers");
        if (providers.Count > MaxProviders) throw new AdminApiException(400, $"A backup file may hold up to {MaxProviders} providers");

        BackupCrypto? crypto = null;
        if (mode == Modes.Passphrase)
        {
            if (file["kdf"] is not JsonObject kdf || Text(kdf["alg"]) != "pbkdf2-sha256" || kdf["iterations"] is not JsonValue it || !it.TryGetValue<int>(out var iterations) || Text(kdf["salt"]) is not { } salt)
                throw new AdminApiException(400, "The backup file's encryption parameters are not valid");
            byte[] saltBytes;
            try { saltBytes = Convert.FromBase64String(salt); }
            catch (FormatException) { throw new AdminApiException(400, "The backup file's encryption parameters are not valid"); }
            var passphrase = Text(body["passphrase"]);
            if (string.IsNullOrEmpty(passphrase)) throw new AdminApiException(400, "This backup is encrypted: enter its passphrase");
            crypto = BackupCrypto.Open(passphrase, saltBytes, iterations);
            if (!crypto.Verify(Text(file["check"]))) throw new AdminApiException(400, "Wrong passphrase, or the file was changed");
        }

        var result = new List<Planned>();
        for (var i = 0; i < providers.Count; i++)
            result.Add(PlanOne(i, providers[i] as JsonObject, mode, crypto));
        return (mode, result);
    }

    private Planned PlanOne(int index, JsonObject? item, string mode, BackupCrypto? crypto)
    {
        var name = Text(item?["name"])?.Trim() ?? $"#{index + 1}";
        var ignored = new List<string>();
        try
        {
            if (item is null) throw new AdminApiException(400, "Not a provider");
            var endpoints = Json.Deserialize<List<ProviderEndpoint>>(item["endpoints"]?.ToJsonString()) ?? [];
            if (endpoints.Count == 0) throw new AdminApiException(400, "Configure one endpoint per protocol");
            var scheme = Text(item["auth_scheme"]) ?? AuthSchemes.Bearer;
            if (!AuthSchemes.All.Contains(scheme)) throw new AdminApiException(400, $"Unknown auth scheme: {scheme}");

            var templateId = Text(item["template_id"]);
            var template = (templateId is null ? null : catalog.Get(templateId)) ?? catalog.Get(ImportService.CustomTemplateId(endpoints[0].Protocol))
                ?? throw new AdminApiException(400, "No provider template for this backup");
            var protocols = endpoints.Select(e => e.Protocol).ToList();
            var preferred = (Json.Deserialize<List<ApiProtocol>>(item["preferred_upstream_protocols"]?.ToJsonString()) ?? []).Where(protocols.Contains).Distinct().ToList();

            var headers = new Dictionary<string, string>(template.DefaultHeaders, StringComparer.OrdinalIgnoreCase);
            if (item["extra_headers"] is JsonObject fileHeaders)
                foreach (var (k, v) in fileHeaders)
                {
                    var value = Text(v);
                    if (value is not null && ProviderEndpoints.IsValidExtraHeader(k, value)) headers[k] = value;
                    else ignored.Add($"header:{k}");
                }
            var proxy = Text(item["http_proxy"]);
            if (proxy is not null && !(Uri.TryCreate(proxy, UriKind.Absolute, out var pu) && pu.Scheme is "http" or "https" or "socks5"))
            {
                proxy = null;
                ignored.Add("proxy");
            }
            var multiplier = item["price_multiplier"] is JsonValue pm && pm.TryGetValue<decimal>(out var m) && m >= 0 ? m : template.DefaultPriceMultiplier;
            var settings = item["settings"] is JsonObject s ? (JsonObject)s.DeepClone() : (JsonObject)template.Settings.DeepClone();
            settings.Remove("unified_gateway");

            string? key = null;
            if (mode == Modes.Plaintext) key = Text(item["api_key"]);
            else if (mode == Modes.Passphrase && Text(item["api_key_sealed"]) is { } sealedKey)
            {
                if (crypto!.TryOpen(sealedKey, out var plain)) key = plain;
                else throw new AdminApiException(400, "Could not decrypt this provider's API key");
            }
            if (key is { Length: 0 }) key = null;

            var provider = new Provider
            {
                TemplateId = template.Id, TemplateVersion = template.Version, TemplateSnapshotJson = ProviderEndpoints.Snapshot(template, null), Name = name,
                Icon = Text(item["icon"]) is { Length: > 0 and <= 200 } icon ? icon : template.Icon, Category = template.Category,
                Endpoints = endpoints.Select(e => new ProviderEndpoint { Protocol = e.Protocol, BaseUrl = e.BaseUrl, FullUrl = e.FullUrl }).ToList(),
                PreferredUpstreamProtocols = preferred.Count > 0 ? preferred : template.PreferredUpstreamProtocols.Where(protocols.Contains).DefaultIfEmpty(protocols[0]).ToList(),
                AuthScheme = scheme, ExtraHeaders = headers, HttpProxy = proxy, PriceMultiplier = multiplier,
                PriceKey = Text(item["price_key"]) ?? template.PriceKey ?? template.Id, AdapterId = template.Adapter, Settings = settings,
                Website = Text(item["website"]) ?? template.Website, Notes = Text(item["notes"]),
                Enabled = !(item["enabled"] is JsonValue en && en.TryGetValue<bool>(out var on) && !on),
            };
            ProviderEndpoints.Validate(provider);

            var entries = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            var ids = new List<string>();
            if (item["models"] is JsonArray models)
                foreach (var node in models)
                    if (node is JsonObject entry && Text(entry["model_id"])?.Trim() is { Length: > 0 } id && entries.TryAdd(id, entry)) ids.Add(id);
            ProviderEndpoints.ValidateModelIds(ids);
            return new Planned(index, name, provider, key, ids, entries, ignored, null);
        }
        catch (AdminApiException e)
        {
            return new Planned(index, name, null, null, [], [], ignored, e.Message);
        }
    }

    // ------------------------------------------------------------------ existing providers

    private sealed record ExistingProvider(string Id, string Name, string? KeyHash, HashSet<string> Urls);

    private static HashSet<string> Urls(Provider p) => p.Endpoints.Select(e => e.BaseUrl.TrimEnd('/').ToLowerInvariant()).ToHashSet();

    private async Task<List<ExistingProvider>> LoadExistingAsync(CancellationToken ct)
    {
        var result = new List<ExistingProvider>();
        foreach (var p in await db.Providers.ListAsync(ct))
            result.Add(new ExistingProvider(p.Id, p.Name, ImportService.KeyHash(p.ApiKeyEnc is null ? null : Unprotect(p.ApiKeyEnc)), Urls(p)));
        return result;
    }

    /// <summary>new | sameHost (an address already used by another key) | same (same key and address) | invalid.</summary>
    private static string Status(Planned plan, List<ExistingProvider> existing, out ExistingProvider? shown)
    {
        shown = null;
        if (plan.Provider is null || plan.Error is not null) return "invalid";
        var urls = Urls(plan.Provider);
        var hash = ImportService.KeyHash(plan.Key);
        var sameAddress = existing.Where(e => e.Urls.Overlaps(urls)).ToList();
        var same = sameAddress.FirstOrDefault(e => e.KeyHash == hash);
        shown = same ?? sameAddress.FirstOrDefault();
        return same is not null ? "same" : sameAddress.Count > 0 ? "sameHost" : "new";
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
