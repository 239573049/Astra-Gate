using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Astra.Core;
using Astra.Core.Billing;
using Astra.Core.Models;

namespace Astra.Server.Api;

// DTOs mirroring web/src/api/types.ts. PricingSchedule objects travel as raw JSON in storage (snake_case) format.

public static class PricingJson
{
    public static JsonNode? ToNode(PricingSchedule? p) => p is null ? null : JsonNode.Parse(Json.Serialize(p));

    public static PricingSchedule? FromNode(JsonNode? node) => node is null ? null : Json.Deserialize<PricingSchedule>(node.ToJsonString());
}

public sealed record ErrorBody(string Error, object? Details = null);

public class ModelDto
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Vendor { get; init; } = "";
    public string? Family { get; init; }
    public List<string> Aliases { get; init; } = [];
    public long? ContextWindow { get; init; }
    public long? MaxOutputTokens { get; init; }
    public ModelCapabilities Capabilities { get; init; } = new();
    public JsonNode? Pricing { get; init; }
    public string Source { get; init; } = "";
    public List<string> UserModifiedFields { get; init; } = [];
    public bool Enabled { get; init; }
    public List<string> ProviderPriceKeys { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ModelPriceDto(string PriceKey, string? UpstreamModelId, JsonNode? Pricing, string Source, bool UserModified, DateTimeOffset UpdatedAt);

public sealed record ProviderOverrideRefDto(string ProviderId, string ProviderName, long ProviderModelId, string ModelId, JsonNode? Pricing);

public sealed class ModelDetailDto : ModelDto
{
    public List<ModelPriceDto> ProviderPrices { get; init; } = [];
    public List<ProviderOverrideRefDto> ProviderOverrides { get; init; } = [];
}

public sealed class ModelInput
{
    public string? Id { get; set; }
    public string DisplayName { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string? Family { get; set; }
    public List<string>? Aliases { get; set; }
    public long? ContextWindow { get; set; }
    public long? MaxOutputTokens { get; set; }
    public ModelCapabilities? Capabilities { get; set; }
    public JsonNode? Pricing { get; set; }
    public bool? Enabled { get; set; }
}

public sealed record ModelPriceInput(string? UpstreamModelId, JsonNode Pricing);

public sealed record ResetFieldInput(string Field);

public sealed record PriceKeyInfo(string PriceKey, string Label, List<string> TemplateIds);

public sealed class BillingSimulateRequest
{
    public JsonNode? Pricing { get; set; }
    public string? ModelId { get; set; }
    public string? ProviderId { get; set; }
    public SimulateUsage Usage { get; set; } = new();
    public DateTimeOffset? RequestTimeUtc { get; set; }
    public decimal? ProviderMultiplier { get; set; }
    public string? Locale { get; set; }
}

public sealed class SimulateUsage
{
    public Dictionary<string, long> Tokens { get; set; } = new();
    public Dictionary<string, long>? Calls { get; set; }
    public string? ServiceTier { get; set; }

    public NormalizedUsage ToNormalized()
    {
        var u = new NormalizedUsage { ServiceTier = ServiceTier };
        foreach (var (k, v) in Tokens) u.Add(k, v);
        if (Calls is not null)
        {
            foreach (var (k, v) in Calls.Where(kv => kv.Value > 0)) u.Calls[k] = v;
        }
        return u;
    }
}

public sealed record BillingItemDto(
    string TokenType, long Tokens, bool IsPerCall, decimal BaseUnitPrice, decimal UnitPrice, decimal Multiplier,
    List<string> MultiplierSources, string Tier, string PricedAs, long CostNanos, string? Note);

public sealed record BillingResultDto(
    List<BillingItemDto> Items, long TotalNanos, decimal TotalUsd, bool Priced, List<BillingTraceStep> Trace,
    string Description, string PricingSource, string? PriceKey)
{
    public static BillingResultDto From(BillingResult r, ResolvedPricing resolved) => new(
        r.Items.Select(i => new BillingItemDto(i.TokenType, i.Tokens, i.IsPerCall, i.BaseUnitPrice, i.UnitPrice, i.Multiplier,
            i.MultiplierSources, i.Tier, i.PricedAs, i.CostNanos, i.Note)).ToList(),
        r.TotalNanos, r.TotalUsd, r.Priced, r.Trace, r.Description, SourceId(resolved.Source), resolved.PriceKey);

    public static string SourceId(PricingSource s) => s switch
    {
        Core.Billing.PricingSource.ProviderOverride => "provider_override",
        Core.Billing.PricingSource.ProviderPrice => "provider_price",
        Core.Billing.PricingSource.SystemDefault => "system_default",
        _ => "none",
    };
}

public sealed record VersionDto(string Version, string ApiVersion);
