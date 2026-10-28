using Astra.Desktop.Core.Overview;

namespace Astra.Desktop.Tests.Overview;

/// <summary>Expected strings are what <c>web/src/lib/format.ts</c> prints (Intl en-US) for the same inputs.</summary>
public sealed class StatsFormatTests
{
    [Theory]
    [InlineData("0", "$0")]
    [InlineData("12.3456", "$12.35")]
    [InlineData("1", "$1.00")]
    [InlineData("1234.5", "$1,234.50")]
    [InlineData("0.0213", "$0.0213")]
    [InlineData("0.009", "$0.009")]
    [InlineData("0.00949", "$0.00949")]
    [InlineData("0.123456", "$0.123")]
    [InlineData("0.5", "$0.5")]
    [InlineData("0.99951", "$1")]
    [InlineData("-0.5", "$-0.5")]
    [InlineData("0.00000000001", "$0")]
    public void Usd_keeps_three_significant_digits_below_one_dollar(string value, string expected)
        => Assert.Equal(expected, StatsFormat.Usd(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Usd_of_nothing_is_a_dash() => Assert.Equal("—", StatsFormat.Usd(null));

    [Theory]
    [InlineData("0", "$0")]
    [InlineData("0.012345", "$0.012")]
    [InlineData("0.01", "$0.01")]
    [InlineData("2.5", "$2.5")]
    [InlineData("12.34", "$12")]
    [InlineData("123.4", "$120")]
    [InlineData("999", "$1,000")]
    [InlineData("1234", "$1.2K")]
    [InlineData("12345", "$12.3K")]
    [InlineData("2500000", "$2.5M")]
    public void UsdAxis_is_two_significant_digits_and_compact_from_a_thousand(string value, string expected)
        => Assert.Equal(expected, StatsFormat.UsdAxis(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Money_in_nanodollars_never_passes_through_a_float()
    {
        Assert.Equal("$12.35", StatsFormat.Nanos(12_345_678_901));
        Assert.Equal("$0.00752", StatsFormat.Nanos(7_520_000));
        Assert.Equal("$0", StatsFormat.Nanos(0));
        Assert.Equal("—", StatsFormat.Nanos(null));
        // 0.1 + 0.2 style drift cannot happen: 300_000_000 nanos is exactly $0.3.
        Assert.Equal("$0.3", StatsFormat.Nanos(300_000_000));
    }

    [Theory]
    [InlineData("1234", false, "1,234")]
    [InlineData("9999", true, "9,999")]
    [InlineData("10000", true, "10K")]
    [InlineData("12345", true, "12.3K")]
    [InlineData("8100000", true, "8.1M")]
    [InlineData("999950", true, "1M")]
    [InlineData("69100000", true, "69.1M")]
    [InlineData("3000000000", true, "3B")]
    [InlineData("1.88", false, "1.88")]
    [InlineData("0.4583", false, "0.458")]
    [InlineData("0", false, "0")]
    public void Tokens_groups_digits_and_compacts_only_when_asked(string value, bool compact, string expected)
        => Assert.Equal(expected, StatsFormat.Tokens(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), compact));

    [Fact]
    public void Tokens_of_nothing_is_a_dash() => Assert.Equal("—", StatsFormat.Tokens(null));

    [Fact]
    public void Latency_rate_and_percent()
    {
        Assert.Equal("1180 ms", StatsFormat.Ms(1180));
        Assert.Equal("12.3 s", StatsFormat.Ms(12_345));
        Assert.Equal("—", StatsFormat.Ms(null));
        Assert.Equal("62.4 tok/s", StatsFormat.Tps(62.44));
        Assert.Equal("—", StatsFormat.Tps(-1));
        Assert.Equal("—", StatsFormat.Tps(double.NaN));
        Assert.Equal("59.5%", StatsFormat.Percent(0.595));
        Assert.Equal("100%", StatsFormat.Percent(1));
        Assert.Equal("0%", StatsFormat.Percent(0));
    }

    [Fact]
    public void Time_is_seconds_today_and_month_day_otherwise_in_the_given_zone()
    {
        var now = Fakes.Noon;
        Assert.Equal("11:57:00", StatsFormat.Time(now.AddMinutes(-3), now, TimeSpan.Zero, "zh"));
        Assert.Equal("10/25 23:30", StatsFormat.Time(now.AddHours(-12.5), now, TimeSpan.Zero, "zh"));
        Assert.Equal("10/25, 23:30", StatsFormat.Time(now.AddHours(-12.5), now, TimeSpan.Zero, "en"));
        // The same instant is the next local day in UTC+8.
        Assert.Equal("19:57:00", StatsFormat.Time(now.AddMinutes(-3), now, TimeSpan.FromHours(8), "zh"));
        Assert.Equal("10/26 07:30", StatsFormat.Time(now.AddHours(-12.5), now.AddDays(1), TimeSpan.FromHours(8), "zh"));
    }

    [Fact]
    public void DateTime_follows_the_locale()
    {
        var at = new DateTimeOffset(2026, 10, 26, 11, 57, 0, TimeSpan.Zero);
        Assert.Equal("2026/10/26 11:57", StatsFormat.DateTime(at, TimeSpan.Zero, "zh"));
        Assert.Equal("10/26/2026, 11:57", StatsFormat.DateTime(at, TimeSpan.Zero, "en"));
        Assert.Equal("—", StatsFormat.DateTime(null, TimeSpan.Zero, "zh"));
    }
}
