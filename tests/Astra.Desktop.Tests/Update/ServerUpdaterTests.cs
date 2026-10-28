using Astra.Core;
using Astra.Desktop.Core.Update;

namespace Astra.Desktop.Tests.Update;

internal sealed class FakeControl : IServerControl
{
    public bool Running;
    public int Stops, Starts;
    public string? VersionToReport = "0.2.0";
    public Func<Task<string?>>? Probe;
    public Func<Task>? OnStop;
    public Func<Task<bool>>? IsRunningOverride;

    public Task<bool> IsRunningAsync() => IsRunningOverride?.Invoke() ?? Task.FromResult(Running);
    public async Task StopAsync() { Stops++; if (OnStop is not null) await OnStop(); Running = false; }
    public Task StartAsync() { Starts++; Running = true; return Task.CompletedTask; }
    public Task<string?> ProbeVersionAsync() => Probe?.Invoke() ?? Task.FromResult(VersionToReport);
}

public sealed class ServerUpdaterTests : IDisposable
{
    private const string Pkg = "@aidotnet/server-osx-arm64";
    private static readonly string Sha = ServerStagingTests.RealSha;
    private readonly TempDir _tmp = new();

    public void Dispose() => _tmp.Dispose();

    private sealed class H
    {
        public required AstraPaths Paths;
        public required FakeControl Control;
        public required string OldBinary;
        public required UpdateLayout Layout;
        public required UpdateManifest Manifest;
        public Func<ServerUpdateOptions> Options = null!;
        public IServerPackageSource Source = new FakePackageSource(() => ServerStagingTests.FakeBinary);
        public bool Force;
    }

