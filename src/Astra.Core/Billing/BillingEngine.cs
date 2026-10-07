using System.Globalization;

namespace Astra.Core.Billing;

public sealed class BillingContext
{
    public DateTimeOffset RequestTimeUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Provider-level multiplier; only applied to inherited (system) pricing, never to an instance override.</summary>
    public decimal ProviderMultiplier { get; init; } = 1m;

    /// <summary>Which price table was used (see <see cref="PricingResolver"/>).</summary>
    public PricingSource PricingSource { get; init; } = PricingSource.SystemDefault;

    /// <summary>Provider price key when <see cref="PricingSource"/> is <see cref="Billing.PricingSource.ProviderPrice"/>.</summary>
    public string? PriceKey { get; init; }

    public bool PricingFromOverride => PricingSource == PricingSource.ProviderOverride;

    /// <summary>Builds a context from a resolver result.</summary>
    public static BillingContext From(ResolvedPricing resolved, DateTimeOffset requestTimeUtc, string modelLabel,
        string? providerLabel, string locale = "zh") => new()
    {
        RequestTimeUtc = requestTimeUtc,
        ProviderMultiplier = resolved.Source == PricingSource.ProviderOverride ? 1m : resolved.Multiplier,
        PricingSource = resolved.Source,
        PriceKey = resolved.PriceKey,
        ModelLabel = modelLabel,
        ProviderLabel = providerLabel,
        Locale = locale,
    };

    public string ModelLabel { get; init; } = "";
    public string? ProviderLabel { get; init; }

    /// <summary>"zh" or "en" — language of the archived plain-text description.</summary>
    public string Locale { get; init; } = "zh";
}

public sealed class BillingItem
{
    public string TokenType { get; init; } = "";
    public long Tokens { get; init; }
    public bool IsPerCall { get; init; }

    /// <summary>Price before multipliers (per 1M tokens, or per call).</summary>
    public decimal BaseUnitPrice { get; init; }

    /// <summary>Price after multipliers (per 1M tokens, or per call).</summary>
    public decimal UnitPrice { get; init; }

    public decimal Multiplier { get; init; } = 1m;
    public List<string> MultiplierSources { get; init; } = [];

    /// <summary>"base" or the context tier name the price came from.</summary>
    public string Tier { get; init; } = "base";

    /// <summary>Token type whose price was actually used (differs when a fallback applied).</summary>
    public string PricedAs { get; init; } = "";

    public long CostNanos { get; init; }
    public string? Note { get; init; }
}

public sealed class BillingTraceStep
{
    public string Code { get; init; } = "";
    public Dictionary<string, string> Args { get; init; } = new();

    public static BillingTraceStep Of(string code, params (string Key, object? Value)[] args) => new()
    {
        Code = code,
        Args = args.ToDictionary(a => a.Key, a => Convert.ToString(a.Value, CultureInfo.InvariantCulture) ?? ""),
    };
}

public sealed class BillingResult
{
    public List<BillingItem> Items { get; init; } = [];
    public long TotalNanos { get; init; }
    public bool Priced { get; init; }
    public List<BillingTraceStep> Trace { get; init; } = [];
    public string Description { get; set; } = "";
    public PricingSchedule? PricingSnapshot { get; init; }

    public decimal TotalUsd => Money.FromNanos(TotalNanos);
}

/// <summary>Pure pricing function: usage + schedule + context → itemized cost with an explainable trace.</summary>
public static class BillingEngine
{
    private const decimal TokensPerUnit = 1_000_000m;

