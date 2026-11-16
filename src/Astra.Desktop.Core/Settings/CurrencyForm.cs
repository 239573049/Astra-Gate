using Astra.Core.Currency;

namespace Astra.Desktop.Core.Settings;

/// <summary>
/// Settings › Usage › Display currency: the exchange rates as one line of text ("CNY=7.2, JPY=150" — units of the currency per 1 USD)
/// and back (the web edits them row by row). Rates are typed by the user; Astra never fetches them.
/// </summary>
public static class CurrencyForm
{
    /// <summary>The rate table as text, in the catalog's order.</summary>
    public static string Format(IReadOnlyDictionary<string, string>? rates)
    {
        if (rates is null || rates.Count == 0) return "";
        return string.Join(", ", Currencies.All
            .Where(c => rates.ContainsKey(c.Code))
            .Select(c => c.Code + "=" + rates[c.Code]));
    }

    /// <summary>
    /// Reads "CODE=rate" entries separated by commas, semicolons or line breaks ("CODE rate" and "CODE:rate" work too). Returns the table,
    /// or null with the offending entry in <paramref name="bad"/>. Empty text is an empty table.
    /// </summary>
    public static Dictionary<string, string>? Parse(string? text, out string? bad)
    {
        bad = null;
        var rates = new Dictionary<string, string>();
        foreach (var raw in (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = raw.Split(['=', ':', ' '], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var code = parts.Length == 2 ? parts[0].ToUpperInvariant() : "";
            if (parts.Length != 2 || Currencies.Find(code) is not { } info || info.Code == Currencies.Usd || !Currencies.TryParseRate(parts[1], out _))
            {
                bad = raw;
                return null;
            }
            rates[info.Code] = parts[1];
        }
        return rates;
    }

    /// <summary>Whether <paramref name="code"/> can be the display currency: USD, or a currency with a valid rate.</summary>
    public static bool HasRate(string code, IReadOnlyDictionary<string, string>? rates) =>
        code == Currencies.Usd || (rates is not null && rates.TryGetValue(code, out var text) && Currencies.TryParseRate(text, out _));

    /// <summary>The currencies offered, as value and label ("CNY · ¥").</summary>
    public static IEnumerable<(string Code, string Label)> Options() =>
        Currencies.All.Select(c => (c.Code, c.Code + " · " + c.Symbol.Trim()));
}
