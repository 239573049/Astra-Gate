using Astra.Api.Contracts;
using Astra.Core.Currency;
using Astra.Desktop.Core.Currency;
using Astra.Desktop.Core.Overview;
using Astra.Desktop.Core.Requests;
using Astra.Desktop.Core.Settings;
using Astra.Desktop.Core.Tokens;
using Astra.Desktop.Core.Traces;
using Astra.Desktop.Core.Tray;

namespace Astra.Desktop.Tests.Settings;

public sealed class DisplayCurrencyTests
{
    private static readonly Dictionary<string, string> Rates = new() { ["CNY"] = "7.2", ["JPY"] = "150" };

    [Fact]
    public void Money_reads_as_dollars_unless_a_currency_is_chosen()
    {
        Assert.Equal("$12.35", StatsFormat.Usd(12.345m));
        Assert.Equal("$0.0213", TraceFormat.Usd(0.0213));
        Assert.Equal("$2.50", StatsFormat.Nanos(2_500_000_000));
    }

    [Fact]
    public void Every_formatter_converts_and_uses_the_currencys_symbol_and_decimals()
    {
        using (DisplayMoney.Scoped(Currencies.Resolve("CNY", Rates)))
        {
            Assert.Equal("¥72.00", StatsFormat.Usd(10m));
            Assert.Equal("¥0.0072", StatsFormat.Nanos(1_000_000));
            Assert.Equal("¥72.00", TraceFormat.Usd(10));
            Assert.Equal("¥7.2K", StatsFormat.UsdAxis(1000m));
            Assert.Equal("¥<0.01", TrayMath.FormatCostShort(0.001m));
            Assert.Equal("¥7.20", TrayMath.FormatCostShort(1m));
        }
        using (DisplayMoney.Scoped(Currencies.Resolve("JPY", Rates)))
        {
            Assert.Equal("JP¥1,500", StatsFormat.Usd(10m));
            Assert.Equal("JP¥150", TrayMath.FormatCostShort(1m));
        }
        Assert.Equal("$10.00", StatsFormat.Usd(10m)); // the scope ended
    }

    [Fact]
    public void A_cost_limit_is_typed_in_the_display_currency_and_saved_as_usd()
    {
        using var scope = DisplayMoney.Scoped(Currencies.Resolve("CNY", Rates));
        var shown = BudgetRules.Draft("cost", "day", 10m);
        Assert.Equal(72m, shown.Limit);
        // untouched: the dollars it came with, not re-rounded through the rate
        Assert.Equal(10m, BudgetRules.UsdLimit(shown));
        // edited: converted back to dollars
        Assert.Equal(5m, BudgetRules.UsdLimit(shown with { Limit = 36m }));
        // other metrics are plain numbers
        Assert.Equal(100m, BudgetRules.UsdLimit(BudgetRules.Draft("requests", "day", 100m)));
        var json = BudgetRules.LimitsJson([shown with { Limit = 36m }], null)!;
        Assert.Equal(5m, json["budgets"]![0]!["limit"]!.GetValue<decimal>());
    }

    [Fact]
    public void Rates_round_trip_through_one_line_of_text()
    {
        Assert.Equal("CNY=7.2, JPY=150", CurrencyForm.Format(Rates));
        var parsed = CurrencyForm.Parse("jpy 150; cny=7.2\nEUR:0.92", out var bad);
        Assert.Null(bad);
        Assert.Equal("0.92", parsed!["EUR"]);
        Assert.Equal("150", parsed["JPY"]);
        Assert.Empty(CurrencyForm.Parse("", out _)!);
        foreach (var wrong in new[] { "CNY=0", "XYZ=3", "USD=1", "CNY", "CNY=abc" })
        {
            Assert.Null(CurrencyForm.Parse(wrong, out bad));
            Assert.Equal(wrong, bad);
        }
    }

    [Fact]
    public void Only_a_currency_with_a_rate_can_be_displayed()
    {
        Assert.True(CurrencyForm.HasRate("USD", null));
        Assert.True(CurrencyForm.HasRate("CNY", Rates));
        Assert.False(CurrencyForm.HasRate("EUR", Rates));
    }

    [Fact]
    public void The_context_cell_shows_the_fill_and_tones_it()
    {
        static string T(string key, params (string Name, object? Value)[] args) => key;
        var c = new ContextUsageDto(50_000, 200_000, 0.25, 40_000, 800, 64_000, null);
        var cell = ContextPresentation.Cell(c, T);
        Assert.Equal("25%", cell.First);
        Assert.Equal("50K / 200K", cell.Second);
        Assert.True(ContextPresentation.Cell(null, T).Empty);
        Assert.Equal("1.2%", ContextPresentation.FillText(0.0123));
        Assert.Equal("<0.1%", ContextPresentation.FillText(0.0004));
        Assert.Equal(ContextPresentation.Tone.Ok, ContextPresentation.ToneOf(0.5));
        Assert.Equal(ContextPresentation.Tone.Warn, ContextPresentation.ToneOf(0.75));
        Assert.Equal(ContextPresentation.Tone.Danger, ContextPresentation.ToneOf(0.95));
        var segments = ContextPresentation.WindowSegments(c);
        Assert.Equal([("cached", 40_000L), ("fresh", 10_000L), ("output", 800L), ("free", 149_200L)], segments);
    }
}
