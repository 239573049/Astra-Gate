using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Requests;

namespace Astra.Desktop.Tests.Requests;

public class DebounceTests
{
    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException();
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task A_burst_of_changes_settles_once_on_the_last_value()
    {
        using var d = new Debounce<string>("", ImmediateDispatcher.Instance, TimeSpan.FromMilliseconds(60));
        var seen = new List<string>();
        d.Settled += seen.Add;
        d.Set("g");
        await Task.Delay(20);
        d.Set("gp");
        await Task.Delay(20);
        d.Set("gpt");
        Assert.Equal("", d.Value);
        Assert.Equal("gpt", d.Latest);
        await Until(() => seen.Count > 0);
        await Task.Delay(120);
        Assert.Equal(["gpt"], seen);
        Assert.Equal("gpt", d.Value);
    }

    [Fact]
    public async Task Setting_the_settled_value_again_raises_nothing()
    {
        using var d = new Debounce<string>("a", ImmediateDispatcher.Instance, TimeSpan.FromMilliseconds(20));
        var seen = 0;
        d.Settled += _ => seen++;
        d.Set("b");
        d.Set("a");
        await Task.Delay(150);
        Assert.Equal(0, seen);
    }

    [Fact]
    public async Task Dispose_cancels_a_pending_change()
    {
        var d = new Debounce<int>(0, ImmediateDispatcher.Instance, TimeSpan.FromMilliseconds(40));
        var seen = 0;
        d.Settled += _ => seen++;
        d.Set(1);
        d.Dispose();
        await Task.Delay(150);
        Assert.Equal(0, seen);
        Assert.Equal(0, d.Value);
    }

    [Fact]
    public async Task Filter_picks_compare_by_content_so_the_same_picks_do_not_requery()
    {
        using var d = new Debounce<RequestFilterValues>(RequestFilterValues.Empty, ImmediateDispatcher.Instance, TimeSpan.FromMilliseconds(20));
        var seen = 0;
        d.Settled += _ => seen++;
        d.Set(RequestFilterValues.Empty.With(RequestFilterId.Client, ["a"]));
        d.Set(RequestFilterValues.Empty);
        await Task.Delay(150);
        Assert.Equal(0, seen);
    }
}
