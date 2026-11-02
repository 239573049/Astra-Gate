using Astra.Desktop.Core.Api;
using Astra.Desktop.Core.Connect;
using Astra.Desktop.Core.Tray;

namespace Astra.Desktop.Tests.Connect;

/// <summary>The employee join link (<c>astra://join?gateway=…&amp;token=…</c>): what counts as one, how it reaches the running app, and how failures are worded.</summary>
public sealed class JoinLinkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "astra-jl-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void A_link_carries_the_gateway_and_the_one_time_code()
    {
        var link = JoinLink.TryParse("astra://join?gateway=https%3A%2F%2Fastra.example.com%2F&token=ij_abc-DEF_123");

        Assert.Equal(new JoinLink("https://astra.example.com", "ij_abc-DEF_123"), link);
        Assert.Equal("https://astra.example.com", link!.Display);
    }

    [Fact]
    public void A_gateway_with_a_path_or_port_keeps_them_and_the_display_drops_the_path()
    {
        var link = JoinLink.TryParse("astra://join?gateway=http://10.0.0.5:8080/astra&token=ij_x");

        Assert.Equal("http://10.0.0.5:8080/astra", link!.GatewayUrl);
        Assert.Equal("http://10.0.0.5:8080", link.Display);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://astra.example.com")]
    [InlineData("astra://other?gateway=https://a.example.com&token=ij_x")]
    [InlineData("astra://join?gateway=https://a.example.com")] // no code
    [InlineData("astra://join?token=ij_x")] // no gateway
    [InlineData("astra://join?gateway=ftp://a.example.com&token=ij_x")]
    [InlineData("astra://join?gateway=javascript:alert(1)&token=ij_x")]
    [InlineData("astra://join?gateway=https://user:pw@a.example.com&token=ij_x")]
    [InlineData("astra://join?gateway=https://a.example.com&token=ij%20x")] // whitespace in the code
    public void Anything_else_is_not_a_join_link(string? text)
    {
        Assert.Null(JoinLink.TryParse(text));
    }

    [Fact]
    public void An_oversized_link_is_refused()
    {
        Assert.Null(JoinLink.TryParse("astra://join?gateway=https://a.example.com&token=" + new string('x', 3000)));
    }

    [Fact]
    public void The_first_argument_that_is_a_link_is_the_one()
    {
        var link = JoinLink.FromArgs(["--hidden", "astra://join?gateway=https://a.example.com&token=ij_1", "astra://join?gateway=https://b.example.com&token=ij_2"]);

        Assert.Equal("ij_1", link!.Token);
        Assert.Null(JoinLink.FromArgs(["--hidden"]));
    }

    [Fact]
    public void A_posted_link_waits_for_the_page_and_is_taken_once()
    {
        JoinLinkInbox.Take(); // nothing left over from another test
        var arrived = 0;
        void Count() => arrived++;
        JoinLinkInbox.Arrived += Count;
        try
        {
            JoinLinkInbox.Post(new JoinLink("https://a.example.com", "ij_1"));
        }
        finally
        {
            JoinLinkInbox.Arrived -= Count;
        }

        Assert.Equal(1, arrived);
        Assert.Equal("ij_1", JoinLinkInbox.Take()!.Token);
        Assert.Null(JoinLinkInbox.Take());
    }

    [Fact]
    public void A_second_launch_hands_its_link_to_the_running_app_which_also_comes_to_the_front()
    {
        using var first = SingleInstance.TryClaim(_root)!;
        using var activated = new ManualResetEventSlim();
        using var opened = new ManualResetEventSlim();
        string? received = null;
        first.Listen(activated.Set, text =>
        {
            received = text;
            opened.Set();
        });
        const string link = "astra://join?gateway=https://a.example.com&token=ij_1";

        Assert.True(SingleInstance.SignalRunning(_root, TimeSpan.FromSeconds(5), link));

        Assert.True(activated.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(opened.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(link, received);
    }

    [Theory]
    [InlineData(410, "connect.problem.dead")]
    [InlineData(404, "connect.problem.unknown")]
    [InlineData(401, "connect.problem.key")]
    [InlineData(403, "connect.problem.disabled")]
    [InlineData(502, "connect.problem.unreachable")]
    [InlineData(500, "connect.problem.other")]
    public void A_failed_join_is_worded_by_the_status_the_local_server_relays(int status, string key)
    {
        Assert.Equal(key, ConnectApi.JoinProblemKey(new ApiException(status, "x")));
    }
}
