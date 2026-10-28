using Astra.Desktop.Core.Requests;

namespace Astra.Desktop.Tests.Requests;

public class TableSortTests
{
    private static List<object?> Sort(object?[] values, bool descending)
        => TableSort.Order(values.ToList(), v => v, descending);

    [Fact]
    public void Empty_values_stay_at_the_bottom_in_both_directions()
    {
        object?[] values = [3.0, null, 1.0, "", 2.0];
        Assert.Equal([1.0, 2.0, 3.0, null, ""], Sort(values, false));
        Assert.Equal([3.0, 2.0, 1.0, null, ""], Sort(values, true));
    }

    [Fact]
    public void Equal_values_keep_their_input_order()
    {
        var rows = new[] { ("a", 1L), ("b", 2L), ("c", 1L), ("d", 2L) };
        Assert.Equal(["a", "c", "b", "d"], TableSort.Order(rows, r => r.Item2, false).Select(r => r.Item1));
        Assert.Equal(["b", "d", "a", "c"], TableSort.Order(rows, r => r.Item2, true).Select(r => r.Item1));
    }

    [Fact]
    public void Text_sorts_naturally_ignoring_case()
    {
        object?[] values = ["gpt-10", "GPT-9", "claude", "gpt-9-mini"];
        Assert.Equal(["claude", "GPT-9", "gpt-9-mini", "gpt-10"], Sort(values, false));
    }

    [Fact]
    public void Dates_and_numbers_compare_by_value()
    {
        var early = new DateTimeOffset(2026, 10, 26, 11, 0, 0, TimeSpan.Zero);
        object?[] values = [early.AddMinutes(5), early, early.AddMinutes(1)];
        Assert.Equal([early, early.AddMinutes(1), early.AddMinutes(5)], Sort(values, false));
        Assert.Equal([2L, 10L, 33L], Sort([10L, 33L, 2L], false));
    }

    [Fact]
    public void No_sort_keeps_the_input_order()
        => Assert.Equal(["b", "a"], TableSort.Order(["b", "a"], null, false));

    [Fact]
    public void Columns_keep_their_widths_and_scroll_when_the_container_is_narrower()
    {
        var (total, columns) = TableColumns.Layout([92, 320, 72], 300);
        Assert.Equal(560, total); // web min-width
        var (wide, cols) = TableColumns.Layout([600, 400], 800);
        Assert.Equal(1000, wide);
        Assert.Equal((0d, 600d), cols[0]);
        Assert.Equal((600d, 400d), cols[1]);
        Assert.Equal(3, columns.Length);
    }

    [Fact]
    public void Extra_room_is_shared_in_proportion_to_the_widths()
    {
        var (total, cols) = TableColumns.Layout([100, 300], 800);
        Assert.Equal(800, total);
        Assert.Equal((0d, 200d), cols[0]);
        Assert.Equal((200d, 600d), cols[1]);
    }
}
