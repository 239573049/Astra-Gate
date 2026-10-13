using Astra.Core.Tokens;

namespace Astra.Core.Tests;

public class TokenLimitsTests
{
    private static readonly TimeZoneInfo Shanghai = UsagePeriods.Zone("Asia/Shanghai");

    [Fact]
    public void Periods_Follow_The_Usage_Time_Zone()
    {
        // 2026-10-13 23:30 UTC is already Wednesday 2026-10-14 07:30 in Shanghai.
        var now = new DateTimeOffset(2026, 10, 13, 23, 30, 0, TimeSpan.Zero);
        var (dayFrom, dayReset) = UsagePeriods.Window(TokenLimitPeriods.Day, now, Shanghai);
        Assert.Equal(new DateOnly(2026, 10, 14), dayFrom);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.FromHours(8)), dayReset);

        var (weekFrom, weekReset) = UsagePeriods.Window(TokenLimitPeriods.Week, now, Shanghai);
        Assert.Equal(new DateOnly(2026, 10, 12), weekFrom); // Monday
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.FromHours(8)), weekReset);

        var (monthFrom, monthReset) = UsagePeriods.Window(TokenLimitPeriods.Month, now, Shanghai);
        Assert.Equal(new DateOnly(2026, 10, 1), monthFrom);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(8)), monthReset);

        Assert.Equal((null, null), UsagePeriods.Window(TokenLimitPeriods.Total, now, Shanghai));
        Assert.Equal("2026-10-13", UsagePeriods.DayKey(now, TimeZoneInfo.Utc));
        Assert.Equal("2026-10-14", UsagePeriods.DayKey(now, Shanghai));
    }

    [Fact]
    public void A_Sunday_Belongs_To_The_Week_Starting_The_Monday_Before()
    {
        var sunday = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 12), UsagePeriods.Window(TokenLimitPeriods.Week, sunday, TimeZoneInfo.Utc).From);
    }

    [Fact]
    public void Unknown_Or_Missing_Zones_Fall_Back_To_Local()
    {
        Assert.Equal(TimeZoneInfo.Local, UsagePeriods.Zone(null));
        Assert.Equal(TimeZoneInfo.Local, UsagePeriods.Zone("Mars/Olympus"));
    }

    [Fact]
    public void Validation_And_Parsing()
    {
        var limits = new TokenLimits
        {
            Budgets =
            [
                new TokenBudget { Metric = "cost", Period = "month", Limit = 1 },
                new TokenBudget { Metric = "cost", Period = "month", Limit = 2 },
                new TokenBudget { Metric = "bytes", Period = "day", Limit = 0 },
            ],
            Rpm = 0,
        };
        var errors = limits.Validate();
        Assert.Contains(errors, e => e.Contains("cost:month"));
        Assert.Contains(errors, e => e.Contains("bytes"));
        Assert.Contains(errors, e => e.Contains("rpm"));

        Assert.Null(TokenLimits.Parse(null));
        Assert.Null(TokenLimits.Parse("""{"budgets":[]}"""));
        var parsed = TokenLimits.Parse("""{"budgets":[{"metric":"requests","period":"day","limit":5}],"rpm":10}""")!;
        Assert.Equal("requests:day", Assert.Single(parsed.Budgets).Key);
        Assert.Equal(10, parsed.Rpm);
    }

    [Fact]
    public void Refused_Requests_Do_Not_Count()
    {
        Assert.False(TokenLimits.Counts(new Requests.RequestRecord { ErrorType = TokenLimits.RefusedErrorType }));
        Assert.True(TokenLimits.Counts(new Requests.RequestRecord { ErrorType = "rate_limit_error" }));
    }
}
