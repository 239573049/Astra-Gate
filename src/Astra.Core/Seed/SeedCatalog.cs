using Astra.Core.Billing;
using Astra.Core.Models;

namespace Astra.Core.Seed;

public sealed class SeedFile
{
    public int SeedVersion { get; set; }
    public string? GeneratedAt { get; set; }
    public List<string> Sources { get; set; } = [];
    public List<SeedModel> Models { get; set; } = [];
}

public sealed class SeedModel
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Vendor { get; set; } = "";
    public string? Family { get; set; }
    public List<string> Aliases { get; set; } = [];
    public long? ContextWindow { get; set; }
    public long? MaxOutputTokens { get; set; }
    public ModelCapabilities Capabilities { get; set; } = new();

    /// <summary>Official (vendor) default pricing.</summary>
    public PricingSchedule? Pricing { get; set; }

    /// <summary>Pricing at other AI providers, keyed by price key (provider type).</summary>
    public Dictionary<string, SeedProviderPrice> ProviderPricing { get; set; } = new();
}

public sealed class SeedProviderPrice
{
    public string? UpstreamModelId { get; set; }
    public PricingSchedule Pricing { get; set; } = new();
}

public static class SeedCatalog
{
    private static readonly Lazy<SeedFile> Cached = new(LoadEmbedded);

    public static SeedFile Current => Cached.Value;

    private static SeedFile LoadEmbedded()
    {
        var asm = typeof(SeedCatalog).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("Seed.models.json", StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return Json.Deserialize<SeedFile>(reader.ReadToEnd()) ?? new SeedFile();
    }

    public static SystemModel ToSystemModel(SeedModel s, int seedVersion, DateTimeOffset now) => new()
    {
        Id = s.Id,
        DisplayName = s.DisplayName,
        Vendor = s.Vendor,
        Family = s.Family,
        Aliases = [.. s.Aliases],
        ContextWindow = s.ContextWindow,
        MaxOutputTokens = s.MaxOutputTokens,
        Capabilities = s.Capabilities.Clone(),
        Pricing = s.Pricing?.Clone(),
        Source = "seed",
        SeedVersion = seedVersion,
        Enabled = true,
        CreatedAt = now,
        UpdatedAt = now,
    };
}
