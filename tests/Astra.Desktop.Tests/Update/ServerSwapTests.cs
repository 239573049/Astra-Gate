using Astra.Desktop.Core.Update;

namespace Astra.Desktop.Tests.Update;

public class ServerSwapTests
{
    [Fact]
    public void Backup_copies_the_binary_and_snapshots_companions_into_a_libs_dir()
    {
        using var t = new TempDir();
        var current = Path.Combine(t.Path, "old", "astra-server");
        Directory.CreateDirectory(Path.GetDirectoryName(current)!);
        File.WriteAllText(current, "bin");
        File.WriteAllText(Path.Combine(t.Path, "old", "libe_sqlite3.dylib"), "sqlite-old");

        var backup = ServerSwap.BackupCurrentBinary(current, Path.Combine(t.Path, "backups"), "0.1.0", false);

        Assert.Equal(Path.Combine(t.Path, "backups", "server", "astra-server-0.1.0"), backup);
        Assert.Equal("bin", File.ReadAllText(backup!));
        Assert.Equal("sqlite-old", File.ReadAllText(Path.Combine(backup + "-libs", "libe_sqlite3.dylib")));
        Assert.Null(ServerSwap.BackupCurrentBinary(null, t.Path, "1", false));
        Assert.Null(ServerSwap.BackupCurrentBinary(Path.Combine(t.Path, "none"), t.Path, "1", false));
    }

    [Fact]
    public void Backup_without_a_version_uses_a_timestamp_and_windows_suffix()
    {
        using var t = new TempDir();
        var current = Path.Combine(t.Path, "astra-server.exe");
        File.WriteAllText(current, "bin");
        var backup = ServerSwap.BackupCurrentBinary(current, t.Path, null, true, now: () => DateTimeOffset.Parse("2026-10-26T10:00:00.123Z"));
        Assert.EndsWith("astra-server-2026-10-26T10-00-00-123Z.exe", backup);
    }

