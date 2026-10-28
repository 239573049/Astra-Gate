using Astra.Desktop.Platform.Tray;
using Astra.Desktop.Shell;

namespace Astra.Desktop.UiTests;

/// <summary>The command line the app restarts itself into the tray with (TrayRelaunch) round-trips what the successor needs.</summary>
public class TrayRelaunchTests
{
    [Fact]
    public void The_successor_args_carry_the_predecessor_the_server_and_the_page()
    {
        var args = TrayRelaunch.Args(4242, new PlaceholderPageViewModel(AppRoute.Traces, "sessions/abc/def"), 777);
        Assert.Contains("--hidden", args);
        Assert.Contains("--after=4242", args);
        Assert.Equal(777, TrayRelaunch.OwnedServer(args));
        // A parameter may itself contain slashes (the traces views): only the first one separates it from the route.
        Assert.Equal((AppRoute.Traces, "sessions/abc/def"), TrayRelaunch.Route(args));
    }

    [Fact]
    public void Missing_or_bad_values_are_ignored()
    {
        var args = TrayRelaunch.Args(1, new PlaceholderPageViewModel(AppRoute.Overview, null), null);
        Assert.Null(TrayRelaunch.OwnedServer(args));
        Assert.Equal((AppRoute.Overview, (string?)null), TrayRelaunch.Route(args));
        Assert.Null(TrayRelaunch.Route(["--route=NoSuchPage"]));
        Assert.Null(TrayRelaunch.Route(["--route=42"]));
        Assert.Null(TrayRelaunch.OwnedServer(["--owned-server=abc"]));
    }
}
