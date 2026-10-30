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

    // ---------------------------------------------------------------- packaged app: updates as a whole

    private sealed class FakeDesktopUpdater(string? blocked = null) : IDesktopUpdater
    {
        public List<DesktopRelease> Applied { get; } = [];
        public string Kind => "zip";
        public string? CannotSelfUpdateReason() => blocked;

        public Task ApplyAsync(DesktopRelease release, CancellationToken ct = default)
        {
            Applied.Add(release);
            return Task.CompletedTask;
        }
    }

    private const string Releases = """
        {"channel":"stable","versions":[
          {"version":"0.13.3","notes":"n","files":[{"name":"Astra-0.13.3-darwin-arm64.zip","platform":"darwin-arm64","kind":"zip","size":1,
           "sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}]}]}
        """;

    private UpdateCoordinator AppCoordinator(FakeHandler handler, IDesktopUpdater? desktop, string appVersion = "0.13.0-preview.1")
    {
        var paths = new AstraPaths(_home);
        File.WriteAllText(paths.ConfigFile, """{"port":1}""");
        return new UpdateCoordinator(new UpdateCoordinatorOptions
        {
            Paths = paths,
            Manager = new ServiceManager(new ServiceManagerOptions { Paths = paths, Log = _ => { } }),
            Http = new HttpClient(handler),
            AppVersion = appVersion,
            ApiBase = () => null,
            Track = UpdateTrack.Dmg,
            Desktop = desktop,
            DesktopPlatform = "darwin-arm64",
            DesktopKind = "zip",
        });
    }

    [Fact]
    public async Task A_packaged_app_offers_its_own_update_and_never_asks_for_the_server_manifest()
    {
        var handler = new FakeHandler(_ => FakeHandler.Json(Releases));
        var c = AppCoordinator(handler, new FakeDesktopUpdater());
        var summary = await c.CheckAsync();
        Assert.Equal(UpdateAction.DesktopDmg, summary.Action);
        Assert.Equal("0.13.3", summary.Version);
        Assert.Equal("n", summary.Notes);
        Assert.Same(summary, c.Last);
        Assert.All(handler.Requests, r => Assert.EndsWith("/stable/releases.json", r.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task A_packaged_app_on_the_newest_version_is_up_to_date()
    {
        var c = AppCoordinator(new FakeHandler(_ => FakeHandler.Json(Releases)), new FakeDesktopUpdater(), appVersion: "0.13.3");
        var summary = await c.CheckAsync();
        Assert.Equal(UpdateAction.None, summary.Action);
        Assert.Equal(UpdateApplyOutcome.None, await c.ApplyAsync());
    }

    [Fact]
    public async Task Applying_hands_the_release_to_the_apps_updater()
    {
        var desktop = new FakeDesktopUpdater();
        var c = AppCoordinator(new FakeHandler(_ => FakeHandler.Json(Releases)), desktop);
        Assert.Equal(UpdateApplyOutcome.Desktop, await c.ApplyAsync());
        Assert.Equal("Astra-0.13.3-darwin-arm64.zip", Assert.Single(desktop.Applied).FileName);
    }

    [Fact]
    public async Task A_copy_that_cannot_replace_itself_points_at_the_download_page()
    {
        var desktop = new FakeDesktopUpdater(blocked: "/Volumes/Astra is not writable");
        var c = AppCoordinator(new FakeHandler(_ => FakeHandler.Json(Releases)), desktop);
        var summary = await c.CheckAsync();
        Assert.Equal(UpdateAction.DesktopDownload, summary.Action);
        Assert.Contains(UpdateFeed.DownloadPageUrl, summary.Message);
        Assert.Equal(UpdateApplyOutcome.Download, await c.ApplyAsync());
        Assert.Empty(desktop.Applied);
    }

    [Fact]
    public async Task A_failed_check_keeps_the_last_answer_and_checks_in_flight_are_shared()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var c = AppCoordinator(new FakeHandler(async _ =>
        {
            if (Interlocked.Increment(ref calls) > 1) return FakeHandler.Json("", System.Net.HttpStatusCode.BadGateway);
            await gate.Task;
            return FakeHandler.Json(Releases);
        }), new FakeDesktopUpdater());
        var first = c.CheckAsync();
        var second = c.CheckAsync();
        Assert.Same(first, second);
        gate.SetResult();
        var found = await first;
        Assert.Equal(UpdateAction.DesktopDmg, found.Action);

        var failed = await c.CheckAsync();
        Assert.StartsWith("Update check failed", failed.Message);
        Assert.Same(found, c.Last);
        Assert.Equal(1 + 1, calls);
    }
}
