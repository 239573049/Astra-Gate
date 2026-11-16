using Astra.Api.Contracts;
using Astra.Desktop.Core.Accounts;
using Astra.Desktop.Core.Warmup;

namespace Astra.Desktop.Tests.Settings;

/// <summary>The warm-up form's rules (linked update 36), the same as the web component's.</summary>
public class WarmupLogicTests
{
    [Theory]
    [InlineData("06:00", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("6:00", false)]
    [InlineData("6am", false)]
    [InlineData("", false)]
    public void A_Time_Of_Day_Looks_Like_HH_mm(string text, bool expected) => Assert.Equal(expected, WarmupLogic.IsTime(text));

    [Fact]
    public void Typed_Times_Are_Sorted_Without_Repeats_And_Refused_When_One_Is_Wrong()
    {
        Assert.Equal(["06:00", "11:05"], WarmupLogic.ParseTimes("11:05, 06:00; 06:00"));
        Assert.Empty(WarmupLogic.ParseTimes("")!);
        Assert.Null(WarmupLogic.ParseTimes("06:00, soon"));
    }

    [Fact]
    public void Toggling_A_Weekday_Keeps_The_List_Sorted_And_An_Empty_List_Is_Every_Day()
    {
        Assert.Equal([1, 3], WarmupLogic.Toggle([1], 3));
        Assert.Equal([1], WarmupLogic.Toggle([1, 3], 3));
        Assert.True(WarmupLogic.IsPicked([], 4));
        Assert.False(WarmupLogic.IsPicked([1], 4));
        Assert.Equal([1, 2, 3, 4, 5, 6, 0], WarmupLogic.Days); // Monday first
    }

    [Fact]
    public void The_Sign_In_Line_Says_Who_Is_Signed_In()
    {
        Assert.Contains("me@example.com", ClaudeBridgeLogic.SignInText(new ClaudeSignInDto(true, true, "me@example.com", "max", "claude.ai", null, null)));
        Assert.DoesNotContain("me@example.com", ClaudeBridgeLogic.SignInText(new ClaudeSignInDto(true, false, null, null, null, null, null)));
        Assert.Contains("old", ClaudeBridgeLogic.SignInText(new ClaudeSignInDto(false, false, null, null, null, null, "an old claude")), StringComparison.OrdinalIgnoreCase);
    }
}
