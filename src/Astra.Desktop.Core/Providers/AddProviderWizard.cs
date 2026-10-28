using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Desktop.Core.Providers;

/// <summary>
/// The state and rules of the add-provider wizard (ProvidersPage.tsx <c>AddProviderSheet</c>): step 1 picks a template (a built-in one, a
/// plugin's provider, or "custom"), step 2 fills in the details (name, key, price key, "more options": endpoints, preferred protocols,
/// headers, proxy, multiplier, initial models, notes). No UI types, so every rule is unit-tested. The view subscribes to
/// <see cref="StepChanged"/> (rebuild the whole body) and <see cref="Changed"/> (refresh the footer buttons and hints only).
/// </summary>
public sealed partial class AddProviderWizard
{
    private ProviderTemplateInfo? _template;
    private bool _custom;
    private List<TemplateEndpoint> _endpoints = [];

    /// <summary>Raised when the step (or the picked template) changed, so the body must be rebuilt.</summary>
    public event Action? StepChanged;

    /// <summary>Raised when any value that decides validity or hints changed.</summary>
    public event Action? Changed;

    /// <summary>Step 1 search text.</summary>
    public string Search { get; set; } = "";

    /// <summary>Step 1 selection (a template id, <see cref="ProviderTemplates.CustomChoice"/> or <c>plugin:&lt;provider id&gt;</c>).</summary>
    public string? Choice { get; set; }

    /// <summary>True from the moment a template (or custom) was picked: step 2.</summary>
    public bool IsDetails => _custom || _template is not null;

    /// <summary>Zero-based step index for the stepper.</summary>
    public int StepIndex => IsDetails ? 1 : 0;

    /// <summary>The picked built-in template, null for custom or step 1.</summary>
    public ProviderTemplateInfo? Template => _template;

    /// <summary>True when "custom" was picked.</summary>
    public bool IsCustom => _custom;

    /// <summary>Subscription templates sign in with accounts after creation; their addresses and headers are the template's.</summary>
    public bool Accounts => _template?.IsSubscription == true;

    /// <summary>Provider name.</summary>
    public string Name { get; set; } = "";

    /// <summary>API key text (trimmed on submit).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Selected variant id ("" = the template's default).</summary>
    public string VariantId { get; private set; } = "";

    /// <summary>Selected price key ("" = none).</summary>
    public string PriceKey { get; set; } = "";

    /// <summary>Auth scheme.</summary>
    public string AuthScheme { get; set; } = "bearer";

    /// <summary>Endpoints being edited.</summary>
    public IReadOnlyList<TemplateEndpoint> Endpoints => _endpoints;

    /// <summary>Preferred upstream protocols in priority order.</summary>
    public List<string> Preferred { get; private set; } = [];

    /// <summary>Extra headers being edited (blank names are dropped on submit).</summary>
    public List<(string Name, string Value)> Headers { get; set; } = [];

    /// <summary>HTTP proxy.</summary>
    public string Proxy { get; set; } = "";

    /// <summary>Price multiplier.</summary>
    public decimal Multiplier { get; set; } = 1;

    /// <summary>Notes.</summary>
    public string Notes { get; set; } = "";

    /// <summary>Initial model ids as typed (one per line or comma separated).</summary>
    public string Models { get; set; } = "";

    /// <summary>The typed model ids, split on whitespace / commas (ASCII and full-width), empties dropped.</summary>
    public IReadOnlyList<string> ModelIds => ModelSplit().Split(Models).Select(m => m.Trim()).Where(m => m.Length > 0).ToList();

    [GeneratedRegex(@"[\s,，]+")]
    private static partial Regex ModelSplit();

    /// <summary>Clears everything (the web does it whenever the sheet closes).</summary>
    public void Reset()
    {
        _template = null;
        _custom = false;
        Choice = null;
        Search = "";
        Name = "";
        ApiKey = "";
        VariantId = "";
        PriceKey = "";
        StepChanged?.Invoke();
    }

    /// <summary>Back to step 1 (the footer's Back button keeps the other typed values like the web).</summary>
    public void Back()
    {
        _template = null;
        _custom = false;
        StepChanged?.Invoke();
    }

    /// <summary>Step 1 -> 2 with a built-in template (web <c>pick(tpl)</c>).</summary>
    public void Pick(ProviderTemplateInfo template)
    {
        ResetPicked();
        _template = template;
        _custom = false;
        Name = template.Name;
        PriceKey = template.PriceKey ?? template.Id;
        AuthScheme = template.AuthScheme;
        _endpoints = template.Endpoints.Select(e => e with { }).ToList();
        Preferred = [.. template.PreferredUpstreamProtocols];
        Headers = [.. template.DefaultHeaders];
        Multiplier = template.DefaultPriceMultiplier ?? 1;
        StepChanged?.Invoke();
    }

    /// <summary>Step 1 -> 2 with the custom provider.</summary>
    public void PickCustom()
    {
        ResetPicked();
        _template = null;
        _custom = true;
        Name = "";
        PriceKey = "";
        AuthScheme = "bearer";
        _endpoints = [new TemplateEndpoint("openai-chat", "")];
        Preferred = ["openai-chat"];
        Headers = [];
        Multiplier = 1;
        StepChanged?.Invoke();
    }

    private void ResetPicked()
    {
        VariantId = "";
        Proxy = "";
        Notes = "";
        Models = "";
    }

