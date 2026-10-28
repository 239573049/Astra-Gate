using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Astra.Core.Billing;

namespace Astra.Desktop.Core.Pricing;

/// <summary>What the rule dialog adds (<c>PricingRuleKind</c> of web/src/components/PricingRuleDialog.tsx).</summary>
public enum PricingRuleKind
{
    /// <summary>A context tier: other prices above an input-token threshold.</summary>
    Context,

    /// <summary>A time window: a multiplier for part of the day.</summary>
    Time,

    /// <summary>A service tier: a multiplier for a request's <c>service_tier</c>.</summary>
    Service,
}

/// <summary>
/// The state, defaults, validation and result of the "add a rule" dialog (<c>PricingRuleDialog</c>): a draft the dialog binds to,
/// <see cref="IsValid"/> enabling its Add button, and <see cref="Apply"/> producing the edited <see cref="PricingDocument"/>.
/// Pure, so the rules are tested without a window. Numbers are decimals: a multiplier of 0.5 or a price of 0.000003 is exact.
/// </summary>
public sealed partial class PricingRuleDraft
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The kind of rule being added.</summary>
    public PricingRuleKind Kind { get; }

    /// <summary>Names of the existing service tiers; a new service tier may not reuse one (case-insensitive).</summary>
    public IReadOnlyList<string> ServiceNames { get; }

    /// <summary>The rule's name (the web fills <c>&gt;200K</c> / <c>off-peak</c> / <c>flex</c>).</summary>
    public string Name { get; set; }

    /// <summary>Context tier: input tokens above which it applies; null while the box is empty.</summary>
    public decimal? Threshold { get; set; } = 200_000;

    /// <summary>Context tier: <c>whole</c> or <c>progressive</c>.</summary>
    public string Mode { get; set; } = "whole";

    /// <summary>Context tier: <c>highest</c> or <c>base</c>.</summary>
    public string OutputPolicy { get; set; } = "highest";

    /// <summary>Context tier prices by token type; a cleared box is a null entry (sent as null, like the web).</summary>
    public Dictionary<string, decimal?> Prices { get; } = new(StringComparer.Ordinal);

    /// <summary>Time window: an IANA time zone.</summary>
    public string Timezone { get; set; } = "Asia/Shanghai";

    /// <summary>Time window: first minute, <c>HH:mm</c>.</summary>
    public string Start { get; set; } = "00:30";

    /// <summary>Time window: end (exclusive), <c>HH:mm</c>.</summary>
    public string End { get; set; } = "08:30";

    /// <summary>Time window and service tier: the price multiplier; null while empty.</summary>
    public decimal? Multiplier { get; set; } = 0.5m;

    /// <summary>Time window: ISO weekdays (1 = Monday ... 7 = Sunday) the window starts on.</summary>
    public HashSet<int> Days { get; } = [1, 2, 3, 4, 5, 6, 7];

    /// <summary>Time window: dates to skip, as typed (separated by commas or white space).</summary>
    public string Excluded { get; set; } = "";

    /// <summary>The token types the context tier dialog offers a price for, in the web's order.</summary>
    public static readonly IReadOnlyList<string> ContextPriceTypes = ["input", "cache_read", "cache_write_5m", "cache_write_30m", "cache_write_1h", "cache_write_12h", "output", "reasoning"];

    /// <summary>A draft with the defaults of the web dialog's <c>useEffect</c> reset.</summary>
    /// <param name="kind">What is being added.</param>
    /// <param name="serviceNames">Existing service tier names.</param>
    public PricingRuleDraft(PricingRuleKind kind, IEnumerable<string>? serviceNames = null)
    {
        Kind = kind;
        ServiceNames = serviceNames?.ToList() ?? [];
        Name = kind switch { PricingRuleKind.Context => ">200K", PricingRuleKind.Time => "off-peak", _ => "flex" };
    }

    /// <summary>The dates typed in <see cref="Excluded"/> (<c>split(/[,\s]+/).filter(Boolean)</c>).</summary>
    public IReadOnlyList<string> ExcludedDates => SplitDates(Excluded);

    /// <summary>Splits a typed list of dates on commas and white space, dropping empties (<c>split(/[,\s]+/).filter(Boolean)</c>); the editor's time window rows use it too.</summary>
    public static IReadOnlyList<string> SplitDates(string text) => Separators().Split(text).Where(s => s.Length > 0).ToList();

    /// <summary>Whether <see cref="Timezone"/> names a zone (the web asks <c>Intl.DateTimeFormat</c>; the server <c>TimeZoneHelper.TryFind</c>).</summary>
    public bool TimezoneValid => !string.IsNullOrWhiteSpace(Timezone) && TimeZoneHelper.TryFind(Timezone, out _);

    /// <summary>Whether both times are <c>HH:mm</c> (00:00 to 23:59).</summary>
    public bool TimeValid => IsClockTime(Start) && IsClockTime(End);

    /// <summary>A new service tier reuses an existing name (case-insensitive, trimmed).</summary>
    public bool NameTaken => Kind == PricingRuleKind.Service
        && ServiceNames.Any(n => string.Equals(n, Name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Every typed context price is empty or a non-negative number.</summary>
    public bool PricesValid => Prices.Values.All(v => v is null or >= 0);

    /// <summary>Whether the Add button is enabled (<c>valid</c> of the web dialog).</summary>
    public bool IsValid
    {
        get
        {
            if (Name.Trim().Length == 0 || !PricesValid) return false;
            return Kind switch
            {
                PricingRuleKind.Context => Threshold is { } t && t == decimal.Truncate(t) && t > 0,
                PricingRuleKind.Time => TimezoneValid && TimeValid && Days.Count > 0 && Multiplier is >= 0 && ExcludedDates.All(IsDate),
                _ => !NameTaken && Multiplier is >= 0,
            };
        }
    }

    /// <summary><c>^([01]\d|2[0-3]):[0-5]\d$</c> (ASCII digits only, like JS).</summary>
    public static bool IsClockTime(string text) => ClockTime().IsMatch(text);

    /// <summary><c>validDate</c>: <c>yyyy-MM-dd</c> that is a real calendar day.</summary>
    public static bool IsDate(string text)
        => DatePattern().IsMatch(text) && DateOnly.TryParseExact(text, "yyyy-MM-dd", Inv, DateTimeStyles.None, out _);

    /// <summary>
    /// The document with the rule added, or null while the draft is not valid. A context tier and a time window are appended to
    /// their lists; a service tier is set under its (trimmed) name.
    /// </summary>
    public PricingDocument? Apply(PricingDocument document)
    {
        if (!IsValid) return null;
        var name = Name.Trim();
        switch (Kind)
        {
            case PricingRuleKind.Context:
            {
                var prices = new JsonObject();
                foreach (var (type, price) in Prices) prices[type] = PriceNumber.ToNode(price);
                var rule = new JsonObject
                {
                    ["name"] = name,
                    ["threshold_input_tokens"] = JsonValue.Create((long)Threshold!.Value),
                    ["mode"] = Mode,
                    ["output_policy"] = OutputPolicy,
                    ["prices"] = prices,
                };
                return document.WithRuleAdded(PricingDocument.ContextTiersKey, rule);
            }
            case PricingRuleKind.Time:
            {
                JsonNode? days = null;
                if (Days.Count != 7) days = new JsonArray(Days.Order().Select(d => (JsonNode?)JsonValue.Create(d)).ToArray());
                JsonNode? exclude = null;
                if (ExcludedDates.Count > 0) exclude = new JsonArray(ExcludedDates.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray());
                var rule = new JsonObject
                {
                    ["name"] = name,
                    ["timezone"] = Timezone,
                    ["start"] = Start,
                    ["end"] = End,
                    ["multiplier"] = PriceNumber.ToNode(Multiplier),
                    ["days"] = days,
                    ["exclude_dates"] = exclude,
                };
                return document.WithRuleAdded(PricingDocument.TimeWindowsKey, rule);
            }
            default:
                return document.WithServiceTier(name, new JsonObject { ["multiplier"] = PriceNumber.ToNode(Multiplier) });
        }
    }

    [GeneratedRegex(@"[,\s]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^([01][0-9]|2[0-3]):[0-5][0-9]\z")]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z")]
    private static partial Regex DatePattern();
}
