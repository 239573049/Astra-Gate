using System.Text.Json;
using System.Text.Json.Serialization;

namespace Astra.Core.Billing;

/// <summary>Full pricing schedule for one model. All prices are USD per 1M tokens (per call for per_call items).</summary>
public sealed class PricingSchedule
{
    public string Currency { get; set; } = "USD";
    public string Unit { get; set; } = "per_1m_tokens";
    public PriceSet Base { get; set; } = new();
    public Dictionary<string, ServiceTierRule>? ServiceTiers { get; set; }
    public List<ContextTier>? ContextTiers { get; set; }
    public List<TimeWindowRule>? TimeWindows { get; set; }
    public decimal? PerRequest { get; set; }
    public string? Notes { get; set; }

    public PricingSchedule Clone() => Json.Deserialize<PricingSchedule>(Json.Serialize(this))!;

    /// <summary>Validates structural rules; returns human readable problems.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!string.Equals(Currency, "USD", StringComparison.OrdinalIgnoreCase))
            errors.Add("currency must be USD");
        foreach (var (type, price) in Base.Tokens)
        {
            if (price is < 0) errors.Add($"base.{type} must be >= 0");
        }
        if (ContextTiers is { } tiers)
        {
            foreach (var t in tiers)
            {
                if (t.ThresholdInputTokens <= 0) errors.Add($"context tier '{t.Name}': threshold must be > 0");
                if (t.Mode is not ("whole" or "progressive")) errors.Add($"context tier '{t.Name}': mode must be whole|progressive");
                if (t.OutputPolicy is not ("highest" or "base")) errors.Add($"context tier '{t.Name}': output_policy must be highest|base");
            }
            if (tiers.Select(t => t.ThresholdInputTokens).Distinct().Count() != tiers.Count)
                errors.Add("context tier thresholds must be unique");
        }
        if (TimeWindows is { } windows)
        {
            foreach (var w in windows)
            {
                if (!TimeOnly.TryParse(w.Start, out _) || !TimeOnly.TryParse(w.End, out _))
                    errors.Add($"time window '{w.Name}': start/end must be HH:mm");
                if (!TimeZoneHelper.TryFind(w.Timezone, out _))
                    errors.Add($"time window '{w.Name}': unknown timezone '{w.Timezone}'");
                if (w.Days?.Any(d => d is < 1 or > 7) == true)
                    errors.Add($"time window '{w.Name}': days must be ISO weekdays 1(Mon)..7(Sun)");
                if (w.ExcludeDates?.Any(d => !DateOnly.TryParseExact(d, "yyyy-MM-dd", out _)) == true)
                    errors.Add($"time window '{w.Name}': exclude_dates must be yyyy-MM-dd");
                if (w.Multiplier is null && w.Prices is null)
                    errors.Add($"time window '{w.Name}': needs multiplier or prices");
            }
        }
        if (ServiceTiers is { } st)
        {
            foreach (var (name, rule) in st)
            {
                if (rule.Multiplier is null && rule.Prices is null)
                    errors.Add($"service tier '{name}': needs multiplier or prices");
            }
        }
        return errors;
    }
}

/// <summary>Token prices keyed by token type, plus per-call prices. Serialized as a flat object with a "per_call" member.</summary>
[JsonConverter(typeof(PriceSetJsonConverter))]
public sealed class PriceSet
{
    public Dictionary<string, decimal?> Tokens { get; set; } = new();
    public Dictionary<string, decimal> PerCall { get; set; } = new();

    public decimal? this[string type]
    {
        get => Tokens.TryGetValue(type, out var v) ? v : null;
        set => Tokens[type] = value;
    }

    /// <summary>Returns a copy where every explicitly-set price in <paramref name="overlay"/> replaces ours.</summary>
    public PriceSet Overlay(PriceSet? overlay)
    {
        var result = new PriceSet
        {
            Tokens = new Dictionary<string, decimal?>(Tokens),
            PerCall = new Dictionary<string, decimal>(PerCall),
        };
        if (overlay is null) return result;
        foreach (var (k, v) in overlay.Tokens)
        {
            if (v is not null) result.Tokens[k] = v;
        }
        foreach (var (k, v) in overlay.PerCall) result.PerCall[k] = v;
        return result;
    }
}

