using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;
using Astra.Core.Seed;

namespace Astra.Providers.Templates;

public sealed class TemplateAuth
{
    public string Scheme { get; set; } = AuthSchemes.Bearer;
}

public sealed class TemplateVariant
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? PriceKey { get; set; }
    public List<ProviderEndpoint>? EndpointsOverride { get; set; }
}

public sealed class TemplateModelListEndpoint
{
    public ApiProtocol Protocol { get; set; }
    public string Path { get; set; } = "/models";
}

/// <summary>Versioned data, not provider-specific code. The API uses camelCase; snapshots use storage JSON.</summary>
public sealed class ProviderTemplate
{
    public string Id { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Category { get; set; } = "custom";
    public string? Icon { get; set; }
    public string? Website { get; set; }
    public string? ApiKeyUrl { get; set; }
    public string? DocsUrl { get; set; }
    public List<ProviderEndpoint> Endpoints { get; set; } = [];
    public List<ApiProtocol> PreferredUpstreamProtocols { get; set; } = [];
    public TemplateAuth Auth { get; set; } = new();

    /// <summary>Convenience view of <see cref="Auth"/>.Scheme for API consumers (camelCase: authScheme).</summary>
    public string AuthScheme => Auth.Scheme;

    public bool RequiresApiKey { get; set; } = true;
    public List<string> Models { get; set; } = [];
    public string? PriceKey { get; set; }
    public decimal DefaultPriceMultiplier { get; set; } = 1m;
    public string? Adapter { get; set; }
    public Dictionary<string, string> DefaultHeaders { get; set; } = [];
    public JsonObject Settings { get; set; } = new();
    public List<TemplateVariant>? Variants { get; set; }
    public TemplateModelListEndpoint? ModelListEndpoint { get; set; }

    /// <summary>Populate the default model IDs from the embedded system catalog (no network at startup).</summary>
    public string? ModelVendor { get; set; }

    public ProviderTemplate Clone() => JsonSerializer.Deserialize<ProviderTemplate>(JsonSerializer.Serialize(this, Json.Api), Json.Api)!;

    public ProviderTemplate WithVariant(string? variantId)
    {
        var copy = Clone();
        if (string.IsNullOrEmpty(variantId)) return copy;
        var variant = Variants?.FirstOrDefault(v => v.Id == variantId)
            ?? throw new ArgumentException($"Unknown variant '{variantId}' for '{Id}'");
        if (variant.EndpointsOverride is { Count: > 0 }) copy.Endpoints = Json.Deserialize<List<ProviderEndpoint>>(Json.Serialize(variant.EndpointsOverride))!;
        if (variant.PriceKey is not null) copy.PriceKey = variant.PriceKey;
        return copy;
    }
}

public sealed class ProviderTemplateCatalog
{
    private readonly IReadOnlyList<ProviderTemplate> _templates = Load();
    public IReadOnlyList<ProviderTemplate> All => _templates;
    public ProviderTemplate? Get(string id) => _templates.FirstOrDefault(t => t.Id == id);

    private static IReadOnlyList<ProviderTemplate> Load()
    {
        var result = new List<ProviderTemplate>();
        var assembly = typeof(ProviderTemplateCatalog).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.Contains(".Templates.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            var node = JsonNode.Parse(stream)!;
            var rows = node is JsonArray array ? array : new JsonArray(node.DeepClone());
            foreach (var row in rows)
            {
                var template = row?.Deserialize<ProviderTemplate>(Json.Api);
                if (template is null || template.Id.Length == 0) continue;
                if (result.Any(t => t.Id == template.Id)) throw new InvalidOperationException($"Duplicate provider template '{template.Id}'");
                if (template.Name.Length == 0 || template.Version < 1 || template.Endpoints.Count == 0)
                    throw new InvalidOperationException($"Invalid provider template '{template.Id}'");
                if (template.PreferredUpstreamProtocols.Count == 0)
                    template.PreferredUpstreamProtocols = template.Endpoints.Select(e => e.Protocol).Distinct().ToList();
                if (template.Models.Count == 0)
                {
                    if (template.PriceKey is { } key && SeedCatalog.Current.Models.Any(m => m.ProviderPricing.ContainsKey(key)))
                        template.Models = SeedCatalog.Current.Models.Where(m => m.ProviderPricing.ContainsKey(key))
                            .Select(m => m.ProviderPricing[key].UpstreamModelId ?? m.Id).Distinct().ToList();
                    else if (template.ModelVendor is { } vendor)
                        template.Models = SeedCatalog.Current.Models.Where(m => m.Vendor == vendor).Select(m => m.Id).ToList();
                }
                result.Add(template);
            }
        }
        return result;
    }
}
