using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Models;
using Astra.Data;

namespace Astra.Server.Api;

/// <summary>One change of a preview run; <see cref="Id"/> is bound to its snapshot ("&lt;snapshotId&gt;:&lt;index&gt;").</summary>
internal sealed record SyncChangePlan(
    string ModelId,
    string? PriceKey,
    string Kind,
    string? Field,
    JsonNode? Before,
    JsonNode? After,
    bool SkippedBecauseUserModified,
    SyncWritePlan Write)
{
    public string Id { get; set; } = "";
}

/// <summary>Server-side write instruction behind a change — clients only ever submit change ids, never values.</summary>
internal abstract record SyncWritePlan;

internal sealed record NewModelPlan(SystemModel Model, List<ModelPrice> Prices) : SyncWritePlan;

internal sealed record FieldPlan(string Field, JsonNode? After) : SyncWritePlan;

internal sealed record PricePlan(string PriceKey, string? UpstreamModelId, PricingSchedule Pricing) : SyncWritePlan;

internal sealed class SyncSnapshot
{
    public required string Id { get; init; }
    public DateTimeOffset FetchedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public required List<SyncChangePlan> Changes { get; init; }
}

// API contract (web/src/api/types.ts): camelCase envelopes, pricing stays raw snake_case storage JSON.
public sealed record SyncChangeDto(
    string Id, string ModelId, string? PriceKey, string Kind, string? Field,
    JsonNode? Before, JsonNode? After, bool SkippedBecauseUserModified);

public sealed record SyncPreviewDto(string Source, DateTimeOffset FetchedAt, List<SyncChangeDto> Changes);

public sealed class SyncApplyRequest
{
    public List<string>? ChangeIds { get; set; }
}

public sealed record SyncApplyResult(int Applied);

/// <summary>
/// models.dev catalog sync (plan §8): <c>POST /api/models/sync/preview</c> fetches
/// <see cref="SourceUrl"/>, diffs it against the stored system models and keeps server-side snapshots
/// (15 min TTL, max 8, size/count capped). <c>POST /api/models/sync/apply</c> consumes change ids bound
/// to one snapshot and writes through a single transaction after re-checking every item.
///
/// Only chat text models are synced (embedding/image/audio/… excluded like scripts/gen-seed.mjs).
/// Only the basic cost keys (input/output/cache_read/cache_write) and base model attributes are mapped;
/// service_tiers/context_tiers/time_windows/per_request/notes of existing pricing are preserved — the
/// upstream base prices never replace the whole schedule. Models with source=user, user-modified fields
/// and user-modified prices are listed with skippedBecauseUserModified=true but never written; apply
/// rejects instead of overwriting anything that changed since the preview.
/// </summary>
public sealed class ModelSyncService(AstraDatabase db, IHttpClientFactory factory)
{
    public const string HttpClientName = "model-sync";
    // The model catalog lives on the official website (astra-gate.si, maintained
    // under docs/public/api, refreshed from models.dev by docs/scripts/refresh-model-catalog.mjs).
    public const string SourceUrl = "https://astra-gate.si/api/models-catalog.json";

    internal const string KindNewModel = "new_model";
    internal const string KindField = "field";
    internal const string KindPrice = "price";

    internal const int MaxSnapshots = 8;
    internal const int MaxChanges = 5000;
    internal const long MaxCatalogBytes = 32 * 1024 * 1024;
    internal const long MaxSnapshotBytes = 4 * 1024 * 1024;

    /// <summary>Test hook; 15 minutes in production.</summary>
    internal TimeSpan SnapshotTtl { get; set; } = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private readonly List<SyncSnapshot> _snapshots = [];

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

