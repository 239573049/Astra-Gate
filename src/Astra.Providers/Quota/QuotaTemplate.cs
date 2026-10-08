using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Models;

namespace Astra.Providers.Quota;

/// <summary>How a balance / quota request is sent. <c>{{name}}</c> placeholders are filled from the query variables.</summary>
public sealed class QuotaRequestSpec
{
    public string Method { get; set; } = "GET";

    /// <summary>Absolute URL template, e.g. <c>{{origin}}/user/balance</c>.</summary>
    public string Url { get; set; } = "";

    /// <summary><c>provider</c>: the provider's own auth scheme and API key; <c>none</c>: only the headers below.</summary>
    public string Auth { get; set; } = QuotaAuth.Provider;

    public Dictionary<string, string> Headers { get; set; } = [];
}

public static class QuotaAuth
{
    public const string Provider = "provider";
    public const string None = "none";
}

/// <summary>An extra value a template needs (e.g. New API's user id); <see cref="Secret"/> values are stored encrypted.</summary>
public sealed class QuotaParamSpec
{
    public string Name { get; set; } = "";
    public bool Secret { get; set; }
    public bool Required { get; set; }
}

/// <summary>A built-in balance / quota query (data, not code): one request plus a declarative extractor.</summary>
public sealed class QuotaTemplate
{
    public const string CustomId = "custom";

    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary><c>balance</c> (money left) or <c>plan</c> (usage windows of a coding plan).</summary>
    public string Kind { get; set; } = "balance";

    /// <summary>Provider template ids this query is the default for.</summary>
    public List<string> AppliesTo { get; set; } = [];

    /// <summary>Upstream hosts this query is the default for (exact host match on the provider's endpoint).</summary>
    public List<string> Hosts { get; set; } = [];

    public List<QuotaParamSpec> Params { get; set; } = [];
    public QuotaRequestSpec Request { get; set; } = new();
    public JsonObject Extract { get; set; } = new();
}

/// <summary>The built-in quota templates, embedded as <c>Quota/quota-templates.json</c>.</summary>
public sealed class QuotaTemplateCatalog
{
    private readonly IReadOnlyList<QuotaTemplate> _templates = Load();

    public IReadOnlyList<QuotaTemplate> All => _templates;

    public QuotaTemplate? Get(string? id) => id is null ? null : _templates.FirstOrDefault(t => t.Id == id);

    /// <summary>
    /// The template a provider uses when none is chosen: first by provider template id, then by the host of
    /// its endpoints. Null when nothing matches (the user then picks one or writes a custom query).
    /// </summary>
    public QuotaTemplate? Suggest(Provider provider)
    {
        if (provider.TemplateId is { } templateId && _templates.FirstOrDefault(t => t.AppliesTo.Contains(templateId)) is { } byTemplate)
        {
            // custom-* templates map to New API only as a fallback: a known host wins over it.
            if (byTemplate.Hosts.Count > 0 || !templateId.StartsWith("custom-", StringComparison.Ordinal)) return byTemplate;
            return ByHost(provider) ?? byTemplate;
        }
        return ByHost(provider);
    }

    private QuotaTemplate? ByHost(Provider provider)
    {
        foreach (var endpoint in provider.Endpoints)
        {
            if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri)) continue;
            if (_templates.FirstOrDefault(t => t.Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) is { } hit) return hit;
        }
        return null;
    }

    private static IReadOnlyList<QuotaTemplate> Load()
    {
        var assembly = typeof(QuotaTemplateCatalog).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".Quota.quota-templates.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        var result = new List<QuotaTemplate>();
        foreach (var row in JsonNode.Parse(stream)!.AsArray())
        {
            var template = Json.DeserializeApi<QuotaTemplate>(row) ?? throw new InvalidOperationException("Invalid quota template");
            if (template.Id.Length == 0 || template.Id == QuotaTemplate.CustomId || result.Any(t => t.Id == template.Id))
                throw new InvalidOperationException($"Invalid or duplicate quota template id '{template.Id}'");
            var errors = QuotaConfig.ValidateRequest(template.Request, "request");
            errors.AddRange(QuotaExtractor.Validate(template.Extract));
            if (errors.Count > 0) throw new InvalidOperationException($"Invalid quota template '{template.Id}': {string.Join("; ", errors)}");
            result.Add(template);
        }
        return result;
    }
}
