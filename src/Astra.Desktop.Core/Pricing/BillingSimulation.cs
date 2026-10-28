using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;

namespace Astra.Desktop.Core.Pricing;

/// <summary>
/// The "what would this cost?" simulator's request and result text (<c>BillingSimulator</c> / <c>BillingResultView</c> of
/// web/src/components/Billing.tsx). Pure: the Avalonia control only collects the inputs and prints these.
/// </summary>
public static class BillingSimulation
{
    /// <summary>The usage the simulator starts with (<c>DEFAULT_TOKENS</c>): 1M input, 100K output tokens.</summary>
    public static Dictionary<string, decimal?> DefaultTokens() => new(StringComparer.Ordinal) { ["input"] = 1_000_000, ["output"] = 100_000 };

    /// <summary>
    /// The <c>POST /api/billing/simulate</c> body. Like the web: a chosen provider prices <paramref name="modelId"/> at that provider;
    /// a model without a document prices the system model; otherwise the document itself is priced (<c>pricing: null</c> when none).
    /// Only token types with an amount above zero are sent; a fractional count is cut to a whole token (the API counts whole tokens).
    /// </summary>
    /// <param name="modelId">The saved model being priced, or null to price a free-standing document.</param>
    /// <param name="pricing">The (possibly unsaved) pricing document.</param>
    /// <param name="providerId">An AI provider id, or empty for the official price.</param>
    /// <param name="tokens">Token amounts by type; null or empty boxes are skipped.</param>
    /// <param name="serviceTier">A <c>service_tier</c> to apply; empty is none.</param>
    /// <param name="requestTime">When the request is assumed to happen (for time windows); null is "now" on the server.</param>
    /// <param name="locale">"zh" or "en": the language of the description the server writes.</param>
    public static BillingSimulateRequest BuildRequest(
        string? modelId, JsonNode? pricing, string providerId, IReadOnlyDictionary<string, decimal?> tokens, string serviceTier,
        DateTimeOffset? requestTime, string locale)
    {
        var usage = new SimulateUsage { ServiceTier = string.IsNullOrEmpty(serviceTier) ? null : serviceTier };
        foreach (var (type, amount) in tokens)
            if (amount is > 0) usage.Tokens[type] = (long)decimal.Truncate(amount.Value);
        var request = new BillingSimulateRequest { Usage = usage, RequestTimeUtc = requestTime?.ToUniversalTime(), Locale = locale };
        if (!string.IsNullOrEmpty(providerId))
        {
            request.ModelId = modelId;
            request.ProviderId = providerId;
        }
        else if (!string.IsNullOrEmpty(modelId) && pricing is null) request.ModelId = modelId;
        else request.Pricing = pricing?.DeepClone();
        return request;
    }

    /// <summary>An AI provider offered in the simulator's "price at" box.</summary>
    /// <param name="Id">Provider id (sent as <c>providerId</c>).</param>
    /// <param name="Name">Provider name.</param>
    /// <param name="Detail">The price key or template id, shown beside the name.</param>
    public sealed record ProviderOption(string Id, string Name, string? Detail);

    /// <summary>The provider choices of <c>GET /api/providers</c> (raw JSON until the providers store is typed): <c>detail: p.priceKey ?? p.templateId</c>.</summary>
    public static List<ProviderOption> ProviderOptions(JsonArray? providers)
    {
        var list = new List<ProviderOption>();
        foreach (var node in providers ?? [])
        {
            if (node is not JsonObject p || Text(p["id"]) is not { Length: > 0 } id) continue;
            list.Add(new ProviderOption(id, Text(p["name"]) ?? id, Text(p["priceKey"]) ?? Text(p["templateId"])));
        }
        return list;
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// The request time picked in the simulator's date and time fields, as an instant in <paramref name="zone"/> (web: a
    /// <c>datetime-local</c> input, read as local time by <c>new Date(time)</c>). Like that input it has no value until both parts are
    /// set: a date without a time, or a time without a date, prices "now" (null).
    /// </summary>
    public static DateTimeOffset? RequestTime(DateOnly? date, TimeOnly? time, TimeZoneInfo zone)
    {
        if (date is not { } d || time is not { } t) return null;
        var local = d.ToDateTime(t, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>
    /// The text of the "request time" box (<c>yyyy-MM-dd HH:mm</c>, or with a <c>T</c> like a browser's datetime-local) as a moment in
    /// <paramref name="zone"/>; false for text that is not a date and time. Empty is "now" (true, null).
    /// </summary>
    public static bool TryParseRequestTime(string? text, TimeZoneInfo zone, out DateTimeOffset? time)
    {
        time = null;
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t)) return true;
        string[] formats = ["yyyy-MM-dd HH:mm", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss"];
        if (!DateTime.TryParseExact(t, formats, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var local)) return false;
        time = new DateTimeOffset(local, zone.GetUtcOffset(local));
        return true;
    }

    /// <summary>One line of the result table, with the web's sort values and cell texts.</summary>
    /// <param name="Item">The API item.</param>
    public sealed record ItemRow(BillingItemDto Item)
    {
        /// <summary>Token type, with "→ pricedAs" when it was billed as another type.</summary>
        public string TypeText => Item.PricedAs.Length > 0 && Item.PricedAs != Item.TokenType ? $"{Item.TokenType} → {Item.PricedAs}" : Item.TokenType;

        /// <summary>"1,000,000" or "3×" for a per-call item.</summary>
        public string TokensText => Item.IsPerCall ? $"{Item.Tokens}×" : PriceFormat.Tokens(Item.Tokens);

        /// <summary>"$3" plus " (×0.5)" when a multiplier applied.</summary>
        public string UnitPriceText => PriceFormat.UnitPrice(Item.UnitPrice) + (Item.Multiplier != 1 ? $" (×{PriceNumber.ToJsText(Item.Multiplier)})" : "");

        /// <summary>The cost, as dollars.</summary>
        public string CostText => PriceFormat.Nanos(Item.CostNanos);
    }

    /// <summary>The rows of a result (<c>rows</c> of <c>BillingResultView</c>).</summary>
    public static List<ItemRow> Rows(BillingResultDto result) => result.Items.Select(i => new ItemRow(i)).ToList();

    /// <summary>The pricing-source badge text (<c>PricingSourceBadge</c>) and its tone name: orange, accent, neutral or red.</summary>
    public static (string Text, string Tone) SourceBadge(string? source, string? priceKey) => source switch
    {
        "provider_override" => (Loc.T("pricingSource.override"), "orange"),
        "provider_price" => (Loc.T("pricingSource.provider", ("key", priceKey ?? "")), "accent"),
        "system_default" => (Loc.T("pricingSource.default"), "neutral"),
        _ => (Loc.T("pricingSource.none"), "red"),
    };
}
