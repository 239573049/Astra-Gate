using Astra.Desktop.Core.Settings;

namespace Astra.Desktop.Tests.Settings;

public class SettingsFormatTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 26, 12, 5, 9, TimeSpan.Zero);

    [Fact]
    public void DateTime_is_the_web_intl_shape_per_language()
    {
        Assert.Equal("2026/10/26 12:05", SettingsFormat.DateTime(At, "zh", TimeZoneInfo.Utc));
        Assert.Equal("10/26/2026, 12:05", SettingsFormat.DateTime(At, "en", TimeZoneInfo.Utc));
        Assert.Equal("—", SettingsFormat.DateTime(null, "zh"));
    }

    [Fact]
    public void Full_is_to_locale_string()
    {
        Assert.Equal("2026/10/26 12:05:09", SettingsFormat.Full(At, "zh", TimeZoneInfo.Utc));
        Assert.Equal("10/26/2026, 12:05:09 PM", SettingsFormat.Full(At, "en", TimeZoneInfo.Utc));
    }

    [Fact]
    public void Time_shows_seconds_today_and_the_date_on_other_days()
    {
        var now = new DateTimeOffset(2026, 10, 26, 18, 0, 0, TimeSpan.Zero);
        Assert.Equal("12:05:09", SettingsFormat.Time(At, now, "zh", TimeZoneInfo.Utc));
        Assert.Equal("10/25 12:05", SettingsFormat.Time(At.AddDays(-1), now, "zh", TimeZoneInfo.Utc));
        Assert.Equal("10/25, 12:05", SettingsFormat.Time(At.AddDays(-1), now, "en", TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(30_000, "zh", "1 分钟")]
    [InlineData(12 * 60_000, "zh", "12 分钟")]
    [InlineData(119 * 60_000, "en", "119 minutes")]
    [InlineData(120 * 60_000, "en", "2 hours")]
    [InlineData(47 * 3_600_000, "zh", "47 小时")]
    [InlineData(48 * 3_600_000, "en", "2 days")]
    [InlineData(60_000, "en", "1 minute")]
    public void Duration_uses_one_unit(double ms, string language, string expected) => Assert.Equal(expected, SettingsFormat.Duration(ms, language));

    [Fact]
    public void Count_groups_thousands() => Assert.Equal("1,234,567", SettingsFormat.Count(1_234_567));
}
