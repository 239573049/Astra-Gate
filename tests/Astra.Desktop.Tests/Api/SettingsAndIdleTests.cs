using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Shell;

namespace Astra.Desktop.Tests.Api;

public class SettingsAndIdleTests
{
    [Theory]
    [InlineData("http://127.0.0.1:17321", "fb", "127.0.0.1:17321")]
    [InlineData("https://gate.example.com/", "fb", "gate.example.com")]
    [InlineData("HTTPS://x.y:8443", "fb", "x.y:8443")]
    [InlineData("", "fb", "fb")]
    [InlineData(null, "fb", "fb")]
    [InlineData("  ", null, null)]
    public void Sidebar_address_is_the_gateway_url_without_scheme_else_the_fallback(string? url, string? fallback, string? expected)
        => Assert.Equal(expected, SettingsStore.DisplayAddress(url, fallback));

    [Fact]
    public async Task WhenIdle_waits_for_a_running_fetch_and_returns_at_once_when_none()
    {
        using var qc = new QueryClient();
        await qc.WhenIdleAsync(TimeSpan.FromSeconds(2)); // nothing cached

        var gate = new TaskCompletionSource<int>();
        var q = qc.Query<int>("slow", _ => gate.Task);
        using var _ = q.Activate();
        var idle = qc.WhenIdleAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(idle.IsCompleted);

        gate.SetResult(7);
        await idle;
        Assert.Equal(7, q.Data);
    }

    [Fact]
    public async Task WhenIdle_gives_up_after_its_timeout_on_a_hung_request()
    {
        using var qc = new QueryClient();
        var q = qc.Query<int>("hung", _ => new TaskCompletionSource<int>().Task);
        using var _ = q.Activate();
        await qc.WhenIdleAsync(TimeSpan.FromMilliseconds(200));
        // Returning at all is the assertion: a hung request must not hang the caller.
    }

    [Fact]
    public void FixedClock_stands_still_until_moved()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero));
        IClock c = clock;
        Assert.Equal(c.Now, c.Now);
        clock.Now = clock.Now.AddHours(1);
        Assert.Equal(13, c.Now.Hour);
    }
}