    /// <summary>models.dev provider id → system vendor (official sources), mirroring scripts/gen-seed.mjs.</summary>
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.Ordinal)
    {
        ["openai"] = "openai",
        ["anthropic"] = "anthropic",
        ["google"] = "google",
        ["xai"] = "xai",
        ["deepseek"] = "deepseek",
        ["moonshotai"] = "moonshotai",
        ["zhipuai"] = "zhipuai",
        ["alibaba"] = "alibaba",
        ["minimax"] = "minimax",
        ["volcengine"] = "bytedance",
    };

    /// <summary>Providers whose prices are recorded per model under their models.dev id as price key.</summary>
    private static readonly string[] ThirdParty =
    [
        "openrouter", "siliconflow", "siliconflow-cn", "volcengine", "alibaba", "alibaba-cn",
        "moonshotai-cn", "zai", "minimax-cn", "google-vertex", "google-vertex-anthropic", "amazon-bedrock", "azure",
    ];

    // scripts/gen-seed.mjs EXCLUDE: not chat text models.
    private static readonly Regex ExcludeRegex =
        new("embedding|tts|image|veo|lyria|realtime|live|audio|transcri|moderation|whisper|dall-e|deep-research|computer-use|asr|livetranslate|ocr|-mt-|character|evolving|customtools|search|codex-spark",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AlibabaOnly = new("^(qwen|qwq|qvq)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DoubaoOnly = new("^doubao", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>DeepSeek peak windows (models.dev lists the off-peak price); same as scripts/gen-seed.mjs.</summary>
    private static readonly List<TimeWindowRule> DeepSeekPeakWindows =
    [
        new() { Name = "peak-01-04", Timezone = "UTC", Start = "01:00", End = "04:00", Days = [1, 2, 3, 4, 5], Multiplier = 2 },
        new() { Name = "peak-06-10", Timezone = "UTC", Start = "06:00", End = "10:00", Days = [1, 2, 3, 4, 5], Multiplier = 2 },
    ];

    private const string DeepSeekNotes = "Base = off-peak price; peak windows x2 (Mon-Fri UTC). " +
        "Add Chinese public holidays to exclude_dates. Source: api-docs.deepseek.com/quick_start/pricing (2026-10-06)";

    // ----- preview -----

    public async Task<SyncPreviewDto> PreviewAsync(CancellationToken ct = default)
    {
        var root = await FetchCatalogAsync(ct);
        var desired = ParseCatalog(root);
        var changes = await DiffAsync(desired, ct);
        var snapshot = StoreSnapshot(changes);
        return new SyncPreviewDto(SourceUrl, snapshot.FetchedAt, snapshot.Changes.Select(ToDto).ToList());
    }

    private async Task<JsonObject> FetchCatalogAsync(CancellationToken ct)
    {
        using var http = factory.CreateClient(HttpClientName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await http.GetAsync(SourceUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new AdminApiException(502, $"Model catalog returned HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > MaxCatalogBytes)
                throw new AdminApiException(502, $"Model catalog exceeds {MaxCatalogBytes} bytes");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaxCatalogBytes)
                    throw new AdminApiException(502, $"Model catalog exceeds {MaxCatalogBytes} bytes");
                buffer.Write(chunk, 0, read);
            }
            return JsonNode.Parse(Encoding.UTF8.GetString(buffer.ToArray())) as JsonObject
                ?? throw new AdminApiException(502, "Model catalog root must be a JSON object");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AdminApiException(504, "Model catalog request timed out"); }
        catch (HttpRequestException ex)
        { throw new AdminApiException(502, $"Model catalog request failed: {ex.Message}"); }
        catch (IOException ex)
        { throw new AdminApiException(502, $"Model catalog could not be read: {ex.Message}"); }
        catch (JsonException)
        { throw new AdminApiException(502, "Model catalog is not valid JSON"); }
    }

    private SyncSnapshot StoreSnapshot(List<SyncChangePlan> changes)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            _snapshots.RemoveAll(s => s.ExpiresAt < now);
            if (changes.Count > MaxChanges)
                throw new AdminApiException(StatusCodes.Status502BadGateway,
                    $"Model catalog produced {changes.Count} changes; the sync limit is {MaxChanges}");
            long size = 0;
            foreach (var change in changes)
            {
                size += (change.Before?.ToJsonString().Length ?? 0) + (change.After?.ToJsonString().Length ?? 0)
                        + change.ModelId.Length + change.Kind.Length + 64;
            }
            if (size > MaxSnapshotBytes)
                throw new AdminApiException(StatusCodes.Status502BadGateway,
                    $"Model catalog diff exceeds the sync snapshot size limit of {MaxSnapshotBytes} bytes");

            var snapshot = new SyncSnapshot { Id = Ulid.NewUlid(now), FetchedAt = now, ExpiresAt = now + SnapshotTtl, Changes = changes };
            for (var i = 0; i < changes.Count; i++) changes[i].Id = $"{snapshot.Id}:{i}";
            _snapshots.Add(snapshot);
            while (_snapshots.Count > MaxSnapshots) _snapshots.RemoveAt(0);
            return snapshot;
        }
    }

    internal SyncSnapshot? FindSnapshot(string snapshotId)
    {
        lock (_gate)
        {
            return _snapshots.FirstOrDefault(s => s.Id == snapshotId);
        }
    }

    private static SyncChangeDto ToDto(SyncChangePlan c) =>
        new(c.Id, c.ModelId, c.PriceKey, c.Kind, c.Field, c.Before, c.After, c.SkippedBecauseUserModified);

    // ----- upstream catalog parsing (mirrors scripts/gen-seed.mjs) -----

    private sealed class DesiredModel
    {
        public string Id { get; init; } = "";
        public string Key { get; init; } = "";
        public string VendorProvider { get; init; } = "";
        public string Vendor { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string? Family { get; init; }
        public List<string> Aliases { get; } = [];
        public long? ContextWindow { get; init; }
        public long? MaxOutputTokens { get; init; }
        public ModelCapabilities Capabilities { get; init; } = new();
        public PricingSchedule? Pricing { get; init; }
        public List<ModelPrice> Prices { get; } = [];
    }

    private static List<DesiredModel> ParseCatalog(JsonObject root)
    {
        if (!root.Any(kv => Vendors.ContainsKey(kv.Key) && kv.Value is JsonObject p && p["models"] is JsonObject))
            throw new AdminApiException(502, "Model catalog has no official model collections");
        var byKey = new Dictionary<string, DesiredModel>(StringComparer.Ordinal);
        var models = new List<DesiredModel>();

        foreach (var (providerId, node) in root)
        {
            if (!Vendors.TryGetValue(providerId, out var vendor) || node is not JsonObject provider) continue;
            if (provider["models"] is not JsonObject entries) continue;
            var only = providerId switch
            {
                "alibaba" => AlibabaOnly,
                "volcengine" => DoubaoOnly,
                _ => null,
            };

            // Shortest ids first so dated variants become aliases of the undated model (gen-seed ordering).
            foreach (var (rawId, modelNode) in OrderedEntries(entries))
            {
                if (!IsChatModel(modelNode, rawId, out var m)) continue;
                if (only is { } filter && !filter.IsMatch(rawId)) continue;
                var key = $"{providerId}/{ModelIdMatcher.Normalize(rawId)}";
                if (byKey.TryGetValue(key, out var known))
                {
                    if (known.Id != rawId && !known.Aliases.Contains(rawId, StringComparer.Ordinal)) known.Aliases.Add(rawId);
                    continue;
                }
                var desired = new DesiredModel
                {
                    Id = rawId,
                    Key = key,
                    VendorProvider = providerId,
                    Vendor = vendor,
                    DisplayName = (AsString(m["name"]) ?? rawId).Trim(),
                    Family = AsString(m["family"]),
                    ContextWindow = LongIn(m["limit"], "context"),
                    MaxOutputTokens = LongIn(m["limit"], "output"),
                    Capabilities = CapabilitiesOf(m, m["cost"] as JsonObject ?? []),
                    Pricing = ToPricing(m["cost"] as JsonObject ?? [], providerId),
                };
                byKey[key] = desired;
                models.Add(desired);
            }
        }

        foreach (var providerId in ThirdParty)
        {
            if (root[providerId] is not JsonObject provider || provider["models"] is not JsonObject entries) continue;
            foreach (var (rawId, modelNode) in OrderedEntries(entries))
            {
                if (!IsChatModel(modelNode, rawId, out var m)) continue;
                var target = FindSystemModel(m, rawId, byKey, models);
                if (target is null || target.VendorProvider == providerId) continue;
                if (target.Prices.Any(p => p.PriceKey == providerId)) continue;
                var pricing = ToPricing(m["cost"] as JsonObject ?? [], providerId);
                if (pricing is null) continue;
                target.Prices.Add(new ModelPrice
                {
                    ModelId = target.Id,
                    PriceKey = providerId,
                    UpstreamModelId = rawId != target.Id ? rawId : null,
                    Pricing = pricing,
                });
            }
        }

        if (models.GroupBy(m => m.Id, StringComparer.Ordinal).Any(g => g.Count() > 1))
            throw new AdminApiException(502, "Model catalog contains ambiguous model IDs across vendors");
        return models.OrderBy(d => d.Vendor, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
    }

    private static bool IsChatModel(JsonNode? node, string rawId, out JsonObject m)
    {
        m = null!;
        if (node is not JsonObject model || rawId.Length == 0 || rawId[0] == '~' || ExcludeRegex.IsMatch(rawId)) return false;
        if (model["cost"] is not JsonObject cost) return false;
        var outputs = StringList(model, "modalities", "output") ?? ["text"];
        if (!outputs.Contains("text")) return false;
        if (rawId.Length > 512 || string.IsNullOrWhiteSpace(rawId) || rawId.Any(char.IsControl))
            throw new AdminApiException(502, "Model catalog contains an invalid model ID");
        foreach (var key in new[] { "input", "output", "cache_read", "cache_write" })
            if (cost[key] is not null && (Dec(cost, key) is not { } amount || amount < 0))
                throw new AdminApiException(502, "Model catalog contains invalid base prices");
        if (LongIn(model["limit"], "context") is <= 0 || LongIn(model["limit"], "output") is <= 0)
            throw new AdminApiException(502, "Model catalog contains invalid token limits");
        m = model;
        return true;
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> OrderedEntries(JsonObject o) =>
        o.OrderBy(kv => kv.Key.Length).ThenBy(kv => kv.Key, StringComparer.Ordinal);

    private static DesiredModel? FindSystemModel(JsonObject m, string rawId,
        Dictionary<string, DesiredModel> byKey, List<DesiredModel> models)
    {
        var canonical = AsString(m["canonical_model_id"]);
        if (!string.IsNullOrWhiteSpace(canonical))
        {
            var slash = canonical.IndexOf('/');
            if (slash > 0)
            {
                var key = $"{canonical[..slash]}/{ModelIdMatcher.Normalize(canonical[(slash + 1)..])}";
                if (byKey.TryGetValue(key, out var hit)) return hit;
            }
        }
        // Same-vendor duplicate or a unique normalized-id match across the desired catalog.
        var n = ModelIdMatcher.Normalize(rawId);
        if (n.Length == 0) return null;
        var hits = models.Where(x => ModelIdMatcher.Normalize(x.Id) == n
                                     || x.Aliases.Any(a => ModelIdMatcher.Normalize(a) == n)).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    /// <summary>Basic cost keys only: input/output/cache_read/cache_write (+ the DeepSeek peak-window rule for new models).</summary>
    private static PricingSchedule? ToPricing(JsonObject cost, string providerId)
    {
        var input = Dec(cost, "input");
        var output = Dec(cost, "output");
        if (input is null || output is null) return null;
        var baseSet = new PriceSet { ["input"] = Round6(input.Value), ["output"] = Round6(output.Value) };
        if (Dec(cost, "cache_read") is { } cacheRead) baseSet["cache_read"] = Round6(cacheRead);
        if (Dec(cost, "cache_write") is { } cacheWrite) baseSet["cache_write_5m"] = Round6(cacheWrite);
        var pricing = new PricingSchedule { Base = baseSet };
        if (providerId == "deepseek")
        {
            pricing.TimeWindows = DeepSeekPeakWindows.Select(w => new TimeWindowRule
            {
                Name = w.Name, Timezone = w.Timezone, Start = w.Start, End = w.End,
                Days = w.Days is null ? null : [.. w.Days], Multiplier = w.Multiplier,
            }).ToList();
            pricing.Notes = DeepSeekNotes;
        }
        return pricing;
    }

    private static ModelCapabilities CapabilitiesOf(JsonObject m, JsonObject cost)
    {
        var input = StringList(m, "modalities", "input") ?? [];
        return new ModelCapabilities
        {
            Vision = input.Contains("image") ? true : null,
            Tools = Bool(m, "tool_call") == true ? true : null,
            Reasoning = Bool(m, "reasoning") == true ? true : null,
            PromptCache = Dec(cost, "cache_read") is not null ? true : null,
            Audio = input.Contains("audio") ? true : null,
            Pdf = input.Contains("pdf") ? true : null,
            StructuredOutput = Bool(m, "structured_output") == true ? true : null,
        };
    }

    // ----- diff against the stored catalog -----

    private async Task<List<SyncChangePlan>> DiffAsync(List<DesiredModel> desired, CancellationToken ct)
    {
        var stored = await db.Models.ListAsync(null, ct);
        var byId = stored.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var changes = new List<SyncChangePlan>();

        foreach (var d in desired)
        {
            var existing = FindExisting(d, byId, stored);
            if (existing is null)
            {
                changes.Add(NewModelChange(d));
                continue;
            }
            await DiffModel(changes, existing, d, ct);
        }
        return changes;
    }

    private static SystemModel? FindExisting(DesiredModel d, Dictionary<string, SystemModel> byId, IReadOnlyList<SystemModel> stored)
    {
        if (byId.TryGetValue(d.Id, out var exact)) return exact;
        // Safe unique fallback: exactly one stored model (id or alias) normalizes to the desired id.
        var n = ModelIdMatcher.Normalize(d.Id);
        if (n.Length == 0) return null;
        var hits = stored.Where(m => ModelIdMatcher.Normalize(m.Id) == n
                                     || m.Aliases.Any(a => ModelIdMatcher.Normalize(a) == n)).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    private static SyncChangePlan NewModelChange(DesiredModel d)
    {
        var now = DateTimeOffset.UtcNow;
        var model = new SystemModel
        {
            Id = d.Id,
            DisplayName = d.DisplayName,
            Vendor = d.Vendor,
            Family = d.Family,
            Aliases = [.. d.Aliases],
            ContextWindow = d.ContextWindow,
            MaxOutputTokens = d.MaxOutputTokens,
            Capabilities = d.Capabilities,
            Pricing = d.Pricing?.Clone(),
            Source = "sync",
            Enabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var prices = d.Prices.Select(p => new ModelPrice
        {
            ModelId = d.Id,
            PriceKey = p.PriceKey,
            UpstreamModelId = p.UpstreamModelId,
            Pricing = p.Pricing.Clone(),
            Source = "sync",
            UserModified = false,
            UpdatedAt = now,
        }).ToList();
        return new SyncChangePlan(d.Id, null, KindNewModel, null, null, NewModelSummary(model, prices), false,
            new NewModelPlan(model, prices));
    }

    private async Task DiffModel(List<SyncChangePlan> changes, SystemModel existing, DesiredModel d, CancellationToken ct)
    {
        var userOwned = string.Equals(existing.Source, "user", StringComparison.Ordinal);

        void AddField(string field, JsonNode? current, JsonNode? after)
        {
            if (JsonEqual(current, after)) return;
            var skipped = userOwned || existing.UserModifiedFields.Contains(FieldToStorage[field]);
            changes.Add(new SyncChangePlan(existing.Id, null, KindField, field, current, after, skipped,
                new FieldPlan(field, after)));
        }

        AddField("displayName", FieldNode(existing, "displayName"), Node(d.DisplayName));
        if (d.Family is not null)
            AddField("family", FieldNode(existing, "family"), Node(d.Family));
        AddField("aliases", FieldNode(existing, "aliases"), Node(MergeAliases(existing.Aliases, d.Aliases)));
        if (d.ContextWindow is not null)
            AddField("contextWindow", FieldNode(existing, "contextWindow"), Node(d.ContextWindow));
        if (d.MaxOutputTokens is not null)
            AddField("maxOutputTokens", FieldNode(existing, "maxOutputTokens"), Node(d.MaxOutputTokens));
        AddField("capabilities", FieldNode(existing, "capabilities"),
            CapabilitiesNode(MergeCapabilities(existing.Capabilities, d.Capabilities)));
        var mergedPricing = MergePricing(existing.Pricing, d.Pricing);
        if (mergedPricing is not null)
            AddField("pricing", FieldNode(existing, "pricing"), PricingNode(mergedPricing));

        if (d.Prices.Count == 0) return;
        var rows = await db.Models.ListPricesAsync(existing.Id, ct);
        foreach (var desiredPrice in d.Prices)
        {
            var row = rows.FirstOrDefault(r => r.PriceKey == desiredPrice.PriceKey);
            if (row is null)
            {
                changes.Add(new SyncChangePlan(existing.Id, desiredPrice.PriceKey, KindPrice, null, null,
                    PriceNode(desiredPrice.UpstreamModelId, desiredPrice.Pricing), userOwned,
                    new PricePlan(desiredPrice.PriceKey, desiredPrice.UpstreamModelId, desiredPrice.Pricing)));
                continue;
            }
            var merged = MergePricing(row.Pricing, desiredPrice.Pricing);
            if (merged is null || JsonEqual(PricingNode(row.Pricing), PricingNode(merged))) continue;
            var skipped = userOwned || row.UserModified;
            changes.Add(new SyncChangePlan(existing.Id, desiredPrice.PriceKey, KindPrice, null,
                PriceNode(row.UpstreamModelId, row.Pricing), PriceNode(row.UpstreamModelId, merged), skipped,
                new PricePlan(desiredPrice.PriceKey, row.UpstreamModelId, merged)));
        }
    }

    /// <summary>Upstream base prices overlay the stored schedule; service_tiers/context_tiers/time_windows/per_request/notes are kept.</summary>
    private static PricingSchedule? MergePricing(PricingSchedule? existing, PricingSchedule? upstream)
    {
        if (existing is null) return upstream?.Clone();
        var merged = existing.Clone();
        if (upstream is not null)
        {
            foreach (var (type, price) in upstream.Base.Tokens)
            {
                if (price is not null) merged.Base[type] = price;
            }
        }
        return merged.Validate().Count == 0 ? merged : null;
    }

    private static List<string> MergeAliases(List<string> existing, IReadOnlyList<string> upstream)
    {
        var merged = existing.ToList();
        foreach (var alias in upstream)
        {
            if (!merged.Contains(alias, StringComparer.Ordinal)) merged.Add(alias);
        }
        return merged;
    }

    /// <summary>models.dev only encodes "true" flags (absent ≈ unknown), so capabilities merge additively.</summary>
    private static ModelCapabilities MergeCapabilities(ModelCapabilities existing, ModelCapabilities upstream)
    {
        var merged = existing.Clone();
        if (upstream.Vision == true) merged.Vision = true;
        if (upstream.Tools == true) merged.Tools = true;
        if (upstream.Reasoning == true) merged.Reasoning = true;
        if (upstream.PromptCache == true) merged.PromptCache = true;
        if (upstream.Audio == true) merged.Audio = true;
        if (upstream.Pdf == true) merged.Pdf = true;
        if (upstream.StructuredOutput == true) merged.StructuredOutput = true;
        return merged;
    }

    // ----- apply -----

    public async Task<IResult> ApplyAsync(SyncApplyRequest? request, CancellationToken ct = default)
    {
        if (request?.ChangeIds is not { } submitted || submitted.Count > MaxChanges || submitted.Any(id => string.IsNullOrWhiteSpace(id)))
            throw new AdminApiException(400, "changeIds must be an array of valid change IDs");
        var ids = submitted
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0) return Results.Ok(new SyncApplyResult(0));

        var snapshotId = "";
        SyncSnapshot? snapshot = null;
        var changes = new List<SyncChangePlan>();
        foreach (var id in ids)
        {
            var sep = id.LastIndexOf(':');
            if (sep <= 0 || !int.TryParse(id[(sep + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                throw new AdminApiException(StatusCodes.Status400BadRequest, $"Unknown change id '{id}'");
            var owner = id[..sep];
            if (snapshot is null)
            {
                snapshot = FindSnapshot(owner)
                    ?? throw new AdminApiException(StatusCodes.Status400BadRequest,
                        $"Change id '{id}' does not belong to any current preview; run sync preview again");
                snapshotId = owner;
            }
            else if (owner != snapshotId)
            {
                throw new AdminApiException(StatusCodes.Status400BadRequest,
                    "Change ids span multiple previews; apply one preview at a time");
            }
            if (index < 0 || index >= snapshot.Changes.Count || snapshot.Changes[index].Id != id)
                throw new AdminApiException(StatusCodes.Status400BadRequest, $"Change id '{id}' is not part of its preview");
            changes.Add(snapshot.Changes[index]);
        }

        if (snapshot is null || snapshot.ExpiresAt < DateTimeOffset.UtcNow)
            throw new AdminApiException(StatusCodes.Status400BadRequest, "This preview has expired; run sync preview again and reselect");

        // Preflight every selected change against the current database before writing anything.
        var now = DateTimeOffset.UtcNow;
        var problems = new List<object>();
        var modelCache = new Dictionary<string, SystemModel?>(StringComparer.Ordinal);
        var priceCache = new Dictionary<(string ModelId, string PriceKey), ModelPrice?>();
        async Task<SystemModel?> CurrentModelAsync(string id)
        {
            if (!modelCache.TryGetValue(id, out var model))
            {
                model = await db.Models.GetAsync(id, ct);
                modelCache[id] = model;
            }
            return model;
        }

        foreach (var change in changes)
        {
            if (change.SkippedBecauseUserModified)
            {
                problems.Add(new { changeId = change.Id, modelId = change.ModelId, priceKey = change.PriceKey, reason = "Change was protected in the preview" });
                continue;
            }
            string? problem = null;
            switch (change.Write)
            {
                case NewModelPlan plan:
                    if (await CurrentModelAsync(plan.Model.Id) is not null) problem = "model already exists";
                    break;
                case FieldPlan field:
                {
                    var model = await CurrentModelAsync(change.ModelId);
                    if (model is null) problem = "model no longer exists";
                    else if (string.Equals(model.Source, "user", StringComparison.Ordinal)) problem = "model is user-owned";
                    else if (model.UserModifiedFields.Contains(FieldToStorage[field.Field]))
                        problem = $"field '{field.Field}' is user-modified";
                    else if (!JsonEqual(FieldNode(model, field.Field), change.Before))
                        problem = "model changed since the preview";
                    break;
                }
                case PricePlan price:
                {
                    var model = await CurrentModelAsync(change.ModelId);
                    if (model is null) problem = "model no longer exists";
                    else if (string.Equals(model.Source, "user", StringComparison.Ordinal)) problem = "model is user-owned";
                    else
                    {
                        var row = await db.Models.GetPriceAsync(change.ModelId, price.PriceKey, ct);
                        priceCache[(change.ModelId, price.PriceKey)] = row;
                        if (row is null)
                        {
                            if (change.Before is not null) problem = "price row changed since the preview";
                        }
                        else if (row.UserModified) problem = $"price '{price.PriceKey}' is user-modified";
                        else if (!JsonEqual(PriceNode(row.UpstreamModelId, row.Pricing), change.Before))
                            problem = "price changed since the preview";
                    }
                    break;
                }
            }
            if (problem is not null)
                problems.Add(new { changeId = change.Id, modelId = change.ModelId, priceKey = change.PriceKey, reason = problem });
        }

        if (problems.Count > 0)
            return Results.Json(
                new ErrorBody("Selected changes conflict with the current catalog; nothing was applied", problems),
                statusCode: StatusCodes.Status409Conflict);

        // Recheck these immutable observations inside the write transaction to close the preflight/write race.
        var expectedModels = modelCache.ToDictionary(kv => kv.Key, kv => Json.Deserialize<SystemModel>(Json.Serialize(kv.Value)), StringComparer.Ordinal);
        var expectedPrices = priceCache.ToDictionary(kv => kv.Key, kv => Json.Deserialize<ModelPrice>(Json.Serialize(kv.Value)));
        // Atomic write: new models first, then field updates (merged per model), then price upserts.
        var newModels = new List<SystemModel>();
        var updatedModels = new List<SystemModel>();
        var upsertPrices = new List<ModelPrice>();
        foreach (var change in changes)
        {
            switch (change.Write)
            {
                case NewModelPlan plan:
                    plan.Model.CreatedAt = now;
                    plan.Model.UpdatedAt = now;
                    newModels.Add(plan.Model);
                    foreach (var price in plan.Prices)
                    {
                        price.UpdatedAt = now;
                        upsertPrices.Add(price);
                    }
                    break;
                case FieldPlan field:
                {
                    var model = (await CurrentModelAsync(change.ModelId))!; // preflight proved presence
                    ApplyField(model, field.Field, field.After);
                    model.Source = "sync";
                    model.UpdatedAt = now;
                    if (updatedModels.All(m => m.Id != model.Id)) updatedModels.Add(model);
                    break;
                }
                case PricePlan price:
                {
                    upsertPrices.Add(new ModelPrice
                    {
                        ModelId = change.ModelId,
                        PriceKey = price.PriceKey,
                        UpstreamModelId = price.UpstreamModelId,
                        Pricing = price.Pricing.Clone(),
                        Source = "sync",
                        UserModified = false,
                        UpdatedAt = now,
                    });
                    break;
                }
            }
        }

        if (!await db.Models.ApplySyncBatchAsync(newModels, updatedModels, upsertPrices, expectedModels, expectedPrices, ct))
            return Results.Json(new ErrorBody("Catalog changed during sync; nothing was applied. Run preview again"), statusCode: 409);
        return Results.Ok(new SyncApplyResult(changes.Count));
    }

    private static void ApplyField(SystemModel model, string field, JsonNode? after)
    {
        switch (field)
        {
            case "displayName": model.DisplayName = AsString(after) ?? model.DisplayName; break;
            case "family": model.Family = AsString(after); break;
            case "aliases":
                if (after is not null)
                    model.Aliases = Json.Deserialize<List<string>>(after.ToJsonString()) ?? model.Aliases;
                break;
            case "contextWindow": model.ContextWindow = LongOrNull(after); break;
            case "maxOutputTokens": model.MaxOutputTokens = LongOrNull(after); break;
            case "capabilities":
                if (after is not null)
                    model.Capabilities = after.Deserialize<ModelCapabilities>(Json.Api) ?? model.Capabilities;
                break;
            case "pricing": model.Pricing = after is null ? null : PricingJson.FromNode(after); break;
        }
    }

    // ----- JSON helpers (canonical representations before/after are built the same way) -----

    private static JsonNode? FieldNode(SystemModel m, string field) => field switch
    {
        "displayName" => Node(m.DisplayName),
        "family" => Node(m.Family),
        "aliases" => Node(m.Aliases),
        "contextWindow" => Node(m.ContextWindow),
        "maxOutputTokens" => Node(m.MaxOutputTokens),
        "capabilities" => CapabilitiesNode(m.Capabilities),
        "pricing" => PricingNode(m.Pricing),
        _ => null,
    };

    private static JsonNode NewModelSummary(SystemModel model, List<ModelPrice> prices) => new JsonObject
    {
        ["displayName"] = model.DisplayName,
        ["vendor"] = model.Vendor,
        ["family"] = model.Family,
        ["aliases"] = Node(model.Aliases),
        ["contextWindow"] = Node(model.ContextWindow),
        ["maxOutputTokens"] = Node(model.MaxOutputTokens),
        ["capabilities"] = CapabilitiesNode(model.Capabilities),
        ["pricing"] = PricingNode(model.Pricing),
        ["providerPrices"] = new JsonObject(prices.Select(p => KeyValuePair.Create<string, JsonNode?>(p.PriceKey,
            new JsonObject { ["upstreamModelId"] = p.UpstreamModelId, ["pricing"] = PricingNode(p.Pricing) }))),
    };

    private static JsonNode? Node(string? value) => value is null ? null : JsonValue.Create(value);
    private static JsonNode? Node(long? value) => value is null ? null : JsonValue.Create(value);
    private static JsonNode Node(IReadOnlyList<string> values) => new JsonArray(values.Select(v => JsonValue.Create(v)).ToArray());
    private static JsonNode? CapabilitiesNode(ModelCapabilities c) => JsonNode.Parse(JsonSerializer.Serialize(c, Json.Api));
    private static JsonNode? PricingNode(PricingSchedule? p) => PricingJson.ToNode(p);
    private static JsonNode? PriceNode(string? upstreamModelId, PricingSchedule? p) => new JsonObject
    {
        ["upstreamModelId"] = upstreamModelId,
        ["pricing"] = PricingNode(p),
    };

    private static bool JsonEqual(JsonNode? a, JsonNode? b) =>
        JsonNode.DeepEquals(a, b);

    private static string? AsString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    private static long? LongOrNull(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var l) ? l : null;

    private static decimal? Dec(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<decimal>(out var d) ? d : null;

    private static long? LongIn(JsonNode? node, string key) =>
        node is JsonObject o && o.TryGetPropertyValue(key, out var child) && child is JsonValue value && value.TryGetValue<long>(out var l) ? l : null;

    private static bool? Bool(JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var node) && node is JsonValue value && value.TryGetValue<bool>(out var b) ? b : null;

    private static List<string>? StringList(JsonObject o, string container, string key) =>
        o[container] is JsonObject c && c[key] is JsonArray array
            ? array.Select(v => v is JsonValue value && value.TryGetValue<string>(out var s) ? s : null)
                .Where(s => s is not null).Cast<string>().ToList()
            : null;

    private static decimal Round6(decimal value) => decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}