    /// <summary>The "Continue" button: moves the selection to step 2 and returns null, or returns the plugin provider id to open instead.</summary>
    public string? Proceed(IReadOnlyList<ProviderTemplateInfo> templates, IReadOnlyCollection<string> addedSubscriptionIds, IReadOnlyList<Astra.Api.Contracts.ProviderDto> pluginProviders)
    {
        if (Choice == ProviderTemplates.CustomChoice) PickCustom();
        else if (Choice is { } c && c.StartsWith(ProviderTemplates.PluginChoice, StringComparison.Ordinal))
            return pluginProviders.FirstOrDefault(p => ProviderTemplates.PluginChoice + p.Id == c)?.Id;
        else if (templates.FirstOrDefault(x => x.Id == Choice) is { } found && !addedSubscriptionIds.Contains(found.Id)) Pick(found);
        return null;
    }

    /// <summary>The variant picker: sets the price key and the endpoints from the variant (web <c>onChange</c> of the variant select).</summary>
    public void ChooseVariant(string variantId)
    {
        if (_template is not { } tpl) return;
        VariantId = variantId;
        var variant = tpl.Variants.FirstOrDefault(x => x.Id == variantId);
        PriceKey = variant?.PriceKey ?? tpl.PriceKey ?? tpl.Id;
        SetEndpoints((variant?.EndpointsOverride ?? tpl.Endpoints).Select(e => e with { }).ToList());
        Changed?.Invoke();
    }

    /// <summary>Replaces the endpoints; the preferred order follows (removed protocols drop out, new ones join at the end).</summary>
    public void SetEndpoints(List<TemplateEndpoint> next)
    {
        Preferred = FollowEndpoints(Preferred, _endpoints, next);
        _endpoints = next;
    }

    /// <summary>The protocol order after the endpoints changed (web <c>followEndpoints</c>).</summary>
    public static List<string> FollowEndpoints(IReadOnlyList<string> preferred, IReadOnlyList<TemplateEndpoint> before, IReadOnlyList<TemplateEndpoint> after)
    {
        var had = before.Select(e => e.Protocol).ToHashSet();
        var now = after.Select(e => e.Protocol).ToList();
        return [.. preferred.Where(now.Contains), .. now.Where(p => !had.Contains(p) && !preferred.Contains(p))];
    }

    /// <summary>The chips report the options order; keep the priority order in which protocols were picked (web <c>onValueChange</c>).</summary>
    public void SetPreferred(IReadOnlyCollection<string> next)
    {
        var kept = Preferred.Where(next.Contains).ToList();
        var added = next.Where(x => !kept.Contains(x)).ToList();
        Preferred = [.. kept, .. added];
        Changed?.Invoke();
    }

    /// <summary>Templates this one's endpoints refuse the key of (<c>keyAlternatives</c>, memory provider-key-alternatives), resolved against the catalog.</summary>
    public List<ProviderTemplateInfo> Alternatives(IEnumerable<ProviderTemplateInfo> all)
        => (_template?.KeyAlternatives ?? []).SelectMany(id => all.Where(x => x.Id == id)).ToList();

    /// <summary>Whether every endpoint is an http(s) address (subscription templates need none).</summary>
    public bool EndpointsOk => Accounts || (_endpoints.Count > 0 && _endpoints.All(e => HttpUrl().IsMatch(e.BaseUrl.Trim())));

    [GeneratedRegex(@"^https?://")]
    private static partial Regex HttpUrl();

    /// <summary>The Create button's enabled state (web <c>canSubmit</c>).</summary>
    public bool CanSubmit => IsDetails && EndpointsOk
        && (_custom ? Name.Trim() != "" : !_template!.RequiresApiKey || AuthScheme == "none" || ApiKey.Trim() != "");

    /// <summary>The "template models" hint applies: a template with models and none typed.</summary>
    public bool ShowTemplateModelsHint => _template is { ModelCount: > 0 } && ModelIds.Count == 0;

    /// <summary>The <c>POST /api/providers</c> body (web <c>submit</c>): only fields the user may set; subscription templates send just the identity.</summary>
    public JsonObject BuildRequest()
    {
        var body = new JsonObject
        {
            ["templateId"] = _custom ? null : _template!.Id,
            ["variantId"] = VariantId.Length > 0 ? VariantId : null,
            ["name"] = Name.Trim() is { Length: > 0 } n ? n : null,
            ["apiKey"] = ApiKey.Trim() is { Length: > 0 } k ? k : null,
            ["priceKey"] = PriceKey,
        };
        if (Accounts) return body;
        var endpoints = new JsonArray();
        foreach (var e in _endpoints)
        {
            var o = new JsonObject { ["protocol"] = e.Protocol, ["baseUrl"] = e.BaseUrl.Trim() };
            if (e.FullUrl is { } f) o["fullUrl"] = f;
            endpoints.Add((JsonNode)o);
        }
        body["endpoints"] = endpoints;
        body["authScheme"] = AuthScheme;
        body["preferredUpstreamProtocols"] = new JsonArray(Preferred.Where(p => _endpoints.Any(e => e.Protocol == p)).Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
        var headers = new JsonObject();
        foreach (var (hk, hv) in Headers)
            if (hk.Trim().Length > 0) headers[hk.Trim()] = hv;
        body["extraHeaders"] = headers;
        body["httpProxy"] = Proxy.Trim() is { Length: > 0 } px ? px : null;
        body["priceMultiplier"] = Multiplier;
        body["notes"] = Notes.Trim() is { Length: > 0 } nt ? nt : null;
        if (ModelIds.Count > 0) body["models"] = new JsonArray(ModelIds.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
        return body;
    }

    /// <summary>Tells the view a value changed (call after a text edit).</summary>
    public void NotifyChanged() => Changed?.Invoke();
}
