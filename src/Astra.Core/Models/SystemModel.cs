using Astra.Core.Billing;

namespace Astra.Core.Models;

public sealed class ModelCapabilities
{
    public bool? Vision { get; set; }
    public bool? Tools { get; set; }
    public bool? Reasoning { get; set; }
    public bool? PromptCache { get; set; }
    public bool? Audio { get; set; }
    public bool? Pdf { get; set; }
    public bool? StructuredOutput { get; set; }

    /// <summary>Per-field overlay: non-null values in <paramref name="overlay"/> win.</summary>
    public ModelCapabilities Overlay(ModelCapabilities? overlay) => overlay is null ? Clone() : new()
    {
        Vision = overlay.Vision ?? Vision,
        Tools = overlay.Tools ?? Tools,
        Reasoning = overlay.Reasoning ?? Reasoning,
        PromptCache = overlay.PromptCache ?? PromptCache,
        Audio = overlay.Audio ?? Audio,
        Pdf = overlay.Pdf ?? Pdf,
        StructuredOutput = overlay.StructuredOutput ?? StructuredOutput,
    };

    public ModelCapabilities Clone() => (ModelCapabilities)MemberwiseClone();
}

/// <summary>A model in the system catalog ("模型管理"). Prices are per AI provider: an official default plus per price-key tables.</summary>
public sealed class SystemModel
{
    /// <summary>Canonical model id, e.g. "claude-sonnet-4-6".</summary>
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";

    /// <summary>Model maker, e.g. "anthropic". Its official price is the default pricing.</summary>
    public string Vendor { get; set; } = "";
    public string? Family { get; set; }
    public List<string> Aliases { get; set; } = [];
    public long? ContextWindow { get; set; }
    public long? MaxOutputTokens { get; set; }
    public ModelCapabilities Capabilities { get; set; } = new();

    /// <summary>Official (vendor) default pricing.</summary>
    public PricingSchedule? Pricing { get; set; }

    /// <summary>"seed" | "sync" | "user".</summary>
    public string Source { get; set; } = "user";
    public List<string> UserModifiedFields { get; set; } = [];
    public int? SeedVersion { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>System price of a model at one AI provider (price key = provider type, usually the template id).</summary>
public sealed class ModelPrice
{
    public long Id { get; set; }
    public string ModelId { get; set; } = "";
    public string PriceKey { get; set; } = "";
    public PricingSchedule Pricing { get; set; } = new();

    /// <summary>The upstream model id this provider uses for the model, if it differs (helps auto-linking).</summary>
    public string? UpstreamModelId { get; set; }

    public string Source { get; set; } = "user";
    public bool UserModified { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Per-field overrides stored on a provider model. Null = inherit from the system model.</summary>
public sealed class ModelOverrides
{
    public string? DisplayName { get; set; }
    public long? ContextWindow { get; set; }
    public long? MaxOutputTokens { get; set; }
    public ModelCapabilities? Capabilities { get; set; }

    /// <summary>Whole-object override: replaces the inherited price and is not scaled by the provider multiplier.</summary>
    public PricingSchedule? Pricing { get; set; }

    public bool IsEmpty => DisplayName is null && ContextWindow is null && MaxOutputTokens is null && Capabilities is null && Pricing is null;
}
