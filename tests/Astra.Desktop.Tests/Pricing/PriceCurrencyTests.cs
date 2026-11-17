using Astra.Core.Currency;
using Astra.Desktop.Core.Currency;
using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Tests.Pricing;

/// <summary>Unit prices in the display currency: shown converted, typed in it, saved as exact USD decimals.</summary>
public sealed class PriceCurrencyTests
{
    private static readonly Dictionary<string, string> Rates = new() { ["CNY"] = "7.2" };

    [Fact]
    public void Unit_prices_read_in_the_display_currency_and_stay_dollars_by_default()
    {
        Assert.Equal("$3", PriceFormat.UnitPrice(3m));
        using var scope = DisplayMoney.Scoped(Currencies.Resolve("CNY", Rates));
        Assert.Equal("¥21.6", PriceFormat.UnitPrice(3m));
        Assert.Equal("¥0.54", PriceFormat.UnitPrice(0.075m));
        Assert.Equal("¥21.6 / ¥108", PriceFormat.Summary(System.Text.Json.Nodes.JsonNode.Parse("""{"base":{"input":3,"output":15}}""")));
    }

    [Fact]
    public void Typed_amounts_become_exact_usd_decimals()
    {
        var cny = Currencies.Resolve("CNY", Rates);
        Assert.Equal(3m, PriceCurrency.ToUsd(21.6m, cny));
        Assert.Equal(0.075m, PriceCurrency.ToUsd(0.54m, cny));
        Assert.Equal(21.6m, PriceCurrency.ToInput(3m, cny));
        Assert.Null(PriceCurrency.ToUsd(null, cny));
        // 1 / 7.2 has no finite tail: it is cut at 15 significant digits, not left as 28 digits of noise
        Assert.Equal(0.138888888888889m, PriceCurrency.ToUsd(1m, cny));
        Assert.Equal("≈ $3", PriceCurrency.Equivalent(3m, cny));
    }

    [Fact]
    public void In_usd_nothing_is_rounded_or_shown()
    {
        var usd = DisplayCurrencyContext.UsdContext;
        Assert.Equal(0.123456789012m, PriceCurrency.ToInput(0.123456789012m, usd));
        Assert.Equal(0.123456789012m, PriceCurrency.ToUsd(0.123456789012m, usd));
        Assert.Equal("", PriceCurrency.Equivalent(3m, usd));
    }
}
