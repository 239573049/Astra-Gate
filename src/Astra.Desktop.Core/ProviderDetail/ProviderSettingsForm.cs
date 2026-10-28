using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Api.Contracts;
using Astra.Core;
using Astra.Desktop.Core.Pricing;
using Astra.Desktop.Core.Providers;

namespace Astra.Desktop.Core.ProviderDetail;

/// <summary>An endpoint row of the settings tab (web <c>TemplateEndpoint</c>); <see cref="FullUrl"/> null = not set on the wire.</summary>
public sealed record EndpointRow(string Protocol, string BaseUrl, bool? FullUrl);

/// <summary>
/// The settings tab's working copy (web <c>ProviderUpdate</c> + the separate <c>headers</c> list of <c>SettingsTab</c>). Immutable: every edit
/// is a copy, so the page can compare it with the saved copy (<see cref="IsDirty"/>) and build the PATCH of the changed fields only
/// (<see cref="Patch"/>). Comparison is by JSON text, the web's <c>JSON.stringify</c> check.
/// </summary>
public sealed partial record ProviderSettingsForm(
    string Name, bool Enabled, string? Notes, string AuthScheme, IReadOnlyList<EndpointRow> Endpoints, IReadOnlyList<string> Preferred,
    decimal PriceMultiplier, string? PriceKey, string? HttpProxy, IReadOnlyList<(string Name, string Value)> Headers, JsonObject Settings, string ApiKey = "")
{
    /// <summary>The form of a provider (web <c>toUpdate</c>).</summary>
    public static ProviderSettingsForm From(ProviderDto p) => new(
        p.Name, p.Enabled, p.Notes, p.AuthScheme,
        p.Endpoints.Select(e => new EndpointRow(e.Protocol.ToId(), e.BaseUrl, e.FullUrl)).ToList(),
        p.PreferredUpstreamProtocols.Select(x => x.ToId()).ToList(),
        p.PriceMultiplier, p.PriceKey, p.HttpProxy, p.ExtraHeaders.Select(kv => (kv.Key, kv.Value)).ToList(),
        (JsonObject)p.Settings.DeepClone());

    /// <summary>The fields as the PATCH body names them, in the web's order (apiKey left out: it is never compared).</summary>
    public JsonObject ToJson()
    {
        var endpoints = new JsonArray();
        foreach (var e in Endpoints)
        {
            var o = new JsonObject { ["protocol"] = e.Protocol, ["baseUrl"] = e.BaseUrl };
            if (e.FullUrl is { } f) o["fullUrl"] = f;
            endpoints.Add((JsonNode)o);
        }
        var headers = new JsonObject();
        foreach (var (k, v) in Headers) if (k.Trim().Length > 0) headers[k] = v;
        return new JsonObject
        {
            ["name"] = Name,
            ["endpoints"] = endpoints,
            ["preferredUpstreamProtocols"] = new JsonArray(Preferred.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            ["authScheme"] = AuthScheme,
            ["extraHeaders"] = headers,
            ["httpProxy"] = HttpProxy,
            ["priceMultiplier"] = PriceMultiplier,
            ["priceKey"] = PriceKey,
            ["settings"] = Settings.DeepClone(),
            ["enabled"] = Enabled,
            ["notes"] = Notes,
        };
    }

    /// <summary>Whether this copy differs from <paramref name="saved"/>.</summary>
    public bool IsDirty(ProviderSettingsForm saved) => PricingDocument.CompactJson(ToJson()) != PricingDocument.CompactJson(saved.ToJson());

    /// <summary>The changed fields only (web <c>save</c>), plus a typed API key.</summary>
    public JsonObject Patch(ProviderSettingsForm saved)
    {
        var now = ToJson();
        var before = saved.ToJson();
        var patch = new JsonObject();
        foreach (var (k, v) in now)
            if (PricingDocument.CompactJson(v) != PricingDocument.CompactJson(before[k])) patch[k] = v?.DeepClone();
        if (ApiKey.Length > 0) patch["apiKey"] = ApiKey;
        return patch;
    }

    /// <summary>A copy with endpoint <paramref name="index"/> replaced.</summary>
    public ProviderSettingsForm WithEndpoint(int index, Func<EndpointRow, EndpointRow> change)
        => this with { Endpoints = Endpoints.Select((e, i) => i == index ? change(e) : e).ToList() };

    /// <summary>A copy without endpoint <paramref name="index"/>.</summary>
    public ProviderSettingsForm WithoutEndpoint(int index) => this with { Endpoints = Endpoints.Where((_, i) => i != index).ToList() };

    /// <summary>A copy with header <paramref name="index"/> replaced.</summary>
    public ProviderSettingsForm WithHeader(int index, string name, string value)
        => this with { Headers = Headers.Select((h, i) => i == index ? (name, value) : h).ToList() };

    /// <summary>A copy without header <paramref name="index"/>.</summary>
    public ProviderSettingsForm WithoutHeader(int index) => this with { Headers = Headers.Where((_, i) => i != index).ToList() };

    /// <summary>Adds a header from the sheet, replacing one of the same name (case-insensitive), at the end (web <c>onAdd</c>).</summary>
    public ProviderSettingsForm AddHeader(string name, string value)
        => this with { Headers = [.. Headers.Where(h => !string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)), (name, value)] };

    /// <summary>
    /// The chip group reports the selection in option order; keep the order in which protocols were picked (web <c>onValueChange</c>):
    /// what stays keeps its place, what is new goes last.
    /// </summary>
    public static List<string> ReorderPreferred(IReadOnlyList<string> current, IReadOnlyCollection<string> next)
    {
        var kept = current.Where(next.Contains).ToList();
        kept.AddRange(next.Where(x => !kept.Contains(x)));
        return kept;
    }

    /// <summary>The chip label: "2. OpenAI Chat" for a picked protocol, the bare name otherwise.</summary>
    public static string ChipLabel(IReadOnlyList<string> preferred, string protocol)
    {
        var i = preferred.ToList().IndexOf(protocol);
        var name = ProviderProtocols.Name(protocol);
        return i >= 0 ? $"{i + 1}. {name}" : name;
    }

    /// <summary>Preferred protocols with no endpoint address (skipped when an upstream is picked; the web lists them as warnings).</summary>
    public IEnumerable<string> PreferredMissing => Preferred.Where(p => !Endpoints.Any(e => e.Protocol == p));

    /// <summary>The price-key select value: "inherit" (null), else "price:&lt;key&gt;" ("price:" = none).</summary>
    public string PriceKeyChoice => PriceKey is null ? "inherit" : "price:" + PriceKey;

    /// <summary>The price key a select value stands for.</summary>
    public static string? PriceKeyOf(string choice) => choice == "inherit" ? null : choice["price:".Length..];

    /// <summary>An endpoint address the add sheet accepts (web <c>/^https?:\/\/\S+$/</c>).</summary>
    public static bool ValidBaseUrl(string text) => UrlPattern().IsMatch(text.Trim());

    /// <summary>A header name the add sheet accepts (web <c>/^[A-Za-z0-9-]+$/</c>).</summary>
    public static bool ValidHeaderName(string text) => HeaderPattern().IsMatch(text.Trim());

    [GeneratedRegex(@"^https?://\S+$")]
    private static partial Regex UrlPattern();

    [GeneratedRegex("^[A-Za-z0-9-]+$")]
    private static partial Regex HeaderPattern();
}
