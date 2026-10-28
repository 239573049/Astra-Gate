using Astra.Desktop.Core;

namespace Astra.Desktop.Tests;

public class ServerBinaryResolverTests
{
    private static Func<string, bool> Exists(params string[] paths) => p => paths.Contains(p);

    [Fact]
    public void Install_wins_over_everything()
    {
        var r = ServerBinaryResolver.Resolve(new InstallInfo("/i/s", "0.12.0"), "/e/s", new BundledServer("/b/s", "0.12.0"), "/repo", Exists("/i/s", "/e/s", "/b/s"));
        Assert.Equal(new ServerBinary("/i/s", ServerBinarySource.Install, "0.12.0"), r);
    }

    [Fact]
    public void A_stale_install_gives_way_to_a_newer_bundled_server()
    {
        var r = ServerBinaryResolver.Resolve(new InstallInfo("/i/s", "0.11.0"), "/e/s", new BundledServer("/b/s", "0.12.0"), null, Exists("/i/s", "/e/s", "/b/s"));
        Assert.Equal(ServerBinarySource.Env, r!.Source);
        r = ServerBinaryResolver.Resolve(new InstallInfo("/i/s", "0.11.0"), null, new BundledServer("/b/s", "0.12.0"), null, Exists("/i/s", "/b/s"));
        Assert.Equal(new ServerBinary("/b/s", ServerBinarySource.Bundled, "0.12.0"), r);
    }

    [Fact]
    public void An_install_without_a_recorded_version_is_never_stale()
    {
        var r = ServerBinaryResolver.Resolve(new InstallInfo("/i/s", null), null, new BundledServer("/b/s", "9.0.0"), null, Exists("/i/s", "/b/s"));
        Assert.Equal(ServerBinarySource.Install, r!.Source);
    }

    [Fact]
    public void Only_existing_candidates_are_returned()
    {
        Assert.Null(ServerBinaryResolver.Resolve(null, "/e/s", null, null, Exists()));
        var r = ServerBinaryResolver.Resolve(new InstallInfo("/gone", "1.0.0"), null, null, "/repo", Exists(ServerBinaryResolver.DevPath("/repo")));
        Assert.Equal(ServerBinarySource.Dev, r!.Source);
    }

    [Fact]
    public void Only_a_target_with_a_known_different_version_replaces_a_running_server()
    {
        var known = new ServerBinary("/b", ServerBinarySource.Bundled, "0.12.0");
        Assert.True(ServerBinaryResolver.ShouldReplaceRunning("0.11.7", known));
        Assert.False(ServerBinaryResolver.ShouldReplaceRunning("0.12.0", known));
        Assert.False(ServerBinaryResolver.ShouldReplaceRunning(null, known));
        Assert.False(ServerBinaryResolver.ShouldReplaceRunning("0.11.7", new ServerBinary("/e", ServerBinarySource.Env, null)));
        Assert.False(ServerBinaryResolver.ShouldReplaceRunning("0.11.7", null));
    }
}
