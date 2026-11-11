using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace Astra.Server.IntegrationTests;

/// <summary>The WebDAV copy of the provider backup (issue #33): set-up, test, upload, restore, and what must never leave.</summary>
public sealed class WebDavBackupTests
{
    private const string Key = "sk-webdav-secret-0123456789";
    private const string Password = "dav-pass-1234";

    /// <summary>An in-memory WebDAV folder: MKCOL / PUT / GET with Basic auth, recording every request.</summary>
    private sealed class FakeDav(string user = "alice", string password = Password) : HttpMessageHandler
    {
        public readonly Dictionary<string, byte[]> Files = new();
        public readonly List<(string Method, string Path, string? Auth)> Seen = [];
        public bool Redirect;
        private bool _folder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var auth = request.Headers.Authorization?.ToString();
            var path = request.RequestUri!.AbsolutePath;
            Seen.Add((request.Method.Method, path, auth));
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            if (auth != expected) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            if (Redirect) return new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.invalid/") } };
            switch (request.Method.Method)
            {
                case "MKCOL":
                    if (_folder) return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
                    _folder = true;
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "PUT":
                    Files[path] = await request.Content!.ReadAsByteArrayAsync(ct);
                    return new HttpResponseMessage(HttpStatusCode.Created);
                case "GET":
                    return Files.TryGetValue(path, out var bytes) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new HttpResponseMessage(HttpStatusCode.NotFound);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotImplemented);
            }
        }
    }

    private static Task<TestHost> StartAsync(FakeDav dav) =>
        TestHost.StartAsync(builder => builder.Services.AddHttpClient(WebDavBackupService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => dav));

    private static async Task SetUpAsync(TestHost host, string user = "alice", string password = Password)
    {
        var (status, _) = await host.SendAsync(HttpMethod.Put, "/api/backup/webdav", new { url = "https://dav.example.invalid/remote.php/dav/files/alice", username = user, password });
        Assert.Equal(HttpStatusCode.OK, status);
    }

    private static async Task CreateProviderAsync(TestHost host)
    {
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/providers", new
        {
            name = "Alpha", apiKey = Key, models = new[] { "gpt-5" }, endpoints = new[] { new { protocol = "openai-chat", baseUrl = "https://a.example.invalid/v1" } },
        });
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task The_Password_Is_Stored_Protected_And_Never_Returned()
    {
        var dav = new FakeDav();
        await using var host = await StartAsync(dav);
        await SetUpAsync(host);

        var (_, status) = await host.SendAsync(HttpMethod.Get, "/api/backup/webdav");
        Assert.True(status!["configured"]!.GetValue<bool>());
        Assert.True(status["hasPassword"]!.GetValue<bool>());
        Assert.Equal("astra", status["folder"]!.GetValue<string>());
        Assert.DoesNotContain(Password, status.ToJsonString());

        // a password left out keeps the stored one; a new login (user or address) drops it
        var (_, same) = await host.SendAsync(HttpMethod.Put, "/api/backup/webdav", new { url = "https://dav.example.invalid/remote.php/dav/files/alice/", username = "alice" });
        Assert.True(same!["hasPassword"]!.GetValue<bool>());
        var (_, other) = await host.SendAsync(HttpMethod.Put, "/api/backup/webdav", new { url = "https://dav.example.invalid/remote.php/dav/files/alice", username = "bob" });
        Assert.False(other!["hasPassword"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("ftp://dav.example.invalid/x", "astra")]
    [InlineData("https://user:pw@dav.example.invalid/x", "astra")]
    [InlineData("https://dav.example.invalid/x?y=1", "astra")]
    [InlineData("https://dav.example.invalid/x", "../up")]
    [InlineData("https://dav.example.invalid/x", "a/b")]
    public async Task A_Bad_Address_Or_Folder_Is_Refused(string url, string folder)
    {
        await using var host = await TestHost.StartAsync();
        var (status, _) = await host.SendAsync(HttpMethod.Put, "/api/backup/webdav", new { url, username = "alice", folder });
        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task Upload_Then_Restore_Round_Trips_Without_Keys_And_Sends_Basic_Auth()
    {
        var dav = new FakeDav();
        await using var source = await StartAsync(dav);
        await SetUpAsync(source);
        await CreateProviderAsync(source);

        var (status, test) = await source.SendAsync(HttpMethod.Post, "/api/backup/webdav/test");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(test!["ok"]!.GetValue<bool>());

        (status, var up) = await source.SendAsync(HttpMethod.Post, "/api/backup/webdav/upload", new { secrets = "none" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, up!["uploaded"]!.GetValue<int>());
        Assert.Contains(dav.Files.Keys, p => p.EndsWith("/astra/providers-latest.json"));
        Assert.Single(dav.Files.Keys, p => p.Contains("/astra/providers-2") && p.EndsWith("Z.json"));
        Assert.All(dav.Seen, s => Assert.StartsWith("Basic ", s.Auth));
        Assert.DoesNotContain(Key, Encoding.UTF8.GetString(dav.Files.Values.First()));

        await using var target = await StartAsync(dav);
        await SetUpAsync(target);
        (status, var file) = await target.SendAsync(HttpMethod.Post, "/api/backup/webdav/restore");
        Assert.Equal(HttpStatusCode.OK, status);
        (status, var preview) = await target.SendAsync(HttpMethod.Post, "/api/providers/backup/preview", new { file });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("new", preview!["items"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_Passphrase_Backup_May_Be_Uploaded_But_Plaintext_Never()
    {
        var dav = new FakeDav();
        await using var host = await StartAsync(dav);
        await SetUpAsync(host);
        await CreateProviderAsync(host);

        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/backup/webdav/upload", new { secrets = "plaintext" });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(dav.Files); // refused before anything was sent

        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/backup/webdav/upload", new { secrets = "passphrase", passphrase = "correct horse battery" });
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.All(dav.Files.Values, bytes => Assert.DoesNotContain(Key, Encoding.UTF8.GetString(bytes)));
    }

    [Fact]
    public async Task A_Refused_Login_Is_Reported_Without_The_Password()
    {
        var dav = new FakeDav();
        await using var host = await StartAsync(dav);
        await SetUpAsync(host, password: "not-the-password");

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/backup/webdav/test");
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("refused the login", body!["error"]!.GetValue<string>());
        Assert.DoesNotContain("not-the-password", body.ToJsonString());
        Assert.DoesNotContain("example.invalid", body.ToJsonString());
    }

    [Fact]
    public async Task A_Redirect_Is_Not_Followed_So_The_Login_Stays_With_The_Chosen_Host()
    {
        var dav = new FakeDav { Redirect = true };
        await using var host = await StartAsync(dav);
        await SetUpAsync(host);

        var (status, body) = await host.SendAsync(HttpMethod.Post, "/api/backup/webdav/test");
        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Contains("redirected", body!["error"]!.GetValue<string>());
        Assert.Single(dav.Seen); // the redirect target was never asked
    }

    [Fact]
    public async Task Restore_Without_A_Backup_Or_Without_Setup_Says_So()
    {
        var dav = new FakeDav();
        await using var host = await StartAsync(dav);
        var (status, _) = await host.SendAsync(HttpMethod.Post, "/api/backup/webdav/restore");
        Assert.Equal(HttpStatusCode.Conflict, status); // not set up

        await SetUpAsync(host);
        (status, _) = await host.SendAsync(HttpMethod.Post, "/api/backup/webdav/restore");
        Assert.Equal(HttpStatusCode.NotFound, status); // nothing uploaded yet
    }
}
