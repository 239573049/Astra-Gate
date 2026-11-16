using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Server.Api;
using Astra.Server.Backup;

namespace Astra.Server.IntegrationTests;

/// <summary>The four sync targets against fake servers: conditional writes, versions, the request shapes each service needs, and what must never go on the wire.</summary>
public sealed class SyncTargetsTests
{
    private sealed class Fake(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, Dictionary<string, string> Headers, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Seen.Add((request.Method, request.RequestUri!.ToString(), headers, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Reply(HttpStatusCode code, string body = "", string? etag = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(body) };
        if (etag is not null) r.Headers.TryAddWithoutValidation("ETag", etag);
        return r;
    }

    private static Func<HttpClient> Client(Fake f) => () => new HttpClient(f, disposeHandler: false);

    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("sealed-bytes");

    // ---------------------------------------------------------------- WebDAV

    [Fact]
    public async Task WebDav_creates_with_if_none_match_and_updates_with_the_etag()
    {
        var fake = new Fake((req, _) => req.Method.Method switch
        {
            "MKCOL" => Reply(HttpStatusCode.Created),
            "PUT" when req.Headers.TryGetValues("If-Match", out var m) && m.Single() == "\"stale\"" => Reply(HttpStatusCode.PreconditionFailed),
            "PUT" => Reply(HttpStatusCode.Created, etag: "\"v2\""),
            _ => Reply(HttpStatusCode.NotFound),
        });
        var target = new WebDavTarget(new SyncTargetConfig { Kind = "webdav", Url = "https://dav.example.invalid/dav", Username = "me", Folder = "astra" }, "pw", Client(fake));
        Assert.Null(await target.GetAsync("f", default));

        Assert.Equal("\"v2\"", await target.PutAsync("f", Bytes, null, default));
        var create = fake.Seen.Single(s => s.Method == HttpMethod.Put);
        Assert.Equal("*", create.Headers["If-None-Match"]);
        Assert.Equal("https://dav.example.invalid/dav/astra/f", create.Url);
        Assert.StartsWith("Basic ", create.Headers["Authorization"]);

        await Assert.ThrowsAsync<SyncPreconditionException>(() => target.PutAsync("f", Bytes, "\"stale\"", default));
        Assert.Equal("\"v2\"", await target.PutAsync("f", Bytes, "\"v1\"", default));
    }

    [Fact]
    public async Task WebDav_refuses_a_redirect_and_a_login_failure_without_echoing_the_credentials()
    {
        var target = new WebDavTarget(new SyncTargetConfig { Kind = "webdav", Url = "https://dav.example.invalid", Username = "me", Folder = "" }, "pw", Client(new Fake((_, _) => Reply(HttpStatusCode.Found))));
        var ex = await Assert.ThrowsAsync<AdminApiException>(() => target.GetAsync("f", default));
        Assert.DoesNotContain("pw", ex.Message);
        var denied = new WebDavTarget(new SyncTargetConfig { Kind = "webdav", Url = "https://dav.example.invalid", Username = "me", Folder = "" }, "pw", Client(new Fake((_, _) => Reply(HttpStatusCode.Unauthorized))));
        Assert.Contains("refused the login", (await Assert.ThrowsAsync<AdminApiException>(() => denied.GetAsync("f", default))).Message);
    }

    // ---------------------------------------------------------------- S3

    private static S3Target S3(Fake f, SyncTargetConfig? c = null) =>
        new(c ?? new SyncTargetConfig { Kind = "s3", Bucket = "mybucket", Username = "AKIAEXAMPLE", Folder = "astra", Region = "eu-west-1" }, "secretkey", Client(f), () => new DateTimeOffset(2026, 11, 16, 1, 2, 3, TimeSpan.Zero));

    [Fact]
    public void S3_addresses_virtual_hosted_path_style_and_custom_endpoints()
    {
        var (aws, host, _) = S3(new Fake((_, _) => Reply(HttpStatusCode.OK))).Locate("f");
        Assert.Equal("mybucket.s3.eu-west-1.amazonaws.com", host);
        Assert.Equal("https://mybucket.s3.eu-west-1.amazonaws.com/astra/f", aws);

        var minio = new SyncTargetConfig { Kind = "s3", Bucket = "b1", Url = "http://nas.local:9000", PathStyle = true, Folder = "x y" };
        var located = S3(new Fake((_, _) => Reply(HttpStatusCode.OK)), minio).Locate("f.bin");
        Assert.Equal("http://nas.local:9000/b1/x%20y/f.bin", located.Url);
        Assert.Equal("nas.local:9000", located.Host);

        var r2 = new SyncTargetConfig { Kind = "s3", Bucket = "b1", Url = "https://acct.r2.cloudflarestorage.com", Region = "auto", Folder = "" };
        Assert.Equal("https://b1.acct.r2.cloudflarestorage.com/f", S3(new Fake((_, _) => Reply(HttpStatusCode.OK)), r2).Locate("f").Url);
    }

    [Fact]
    public async Task S3_signs_the_payload_but_sends_the_conditions_outside_the_signature()
    {
        var fake = new Fake((req, _) => req.Method == HttpMethod.Put ? Reply(HttpStatusCode.OK, etag: "\"e1\"") : Reply(HttpStatusCode.NotFound));
        var target = S3(fake);
        Assert.Equal("\"e1\"", await target.PutAsync("f", Bytes, null, default));
        var put = fake.Seen.Single();
        Assert.Equal("*", put.Headers["If-None-Match"]);
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date", put.Headers["Authorization"]);
        Assert.DoesNotContain("if-none-match", put.Headers["Authorization"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Credential=AKIAEXAMPLE/20261116/eu-west-1/s3/aws4_request", put.Headers["Authorization"]);
        Assert.Equal(S3Signer.Sha256Hex(Bytes), put.Headers["x-amz-content-sha256"]);
        Assert.DoesNotContain("secretkey", string.Join(" ", put.Headers.Values));
        Assert.Equal("20261116T010203Z", put.Headers["x-amz-date"]);
    }

    [Fact]
    public async Task S3_reports_a_lost_race_and_works_without_conditional_writes()
    {
        var lost = S3(new Fake((_, _) => Reply(HttpStatusCode.PreconditionFailed)));
        await Assert.ThrowsAsync<SyncPreconditionException>(() => lost.PutAsync("f", Bytes, "\"old\"", default));

        // A server that does not know If-Match: the ETag is checked just before an unconditional write.
        var current = "\"cur\"";
        var fake = new Fake((req, _) =>
        {
            if (req.Method == HttpMethod.Head) return Reply(HttpStatusCode.OK, etag: current);
            return req.Headers.Contains("If-Match") ? Reply(HttpStatusCode.NotImplemented) : Reply(HttpStatusCode.OK, etag: "\"new\"");
        });
        var target = S3(fake);
        Assert.Equal("\"new\"", await target.PutAsync("f", Bytes, "\"cur\"", default));
        await Assert.ThrowsAsync<SyncPreconditionException>(() => target.PutAsync("f", Bytes, "\"other\"", default));
    }

    // ---------------------------------------------------------------- GitHub

    private static GitHubTarget Gh(Fake f, string branch = "") =>
        new(new SyncTargetConfig { Kind = "github", Url = "me/private-sync", Folder = "astra", Branch = branch }, "ghp_token", Client(f));

    [Fact]
    public async Task GitHub_reads_the_blob_sha_and_writes_with_it()
    {
        var content = Convert.ToBase64String(Bytes);
        var fake = new Fake((req, body) =>
        {
            if (req.Method == HttpMethod.Get)
                return req.RequestUri!.AbsolutePath.EndsWith("/missing", StringComparison.Ordinal) ? Reply(HttpStatusCode.NotFound)
                    : Reply(HttpStatusCode.OK, $$"""{"sha":"abc123","encoding":"base64","content":"{{content[..4]}}\n{{content[4..]}}"}""");
            var o = JsonNode.Parse(body!)!;
            if (o["sha"]?.GetValue<string>() == "stale") return Reply(HttpStatusCode.UnprocessableEntity);
            return Reply(HttpStatusCode.OK, """{"content":{"sha":"def456"}}""");
        });
        var target = Gh(fake, "main");
        Assert.Null(await target.GetAsync("missing", default));
        var got = await target.GetAsync("f", default);
        Assert.Equal(Bytes, got!.Bytes);
        Assert.Equal("abc123", got.Version);
        Assert.Contains("contents/astra/f?ref=main", fake.Seen[1].Url);

        Assert.Equal("def456", await target.PutAsync("f", Bytes, "abc123", default));
        var put = fake.Seen.Last();
        Assert.Equal("abc123", JsonNode.Parse(put.Body!)!["sha"]!.GetValue<string>());
        Assert.Equal("main", JsonNode.Parse(put.Body!)!["branch"]!.GetValue<string>());
        Assert.Equal("Bearer ghp_token", put.Headers["Authorization"]);
        Assert.DoesNotContain("ghp_token", put.Url);
        await Assert.ThrowsAsync<SyncPreconditionException>(() => target.PutAsync("f", Bytes, "stale", default));
        // creating where a file already exists answers 422 as well: a conflict, not a failure
        var exists = Gh(new Fake((_, _) => Reply(HttpStatusCode.UnprocessableEntity)));
        await Assert.ThrowsAsync<SyncPreconditionException>(() => exists.PutAsync("f", Bytes, null, default));
    }

    [Fact]
    public async Task GitHub_falls_back_to_the_raw_bytes_for_a_large_file_and_refuses_a_public_repository()
    {
        var fake = new Fake((req, _) =>
        {
            if (req.Headers.Accept.Any(a => a.MediaType == "application/vnd.github.raw+json")) return Reply(HttpStatusCode.OK, "sealed-bytes");
            return Reply(HttpStatusCode.OK, """{"sha":"big","encoding":"none","content":""}""");
        });
        var got = await Gh(fake).GetAsync("f", default);
        Assert.Equal(Bytes, got!.Bytes);
        Assert.Equal("big", got.Version);

        var publicRepo = Gh(new Fake((_, _) => Reply(HttpStatusCode.OK, """{"private":false}""")));
        Assert.Contains("public", (await Assert.ThrowsAsync<AdminApiException>(() => publicRepo.TestAsync(default))).Message);
        var privateRepo = Gh(new Fake((req, _) => req.Method == HttpMethod.Put ? Reply(HttpStatusCode.OK, """{"content":{"sha":"x"}}""") : req.RequestUri!.AbsolutePath.EndsWith("private-sync", StringComparison.Ordinal) ? Reply(HttpStatusCode.OK, """{"private":true}""") : Reply(HttpStatusCode.NotFound)));
        await privateRepo.TestAsync(default);
    }

    // ---------------------------------------------------------------- folder + validation

    [Fact]
    public async Task A_folder_target_makes_the_conditional_write_with_size_and_time()
    {
        var dir = Path.Combine(Path.GetTempPath(), "astra-folder-" + Guid.NewGuid().ToString("N"));
        try
        {
            var target = new FolderTarget(new SyncTargetConfig { Kind = "folder", Url = dir, Folder = "sync" });
            Assert.Null(await target.GetAsync("f", default));
            var v1 = await target.PutAsync("f", Bytes, null, default);
            await Assert.ThrowsAsync<SyncPreconditionException>(() => target.PutAsync("f", Bytes, null, default));
            await Assert.ThrowsAsync<SyncPreconditionException>(() => target.PutAsync("f", Bytes, "0-0", default));
            Assert.Equal(Bytes, (await target.GetAsync("f", default))!.Bytes);
            Assert.NotEqual("", await target.PutAsync("f", Encoding.UTF8.GetBytes("longer sealed bytes"), v1, default));
            await target.TestAsync(default);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("folder", "relative/path", "", "")]
    [InlineData("webdav", "ftp://x", "", "")]
    [InlineData("webdav", "https://user:pw@x/", "", "")]
    [InlineData("s3", "", "ab", "")]
    [InlineData("s3", "ftp://e", "mybucket", "")]
    [InlineData("github", "not-a-repo", "", "")]
    [InlineData("github", "a/b/c", "", "")]
    [InlineData("github", "a/b", "", "bad branch")]
    [InlineData("other", "", "", "")]
    public void Targets_that_make_no_sense_are_refused_before_any_connection(string kind, string url, string bucket, string branch) =>
        Assert.Throws<AdminApiException>(() => SyncTargets.Validate(new SyncTargetConfig { Kind = kind, Url = url, Bucket = bucket, Branch = branch }));
}
