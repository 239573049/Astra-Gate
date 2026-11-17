using Astra.Core.Currency;
using Astra.Desktop.Core.Currency;

namespace Astra.Desktop.Core.Pricing;

/// <summary>
/// Unit prices in the display currency (the web's <c>PriceInput</c>): the pricing JSON stays USD, so a price box shows
/// <see cref="ToInput"/> of the stored USD value and saves <see cref="ToUsd"/> of what was typed. Both work on <see cref="decimal"/>;
/// in USD they are the identity, so an untouched price is never rewritten.
/// </summary>
public static class PriceCurrency
{
    /// <summary>A USD unit price as the number a box in the display currency shows (8 significant digits, like <see cref="PriceFormat.UnitPrice(decimal?)"/>).</summary>
    public static decimal? ToInput(decimal? usd, DisplayCurrencyContext? currency = null)
    {
        var c = currency ?? DisplayMoney.Current;
        if (usd is not { } v || c.IsUsd) return usd;
        return PriceNumber.Normalize(PriceFormat.RoundSignificant(v * c.Rate, 8));
    }

    /// <summary>What a number typed in the display currency is in USD (15 significant digits: no division tail such as 1/3 left over).</summary>
    public static decimal? ToUsd(decimal? typed, DisplayCurrencyContext? currency = null)
    {
        var c = currency ?? DisplayMoney.Current;
        if (typed is not { } v || c.IsUsd) return typed;
        return PriceNumber.Normalize(PriceFormat.RoundSignificant(v / c.Rate, 15));
    }

    /// <summary>The USD value shown beside a box typed in another currency ("≈ $3"); empty in USD or for no value.</summary>
    public static string Equivalent(decimal? usd, DisplayCurrencyContext? currency = null)
    {
        var c = currency ?? DisplayMoney.Current;
        if (c.IsUsd || usd is not { } v) return "";
        return "≈ $" + PriceNumber.ToJsText(PriceFormat.RoundSignificant(v, 8));
    }
}