    [Fact]
    public void Prune_counts_a_backup_and_its_libs_as_one_entry()
    {
        using var t = new TempDir();
        foreach (var v in new[] { "0.1.0", "0.2.0", "0.3.0", "0.4.0" })
        {
            File.WriteAllText(Path.Combine(t.Path, $"astra-server-{v}"), "bin");
            Directory.CreateDirectory(Path.Combine(t.Path, $"astra-server-{v}-libs"));
            File.WriteAllText(Path.Combine(t.Path, $"astra-server-{v}-libs", "libe_sqlite3.dylib"), "lib");
        }

        ServerSwap.PruneBackups(t.Path, 2);

        Assert.Equal(new[] { "astra-server-0.3.0", "astra-server-0.3.0-libs", "astra-server-0.4.0", "astra-server-0.4.0-libs" },
            Directory.GetFileSystemEntries(t.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        ServerSwap.PruneBackups(Path.Combine(t.Path, "missing")); // tolerated
    }

    [Fact]
    public void Installing_a_managed_binary_leaves_no_temp_and_is_executable()
    {
        using var t = new TempDir();
        var layout = UpdateLayout.For(new Astra.Core.AstraPaths(t.Path));
        var staged = Path.Combine(t.Path, "staged");
        File.WriteAllText(staged, "new");

        var target = ServerSwap.InstallManagedBinary(staged, layout, "0.2.0", false);

        Assert.Equal(Path.Combine(t.Path, "server", "astra-server-0.2.0"), target);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.False(File.Exists(target + ".tmp"));
        if (!OperatingSystem.IsWindows()) Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(target));
    }

    [Fact]
    public void Companions_are_snapshotted_committed_restored_and_unchanged_ones_left_alone()
    {
        using var t = new TempDir();
        var serverDir = Path.Combine(t.Path, "server");
        Directory.CreateDirectory(serverDir);
        var staged = Path.Combine(t.Path, "libe_sqlite3.dylib");
        var same = Path.Combine(t.Path, "e_sqlite3.dll");
        File.WriteAllText(staged, "new");
        File.WriteAllText(same, "same");
        File.WriteAllText(Path.Combine(serverDir, "libe_sqlite3.dylib"), "old");
        File.WriteAllText(Path.Combine(serverDir, "e_sqlite3.dll"), "same");

        ServerSwap.InstallNativeCompanions([staged, same], serverDir);

        Assert.Equal("new", File.ReadAllText(Path.Combine(serverDir, "libe_sqlite3.dylib")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(serverDir, "libe_sqlite3.dylib.prev")));
        Assert.False(File.Exists(Path.Combine(serverDir, "e_sqlite3.dll.prev")));
        Assert.False(File.Exists(Path.Combine(serverDir, "libe_sqlite3.dylib.tmp")));

        ServerSwap.RestoreNativeCompanions(serverDir);
        Assert.Equal("old", File.ReadAllText(Path.Combine(serverDir, "libe_sqlite3.dylib")));
        Assert.False(File.Exists(Path.Combine(serverDir, "libe_sqlite3.dylib.prev")));

        ServerSwap.InstallNativeCompanions([staged], serverDir);
        ServerSwap.CommitNativeCompanions(serverDir);
        Assert.Equal("new", File.ReadAllText(Path.Combine(serverDir, "libe_sqlite3.dylib")));
        Assert.False(File.Exists(Path.Combine(serverDir, "libe_sqlite3.dylib.prev")));
    }

    [Fact]
    public void A_fresh_companion_with_no_previous_copy_leaves_no_snapshot()
    {
        using var t = new TempDir();
        var staged = Path.Combine(t.Path, "libe_sqlite3.so");
        File.WriteAllText(staged, "x");
        ServerSwap.InstallNativeCompanions([staged], Path.Combine(t.Path, "server"));
        Assert.Equal("x", File.ReadAllText(Path.Combine(t.Path, "server", "libe_sqlite3.so")));
        Assert.False(File.Exists(Path.Combine(t.Path, "server", "libe_sqlite3.so.prev")));
    }

    [Fact]
    public void WebRoot_swaps_with_a_prev_and_commits_or_restores()
    {
        using var t = new TempDir();
        var serverDir = Path.Combine(t.Path, "server");
        var staged = Path.Combine(t.Path, "pkg", "bin", "astra-server");
        Directory.CreateDirectory(Path.Combine(t.Path, "pkg", "bin", "wwwroot", "assets"));
        File.WriteAllText(Path.Combine(t.Path, "pkg", "bin", "wwwroot", "index.html"), "new-ui");
        File.WriteAllText(Path.Combine(t.Path, "pkg", "bin", "wwwroot", "assets", "a.js"), "js");
        Directory.CreateDirectory(Path.Combine(serverDir, "wwwroot"));
        File.WriteAllText(Path.Combine(serverDir, "wwwroot", "index.html"), "old-ui");

        Assert.True(ServerSwap.InstallManagedWebRoot(staged, serverDir));
        Assert.Equal("new-ui", File.ReadAllText(Path.Combine(serverDir, "wwwroot", "index.html")));
        Assert.Equal("js", File.ReadAllText(Path.Combine(serverDir, "wwwroot", "assets", "a.js")));
        Assert.Equal("old-ui", File.ReadAllText(Path.Combine(serverDir, "wwwroot.prev", "index.html")));

        ServerSwap.RestoreManagedWebRoot(serverDir);
        Assert.Equal("old-ui", File.ReadAllText(Path.Combine(serverDir, "wwwroot", "index.html")));
        Assert.False(Directory.Exists(Path.Combine(serverDir, "wwwroot.prev")));

        ServerSwap.InstallManagedWebRoot(staged, serverDir);
        ServerSwap.CommitManagedWebRoot(serverDir);
        Assert.False(Directory.Exists(Path.Combine(serverDir, "wwwroot.prev")));
        ServerSwap.RestoreManagedWebRoot(serverDir); // no .prev: no-op
        Assert.Equal("new-ui", File.ReadAllText(Path.Combine(serverDir, "wwwroot", "index.html")));
    }

    [Fact]
    public void A_package_without_wwwroot_touches_nothing()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Path, "server", "wwwroot"));
        Assert.False(ServerSwap.InstallManagedWebRoot(Path.Combine(t.Path, "pkg", "astra-server"), Path.Combine(t.Path, "server")));
        Assert.True(Directory.Exists(Path.Combine(t.Path, "server", "wwwroot")));
    }

    [Fact]
    public void Old_managed_binaries_are_pruned_oldest_version_first_and_never_the_current_one()
    {
        using var t = new TempDir();
        foreach (var v in new[] { "0.9.0", "0.10.0", "0.11.0", "0.12.0-preview.1", "0.12.0" })
            File.WriteAllText(Path.Combine(t.Path, $"astra-server-{v}"), v);
        File.WriteAllText(Path.Combine(t.Path, "wwwroot.prev"), "keep");
        File.WriteAllText(Path.Combine(t.Path, "libe_sqlite3.dylib"), "keep");

        ServerSwap.PruneManagedBinaries(t.Path, Path.Combine(t.Path, "astra-server-0.12.0"));

        Assert.Equal(new[] { "astra-server-0.11.0", "astra-server-0.12.0", "astra-server-0.12.0-preview.1", "libe_sqlite3.dylib", "wwwroot.prev" },
            Directory.GetFiles(t.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        ServerSwap.PruneManagedBinaries(Path.Combine(t.Path, "missing"), "x"); // tolerated
    }
}
