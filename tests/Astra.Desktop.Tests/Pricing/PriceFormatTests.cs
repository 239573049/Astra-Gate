using System.Text.Json.Nodes;
using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Tests.Pricing;

/// <summary>web/src/test/format.test.ts and the formatters the models pages print, on decimals.</summary>
public class PriceFormatTests
{
    [Theory]
    [InlineData("4.66984508", "$4.67")]
    [InlineData("0.009", "$0.009")]
    [InlineData("0.01994", "$0.0199")]
    [InlineData("0.0000001", "$0.0000001")]
    [InlineData("0", "$0")]
    [InlineData("1234", "$1,234.00")]
    [InlineData("1", "$1.00")]
    [InlineData("0.999", "$0.999")]
    [InlineData("0.9996", "$1")]
    [InlineData("0.0213", "$0.0213")]
    [InlineData("-3.456", "$-3.46")]
    [InlineData("-0.5", "$-0.5")]
    [InlineData("1234567.891", "$1,234,567.89")]
    public void Usd_uses_two_decimals_from_a_dollar_and_three_significant_digits_below(string input, string expected)
        => Assert.Equal(expected, PriceFormat.Usd(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Usd_of_nothing_is_a_dash() => Assert.Equal("—", PriceFormat.Usd(null));

    [Theory]
    [InlineData(19_900_000, "$0.0199")]
    [InlineData(0, "$0")]
    [InlineData(1_000_000_000, "$1.00")]
    [InlineData(3, "$0.000000003")]
    public void Nanos_are_converted_exactly(long nanos, string expected) => Assert.Equal(expected, PriceFormat.Nanos(nanos));

    [Fact]
    public void Nanos_of_nothing_is_a_dash() => Assert.Equal("—", PriceFormat.Nanos(null));

    [Theory]
    [InlineData("3", "$3")]
    [InlineData("0.66", "$0.66")]
    [InlineData("0.075", "$0.075")]
    [InlineData("1.25", "$1.25")]
    [InlineData("0.000003", "$0.000003")]
    [InlineData("1.500", "$1.5")]
    [InlineData("0.123456789", "$0.12345679")] // 8 significant digits
    [InlineData("12345.6789", "$12345.679")]
    [InlineData("123456789", "$123456790")]
    [InlineData("0.0000001", "$1e-7")] // JS prints small numbers in exponent form
    public void Unit_price_keeps_eight_significant_digits_like_the_web(string input, string expected)
        => Assert.Equal(expected, PriceFormat.UnitPrice(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Unit_price_of_nothing_is_a_dash()
    {
        Assert.Equal("—", PriceFormat.UnitPrice((decimal?)null));
        Assert.Equal("—", PriceFormat.UnitPrice((JsonNode?)null));
        Assert.Equal("—", PriceFormat.UnitPrice(JsonValue.Create("")));
    }

    [Fact]
    public void Unit_price_of_json_values()
    {
        Assert.Equal("$3", PriceFormat.UnitPrice(JsonNode.Parse("3")));
        Assert.Equal("$0.66", PriceFormat.UnitPrice(JsonValue.Create("0.66")));
        Assert.Equal("$1e-7", PriceFormat.UnitPrice(JsonNode.Parse("1e-7")));
        // a string that is not a number is printed as written (String(price) in the web)
        Assert.Equal("abc", PriceFormat.UnitPrice(JsonValue.Create("abc")));
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0L, "—")]
    [InlineData(512L, "512")]
    [InlineData(999L, "999")]
    [InlineData(1000L, "1K")]
    [InlineData(1500L, "2K")] // Math.round(1.5) = 2
    [InlineData(131_072L, "131K")]
    [InlineData(200_000L, "200K")]
    [InlineData(999_999L, "1000K")]
    [InlineData(1_000_000L, "1M")]
    [InlineData(1_047_576L, "1.05M")]
    [InlineData(2_000_000L, "2M")]
    [InlineData(1_500_000L, "1.5M")]
    [InlineData(10_000_000L, "10M")]
    public void Context_windows(long? tokens, string expected) => Assert.Equal(expected, PriceFormat.Context(tokens));

    [Fact]
    public void Summary_of_base_prices()
    {
        Assert.Equal("$3 / $15", PriceFormat.Summary(JsonNode.Parse("""{"base":{"input":3,"output":15}}""")));
        Assert.Equal("$3 / —", PriceFormat.Summary(JsonNode.Parse("""{"base":{"input":3,"output":null}}""")));
        Assert.Equal("—", PriceFormat.Summary(JsonNode.Parse("""{"base":{"input":null,"output":null}}""")));
        Assert.Equal("—", PriceFormat.Summary(JsonNode.Parse("""{"base":{}}""")));
        Assert.Equal("—", PriceFormat.Summary(JsonNode.Parse("{}")));
        Assert.Equal("—", PriceFormat.Summary(null));
    }

    [Theory]
    [InlineData(1234567, "1,234,567")]
    [InlineData(0, "0")]
    [InlineData(5, "5")]
    public void Token_counts_are_grouped(long n, string expected) => Assert.Equal(expected, PriceFormat.Tokens(n));

    [Fact]
    public void Round_significant_handles_exact_powers_of_ten_and_large_values()
    {
        Assert.Equal(1000m, PriceFormat.RoundSignificant(1000m, 3));
        Assert.Equal(0.001m, PriceFormat.RoundSignificant(0.001m, 3));
        Assert.Equal(1230m, PriceFormat.RoundSignificant(1234m, 3));
        Assert.Equal(0.00123m, PriceFormat.RoundSignificant(0.001234m, 3));
        Assert.Equal(0m, PriceFormat.RoundSignificant(0m, 3));
        Assert.Equal(-1230m, PriceFormat.RoundSignificant(-1234m, 3));
    }
}
