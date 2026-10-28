using Astra.Core;
using Astra.Desktop.Core;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Update;

namespace Astra.Desktop.Tests.Update;

public sealed class UpdateCoordinatorTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("astra-upd-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private UpdateCoordinator Coordinator(FakeHandler handler, string? apiBase = null)
    {
        var paths = new AstraPaths(_home);
        // Port 1: the probe finds nothing, so the test never talks to a real server (the default port is the user's).
        File.WriteAllText(paths.ConfigFile, """{"port":1}""");
        return new UpdateCoordinator(new UpdateCoordinatorOptions
        {
            Paths = paths,
            Manager = new ServiceManager(new ServiceManagerOptions { Paths = paths, Log = _ => { } }),
            Http = new HttpClient(handler),
            AppVersion = "0.11.7",
            ApiBase = () => apiBase,
        });
    }

    [Fact]
    public async Task Check_reports_a_newer_feed_version_as_a_server_update()
    {
        var c = Coordinator(new FakeHandler(_ => FakeHandler.Json("""{"version":"0.12.0","apiVersion":"1.0"}""")));
        var summary = await c.CheckAsync();
        Assert.Equal(UpdateAction.Server, summary.Action);
        Assert.Contains("0.12.0", summary.Message);
    }

    [Fact]
    public async Task Check_says_up_to_date_when_nothing_is_newer()
    {
        var c = Coordinator(new FakeHandler(_ => FakeHandler.Json("""{"version":"0.11.7","apiVersion":"1.0"}""")));
        var summary = await c.CheckAsync();
        Assert.Equal(UpdateAction.None, summary.Action);
        Assert.Contains("up to date", summary.Message);
    }

    [Fact]
    public async Task Check_never_throws_a_failure_becomes_the_message()
    {
        var c = Coordinator(new FakeHandler(_ => FakeHandler.Json("", System.Net.HttpStatusCode.InternalServerError)));
        var summary = await c.CheckAsync();
        Assert.Equal(UpdateAction.None, summary.Action);
        Assert.StartsWith("Update check failed", summary.Message);
    }

    [Fact]
    public async Task Apply_with_nothing_newer_is_a_no_op()
    {
        var c = Coordinator(new FakeHandler(_ => FakeHandler.Json("""{"version":"0.11.7","apiVersion":"1.0"}""")));
        Assert.Equal(UpdateApplyOutcome.None, await c.ApplyAsync());
    }

    [Fact]
    public void Server_package_names_match_the_npm_platform_packages()
        => Assert.Matches(@"^@aidotnet/server-(darwin|win32|linux)-(arm64|x64)$", UpdateCoordinator.ServerPackageName());
}
