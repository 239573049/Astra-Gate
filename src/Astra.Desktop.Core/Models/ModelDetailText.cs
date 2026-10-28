using System.Globalization;
using System.Text.Json.Nodes;
using Astra.Api.Contracts;
using Astra.Desktop.Core.I18n;
using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Core.Models;

/// <summary>Texts of the model detail page (web/src/pages/ModelDetailPage.tsx and <c>formatDateTime</c> of web/src/lib/format.ts).</summary>
public static class ModelDetailText
{
    /// <summary>
    /// <c>formatDateTime</c>: "2026/10/06 12:00" in Chinese, "10/06/2026, 12:00" in English (what <c>Intl.DateTimeFormat</c> prints for
    /// year/month/day/hour/minute with <c>hour12: false</c>), shown in <paramref name="zone"/> (default: the machine's); a dash for none.
    /// </summary>
    public static string DateTime(DateTimeOffset? time, string language, TimeZoneInfo? zone = null)
    {
        if (time is not { } t) return PriceFormat.Dash;
        var local = TimeZoneInfo.ConvertTime(t, zone ?? TimeZoneInfo.Local);
        var inv = CultureInfo.InvariantCulture;
        return language == "zh"
            ? local.ToString("yyyy'/'MM'/'dd HH':'mm", inv)
            : local.ToString("MM'/'dd'/'yyyy, HH':'mm", inv);
    }

    /// <summary>The page subtitle: "gpt-4.1 · seed · updated 2026/10/06 12:00" (<c>id · source · models.updated</c>).</summary>
    public static string Subtitle(ModelDto model, string language, TimeZoneInfo? zone = null)
        => $"{model.Id} · {Loc.T("models.source." + model.Source)} · {Loc.T("models.updated", ("time", DateTime(model.UpdatedAt, language, zone)))}";

    /// <summary>The detail line of a provider price row: base prices, the upstream id and the rule flags, joined by " · ".</summary>
    public static string PriceDetail(JsonNode? pricing, string? upstreamModelId)
    {
        var parts = new List<string> { PriceFormat.Summary(pricing) };
        if (!string.IsNullOrEmpty(upstreamModelId)) parts.Add(upstreamModelId);
        if (pricing is JsonObject o)
        {
            if (o["time_windows"] is JsonArray { Count: > 0 }) parts.Add(Loc.T("models.hasTimeWindows"));
            if (o["context_tiers"] is JsonArray { Count: > 0 }) parts.Add(Loc.T("models.hasTiers"));
        }
        return string.Join(" · ", parts);
    }

    /// <summary>The detail line of a provider override row: "gpt-4.1 · $3 / $15".</summary>
    public static string OverrideDetail(ProviderOverrideRefDto o) => $"{o.ModelId} · {PriceFormat.Summary(o.Pricing)}";
}
