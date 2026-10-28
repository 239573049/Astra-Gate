using Astra.Api.Contracts;
using Astra.Desktop.Core.Overview;

namespace Astra.Desktop.Tests.Overview;

public sealed class ActivityHeatmapModelTests
{
    [Fact]
    public void Levels_are_the_webs_ceil_of_quarter_steps()
    {
        Assert.Equal(0, ActivityHeatmapModel.Level(0, 157));
        Assert.Equal(1, ActivityHeatmapModel.Level(5, 0));
        foreach (var (count, level) in new (long, int)[] { (1, 1), (39, 1), (40, 2), (78, 2), (79, 3), (117, 3), (118, 4), (157, 4) })
            Assert.Equal(level, ActivityHeatmapModel.Level(count, 157));
    }

    [Fact]
    public void Build_lays_out_monday_first_weeks_with_month_headers()
    {
        // Today is Monday 2026-10-26 (the visual tests' clock): the window opens Monday 2025-10-27 and ends Sunday 2026-11-01.
        var data = new List<DailyActivityDto> { new("2025-10-27", 10), new("2026-10-26", 40) };
        var zh = ActivityHeatmapModel.Build(data, 365, Fakes.Utc, "zh");
        Assert.Equal(53, zh.Weeks);
        Assert.Equal(40, zh.Peak);
        Assert.Equal(1, zh.Levels[0]);
        Assert.Equal(4, zh.Levels[zh.Days.ToList().IndexOf("2026-10-26")]);
        Assert.Equal(0L, zh.Counts[1]);
        // The first column holds Nov 1st, so it is headed by November (the web screenshot reads 11月 there), not "2025年10月".
        Assert.Equal("11月", zh.MonthLabels[0]);
        Assert.Null(zh.MonthLabels[1]);
        Assert.Equal("12月", zh.MonthLabels[5]); // week of 2025-11-24 .. 11-30 has no 1st; 12-01 opens column 5
        Assert.Equal(12, zh.MonthLabels.Count(l => l is not null) - 1);
        Assert.Equal("Nov", ActivityHeatmapModel.Build(data, 365, Fakes.Utc, "en").MonthLabels[0]);
    }

    [Fact]
    public void A_first_column_without_a_first_of_the_month_names_its_month_and_year()
    {
        var model = ActivityHeatmapModel.Build([], 10, Fakes.Utc, "en"); // opens Monday 2026-10-12
        Assert.Equal("Oct 2026", model.MonthLabels[0]);
        Assert.Equal("Nov", model.MonthLabels[^1]);
        Assert.Equal("2026年10月", ActivityHeatmapModel.Build([], 10, Fakes.Utc, "zh").MonthLabels[0]);
    }

    [Fact]
    public void Calendar_names_match_intl_short_formats()
    {
        Assert.Equal("周一", ActivityHeatmapModel.WeekdayName(0, "zh"));
        Assert.Equal("Sun", ActivityHeatmapModel.WeekdayName(6, "en"));
        Assert.Equal("Oct", ActivityHeatmapModel.MonthName(10, "en"));
        Assert.Equal("2026年10月8日周四 · 4 次请求", ActivityHeatmapModel.DayCount("2026-10-08", 4, "zh", "次请求", "无活动"));
        Assert.Equal("Thu, Oct 8, 2026 · no activity", ActivityHeatmapModel.DayCount("2026-10-08", 0, "en", "requests", "no activity"));
    }

    [Fact]
    public void Bubble_aligns_to_the_edges_and_hangs_below_the_top_rows()
    {
        Assert.Equal(new BubblePlacement(8, 20, false, BubbleAlign.Start), ActivityHeatmapModel.Place(50, 20, 1000));
        Assert.Equal(new BubblePlacement(500, 60, true, BubbleAlign.Center), ActivityHeatmapModel.Place(500, 60, 1000));
        Assert.Equal(new BubblePlacement(992, 45, true, BubbleAlign.End), ActivityHeatmapModel.Place(950, 45, 1000));
    }

    [Fact]
    public void Oklab_mix_keeps_the_endpoints_and_premultiplies_alpha()
    {
        var accent = ((byte)0, (byte)0x7a, (byte)0xff, 1.0);
        var surface = ((byte)255, (byte)255, (byte)255, 0.78);
        Assert.Equal(accent, OklabMix.Mix(accent, 1, surface));
        var none = OklabMix.Mix(accent, 0, surface);
        Assert.Equal(((byte)255, (byte)255, (byte)255), (none.R, none.G, none.B));
        Assert.Equal(0.78, none.A, 6);
        var mid = OklabMix.Mix(accent, 0.26, surface);
        Assert.Equal(0.26 + 0.74 * 0.78, mid.A, 6);
        Assert.InRange(mid.B, 250, 255);
        Assert.InRange(mid.R, 150, 220);
    }
}