    private H Harness(string sub = "main", UpdateManifest? manifest = null, IServerPackageSource? source = null, bool force = false)
    {
        var paths = new AstraPaths(Path.Combine(_tmp.Path, sub, "astra"));
        Directory.CreateDirectory(paths.Root);
        var old = Path.Combine(paths.Root, "node-modules-bin", "astra-server");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);
        File.WriteAllText(old, ServerStagingTests.FakeBinary);
        new InstallJson(old, "0.1.0").Write(paths.InstallFile);
        var control = new FakeControl { Running = true };
        var m = manifest ?? new UpdateManifest("0.2.0", "1.0", Notes: "hello");
        var h = new H { Paths = paths, Control = control, OldBinary = old, Layout = UpdateLayout.For(paths), Manifest = m, Force = force };
        if (source is not null) h.Source = source;
        h.Options = () => new ServerUpdateOptions
        {
            Paths = paths, Manifest = m, PlatformKey = "osx-arm64", ServerPackage = Pkg, CurrentServerPath = old, CurrentVersion = "0.1.0",
            Control = control, PackageSource = h.Source, IsWindows = false, Force = h.Force,
            RestartProbeInterval = TimeSpan.FromMilliseconds(5), RestartProbeTimeout = TimeSpan.FromSeconds(5),
        };
        return h;
    }

    private static UpdateManifest WithSha(string? sha, string key = "osx-arm64")
        => new("0.2.0", "1.0", Platforms: new Dictionary<string, UpdatePlatformInfo> { [key] = new(Pkg, null, sha, null) });

    [Fact]
    public async Task End_to_end_success_stages_swaps_restarts_and_lands_idle()
    {
        var h = Harness(manifest: WithSha(Sha));

        var r = await ServerUpdater.ApplyAsync(h.Options());

        var managed = Path.Combine(h.Layout.ServerDir, "astra-server-0.2.0");
        Assert.Equal(new ServerUpdateResult("0.1.0", "0.2.0", managed), r);
        Assert.True(File.Exists(managed));
        var install = InstallJson.Read(h.Layout.InstallFile)!;
        Assert.Equal((managed, "0.2.0"), (install.ServerPath, install.ServerVersion));
        Assert.Equal((1, 1), (h.Control.Stops, h.Control.Starts));
        var state = UpdateState.Read(h.Layout.StateFile)!;
        Assert.Equal((UpdatePhase.Idle, 0), (state.Phase, state.FailedCount));
        Assert.False(Directory.Exists(h.Layout.StagingDir));
    }

    [Fact]
    public async Task The_running_binary_is_never_replaced_in_place()
    {
        var h = Harness();
        var before = File.ReadAllText(h.OldBinary);
        File.WriteAllText(h.OldBinary, "OLD-BYTES");
        var seen = new List<string>();
        h.Control.OnStop = () => { seen.Add(File.ReadAllText(h.OldBinary)); return Task.CompletedTask; };

        var r = await ServerUpdater.ApplyAsync(h.Options());

        Assert.NotEqual(h.OldBinary, r.ServerPath);
        Assert.Equal("OLD-BYTES", File.ReadAllText(h.OldBinary));
        Assert.Equal(["OLD-BYTES"], seen);
        Assert.NotEqual(before, File.ReadAllText(h.OldBinary));
    }

    [Fact]
    public async Task Persists_each_phase_to_the_state_file()
    {
        var h = Harness();
        var phases = new List<UpdatePhase>();
        h.Control.Probe = () => { phases.Add(UpdateState.Read(h.Layout.StateFile)!.Phase); return Task.FromResult<string?>("0.2.0"); };
        await ServerUpdater.ApplyAsync(h.Options());
        Assert.Equal([UpdatePhase.Restarting], phases);
    }

    [Fact]
    public async Task Keeps_a_rolling_backup_and_snapshots_previous_companions()
    {
        var h = Harness();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(h.OldBinary)!, "libe_sqlite3.dylib"), "sqlite-old");

        await ServerUpdater.ApplyAsync(h.Options());

        var dir = Path.Combine(h.Layout.BackupsDir, "server");
        Assert.Equal(["astra-server-0.1.0", "astra-server-0.1.0-libs"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal("sqlite-old", File.ReadAllText(Path.Combine(dir, "astra-server-0.1.0-libs", "libe_sqlite3.dylib")));
    }

    [Fact]
    public async Task Refuses_a_manifest_that_is_not_newer_or_api_incompatible_before_touching_anything()
    {
        var h = Harness(manifest: new UpdateManifest("0.1.0"));
        await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(h.Options()));
        Assert.Equal(0, h.Control.Starts);
        Assert.Equal(h.OldBinary, InstallJson.Read(h.Layout.InstallFile)!.ServerPath);
        Assert.False(File.Exists(h.Layout.StateFile));

        var h2 = Harness("api", new UpdateManifest("9.0.0", "2.0"));
        var e = await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(h2.Options()));
        Assert.Contains("API major", e.Message);
        Assert.Equal(h2.OldBinary, InstallJson.Read(h2.Layout.InstallFile)!.ServerPath);
    }

    [Fact]
    public async Task Falls_back_to_the_server_package_when_the_manifest_has_no_platform_entry()
    {
        string? asked = null;
        var h = Harness(source: new RecordingSource(p => asked = p));
        await ServerUpdater.ApplyAsync(h.Options());
        Assert.Equal(Pkg, asked);
    }

    private sealed class RecordingSource(Action<string> record) : IServerPackageSource
    {
        public Task FetchAsync(string package, string version, string packageRoot, CancellationToken ct)
        {
            record(package);
            return new FakePackageSource(() => ServerStagingTests.FakeBinary).FetchAsync(package, version, packageRoot, ct);
        }
    }

    [Fact]
    public async Task A_failed_download_or_checksum_marks_failed_without_a_rollback()
    {
        var failing = new FakePackageSource(() => ServerStagingTests.FakeBinary) { Fail = new IOException("registry unreachable") };
        var h = Harness(source: failing);
        var e = await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(h.Options()));
        Assert.Equal("registry unreachable", e.Message);
        Assert.Equal((UpdatePhase.Failed, 1), (UpdateState.Read(h.Layout.StateFile)!.Phase, UpdateState.Read(h.Layout.StateFile)!.FailedCount));
        Assert.Equal(h.OldBinary, InstallJson.Read(h.Layout.InstallFile)!.ServerPath);
        Assert.Equal(0, h.Control.Starts);

        var h2 = Harness("checksum", WithSha("deadbeef"));
        var e2 = await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(h2.Options()));
        Assert.Contains("Checksum mismatch", e2.Message);
        Assert.Equal(h2.OldBinary, InstallJson.Read(h2.Layout.InstallFile)!.ServerPath);
        Assert.Equal(1, UpdateState.Read(h2.Layout.StateFile)!.FailedCount);
        Assert.Equal(0, h2.Control.Stops + h2.Control.Starts);
    }

    [Fact]
    public async Task The_checksum_comes_from_the_entry_keyed_by_the_platform_key()
    {
        var m = new UpdateManifest("0.2.0", "1.0", Platforms: new Dictionary<string, UpdatePlatformInfo>
        {
            ["osx-arm64"] = new(Pkg, null, "deadbeef", null), ["darwin-arm64"] = new(Pkg, null, Sha, null),
        });
        var h = Harness(manifest: m);
        var e = await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(h.Options()));
        Assert.Contains("Checksum mismatch", e.Message);
    }

    [Fact]
    public async Task Rolls_back_to_the_previous_binary_when_the_restarted_server_reports_the_wrong_version()
    {
        var h = Harness();
        h.Control.VersionToReport = "0.1.0";

        var e = await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(h.Options()));

        Assert.Contains("reports 0.1.0", e.Message);
        Assert.Equal(h.OldBinary, InstallJson.Read(h.Layout.InstallFile)!.ServerPath);
        Assert.Equal(1, UpdateState.Read(h.Layout.StateFile)!.FailedCount);
        Assert.Equal(UpdatePhase.Failed, UpdateState.Read(h.Layout.StateFile)!.Phase);
        Assert.Equal((2, 2), (h.Control.Stops, h.Control.Starts)); // stop+start, then stop+start of the rollback
    }

    [Fact]
    public async Task Locks_out_after_two_consecutive_failures_until_forced()
    {
        var failing = new FakePackageSource(() => ServerStagingTests.FakeBinary) { Fail = new IOException("boom") };
        var h = Harness(source: failing);
        await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(h.Options()));
        await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(h.Options()));
        Assert.Equal(2, UpdateState.Read(h.Layout.StateFile)!.FailedCount);

        var locked = await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(h.Options()));
        Assert.Contains("2 update attempts failed", locked.Message);
        Assert.Equal("boom", locked.Hint);
        Assert.Equal(2, failing.Fetches);

        failing.Fail = null;
        h.Force = true;
        var r = await ServerUpdater.ApplyAsync(h.Options());
        Assert.Equal("0.2.0", r.To);
        var s = UpdateState.Read(h.Layout.StateFile)!;
        Assert.Equal((UpdatePhase.Idle, 0), (s.Phase, s.FailedCount));
    }

    [Fact]
    public async Task Waits_for_a_restarted_server_that_is_slow_to_report_its_version()
    {
        var h = Harness();
        var probes = 0;
        h.Control.Probe = () =>
        {
            probes++;
            if (probes == 1) throw new HttpRequestException("timeout");
            return Task.FromResult<string?>(probes == 2 ? null : "0.2.0");
        };
        await ServerUpdater.ApplyAsync(h.Options());
        Assert.Equal((3, 1), (probes, h.Control.Starts));
    }

    [Fact]
    public async Task Gives_up_when_the_restarted_server_never_answers()
    {
        var h = Harness();
        h.Control.VersionToReport = null;
        var o = h.Options();
        var e = await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(new ServerUpdateOptions
        {
            Paths = o.Paths, Manifest = o.Manifest, PlatformKey = o.PlatformKey, ServerPackage = o.ServerPackage, CurrentServerPath = o.CurrentServerPath,
            CurrentVersion = o.CurrentVersion, Control = o.Control, PackageSource = o.PackageSource, IsWindows = false,
            RestartProbeTimeout = TimeSpan.FromMilliseconds(50), RestartProbeInterval = TimeSpan.FromMilliseconds(5),
        }));
        Assert.Contains("reports no version", e.Message);
        Assert.Equal(h.OldBinary, InstallJson.Read(h.Layout.InstallFile)!.ServerPath);
    }

    [Fact]
    public async Task Stops_the_failed_server_on_rollback_even_when_it_does_not_look_running()
    {
        var h = Harness();
        h.Control.VersionToReport = "0.1.0";
        h.Control.IsRunningOverride = () => Task.FromResult(false);
        await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(h.Options()));
        Assert.Equal((1, 2), (h.Control.Stops, h.Control.Starts));
    }

    [Fact]
    public async Task Does_not_stop_a_server_that_was_not_running()
    {
        var h = Harness();
        h.Control.Running = false;
        await ServerUpdater.ApplyAsync(h.Options());
        Assert.Equal((0, 1), (h.Control.Stops, h.Control.Starts));
    }

    [Fact]
    public async Task Installs_the_package_wwwroot_and_restores_the_previous_one_on_rollback()
    {
        var h = Harness(source: new FakePackageSource(() => ServerStagingTests.FakeBinary, "new-ui"));
        Directory.CreateDirectory(Path.Combine(h.Layout.ServerDir, "wwwroot"));
        var index = Path.Combine(h.Layout.ServerDir, "wwwroot", "index.html");
        File.WriteAllText(index, "old-ui");
        await ServerUpdater.ApplyAsync(h.Options());
        Assert.Equal("new-ui", File.ReadAllText(index));
        Assert.False(Directory.Exists(Path.Combine(h.Layout.ServerDir, "wwwroot.prev")));

        var r = Harness("rb", source: new FakePackageSource(() => ServerStagingTests.FakeBinary, "new-ui"));
        r.Control.VersionToReport = "0.1.0";
        Directory.CreateDirectory(Path.Combine(r.Layout.ServerDir, "wwwroot"));
        var rIndex = Path.Combine(r.Layout.ServerDir, "wwwroot", "index.html");
        File.WriteAllText(rIndex, "old-ui");
        await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(r.Options()));
        Assert.Equal("old-ui", File.ReadAllText(rIndex));
        Assert.False(Directory.Exists(Path.Combine(r.Layout.ServerDir, "wwwroot.prev")));
    }

    [Fact]
    public async Task Leaves_an_existing_wwwroot_alone_when_the_package_ships_none()
    {
        var h = Harness();
        Directory.CreateDirectory(Path.Combine(h.Layout.ServerDir, "wwwroot"));
        var index = Path.Combine(h.Layout.ServerDir, "wwwroot", "index.html");
        File.WriteAllText(index, "old-ui");
        await ServerUpdater.ApplyAsync(h.Options());
        Assert.Equal("old-ui", File.ReadAllText(index));
    }

    private static FakePackageSource WithLib(string content) => new(() => ServerStagingTests.FakeBinary, libs: new() { ["libe_sqlite3.dylib"] = content });

    [Fact]
    public async Task Companions_are_replaced_only_after_the_server_stops_and_committed_on_success()
    {
        var h = Harness(source: WithLib("sqlite-new"));
        var lib = Path.Combine(h.Layout.ServerDir, "libe_sqlite3.dylib");
        Directory.CreateDirectory(h.Layout.ServerDir);
        File.WriteAllText(lib, "sqlite-old");
        string? seenWhileRunning = null;
        h.Control.OnStop = () => { seenWhileRunning = File.ReadAllText(lib); return Task.CompletedTask; };

        await ServerUpdater.ApplyAsync(h.Options());

        Assert.Equal("sqlite-old", seenWhileRunning);
        Assert.Equal("sqlite-new", File.ReadAllText(lib));
        Assert.False(File.Exists(lib + ".prev"));
        Assert.False(File.Exists(lib + ".tmp"));
    }

    [Fact]
    public async Task Previous_companions_come_back_on_rollback()
    {
        var h = Harness(source: WithLib("sqlite-new"));
        h.Control.VersionToReport = "0.1.0";
        var lib = Path.Combine(h.Layout.ServerDir, "libe_sqlite3.dylib");
        Directory.CreateDirectory(h.Layout.ServerDir);
        File.WriteAllText(lib, "sqlite-old");

        await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(h.Options()));

        Assert.Equal("sqlite-old", File.ReadAllText(lib));
        Assert.False(File.Exists(lib + ".prev"));
    }

    [Fact]
    public async Task Install_json_desktop_fields_survive_success_and_rollback()
    {
        var h = Harness();
        new InstallJson(h.OldBinary, "0.1.0", "/Apps/Astra.app", "0.1.0").Write(h.Layout.InstallFile);
        await ServerUpdater.ApplyAsync(h.Options());
        var ok = InstallJson.Read(h.Layout.InstallFile)!;
        Assert.Equal(("/Apps/Astra.app", "0.1.0", "0.2.0"), (ok.DesktopPath, ok.DesktopVersion, ok.ServerVersion));

        var r = Harness("rb");
        new InstallJson(r.OldBinary, "0.1.0", "/Apps/Astra.app", "0.1.0").Write(r.Layout.InstallFile);
        r.Control.VersionToReport = "0.1.0";
        await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(r.Options()));
        var back = InstallJson.Read(r.Layout.InstallFile)!;
        Assert.Equal((r.OldBinary, "/Apps/Astra.app"), (back.ServerPath, back.DesktopPath));
    }

    [Fact]
    public async Task Rollback_without_a_previous_managed_binary_removes_install_json()
    {
        var h = Harness();
        File.Delete(h.Layout.InstallFile);
        h.Control.VersionToReport = "0.1.0";
        var o = Bundled(h);

        await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(o));

        Assert.Null(InstallJson.Read(h.Layout.InstallFile));
        Assert.Equal(1, UpdateState.Read(h.Layout.StateFile)!.FailedCount);
        Assert.Equal(2, h.Control.Starts);
    }

    [Fact]
    public async Task Rollback_without_a_previous_managed_binary_keeps_desktop_fields_and_drops_the_server_pointer()
    {
        var h = Harness();
        new InstallJson(null, null, "/apps/Astra.app", "0.1.0").Write(h.Layout.InstallFile);
        h.Control.VersionToReport = "0.1.0";

        await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(Bundled(h)));

        var i = InstallJson.Read(h.Layout.InstallFile)!;
        Assert.Equal(("/apps/Astra.app", "0.1.0", null, null), (i.DesktopPath, i.DesktopVersion, i.ServerPath, i.ServerVersion));
    }

    /// <summary>The server ships inside the app: the caller passes no current path.</summary>
    private static ServerUpdateOptions Bundled(H h)
    {
        var o = h.Options();
        return new ServerUpdateOptions
        {
            Paths = o.Paths, Manifest = o.Manifest, PlatformKey = o.PlatformKey, ServerPackage = o.ServerPackage, CurrentServerPath = null,
            CurrentVersion = o.CurrentVersion, Control = o.Control, PackageSource = o.PackageSource, IsWindows = false,
            RestartProbeInterval = TimeSpan.FromMilliseconds(5), RestartProbeTimeout = TimeSpan.FromSeconds(1),
        };
    }

    [Fact]
    public async Task A_failed_rollback_leaves_the_state_failed_with_the_rollback_error()
    {
        var h = Harness();
        h.Control.VersionToReport = "0.1.0";
        var stops = 0;
        h.Control.OnStop = () => { if (++stops == 2) throw new InvalidOperationException("cannot stop"); return Task.CompletedTask; };

        await Assert.ThrowsAsync<UpdateException>(() => ServerUpdater.ApplyAsync(h.Options()));

        // The final write is the plain failure; the rollback failure was logged, and no second start was attempted.
        Assert.Equal(UpdatePhase.Failed, UpdateState.Read(h.Layout.StateFile)!.Phase);
        Assert.Equal(1, h.Control.Starts);
    }

    [Fact]
    public async Task Prunes_managed_binaries_after_the_swap()
    {
        var h = Harness();
        Directory.CreateDirectory(h.Layout.ServerDir);
        foreach (var v in new[] { "0.0.1", "0.0.2", "0.0.3" }) File.WriteAllText(Path.Combine(h.Layout.ServerDir, $"astra-server-{v}"), v);
        await ServerUpdater.ApplyAsync(h.Options());
        Assert.Equal(["astra-server-0.0.2", "astra-server-0.0.3", "astra-server-0.2.0"], Directory.GetFiles(h.Layout.ServerDir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }
}

public class LayoutTests
{
    [Fact]
    public void Layout_matches_paths_ts()
    {
        var l = UpdateLayout.For(new AstraPaths("/h/.astra"));
        Assert.Equal(Path.Combine("/h/.astra", "updates", "staging"), l.StagingDir);
        Assert.Equal(Path.Combine("/h/.astra", "backups"), l.BackupsDir);
        Assert.Equal(Path.Combine("/h/.astra", "server"), l.ServerDir);
        Assert.Equal(Path.Combine("/h/.astra", "update-state.json"), l.StateFile);
        Assert.Equal(Path.Combine("/h/.astra", "install.json"), l.InstallFile);
        Assert.EndsWith("astra-server-1.2.3.exe", l.ManagedBinaryPath("1.2.3", true));
        Assert.EndsWith("astra-server-1.2.3", l.ManagedBinaryPath("1.2.3", false));
        Assert.Matches("^(osx|win|linux)-(x64|arm64|x86|arm)$", PlatformKey.Current());
    }
}
