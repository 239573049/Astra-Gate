using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Astra.Desktop.Core.Update;

namespace Astra.Desktop.Tests.Update;

/// <summary>Writes a fake platform package: bin/&lt;binary&gt; plus optional wwwroot and native libraries.</summary>
internal sealed class FakePackageSource(Func<string> binary, string? indexHtml = null, Dictionary<string, string>? libs = null, bool windows = false) : IServerPackageSource
{
    public int Fetches { get; private set; }
    public Exception? Fail { get; set; }

    public Task FetchAsync(string package, string version, string packageRoot, CancellationToken ct)
    {
        Fetches++;
        if (Fail is not null) throw Fail;
        var bin = Path.Combine(packageRoot, "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, ServerStaging.BinaryName(windows)), binary());
        if (indexHtml is not null)
        {
            Directory.CreateDirectory(Path.Combine(bin, "wwwroot"));
            File.WriteAllText(Path.Combine(bin, "wwwroot", "index.html"), indexHtml);
        }
        foreach (var (name, content) in libs ?? []) File.WriteAllText(Path.Combine(bin, name), content);
        return Task.CompletedTask;
    }
}

public class ServerStagingTests
{
    internal const string FakeBinary = "#!/bin/sh\necho astra-server\n";
    internal static readonly string RealSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(FakeBinary)));

    [Fact]
    public async Task Picks_up_native_companions_beside_the_binary()
    {
        using var t = new TempDir();
        var src = new FakePackageSource(() => FakeBinary, libs: new()
        {
            ["libe_sqlite3.dylib"] = "d", ["libsqlite3.so.0"] = "v", ["e_sqlite3.dll"] = "w", ["libe_sqlite3.dylib.tmp"] = "half", ["libx.dylib.prev"] = "p",
        });

        var staged = await ServerStaging.StageAsync(src, Path.Combine(t.Path, "staging"), "@aidotnet/server-osx-arm64", "0.2.0", null, false);

        var bin = Path.GetDirectoryName(staged.Path)!;
        Assert.Equal("0.2.0", staged.Version);
        Assert.Equal(new[] { "e_sqlite3.dll", "libe_sqlite3.dylib", "libsqlite3.so.0" }.Select(n => Path.Combine(bin, n)),
            staged.NativeLibraries.Order());
        Assert.EndsWith(Path.Combine("node_modules", "@aidotnet", "server-osx-arm64", "bin", "astra-server"), staged.Path);
    }

    [Fact]
    public async Task A_package_without_companions_stages_with_an_empty_list_and_a_log_line()
    {
        using var t = new TempDir();
        var logs = new List<string>();
        var staged = await ServerStaging.StageAsync(new FakePackageSource(() => FakeBinary), Path.Combine(t.Path, "s"), "@a/b", "1.0.0", RealSha.ToUpperInvariant(), false, logs.Add);
        Assert.Empty(staged.NativeLibraries);
        Assert.Contains(logs, l => l.Contains("no native companion libraries"));
    }

    [Fact]
    public async Task A_checksum_mismatch_wipes_staging()
    {
        using var t = new TempDir();
        var dir = Path.Combine(t.Path, "s");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServerStaging.StageAsync(new FakePackageSource(() => FakeBinary), dir, "@a/b", "1.0.0", "deadbeef", false));
        Assert.Contains("Checksum mismatch", e.Message);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task A_missing_binary_and_a_failed_download_wipe_staging()
    {
        using var t = new TempDir();
        var dir = Path.Combine(t.Path, "s");
        // A windows source writes astra-server.exe; asking for the unix name finds nothing.
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServerStaging.StageAsync(new FakePackageSource(() => FakeBinary, windows: true), dir, "@a/b", "1.0.0", null, false));
        Assert.Contains("does not contain a server binary", e.Message);
        Assert.False(Directory.Exists(dir));

        var failing = new FakePackageSource(() => FakeBinary) { Fail = new IOException("registry unreachable") };
        await Assert.ThrowsAsync<IOException>(() => ServerStaging.StageAsync(failing, dir, "@a/b", "1.0.0", null, false));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task Staging_is_wiped_before_each_run()
    {
        using var t = new TempDir();
        var dir = Path.Combine(t.Path, "s");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "stale"), "x");
        await ServerStaging.StageAsync(new FakePackageSource(() => FakeBinary), dir, "@a/b", "1.0.0", null, false);
        Assert.False(File.Exists(Path.Combine(dir, "stale")));
    }

    private static byte[] Tgz(params (string Name, string Content)[] files)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz, leaveOpen: true))
        {
            foreach (var (name, content) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
        }
        return ms.ToArray();
    }

    [Fact]
    public async Task The_npm_source_downloads_the_registry_tarball_and_strips_package_prefix()
    {
        using var t = new TempDir();
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Tgz(("package/package.json", "{}"), ("package/bin/astra-server", FakeBinary), ("package/bin/wwwroot/index.html", "ui"))) });
        using var http = new HttpClient(handler);

        var staged = await ServerStaging.StageAsync(new NpmRegistryPackageSource(http, "https://reg.example/"), Path.Combine(t.Path, "s"), "@aidotnet/server-osx-arm64", "0.2.0", RealSha, false);

        Assert.Equal("https://reg.example/@aidotnet/server-osx-arm64/-/server-osx-arm64-0.2.0.tgz", handler.Requests.Single().RequestUri!.ToString());
        Assert.Equal(FakeBinary, File.ReadAllText(staged.Path));
        Assert.Equal("ui", File.ReadAllText(Path.Combine(Path.GetDirectoryName(staged.Path)!, "wwwroot", "index.html")));
    }

    [Fact]
    public async Task The_npm_source_refuses_path_traversal_and_http_errors()
    {
        using var t = new TempDir();
        using var evil = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Tgz(("package/../../escape", "x"))) }));
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => new NpmRegistryPackageSource(evil).FetchAsync("@a/b", "1.0.0", Path.Combine(t.Path, "root"), default));
        Assert.Contains("outside its directory", e.Message);
        Assert.False(File.Exists(Path.Combine(t.Path, "escape")));

        using var missing = new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NpmRegistryPackageSource(missing).FetchAsync("@a/b", "1.0.0", Path.Combine(t.Path, "root"), default));
    }
}
