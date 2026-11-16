using Astra.Core.Currency;

namespace Astra.Desktop.Core.Currency;

/// <summary>
/// The display currency every money formatter of the desktop client reads (the web's <c>lib/currency.ts</c>): set from the settings
/// (<see cref="Apply"/>) whenever they load or change, and always USD underneath — amounts arrive as USD nanodollars and only read
/// differently. Astra never fetches exchange rates; they are the user's.
/// </summary>
public static class DisplayMoney
{
    private static DisplayCurrencyContext _current = DisplayCurrencyContext.UsdContext;

    // Tests run in parallel; a scoped currency (the flow of one test) keeps them from seeing each other's choice.
    private static readonly AsyncLocal<DisplayCurrencyContext?> Scope = new();

    /// <summary>The display currency and its rate.</summary>
    public static DisplayCurrencyContext Current => Scope.Value ?? _current;

    /// <summary>Uses <paramref name="currency"/> for the current async flow only, until the result is disposed (tests).</summary>
    public static IDisposable Scoped(DisplayCurrencyContext currency)
    {
        var previous = Scope.Value;
        Scope.Value = currency;
        return new Restore(() => Scope.Value = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>Raised after <see cref="Apply"/> changed the currency or its rate, so views that hold formatted text can redraw.</summary>
    public static event Action? Changed;

    /// <summary>Takes the settings' choice; USD when the code is unknown or has no valid rate. Returns whether anything changed.</summary>
    public static bool Apply(string? code, IReadOnlyDictionary<string, string>? rates)
    {
        var next = Currencies.Resolve(code, rates);
        if (next.Code == _current.Code && next.Rate == _current.Rate) return false;
        _current = next;
        Changed?.Invoke();
        return true;
    }

    /// <summary>A USD amount converted to the display currency.</summary>
    public static decimal Convert(decimal usd) => usd * Current.Rate;

    /// <summary>A USD amount as an editable number in the display currency: its decimals from 1 up, four significant digits below.</summary>
    public static decimal ToInput(decimal usd)
    {
        var c = Current;
        var v = usd * c.Rate;
        if (Math.Abs(v) >= 1) return Math.Round(v, c.Info.Decimals, MidpointRounding.AwayFromZero);
        if (v == 0) return 0;
        var digits = Math.Clamp(3 - (int)Math.Floor(Math.Log10((double)Math.Abs(v))), 0, 15);
        return Math.Round(v, digits, MidpointRounding.AwayFromZero);
    }
}
