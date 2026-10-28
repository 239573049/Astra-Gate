using System.Text.Json.Nodes;
using Astra.Desktop.Core.Pricing;

namespace Astra.Desktop.Tests.Pricing;

/// <summary>The decimal rules of the pricing editor (web NumberInput + formatUnitPrice): exact, never a float.</summary>
public class PriceNumberTests
{
    [Theory]
    [InlineData("3", "3")]
    [InlineData("0.000003", "0.000003")]
    [InlineData("1.50", "1.5")]
    [InlineData("1.500000", "1.5")]
    [InlineData("0.0", "0")]
    [InlineData("0", "0")]
    [InlineData("00012", "12")]
    [InlineData(".5", "0.5")]
    [InlineData("5.", "5")]
    [InlineData("+7", "7")]
    [InlineData("-2.5", "-2.5")]
    [InlineData("  12  ", "12")]
    [InlineData("1e-7", "0.0000001")]
    [InlineData("3E-6", "0.000003")]
    [InlineData("1.5e3", "1500")]
    [InlineData("2.50e0", "2.5")]
    [InlineData("0.1", "0.1")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    public void Parses_to_the_canonical_text(string input, string expected)
    {
        Assert.True(PriceNumber.TryParse(input, out var value));
        Assert.NotNull(value);
        Assert.Equal(expected, PriceNumber.ToText(value!.Value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_is_no_value(string? input)
    {
        Assert.True(PriceNumber.TryParse(input, out var value));
        Assert.Null(value);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1,5")]
    [InlineData("1_000")]
    [InlineData("0x10")]
    [InlineData("$3")]
    [InlineData("-")]
    [InlineData("e5")]
    [InlineData("1e")]
    [InlineData("1.2.3")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e400")]
    [InlineData("99999999999999999999999999999999")] // above decimal.MaxValue
    [InlineData("1e-40")] // would round to a free price
    [InlineData("0.0000000000000000000000000000001")]
    public void Rejects_what_is_not_a_finite_decimal(string input)
    {
        Assert.False(PriceNumber.TryParse(input, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void Zero_with_a_zero_exponent_is_still_zero_not_an_underflow()
    {
        Assert.True(PriceNumber.TryParse("0e-40", out var value));
        Assert.Equal(0m, value);
        Assert.True(PriceNumber.TryParse("0.000", out var zero));
        Assert.Equal("0", PriceNumber.ToText(zero!.Value));
    }

    [Fact]
    public void Tiny_prices_survive_a_round_trip_through_json_without_exponent_or_float_noise()
    {
        PriceNumber.TryParse("0.000003", out var v);
        var node = PriceNumber.ToNode(v);
        Assert.Equal("0.000003", node!.ToJsonString());
        Assert.Equal(0.000003m, PriceNumber.FromNode(node));
        // 0.1 + 0.2 is the classic float trap: here it is exactly 0.3
        Assert.Equal(0.3m, PriceNumber.FromNode(PriceNumber.ToNode(0.1m + 0.2m)));
        Assert.Equal("0.3", PriceNumber.ToNode(0.1m + 0.2m)!.ToJsonString());
    }

    [Fact]
    public void A_price_never_picks_up_binary_float_digits()
    {
        // 0.075 as a double is 0.07499999999999999722...; as the decimal the server stores it is exact
        PriceNumber.TryParse("0.075", out var v);
        Assert.Equal("0.075", PriceNumber.ToNode(v)!.ToJsonString());
        PriceNumber.TryParse("1.1", out var w);
        Assert.Equal("1.1", PriceNumber.ToNode(w)!.ToJsonString());
    }

    [Fact]
    public void Trailing_zero_scale_is_dropped_in_the_written_json()
    {
        Assert.Equal("1.5", PriceNumber.ToNode(1.50m)!.ToJsonString());
        Assert.Equal("2", PriceNumber.ToNode(2.000m)!.ToJsonString());
        Assert.Equal("0", PriceNumber.ToNode(0.00m)!.ToJsonString());
    }

    [Fact]
    public void Null_price_is_json_null()
    {
        Assert.Null(PriceNumber.ToNode(null));
    }

    [Theory]
    [InlineData("3", "3")]
    [InlineData("3.0", "3")]
    [InlineData("1e-7", "0.0000001")]
    [InlineData("1E-7", "0.0000001")]
    [InlineData("2.5E+2", "250")]
    [InlineData("0.000003", "0.000003")]
    public void Reads_any_number_literal_a_client_may_have_written(string literal, string expected)
    {
        var node = JsonNode.Parse(literal);
        Assert.Equal(expected, PriceNumber.ToText(PriceNumber.FromNode(node)!.Value));
    }

    [Fact]
    public void Reads_decimal_strings_like_the_server_does()
    {
        Assert.Equal(0.000003m, PriceNumber.FromNode(JsonValue.Create("0.000003")));
        Assert.Equal(15m, PriceNumber.FromNode(JsonValue.Create("15")));
        Assert.Null(PriceNumber.FromNode(JsonValue.Create("abc")));
        Assert.Null(PriceNumber.FromNode(JsonValue.Create("")));
    }

    [Fact]
    public void Null_and_non_numbers_read_as_null()
    {
        Assert.Null(PriceNumber.FromNode(null));
        Assert.Null(PriceNumber.FromNode(JsonValue.Create(true)));
        Assert.Null(PriceNumber.FromNode(new JsonObject()));
        Assert.Null(PriceNumber.FromNode(new JsonArray()));
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("15", "15")]
    [InlineData("0.000003", "0.000003")]
    [InlineData("0.000001", "0.000001")] // 1e-6 is still plain digits in JS
    [InlineData("0.0000001", "1e-7")]
    [InlineData("0.00000015", "1.5e-7")]
    [InlineData("-0.0000001", "-1e-7")]
    [InlineData("1000000000000000000000", "1e+21")]
    [InlineData("1500000000000000000000", "1.5e+21")]
    [InlineData("999999999999999999999", "999999999999999999999")]
    [InlineData("12345.678", "12345.678")]
    public void Js_text_matches_what_String_of_Number_prints(string input, string expected)
    {
        Assert.Equal(expected, PriceNumber.ToJsText(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture)));
    }
}
