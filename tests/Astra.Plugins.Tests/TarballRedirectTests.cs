using System.Net;
using System.Security.Cryptography;
using Astra.Plugins.Packages;

namespace Astra.Plugins.Tests;

/// <summary>A GitHub Release asset's address always redirects to its storage: the download follows it, carefully.</summary>
public sealed class TarballRedirectTests : IDisposable
{
    private const string Asset = "https://github.com/o/r/releases/download/x-v1/x-1.tgz";
    private const string Storage = "https://objects.example.test/signed/x-1.tgz?sig=abc";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "astra-redirect-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _bytes = TestTarballs.Make(("package/package.json", """{"name":"@t/x","version":"1"}"""));

    private string Sha => Convert.ToHexStringLower(SHA256.HashData(_bytes));
    private string Target => Path.Combine(_dir, "package.tgz");

    private static HttpResponseMessage Redirect(string to, HttpStatusCode code = HttpStatusCode.Found)
    {
        var res = new HttpResponseMessage(code);
        res.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return res;
    }

    [Fact]
    public async Task Follows_the_redirect_to_the_storage_and_checks_the_hash_there()
    {
        var http = new StubHttp(r => r.RequestUri!.AbsoluteUri == Asset ? Redirect(Storage) : StubHttp.Ok(_bytes));
        await new NpmTarballFetcher(http.Client).DownloadVerifiedAsync(Asset, Sha, null, Target);

        Assert.Equal([Asset, Storage], http.Requests);
        Assert.Equal(_bytes, await File.ReadAllBytesAsync(Target));
    }

    [Fact]
    public async Task A_relative_redirect_is_resolved_against_the_address_it_came_from()
    {
        var http = new StubHttp(r => r.RequestUri!.AbsolutePath == "/o/r/moved.tgz" ? StubHttp.Ok(_bytes) : Redirect("/o/r/moved.tgz", HttpStatusCode.TemporaryRedirect));
        await new NpmTarballFetcher(http.Client).DownloadVerifiedAsync(Asset, Sha, null, Target);

        Assert.Equal("https://github.com/o/r/moved.tgz", http.Requests[1]);
    }

    [Fact]
    public async Task Content_that_does_not_match_the_hash_is_refused_whichever_host_served_it()
    {
        var http = new StubHttp(r => r.RequestUri!.AbsoluteUri == Asset ? Redirect(Storage) : StubHttp.Ok(TestTarballs.Make(("package/other.txt", "tampered"))));
        await Assert.ThrowsAsync<InvalidDataException>(() => new NpmTarballFetcher(http.Client).DownloadVerifiedAsync(Asset, Sha, null, Target));

        Assert.False(File.Exists(Target));
    }

    [Fact]
    public async Task A_redirect_off_https_is_refused()
    {
        var http = new StubHttp(_ => Redirect("http://objects.example.test/x-1.tgz"));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => new NpmTarballFetcher(http.Client).DownloadVerifiedAsync(Asset, Sha, null, Target));

        Assert.Contains("isn't https", e.Message);
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task A_redirect_that_never_ends_is_given_up_on()
    {
        var http = new StubHttp(_ => Redirect(Storage));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => new NpmTarballFetcher(http.Client).DownloadVerifiedAsync(Asset, Sha, null, Target));

        Assert.Contains("too many times", e.Message);
        Assert.Equal(NpmTarballFetcher.MaxRedirects + 1, http.Requests.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