    public static BillingResult Calculate(NormalizedUsage usage, PricingSchedule? pricing, BillingContext ctx)
    {
        var trace = new List<BillingTraceStep>();
        foreach (var note in usage.Notes) trace.Add(BillingTraceStep.Of("usage_note", ("text", note)));

        if (pricing is null)
        {
            trace.Add(BillingTraceStep.Of("no_pricing", ("model", ctx.ModelLabel)));
            var zeroItems = usage.Tokens.Where(kv => kv.Value > 0)
                .OrderBy(kv => TokenTypes.SortKey(kv.Key))
                .Select(kv => new BillingItem { TokenType = kv.Key, Tokens = kv.Value, PricedAs = kv.Key, Note = "no_pricing" })
                .Concat(usage.Calls.Where(kv => kv.Value > 0).Select(kv => new BillingItem
                {
                    TokenType = kv.Key, Tokens = kv.Value, IsPerCall = true, PricedAs = kv.Key, Note = "no_pricing",
                }))
                .ToList();
            return Finish(zeroItems, trace, pricing: null, priced: false, ctx);
        }

        var sourceId = ctx.PricingSource switch
        {
            PricingSource.ProviderOverride => "provider_override",
            PricingSource.ProviderPrice => "provider_price",
            _ => "system_default",
        };
        trace.Add(BillingTraceStep.Of("pricing_source", ("source", sourceId), ("price_key", ctx.PriceKey),
            ("model", ctx.ModelLabel), ("provider", ctx.ProviderLabel)));

        var basePrices = pricing.Base;
        var multiplier = 1m;
        var multiplierSources = new List<string>();

        // 1) Service tier (e.g. OpenAI flex / priority).
        var serviceTier = usage.ServiceTier;
        if (!string.IsNullOrWhiteSpace(serviceTier) && !IsDefaultServiceTier(serviceTier))
        {
            var rule = pricing.ServiceTiers?
                .FirstOrDefault(kv => string.Equals(kv.Key, serviceTier, StringComparison.OrdinalIgnoreCase)).Value;
            if (rule is null)
            {
                trace.Add(BillingTraceStep.Of("service_tier_unmatched", ("tier", serviceTier)));
            }
            else
            {
                if (rule.Prices is not null) basePrices = basePrices.Overlay(rule.Prices);
                if (rule.Multiplier is { } m)
                {
                    multiplier *= m;
                    multiplierSources.Add($"service_tier:{serviceTier}×{Fmt(m)}");
                }
                trace.Add(BillingTraceStep.Of("service_tier_applied", ("tier", serviceTier),
                    ("multiplier", rule.Multiplier is { } mm ? Fmt(mm) : ""), ("replaced", rule.Prices is not null)));
            }
        }

        // 2) Time window (e.g. DeepSeek off-peak), evaluated in the window's own timezone.
        var window = pricing.TimeWindows?.FirstOrDefault(w => w.Contains(ctx.RequestTimeUtc));
        if (window is not null)
        {
            if (window.Multiplier is { } wm)
            {
                multiplier *= wm;
                multiplierSources.Add($"time_window:{window.Name}×{Fmt(wm)}");
            }
            TimeZoneHelper.TryFind(window.Timezone, out var tz);
            var local = TimeZoneInfo.ConvertTime(ctx.RequestTimeUtc, tz);
            trace.Add(BillingTraceStep.Of("time_window", ("name", window.Name), ("timezone", window.Timezone),
                ("local_time", local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
                ("range", $"{window.Start}-{window.End}"), ("multiplier", window.Multiplier is { } x ? Fmt(x) : ""),
                ("replaced", window.Prices is not null)));
        }

        // 3) Provider multiplier (system pricing only).
        if (!ctx.PricingFromOverride && ctx.ProviderMultiplier != 1m)
        {
            multiplier *= ctx.ProviderMultiplier;
            multiplierSources.Add($"provider×{Fmt(ctx.ProviderMultiplier)}");
            trace.Add(BillingTraceStep.Of("provider_multiplier", ("multiplier", Fmt(ctx.ProviderMultiplier))));
        }

        PriceSet SetFor(ContextTier? tier) => basePrices.Overlay(tier?.Prices).Overlay(window?.Prices);

        // 4) Context tiers.
        var totalInput = usage.TotalInput;
        var tiers = (pricing.ContextTiers ?? []).OrderBy(t => t.ThresholdInputTokens).ToList();
        var reached = tiers.Where(t => totalInput > t.ThresholdInputTokens).ToList();
        var items = new List<BillingItem>();
        var tokenEntries = usage.Tokens.Where(kv => kv.Value > 0).OrderBy(kv => TokenTypes.SortKey(kv.Key)).ToList();

        if (reached.Count == 0 || reached[^1].Mode != "progressive")
        {
            var tier = reached.LastOrDefault();
            if (tiers.Count > 0)
            {
                trace.Add(tier is null
                    ? BillingTraceStep.Of("context_tier_none", ("total_input", totalInput), ("lowest_threshold", tiers[0].ThresholdInputTokens))
                    : BillingTraceStep.Of("context_tier_whole", ("total_input", totalInput), ("tier", tier.Name), ("threshold", tier.ThresholdInputTokens)));
            }
            var set = SetFor(tier);
            foreach (var (type, count) in tokenEntries)
                items.Add(MakeItem(type, count, set, tier?.Name ?? "base", multiplier, multiplierSources, trace));
        }
        else
        {
            var top = reached[^1];
            trace.Add(BillingTraceStep.Of("context_tier_progressive", ("total_input", totalInput), ("tier", top.Name),
                ("threshold", top.ThresholdInputTokens), ("output_policy", top.OutputPolicy)));

            // Segment sizes along the total-input axis: [0,H1], (H1,H2], ..., (Hk, T].
            var segments = new List<(ContextTier? Tier, long Size)>();
            long previous = 0;
            ContextTier? previousTier = null;
            foreach (var t in reached)
            {
                segments.Add((previousTier, t.ThresholdInputTokens - previous));
                previous = t.ThresholdInputTokens;
                previousTier = t;
            }
            segments.Add((previousTier, totalInput - previous));

            foreach (var (type, count) in tokenEntries)
            {
                if (TokenTypes.IsInputLike(type))
                {
                    long allocated = 0;
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var (segTier, size) = segments[i];
                        var n = i == segments.Count - 1
                            ? count - allocated
                            : (long)Math.Round((decimal)count * size / totalInput, MidpointRounding.AwayFromZero);
                        n = Math.Min(n, count - allocated);
                        if (n <= 0) continue;
                        allocated += n;
                        items.Add(MakeItem(type, n, SetFor(segTier), segTier?.Name ?? "base", multiplier, multiplierSources, trace));
                    }
                }
                else
                {
                    var outTier = top.OutputPolicy == "base" ? null : top;
                    items.Add(MakeItem(type, count, SetFor(outTier), outTier?.Name ?? "base", multiplier, multiplierSources, trace));
                }
            }
        }

        // 5) Per-call items and per-request fee.
        var callSet = SetFor(null);
        foreach (var (type, count) in usage.Calls.Where(kv => kv.Value > 0))
        {
            var price = callSet.PerCall.GetValueOrDefault(type);
            if (!callSet.PerCall.ContainsKey(type))
                trace.Add(BillingTraceStep.Of("missing_price", ("type", type)));
            items.Add(new BillingItem
            {
                TokenType = type, Tokens = count, IsPerCall = true, BaseUnitPrice = price, UnitPrice = price * multiplier,
                Multiplier = multiplier, MultiplierSources = [.. multiplierSources], PricedAs = type,
                CostNanos = Money.ToNanos(count * price * multiplier),
            });
        }
        if (pricing.PerRequest is > 0m)
        {
            var fee = pricing.PerRequest.Value;
            items.Add(new BillingItem
            {
                TokenType = "per_request", Tokens = 1, IsPerCall = true, BaseUnitPrice = fee, UnitPrice = fee * multiplier,
                Multiplier = multiplier, MultiplierSources = [.. multiplierSources], PricedAs = "per_request",
                CostNanos = Money.ToNanos(fee * multiplier),
            });
        }

        return Finish(items, trace, pricing, priced: true, ctx);
    }