public sealed class PriceSetJsonConverter : JsonConverter<PriceSet>
{
    public override PriceSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var set = new PriceSet();
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("price set must be an object");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString()!;
            reader.Read();
            if (name == "per_call")
            {
                // JsonTypeInfo overload: the options overload is RequiresDynamicCode (see JsonContexts).
                var info = JsonContexts.Info<Dictionary<string, decimal>>(options);
                set.PerCall = JsonSerializer.Deserialize(ref reader, info) ?? new();
            }
            else if (reader.TokenType == JsonTokenType.Null)
            {
                set.Tokens[name] = null;
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                set.Tokens[name] = decimal.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                set.Tokens[name] = reader.GetDecimal();
            }
        }
        return set;
    }

    public override void Write(Utf8JsonWriter writer, PriceSet value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (k, v) in value.Tokens.OrderBy(kv => TokenTypes.SortKey(kv.Key)))
        {
            if (v is null) continue;
            writer.WriteNumber(k, v.Value);
        }
        if (value.PerCall.Count > 0)
        {
            writer.WritePropertyName("per_call");
            JsonSerializer.Serialize(writer, value.PerCall, JsonContexts.Info<Dictionary<string, decimal>>(options));
        }
        writer.WriteEndObject();
    }
}

public sealed class ServiceTierRule
{
    /// <summary>Multiplies every price (after context tier selection).</summary>
    public decimal? Multiplier { get; set; }

    /// <summary>Replaces base prices for the listed types.</summary>
    public PriceSet? Prices { get; set; }
}

public sealed class ContextTier
{
    public string Name { get; set; } = "";

    /// <summary>The tier applies when total input tokens are strictly greater than this threshold.</summary>
    public long ThresholdInputTokens { get; set; }

    /// <summary>"whole": the whole request is billed at this tier; "progressive": only the portion above the threshold.</summary>
    public string Mode { get; set; } = "whole";

    /// <summary>Progressive mode only: "highest" bills outputs at the highest reached tier, "base" at base prices.</summary>
    public string OutputPolicy { get; set; } = "highest";

    public PriceSet Prices { get; set; } = new();
}

public sealed class TimeWindowRule
{
    public string Name { get; set; } = "";
    public string Timezone { get; set; } = "UTC";

    /// <summary>Local start time "HH:mm" (inclusive).</summary>
    public string Start { get; set; } = "00:00";

    /// <summary>Local end time "HH:mm" (exclusive). End &lt;= start means the window crosses midnight.</summary>
    public string End { get; set; } = "00:00";

    /// <summary>ISO weekdays 1 (Mon) .. 7 (Sun) on which the window starts; null = every day.</summary>
    public List<int>? Days { get; set; }

    /// <summary>Local dates ("yyyy-MM-dd", in the window's timezone) on which the window does not start, e.g. public holidays.</summary>
    public List<string>? ExcludeDates { get; set; }

    public decimal? Multiplier { get; set; }
    public PriceSet? Prices { get; set; }

    /// <summary>True when <paramref name="utc"/> falls inside this window.</summary>
    public bool Contains(DateTimeOffset utc)
    {
        if (!TimeZoneHelper.TryFind(Timezone, out var tz)) return false;
        var local = TimeZoneInfo.ConvertTime(utc, tz);
        var start = TimeOnly.Parse(Start, System.Globalization.CultureInfo.InvariantCulture);
        var end = TimeOnly.Parse(End, System.Globalization.CultureInfo.InvariantCulture);
        var now = TimeOnly.FromDateTime(local.DateTime);
        DateOnly windowStartDay;
        if (start < end)
        {
            if (now < start || now >= end) return false;
            windowStartDay = DateOnly.FromDateTime(local.DateTime);
        }
        else
        {
            // Crosses midnight (or covers the full day when start == end).
            if (now >= start) windowStartDay = DateOnly.FromDateTime(local.DateTime);
            else if (now < end) windowStartDay = DateOnly.FromDateTime(local.DateTime).AddDays(-1);
            else return false;
        }
        if (ExcludeDates is { Count: > 0 } &&
            ExcludeDates.Contains(windowStartDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)))
            return false;
        if (Days is null || Days.Count == 0) return true;
        var iso = windowStartDay.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)windowStartDay.DayOfWeek;
        return Days.Contains(iso);
    }
}

public static class TimeZoneHelper
{
    public static bool TryFind(string? id, out TimeZoneInfo tz)
    {
        tz = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id) || id.Equals("UTC", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}
