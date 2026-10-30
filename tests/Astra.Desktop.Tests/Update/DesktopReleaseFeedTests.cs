using Astra.Desktop.Core.Update;

namespace Astra.Desktop.Tests.Update;

public class UpdateTrackTests
{
    [Fact]
    public void A_dev_run_never_updates_itself()
        => Assert.Equal(UpdateTrack.Disabled, UpdateTracks.Resolve(true, "/Applications/Astra.app/Contents/MacOS/Astra", null, macOS: true));

    [Fact]
    public void The_clis_install_is_the_npm_track()
    {
        Assert.Equal(UpdateTrack.Npm, UpdateTracks.Resolve(false,
            "/Users/u/.astra/desktop/node_modules/@aidotnet/desktop-darwin-arm64/app/Astra.app/Contents/MacOS/Astra", null, macOS: true));
        Assert.Equal(UpdateTrack.Npm, UpdateTracks.Resolve(false, @"C:\Users\u\.astra\desktop\app\Astra.exe", @"C:\Users\u\.astra\desktop\app", macOS: false));
    }

    [Fact]
    public void A_packaged_app_is_dmg_or_installer_even_with_an_old_npm_record_in_install_json()
    {
        // The maintainer's machine on 2026-10-30: install.json still named the npm desktop of 0.6.0, the app ran from /Applications.
        const string stale = "/Users/u/.astra/desktop/node_modules/@aidotnet/desktop-darwin-arm64/app/Astra.app";
        Assert.Equal(UpdateTrack.Dmg, UpdateTracks.Resolve(false, "/Applications/Astra.app/Contents/MacOS/Astra", stale, macOS: true));
        Assert.Equal(UpdateTrack.Installer, UpdateTracks.Resolve(false, @"C:\Users\u\AppData\Local\Programs\Astra\Astra.exe", null, macOS: false));
        Assert.Equal(UpdateTrack.Installer, UpdateTracks.Resolve(false, "/tmp/.mount_AstraXYZ/usr/bin/astra", null, macOS: false));
        Assert.True(UpdateTrack.Dmg.UpdatesAsWholeApp());
        Assert.True(UpdateTrack.Installer.UpdatesAsWholeApp());
        Assert.False(UpdateTrack.Npm.UpdatesAsWholeApp());
        Assert.False(UpdateTrack.Disabled.UpdatesAsWholeApp());
    }
}

public class DesktopReleaseFeedTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string File(string platform, string kind, string name, string? sha = Sha)
        => $$"""{"name":"{{name}}","platform":"{{platform}}","kind":"{{kind}}","size":42,"sha256":{{(sha is null ? "null" : $"\"{sha}\"")}},"sha512":"x"}""";

    private static string Feed(params (string Version, string[] Files)[] versions)
        => $$"""{"channel":"preview","latest":null,"versions":[{{string.Join(",", versions.Select(v =>
            $$"""{"version":"{{v.Version}}","channel":"stable","notes":"notes {{v.Version}}","files":[{{string.Join(",", v.Files)}}]}"""))}}]}""";

    [Fact]
    public void Picks_the_newest_release_with_this_machines_file()
    {
        var json = Feed(
            ("0.13.2", [File("darwin-arm64", "zip", "Astra-0.13.2-darwin-arm64.zip")]),
            ("0.13.3", [File("darwin-arm64", "zip", "Astra-0.13.3-darwin-arm64.zip"), File("darwin-arm64", "dmg", "Astra-0.13.3-darwin-arm64.dmg")]),
            ("0.13.0-preview.1", [File("darwin-arm64", "zip", "Astra-0.13.0-preview.1-darwin-arm64.zip")]));
        var r = DesktopReleaseFeed.Pick(json, "https://feed.example/api/client-releases/", "preview", "darwin-arm64", "zip", "0.13.0-preview.1")!;
        Assert.Equal("0.13.3", r.Version);
        Assert.Equal("Astra-0.13.3-darwin-arm64.zip", r.FileName);
        Assert.Equal("https://feed.example/api/client-releases/preview/v/0.13.3/Astra-0.13.3-darwin-arm64.zip", r.Url);
        Assert.Equal(42, r.Size);
        Assert.Equal(Sha, r.Sha256);
        Assert.Equal("notes 0.13.3", r.Notes);
    }

    [Fact]
    public void A_newer_release_without_this_file_does_not_hide_an_older_complete_one()
    {
        var json = Feed(
            ("0.13.4", [File("win32-x64", "exe", "Astra-0.13.4-win32-x64.exe"), File("darwin-arm64", "zip", "Astra-0.13.4-darwin-arm64.zip", sha: null)]),
            ("0.13.3", [File("darwin-arm64", "zip", "Astra-0.13.3-darwin-arm64.zip")]));
        Assert.Equal("0.13.3", DesktopReleaseFeed.Pick(json, null, "stable", "darwin-arm64", "zip", "0.13.0")!.Version);
        Assert.Equal("0.13.4", DesktopReleaseFeed.Pick(json, null, "stable", "win32-x64", "exe", "0.13.0")!.Version);
        Assert.Null(DesktopReleaseFeed.Pick(json, null, "stable", "linux-x64", "appimage", "0.13.0"));
    }

    [Fact]
    public void Nothing_newer_is_null_and_the_default_feed_is_used_without_one()
    {
        var json = Feed(("0.13.3", [File("darwin-arm64", "zip", "Astra-0.13.3-darwin-arm64.zip")]));
        Assert.Null(DesktopReleaseFeed.Pick(json, null, "stable", "darwin-arm64", "zip", "0.13.3"));
        Assert.Null(DesktopReleaseFeed.Pick(json, null, "stable", "darwin-arm64", "zip", "0.14.0"));
        Assert.Equal("https://astra-gate.si/api/client-releases/stable/releases.json", DesktopReleaseFeed.ReleasesUrl(null, "nonsense"));
    }

    [Fact]
    public void A_broken_list_is_a_feed_error()
    {
        Assert.Throws<UpdateFeedException>(() => DesktopReleaseFeed.Pick("<html>", null, "stable", "darwin-arm64", "zip", "0.13.0"));
        Assert.Throws<UpdateFeedException>(() => DesktopReleaseFeed.Pick("{}", null, "stable", "darwin-arm64", "zip", "0.13.0"));
    }

    [Fact]
    public void Platform_tags_and_kinds_match_what_release_yml_uploads()
    {
        Assert.Matches("^(darwin|win32|linux)-(arm64|x64)$", DesktopReleaseFeed.PlatformTag());
        Assert.Contains(DesktopReleaseFeed.KindForThisOs(), new[] { "zip", "exe", "appimage" });
    }
}
