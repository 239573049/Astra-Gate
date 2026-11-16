using Astra.Core.Currency;

namespace Astra.Core.Tests;

public sealed class DisplayCurrencyTests
{
    private static readonly Dictionary<string, string> Rates = new() { ["CNY"] = "7.2", ["JPY"] = "150" };

    [Fact]
    public void Unknown_Or_Rateless_Currency_Resolves_To_Usd()
    {
        Assert.True(Currencies.Resolve("USD", Rates).IsUsd);
        Assert.True(Currencies.Resolve("EUR", Rates).IsUsd); // no rate entered
        Assert.True(Currencies.Resolve("XYZ", Rates).IsUsd);
        Assert.True(Currencies.Resolve("CNY", null).IsUsd);
        Assert.Equal("CNY", Currencies.Resolve("cny", Rates).Code);
    }

    [Fact]
    public void Converts_And_Formats_With_The_Currencys_Decimals()
    {
        var cny = Currencies.Resolve("CNY", Rates);
        Assert.Equal("¥72.00", cny.FormatUsd(10m));
        Assert.Equal("¥0.0072", cny.FormatUsd(0.001m)); // three significant digits below 1
        Assert.Equal("¥0", cny.FormatUsd(0m));
        var jpy = Currencies.Resolve("JPY", Rates);
        Assert.Equal("JP¥1,500", jpy.FormatUsd(10m)); // yen has no decimals
        Assert.Equal("$1,234.50", DisplayCurrencyContext.UsdContext.FormatUsd(1234.5m));
    }

    [Fact]
    public void Typed_Amounts_Convert_Back_To_Nanodollars()
    {
        var cny = Currencies.Resolve("CNY", Rates);
        Assert.Equal(1_000_000_000L, cny.ToNanos(7.2m));
        Assert.Equal(5_000_000_000L, DisplayCurrencyContext.UsdContext.ToNanos(5m));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1e3")]
    public void Rates_Must_Be_Positive_Plain_Decimals(string rate) =>
        Assert.NotNull(Currencies.Validate("USD", new Dictionary<string, string> { ["CNY"] = rate }));

    [Fact]
    public void The_Display_Currency_Needs_A_Rate()
    {
        Assert.Null(Currencies.Validate("USD", new Dictionary<string, string>()));
        Assert.Null(Currencies.Validate("CNY", Rates));
        Assert.NotNull(Currencies.Validate("EUR", Rates));
        Assert.NotNull(Currencies.Validate("XYZ", Rates));
        Assert.NotNull(Currencies.Validate("USD", new Dictionary<string, string> { ["USD"] = "1" }));
    }
}
