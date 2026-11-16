using System.Globalization;

namespace Astra.Core.Currency;

/// <summary>A currency Astra can show amounts in: ISO code, symbol and how many decimals an amount of 1 or more gets.</summary>
public sealed record CurrencyInfo(string Code, string Symbol, int Decimals);

/// <summary>
/// Display currencies with user-entered exchange rates. Money itself is always stored as USD nanodollars (AGENTS.md); this
/// only decides how an amount reads. Astra never fetches exchange rates: the table lives in
/// <see cref="AppSettings.CurrencyRates"/> (units of currency per 1 USD, decimal strings) and a currency without a rate cannot
/// be the display currency. Idea from magpie's <c>internal/fx</c> (MIT), minus the network refresh.
/// </summary>
public static class Currencies
{
    public const string Usd = "USD";

    public static readonly IReadOnlyList<CurrencyInfo> All =
    [
        new("USD", "$", 2), new("CNY", "¥", 2), new("EUR", "€", 2), new("GBP", "£", 2), new("JPY", "JP¥", 0),
        new("HKD", "HK$", 2), new("TWD", "NT$", 0), new("KRW", "₩", 0), new("SGD", "S$", 2), new("AUD", "A$", 2),
        new("CAD", "C$", 2), new("CHF", "CHF ", 2), new("RUB", "₽", 2), new("INR", "₹", 2), new("BRL", "R$", 2),
    ];

    public static CurrencyInfo? Find(string? code) =>
        code is null ? null : All.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>A rate text: a plain positive decimal, finite, within a sane range.</summary>
    public static bool TryParseRate(string? text, out decimal rate)
    {
        rate = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out rate)
               && rate > 0 && rate <= 1_000_000_000m;
    }

    /// <summary>What the settings say, resolved: USD when the choice is USD, unknown or has no valid rate.</summary>
    public static DisplayCurrencyContext Resolve(string? code, IReadOnlyDictionary<string, string>? rates)
    {
        var info = Find(code);
        if (info is null || info.Code == Usd) return DisplayCurrencyContext.UsdContext;
        if (rates is null) return DisplayCurrencyContext.UsdContext;
        foreach (var (key, text) in rates)
            if (string.Equals(key, info.Code, StringComparison.OrdinalIgnoreCase) && TryParseRate(text, out var rate))
                return new DisplayCurrencyContext(info, rate);
        return DisplayCurrencyContext.UsdContext;
    }

    /// <summary>
    /// Validates a settings update: the table may only name known currencies (not USD) with a positive rate, and the display
    /// currency needs a rate. Returns an error text, or null when fine.
    /// </summary>
    public static string? Validate(string? display, IReadOnlyDictionary<string, string> rates)
    {
        foreach (var (code, text) in rates)
        {
            var info = Find(code);
            if (info is null || info.Code == Usd) return $"unknown currency {code}";
            if (!TryParseRate(text, out _)) return $"rate of {info.Code} must be a number greater than 0";
        }
        var chosen = Find(display);
        if (chosen is null) return "unknown displayCurrency";
        if (chosen.Code != Usd && !rates.Keys.Any(k => string.Equals(k, chosen.Code, StringComparison.OrdinalIgnoreCase)))
            return $"set the exchange rate of {chosen.Code} first";
        return null;
    }
}

/// <summary>The display currency and its rate: converts USD to it, formats it, and converts typed amounts back.</summary>
public sealed class DisplayCurrencyContext(CurrencyInfo info, decimal rate)
{
    public static readonly DisplayCurrencyContext UsdContext = new(new CurrencyInfo("USD", "$", 2), 1m);

    public CurrencyInfo Info { get; } = info;

    /// <summary>Units of this currency per 1 USD.</summary>
    public decimal Rate { get; } = rate;

    public string Code => Info.Code;
    public string Symbol => Info.Symbol;
    public bool IsUsd => Info.Code == Currencies.Usd;

    public decimal FromUsd(decimal usd) => usd * Rate;

    public decimal ToUsd(decimal amount) => amount / Rate;

    /// <summary>Nanodollars of an amount typed in this currency (rounded to a whole nanodollar).</summary>
    public long ToNanos(decimal amount) => (long)Math.Round(ToUsd(amount) * 1_000_000_000m, MidpointRounding.AwayFromZero);

    /// <summary>
    /// An amount of this currency as text: <see cref="CurrencyInfo.Decimals"/> places from 1 up, three significant digits below
    /// 1 (so per-request costs stay visible); a zero is "{symbol}0". Same shape as the web's <c>formatUsd</c>.
    /// </summary>
    public string Format(decimal amount)
    {
        var abs = Math.Abs(amount);
        if (abs == 0) return Symbol + "0";
        var sign = amount < 0 ? "-" : "";
        var inv = CultureInfo.InvariantCulture;
        if (abs >= 1) return Symbol + sign + Math.Round(abs, Info.Decimals, MidpointRounding.AwayFromZero).ToString("N" + Info.Decimals, inv);
        var digits = Math.Clamp(2 - (int)Math.Floor(Math.Log10((double)abs)), 0, 15);
        return Symbol + sign + Math.Round(abs, digits, MidpointRounding.AwayFromZero).ToString("0.###############", inv);
    }

    /// <summary>A USD amount converted and formatted.</summary>
    public string FormatUsd(decimal usd) => Format(FromUsd(usd));
}
