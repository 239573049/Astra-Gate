using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Astra.Desktop.Core.Update;

namespace Astra.Desktop.Tests.Update;

/// <summary>Records the commands an updater runs and answers them from a script.</summary>
internal sealed class FakeProcesses(Func<string, IReadOnlyList<string>, CommandResult>? answer = null) : IUpdateProcesses
{
    public List<(string File, IReadOnlyList<string> Args)> Ran { get; } = [];
    public List<(string File, IReadOnlyList<string> Args)> Started { get; } = [];

    public Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        Ran.Add((file, args));
        return Task.FromResult(answer?.Invoke(file, args) ?? new CommandResult(0, "", ""));
    }

    public void Start(string file, IReadOnlyList<string> args) => Started.Add((file, args));
}

public sealed class DesktopUpdaterTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("astra-desk-upd-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("new app bytes");
    private static string PayloadSha => Convert.ToHexStringLower(SHA256.HashData(Payload));

    private static DesktopRelease Release(string name, string? sha = null, long? size = null)
        => new("0.13.4", null, name, $"https://feed.example/stable/v/0.13.4/{name}", size ?? Payload.Length, sha ?? PayloadSha);

    private static HttpClient Serving(byte[] body) => new(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }));

    // ---------------------------------------------------------------- download

    [Fact]
    public async Task A_download_lands_only_when_size_and_sha256_match()
    {
        var dest = Path.Combine(_dir, "staging", "Astra.zip");
        await DesktopPayload.DownloadAsync(Serving(Payload), Release("Astra.zip"), dest);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(dest));

        var bad = Path.Combine(_dir, "staging", "bad.zip");
        var e = await Assert.ThrowsAsync<UpdateException>(() => DesktopPayload.DownloadAsync(Serving(Payload), Release("bad.zip", sha: new string('0', 64)), bad));
        Assert.Contains("Checksum mismatch", e.Message);
        Assert.False(File.Exists(bad));
        Assert.False(File.Exists(bad + ".part"));

        var cut = Path.Combine(_dir, "staging", "cut.zip");
        await Assert.ThrowsAsync<UpdateException>(() => DesktopPayload.DownloadAsync(Serving(Payload), Release("cut.zip", size: Payload.Length + 1), cut));
        Assert.False(File.Exists(cut));
    }

    [Fact]
    public async Task A_failed_request_is_an_update_error()
    {
        var http = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<UpdateException>(() => DesktopPayload.DownloadAsync(http, Release("x.zip"), Path.Combine(_dir, "x.zip")));
    }

    // ---------------------------------------------------------------- macOS

    private (string App, string Staging, string Log) MacLayout()
    {
        var app = Path.Combine(_dir, "Applications", "Astra.app");
        Directory.CreateDirectory(Path.Combine(app, "Contents", "MacOS"));
        return (app, Path.Combine(_dir, "home", "updates", "desktop"), Path.Combine(_dir, "home", "logs", "desktop-update.log"));
    }

    /// <summary>ditto "unpacks" an app; PlistBuddy and codesign answer as told.</summary>
    private static FakeProcesses MacTools(string bundleId = MacAppUpdater.BundleId, string currentTeam = "42B564FHYL", string newTeam = "42B564FHYL", bool newSigned = true)
        => new((file, args) =>
        {
            switch (Path.GetFileName(file))
            {
                case "ditto":
                    Directory.CreateDirectory(Path.Combine(args[3], "Astra.app", "Contents"));
                    return new CommandResult(0, "", "");
                case "PlistBuddy":
                    return new CommandResult(0, bundleId + "\n", "");
                case "codesign" when args[0] == "--verify":
                    return new CommandResult(args[^1].Contains("unpacked") && !newSigned ? 1 : 0, "", "");
                case "codesign":
                    return new CommandResult(0, "", $"Identifier=io.astra.desktop\nTeamIdentifier={(args[^1].Contains("unpacked") ? newTeam : currentTeam)}\n");
                default:
                    return new CommandResult(1, "", "unexpected");
            }
        });

    [Fact]
    public async Task Mac_stages_a_verified_app_and_hands_the_swap_to_a_shell()
    {
        var (app, staging, log) = MacLayout();
        var tools = MacTools();
        var updater = new MacAppUpdater(app, staging, log, Serving(Payload), tools);
        Assert.Null(updater.CannotSelfUpdateReason());

        await updater.ApplyAsync(Release("Astra-0.13.4-darwin-arm64.zip"));

        Assert.Equal(["ditto", "PlistBuddy", "codesign", "codesign", "codesign", "codesign"], tools.Ran.Select(r => Path.GetFileName(r.File)));
        var (file, args) = Assert.Single(tools.Started);
        Assert.Equal("/bin/sh", file);
        Assert.Equal(MacAppUpdater.SwapScript, args[1]);
        Assert.Equal([Environment.ProcessId.ToString(), app, Path.Combine(staging, "unpacked", "Astra.app"), log], args.Skip(3));
        // The zip is gone once unpacked; the app it carried waits for the hand-over.
        Assert.False(File.Exists(Path.Combine(staging, "Astra-0.13.4-darwin-arm64.zip")));
    }

    [Theory]
    [InlineData("io.example.other", "42B564FHYL", true, "not an Astra app")]
    [InlineData(MacAppUpdater.BundleId, "OTHERTEAM1", true, "another team")]
    [InlineData(MacAppUpdater.BundleId, "42B564FHYL", false, "not validly signed")]
    public async Task Mac_refuses_an_app_gatekeeper_or_the_user_would_not_trust(string bundleId, string newTeam, bool newSigned, string why)
    {
        var (app, staging, log) = MacLayout();
        var tools = MacTools(bundleId, newTeam: newTeam, newSigned: newSigned);
        var e = await Assert.ThrowsAsync<UpdateException>(() => new MacAppUpdater(app, staging, log, Serving(Payload), tools).ApplyAsync(Release("Astra.zip")));
        Assert.Contains(why, e.Message);
        Assert.Empty(tools.Started);
    }

    [Fact]
    public void Mac_outside_an_app_bundle_cannot_update_itself()
        => Assert.NotNull(new MacAppUpdater(Path.Combine(_dir, "bin"), _dir, "log", Serving(Payload), new FakeProcesses()).CannotSelfUpdateReason());

    /// <summary>The swap shell itself, on dummy apps: waits for the pid, swaps, and opens the result (a fake <c>open</c> on PATH records it).</summary>
    [Fact]
    public async Task Mac_swap_script_replaces_the_app_after_the_process_exits()
    {
        if (OperatingSystem.IsWindows()) return;
        var (app, staging, log) = MacLayout();
        await File.WriteAllTextAsync(Path.Combine(app, "Contents", "marker"), "old");
        var staged = Path.Combine(staging, "unpacked", "Astra.app");
        Directory.CreateDirectory(Path.Combine(staged, "Contents"));
        await File.WriteAllTextAsync(Path.Combine(staged, "Contents", "marker"), "new");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);

        using var waitFor = Process.Start("/bin/sh", ["-c", "sleep 0.5"])!;
        var (code, opened) = await RunScript(MacAppUpdater.SwapScript, waitFor.Id.ToString(), app, staged, log);

        Assert.Equal(0, code);
        Assert.True(waitFor.HasExited);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(app, "Contents", "marker")));
        Assert.False(Directory.Exists(staged));
        Assert.Equal(["Astra.app"], Directory.GetFileSystemEntries(Path.GetDirectoryName(app)!).Select(Path.GetFileName));
        Assert.Equal(app, opened);
        Assert.Contains("swapped", await File.ReadAllTextAsync(log));
    }

    [Fact]
    public async Task Mac_swap_script_keeps_the_old_app_when_the_new_one_is_missing()
    {
        if (OperatingSystem.IsWindows()) return;
        var (app, _, log) = MacLayout();
        await File.WriteAllTextAsync(Path.Combine(app, "Contents", "marker"), "old");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);

        var (code, opened) = await RunScript(MacAppUpdater.SwapScript, "999999", app, Path.Combine(_dir, "nothing", "Astra.app"), log);

        Assert.NotEqual(0, code);
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(app, "Contents", "marker")));
        Assert.Equal(app, opened);
    }

    private async Task<(int Code, string Opened)> RunScript(string script, params string[] args)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("the hand-over shells run on macOS and Linux");
        var bin = Path.Combine(_dir, "fakebin");
        Directory.CreateDirectory(bin);
        var opened = Path.Combine(_dir, "opened");
        var open = Path.Combine(bin, "open");
        await File.WriteAllTextAsync(open, $"#!/bin/sh\nprintf '%s' \"$1\" > '{opened}'\n");
        File.SetUnixFileMode(open, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var psi = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (var a in (string[])["-c", script, "astra-update", .. args]) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = $"{bin}:/usr/bin:/bin";
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (p.ExitCode, File.Exists(opened) ? await File.ReadAllTextAsync(opened) : "");
    }

    // ---------------------------------------------------------------- Windows

    [Fact]
    public async Task Windows_runs_the_verified_installer_silently_once_the_app_has_exited()
    {
        var appDir = Path.Combine(_dir, "Programs", "Astra");
        Directory.CreateDirectory(appDir);
        var staging = Path.Combine(_dir, "updates", "desktop");
        var tools = new FakeProcesses();
        var updater = new WindowsInstallerUpdater(appDir, staging, Serving(Payload), tools);
        Assert.NotNull(updater.CannotSelfUpdateReason());
        await File.WriteAllTextAsync(Path.Combine(appDir, WindowsInstallerUpdater.Uninstaller), "");
        Assert.Null(updater.CannotSelfUpdateReason());

        await updater.ApplyAsync(Release("Astra-0.13.4-win32-x64.exe"));

        var installer = Path.Combine(staging, "Astra-0.13.4-win32-x64.exe");
        Assert.Equal(Payload, await File.ReadAllBytesAsync(installer));
        var (file, args) = Assert.Single(tools.Started);
        Assert.EndsWith("powershell.exe", file);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(args[^1]));
        Assert.Equal(WindowsInstallerUpdater.HandOver(Environment.ProcessId, installer), script);
        Assert.StartsWith($"Wait-Process -Id {Environment.ProcessId} ", script);
        Assert.EndsWith("-ArgumentList '/S','--updated','--force-run'", script);
    }

    [Fact]
    public void Windows_hand_over_quotes_paths_for_powershell()
        => Assert.Contains("-FilePath 'C:\\Users\\O''Brien\\setup.exe'", WindowsInstallerUpdater.HandOver(1, @"C:\Users\O'Brien\setup.exe"));

    // ---------------------------------------------------------------- Linux

    [Fact]
    public async Task AppImage_is_replaced_in_place_and_started_after_the_app_exits()
    {
        if (OperatingSystem.IsWindows()) return;
        var appImage = Path.Combine(_dir, "Astra-0.13.0-x86_64.AppImage");
        await File.WriteAllTextAsync(appImage, "old");
        var tools = new FakeProcesses();
        var updater = new AppImageUpdater(appImage, Serving(Payload), tools);
        Assert.Null(updater.CannotSelfUpdateReason());

        await updater.ApplyAsync(Release("Astra-0.13.4-linux-x64.AppImage"));

        Assert.Equal(Payload, await File.ReadAllBytesAsync(appImage));
        Assert.True(File.GetUnixFileMode(appImage).HasFlag(UnixFileMode.UserExecute));
        Assert.Equal(["Astra-0.13.0-x86_64.AppImage"], Directory.GetFiles(_dir).Select(Path.GetFileName));
        var (file, args) = Assert.Single(tools.Started);
        Assert.Equal("/bin/sh", file);
        Assert.Equal([AppImageUpdater.RelaunchScript, "astra-update", Environment.ProcessId.ToString(), appImage], args.Skip(1));
    }

    [Fact]
    public async Task AppImage_keeps_the_old_file_when_the_download_is_bad()
    {
        if (OperatingSystem.IsWindows()) return;
        var appImage = Path.Combine(_dir, "Astra.AppImage");
        await File.WriteAllTextAsync(appImage, "old");
        var tools = new FakeProcesses();
        await Assert.ThrowsAsync<UpdateException>(() => new AppImageUpdater(appImage, Serving(Payload), tools).ApplyAsync(Release("x", sha: new string('1', 64))));
        Assert.Equal("old", await File.ReadAllTextAsync(appImage));
        Assert.Equal(["Astra.AppImage"], Directory.GetFiles(_dir).Select(Path.GetFileName));
        Assert.Empty(tools.Started);
    }

    [Fact]
    public void Without_APPIMAGE_there_is_nothing_to_replace()
        => Assert.NotNull(new AppImageUpdater(null, Serving(Payload), new FakeProcesses()).CannotSelfUpdateReason());
}