    private static BillingItem MakeItem(string type, long tokens, PriceSet set, string tier, decimal multiplier,
        List<string> multiplierSources, List<BillingTraceStep> trace)
    {
        var (price, pricedAs) = Resolve(set, type);
        string? note = null;
        if (pricedAs is null)
        {
            note = "missing_price";
            if (!trace.Any(t => t.Code == "missing_price" && t.Args["type"] == type))
                trace.Add(BillingTraceStep.Of("missing_price", ("type", type)));
        }
        else if (pricedAs != type)
        {
            note = $"fallback:{pricedAs}";
            if (!trace.Any(t => t.Code == "fallback_price" && t.Args["type"] == type))
                trace.Add(BillingTraceStep.Of("fallback_price", ("type", type), ("priced_as", pricedAs)));
        }
        var unit = price * multiplier;
        return new BillingItem
        {
            TokenType = type,
            Tokens = tokens,
            BaseUnitPrice = price,
            UnitPrice = unit,
            Multiplier = multiplier,
            MultiplierSources = [.. multiplierSources],
            Tier = tier,
            PricedAs = pricedAs ?? type,
            CostNanos = Money.ToNanos(tokens * unit / TokensPerUnit),
            Note = note,
        };
    }

    /// <summary>Finds the price for a token type, walking the fallback chain (reasoning→output, cache_*→input…).</summary>
    public static (decimal Price, string? PricedAs) Resolve(PriceSet set, string type)
    {
        var current = type;
        var guard = 0;
        while (current is not null && guard++ < 8)
        {
            if (set[current] is { } p) return (p, current);
            current = TokenTypes.FallbackOf(current);
        }
        return (0m, null);
    }

    private static bool IsDefaultServiceTier(string tier) =>
        tier.Equals("default", StringComparison.OrdinalIgnoreCase) ||
        tier.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
        tier.Equals("standard", StringComparison.OrdinalIgnoreCase);

    private static BillingResult Finish(List<BillingItem> items, List<BillingTraceStep> trace, PricingSchedule? pricing,
        bool priced, BillingContext ctx)
    {
        var total = items.Sum(i => i.CostNanos);
        trace.Add(BillingTraceStep.Of("total", ("usd", Money.FromNanos(total).ToString("0.000000###", CultureInfo.InvariantCulture))));
        var result = new BillingResult
        {
            Items = items,
            TotalNanos = total,
            Priced = priced,
            Trace = trace,
            PricingSnapshot = pricing?.Clone(),
        };
        result.Description = BillingDescription.Render(result, ctx.Locale);
        return result;
    }

    internal static string Fmt(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);
}
