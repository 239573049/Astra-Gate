using System.Text.Json.Nodes;
using Astra.Core.Models;
using Astra.Gateway.Pipeline;
using Astra.Providers.Templates;
using Astra.Server.Api;

namespace Astra.Server.Relay;

/// <summary>
/// Where the relay sends requests: one provider template — its own endpoints or one of its variants (a plan's or a
/// region's hosts) — on the template's origin or the operator's <see cref="RelayOptions.BaseUrl"/>. Each request becomes a
/// <see cref="RelayCaller"/> with a provider built from the template, so what the pipeline does for this template (its
/// default headers, model ids, subscription handling) applies unchanged.
/// </summary>
internal sealed class RelayTarget
{
    /// <summary>The request header that picks a template variant for one request (<see cref="DefaultVariant"/>: the template's own endpoints).</summary>
    public const string VariantHeader = "x-astra-variant";

    public const string DefaultVariant = "default";

    private readonly Dictionary<string, ProviderTemplate> _variants;
    private readonly string _defaultVariant;
    private readonly Uri? _baseUrl;

    private RelayTarget(ProviderTemplate template, Dictionary<string, ProviderTemplate> variants, string defaultVariant, Uri? baseUrl)
    {
        Template = template;
        _variants = variants;
        _defaultVariant = defaultVariant;
        _baseUrl = baseUrl;
    }

    public ProviderTemplate Template { get; }

    /// <summary>The variant a request gets when it names none.</summary>
    public string Variant => _defaultVariant;

    /// <exception cref="ArgumentException">Unknown template or variant, a template that sends no credential, or one without a usable address.</exception>
    public static RelayTarget Create(RelayOptions options, ProviderTemplateCatalog catalog)
    {
        var template = catalog.Get(options.Provider)
                       ?? throw new ArgumentException($"Unknown provider template '{options.Provider}'. Templates: {string.Join(", ", catalog.All.Select(t => t.Id))}");
        if (template.AuthScheme == AuthSchemes.None)
            throw new ArgumentException($"Template '{template.Id}' sends no credential upstream, and the relay forwards the caller's own.");
        var variants = new Dictionary<string, ProviderTemplate>(StringComparer.Ordinal) { [DefaultVariant] = template };
        foreach (var variant in template.Variants ?? []) variants[variant.Id] = template.WithVariant(variant.Id);
        var chosen = options.Variant ?? DefaultVariant;
        if (!variants.ContainsKey(chosen))
            throw new ArgumentException($"Template '{template.Id}' has no variant '{chosen}'. Variants: {string.Join(", ", variants.Keys)}");
        var target = new RelayTarget(template, variants, chosen, options.BaseUrl is null ? null : new Uri(options.BaseUrl));
        foreach (var each in variants.Values)
            foreach (var endpoint in target.ProviderFor(each).Endpoints)
                if (!Uri.TryCreate(endpoint.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                    throw new ArgumentException($"Template '{template.Id}' has no usable address ({endpoint.BaseUrl}); give one with --base-url.");
        return target;
    }

    /// <summary>
    /// The caller of <paramref name="request"/>: its credential (401 when missing or malformed), then the variant it names
    /// (400 when the template has no such variant).
    /// </summary>
    /// <exception cref="GatewayException">No usable credential, or an unknown variant.</exception>
    public RelayCaller Caller(HttpRequest request)
    {
        var credential = RelayCaller.ReadCredential(request.Headers);
        var variant = VariantOf(request.Headers);
        var template = _variants[variant];
        var provider = ProviderFor(template);
        var list = template.ModelListEndpoint;
        var listUrl = list is not null && provider.EndpointFor(list.Protocol) is { FullUrl: false } endpoint
            ? ProviderProbe.AppendPath(endpoint.BaseUrl, list.Path)
            : null;
        return new RelayCaller(credential, variant, provider, listUrl is null ? null : list, listUrl, request.Headers);
    }

    private string VariantOf(IHeaderDictionary headers)
    {
        var values = headers[VariantHeader];
        if (values.Count == 0) return _defaultVariant;
        var id = values.Count == 1 ? values[0]?.Trim() ?? "" : "";
        return _variants.ContainsKey(id) ? id
            : throw new GatewayException(400, "invalid_request_error", $"{VariantHeader} 只能有一个值，且是以下之一：{string.Join("、", _variants.Keys)}。");
    }

    /// <summary>The provider a request runs against: the template's data, on the operator's origin when one is set.</summary>
    private Provider ProviderFor(ProviderTemplate template) => new()
    {
        Id = template.Id,
        TemplateId = template.Id,
        TemplateVersion = template.Version,
        Name = template.Name,
        Category = template.Category,
        Endpoints = template.Endpoints.Select(e => new ProviderEndpoint
        {
            Protocol = e.Protocol,
            BaseUrl = _baseUrl is not null && Uri.TryCreate(e.BaseUrl, UriKind.Absolute, out var own)
                ? new Uri(_baseUrl, own.AbsolutePath).AbsoluteUri.TrimEnd('/')
                : e.BaseUrl,
            FullUrl = e.FullUrl,
        }).ToList(),
        PreferredUpstreamProtocols = [.. template.PreferredUpstreamProtocols],
        AuthScheme = template.AuthScheme,
        ExtraHeaders = new Dictionary<string, string>(template.DefaultHeaders, StringComparer.OrdinalIgnoreCase),
        Settings = (JsonObject)template.Settings.DeepClone(),
        PriceKey = template.PriceKey,
        AdapterId = template.Adapter,
    };
}
