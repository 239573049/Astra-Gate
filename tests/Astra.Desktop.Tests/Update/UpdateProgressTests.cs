using System.Net;
using System.Security.Cryptography;
using Astra.Core;
using Astra.Desktop.Core;
using Astra.Desktop.Core.Shell;
using Astra.Desktop.Core.Update;

namespace Astra.Desktop.Tests.Update;

/// <summary>The steps an update reports for the bottom-right update card, from the download bytes to how it ended.</summary>
public sealed class UpdateProgressTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("astra-upd-progress-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private const string Releases = """
        {"channel":"stable","versions":[
          {"version":"0.13.3","notes":"n","files":[{"name":"Astra-0.13.3-darwin-arm64.zip","platform":"darwin-arm64","kind":"zip","size":1,
           "sha256":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}]}]}
        """;

    private sealed class Updater(Exception? fail = null) : IDesktopUpdater
    {
        public string Kind => "zip";
        public string? CannotSelfUpdateReason() => null;

        public Task ApplyAsync(DesktopRelease release, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
        {
            for (var i = 0; i <= 200; i++) progress?.Report(new UpdateProgress(UpdateStage.Downloading, release.Version, i, 200));
            progress?.Report(new UpdateProgress(UpdateStage.Verifying, release.Version));
            if (fail is not null) throw fail;
            return Task.CompletedTask;
        }
    }

    private UpdateCoordinator App(IDesktopUpdater desktop, List<UpdateProgress> seen)
    {
        var paths = new AstraPaths(_home);
        File.WriteAllText(paths.ConfigFile, """{"port":1}""");
        return new UpdateCoordinator(new UpdateCoordinatorOptions
        {
            Paths = paths,
            Manager = new ServiceManager(new ServiceManagerOptions { Paths = paths, Log = _ => { } }),
            Http = new HttpClient(new FakeHandler(_ => FakeHandler.Json(Releases))),
            AppVersion = "0.13.0",
            ApiBase = () => null,
            Track = UpdateTrack.Dmg,
            Desktop = desktop,
            DesktopPlatform = "darwin-arm64",
            DesktopKind = "zip",
            Progress = seen.Add,
        });
    }

    [Fact]
    public async Task An_app_update_reports_preparing_the_download_verifying_and_the_relaunch()
    {
        var seen = new List<UpdateProgress>();
        var c = App(new Updater(), seen);

        Assert.Equal(UpdateApplyOutcome.Desktop, await c.ApplyAsync());

        Assert.Equal(UpdateStage.Preparing, seen[0].Stage);
        Assert.All(seen, p => Assert.Equal("0.13.3", p.Version));
        var downloads = seen.Where(p => p.Stage == UpdateStage.Downloading).ToList();
        // 201 ticks thinned to about one per percent; the last byte always goes through.
        Assert.InRange(downloads.Count, 2, 102);
        Assert.Equal(1.0, downloads[^1].Fraction);
        Assert.Equal([UpdateStage.Verifying, UpdateStage.Relaunching], seen.SkipWhile(p => p.Stage != UpdateStage.Verifying).Select(p => p.Stage));
        Assert.Same(seen[^1], c.Current);
    }

    [Fact]
    public async Task A_failed_apply_reports_failed_with_the_message_and_still_throws()
    {
        var seen = new List<UpdateProgress>();
        var c = App(new Updater(new IOException("disk full")), seen);

        var e = await Assert.ThrowsAsync<UpdateException>(() => c.ApplyAsync());

        var last = seen[^1];
        Assert.Equal(UpdateStage.Failed, last.Stage);
        Assert.Equal(e.Message, last.Error);
        Assert.Equal("0.13.3", last.Version);
        Assert.True(last.IsFinished);
    }

    [Fact]
    public async Task A_failed_check_carries_the_error_apart_from_the_message()
    {
        var paths = new AstraPaths(_home);
        File.WriteAllText(paths.ConfigFile, """{"port":1}""");
        var c = new UpdateCoordinator(new UpdateCoordinatorOptions
        {
            Paths = paths,
            Manager = new ServiceManager(new ServiceManagerOptions { Paths = paths, Log = _ => { } }),
            Http = new HttpClient(new FakeHandler(_ => FakeHandler.Json("", HttpStatusCode.BadGateway))),
            AppVersion = "0.11.7",
            ApiBase = () => null,
        });
        var summary = await c.CheckAsync();
        Assert.NotNull(summary.Error);
        Assert.Equal(UpdateAction.None, summary.Action);
    }

    [Fact]
    public void Fraction_is_unknown_without_a_size()
    {
        Assert.Null(new UpdateProgress(UpdateStage.Downloading, Received: 10).Fraction);
        Assert.Equal(0.5, new UpdateProgress(UpdateStage.Downloading, Received: 5, Total: 10).Fraction);
        Assert.Equal(1.0, new UpdateProgress(UpdateStage.Downloading, Received: 12, Total: 10).Fraction);
    }

    [Fact]
    public async Task The_release_download_reports_its_bytes_against_the_feed_size()
    {
        var payload = new byte[300_000];
        Random.Shared.NextBytes(payload);
        var release = new DesktopRelease("1.0.0", null, "Astra.zip", "https://example.test/Astra.zip", payload.Length,
            Convert.ToHexStringLower(SHA256.HashData(payload)));
        using var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }));
        var seen = new List<TransferProgress>();
        var dir = Path.Combine(_home, "dl");

        await DesktopPayload.DownloadAsync(http, release, Path.Combine(dir, "Astra.zip"), progress: new ProgressSink<TransferProgress>(seen.Add));

        Assert.Equal(new TransferProgress(0, payload.Length), seen[0]);
        Assert.Equal(new TransferProgress(payload.Length, payload.Length), seen[^1]);
        Assert.True(seen.Zip(seen.Skip(1)).All(p => p.Second.Received >= p.First.Received));
    }

    [Fact]
    public async Task A_server_swap_reports_download_verify_install_and_restart()
    {
        using var t = new TempDir();
        var paths = new AstraPaths(Path.Combine(t.Path, "astra"));
        Directory.CreateDirectory(paths.Root);
        var old = Path.Combine(paths.Root, "bin", "astra-server");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, ServerStagingTests.FakeBinary);
        new InstallJson(old, "0.1.0").Write(paths.InstallFile);
        var seen = new List<UpdateProgress>();
        var manifest = new UpdateManifest("0.2.0", "1.0", Platforms: new Dictionary<string, UpdatePlatformInfo>
        {
            ["osx-arm64"] = new("@aidotnet/server-osx-arm64", null, ServerStagingTests.RealSha, null),
        });

        await ServerUpdater.ApplyAsync(new ServerUpdateOptions
        {
            Paths = paths, Manifest = manifest, PlatformKey = "osx-arm64", ServerPackage = "@aidotnet/server-osx-arm64", CurrentServerPath = old,
            CurrentVersion = "0.1.0", Control = new FakeControl { Running = true }, IsWindows = false,
            PackageSource = new FakePackageSource(() => ServerStagingTests.FakeBinary),
            RestartProbeInterval = TimeSpan.FromMilliseconds(5), RestartProbeTimeout = TimeSpan.FromSeconds(5),
            Progress = new ProgressSink<UpdateProgress>(seen.Add),
        });

        Assert.Equal(
            [UpdateStage.Downloading, UpdateStage.Downloading, UpdateStage.Verifying, UpdateStage.Installing, UpdateStage.Restarting],
            seen.Select(p => p.Stage));
        Assert.All(seen, p => Assert.Equal("0.2.0", p.Version));
        Assert.Equal(1.0, seen[1].Fraction);
    }
}
