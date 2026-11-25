using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Server.Api;

namespace Astra.Server.Backup;

/// <summary>A file read from a sync target, with the version a conditional write must name (ETag, blob sha, size+mtime).</summary>
public sealed record RemoteFile(byte[] Bytes, string Version);

/// <summary>The conditional write failed because someone else wrote in between: merge again and retry.</summary>
public sealed class SyncPreconditionException() : Exception("The remote file changed in the meantime");

/// <summary>Where a sync (or a manual remote backup) keeps its file. One implementation per <see cref="SyncTargetKinds"/>.</summary>
public interface ISyncTarget
{
    string Describe { get; }

    /// <summary>The file, or null when there is none yet.</summary>
    Task<RemoteFile?> GetAsync(string name, CancellationToken ct);

    /// <summary>
    /// Writes the file only if it still has <paramref name="ifVersion"/> (null: only if it does not exist yet). Returns the new version.
    /// <see cref="SyncPreconditionException"/> when it moved on.
    /// </summary>
    Task<string> PutAsync(string name, byte[] bytes, string? ifVersion, CancellationToken ct);

    /// <summary>Checks that a file can be written and read back (and, for GitHub, that the repository is private).</summary>
    Task TestAsync(CancellationToken ct);
}

/// <summary>Builds the target for a configuration. The secret is the plain password / secret key / token.</summary>
public static class SyncTargets
{
    public const string HttpClientName = "astra-sync";
    public const int MaxBytes = 48 * 1024 * 1024;

    public static ISyncTarget Create(SyncTargetConfig c, string? secret, Func<HttpClient> http, Func<DateTimeOffset>? clock = null) => c.Kind switch
    {
        SyncTargetKinds.Folder => new FolderTarget(c),
        SyncTargetKinds.WebDav => new WebDavTarget(c, secret, http),
        SyncTargetKinds.S3 => new S3Target(c, secret, http, clock ?? (() => DateTimeOffset.UtcNow)),
        SyncTargetKinds.GitHub => new GitHubTarget(c, secret, http),
        _ => throw new AdminApiException(400, "Unknown sync target kind"),
    };

    /// <summary>Validates and normalises what the admin typed (never touches the network).</summary>
    public static void Validate(SyncTargetConfig c)
    {
        if (!SyncTargetKinds.All.Contains(c.Kind)) throw new AdminApiException(400, "The target must be folder, webdav, s3 or github");
        c.Url = c.Url.Trim();
        c.Folder = c.Folder.Trim().Trim('/');
        if (c.Folder.Length > 128 || c.Folder.Split('/').Any(p => p is ".." or "." || p.Any(ch => char.IsControl(ch) || ch is '\\' or ':' or '?' or '#')))
            throw new AdminApiException(400, "The folder is not valid");
        if (c.Username.Length > 200 || c.Username.Any(char.IsControl) || (c.Kind == SyncTargetKinds.WebDav && c.Username.Contains(':')))
            throw new AdminApiException(400, "Invalid user name");
        switch (c.Kind)
        {
            case SyncTargetKinds.Folder:
                if (!Path.IsPathRooted(c.Url)) throw new AdminApiException(400, "The folder must be an absolute path");
                break;
            case SyncTargetKinds.WebDav:
                if (!Uri.TryCreate(c.Url.TrimEnd('/'), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                    throw new AdminApiException(400, "The WebDAV address must be an http(s) URL without credentials, query or fragment");
                break;
            case SyncTargetKinds.S3:
                if (c.Bucket.Length is < 3 or > 63 || !c.Bucket.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-')) throw new AdminApiException(400, "The bucket name is not valid");
                if (c.Url.Length > 0 && !(Uri.TryCreate(c.Url.TrimEnd('/'), UriKind.Absolute, out var ep) && ep.Scheme is "http" or "https" && ep.UserInfo.Length == 0 && ep.Query.Length == 0))
                    throw new AdminApiException(400, "The endpoint must be an http(s) URL");
                if (c.Region.Length > 0 && !c.Region.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-')) throw new AdminApiException(400, "The region is not valid");
                break;
            case SyncTargetKinds.GitHub:
                var parts = c.Url.Split('/');
                if (parts.Length != 2 || parts.Any(p => p.Length == 0 || !p.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_')))
                    throw new AdminApiException(400, "The repository must be written owner/repo");
                if (c.Branch.Length > 200 || c.Branch.Any(ch => char.IsControl(ch) || ch == ' ')) throw new AdminApiException(400, "The branch is not valid");
                break;
        }
    }

    internal static async Task<HttpResponseMessage> SendAsync(Func<HttpClient> http, HttpRequestMessage request, string what, CancellationToken ct)
    {
        using var client = http();
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException) { throw new AdminApiException(502, $"Could not connect to {what}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AdminApiException(504, $"{what} did not answer in time"); }
    }

    internal static async Task<byte[]> ReadCappedAsync(HttpResponseMessage res, string what, CancellationToken ct)
    {
        if (res.Content.Headers.ContentLength > MaxBytes) throw new AdminApiException(502, $"The file on {what} is too large");
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes) throw new AdminApiException(502, $"The file on {what} is too large");
        }
        return buffer.ToArray();
    }

    internal static string ContentVersion(byte[] bytes) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes))[..32];

    internal static AdminApiException Failure(HttpResponseMessage res, string what) => res.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AdminApiException(502, $"{what} refused the login (HTTP {(int)res.StatusCode})"),
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther
            => new AdminApiException(502, $"{what} redirected the request; use its final address"),
        _ => new AdminApiException(502, $"{what} answered HTTP {(int)res.StatusCode}"),
    };

    internal static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>A directory on this machine (a Dropbox / iCloud / network folder). The version is size + modification time.</summary>
public sealed class FolderTarget(SyncTargetConfig c) : ISyncTarget
{
    public string Describe => c.Url;

    private string PathOf(string name) => Path.Combine(c.Url, c.Folder.Replace('/', Path.DirectorySeparatorChar), name);

    private static string VersionOf(string path)
    {
        var info = new FileInfo(path);
        return $"{info.Length}-{info.LastWriteTimeUtc.Ticks}";
    }

    public async Task<RemoteFile?> GetAsync(string name, CancellationToken ct)
    {
        var path = PathOf(name);
        if (!File.Exists(path)) return null;
        try
        {
            var version = VersionOf(path);
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.Length > SyncTargets.MaxBytes) throw new AdminApiException(502, "The file in the folder is too large");
            return new RemoteFile(bytes, version);
        }
        catch (IOException e) { throw new AdminApiException(502, "Could not read the folder: " + e.Message); }
    }

    public async Task<string> PutAsync(string name, byte[] bytes, string? ifVersion, CancellationToken ct)
    {
        var path = PathOf(name);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var exists = File.Exists(path);
            if (ifVersion is null ? exists : !exists || VersionOf(path) != ifVersion) throw new SyncPreconditionException();
            var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
            return VersionOf(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new AdminApiException(502, "Could not write to the folder: " + e.Message); }
    }

    public async Task TestAsync(CancellationToken ct)
    {
        await PutAsync(".astra-test", Encoding.UTF8.GetBytes("astra"), (await GetAsync(".astra-test", ct))?.Version, ct);
        if ((await GetAsync(".astra-test", ct))?.Bytes.SequenceEqual(Encoding.UTF8.GetBytes("astra")) != true) throw new AdminApiException(502, "The folder did not return what was written");
    }
}

/// <summary>A folder on a WebDAV server: Basic auth, <c>If-Match</c> with the ETag (a server without ETags falls back to comparing the content), no redirects, TLS validated.</summary>
public sealed class WebDavTarget(SyncTargetConfig c, string? secret, Func<HttpClient> http) : ISyncTarget
{
    private const string What = "the WebDAV server";
    public string Describe => c.Url;

    private string FolderPath() =>
        c.Folder.Length == 0 ? "" : string.Join('/', c.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString)) + "/";

    private string Url(string name) => $"{c.Url.TrimEnd('/')}/{FolderPath()}{Uri.EscapeDataString(name)}";

    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (c.Username.Length > 0 || secret is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.Username}:{secret}")));
        return request;
    }

    private static string? StrongEtag(HttpResponseMessage res) => res.Headers.ETag is { IsWeak: false } etag ? etag.Tag : null;

    public async Task<RemoteFile?> GetAsync(string name, CancellationToken ct)
    {
        using var res = await SyncTargets.SendAsync(http, Request(HttpMethod.Get, Url(name)), What, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw SyncTargets.Failure(res, What);
        var bytes = await SyncTargets.ReadCappedAsync(res, What, ct);
        return new RemoteFile(bytes, StrongEtag(res) ?? SyncTargets.ContentVersion(bytes));
    }

    public async Task<string> PutAsync(string name, byte[] bytes, string? ifVersion, CancellationToken ct)
    {
        await EnsureFolderAsync(ct);
        var blind = ifVersion is not null && ifVersion.StartsWith("sha256:", StringComparison.Ordinal);
        if (blind && (await GetAsync(name, ct)) is { } now && now.Version != ifVersion) throw new SyncPreconditionException();
        var conditional = ifVersion is null || !blind;
        using var request = Request(HttpMethod.Put, Url(name));
        request.Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } };
        if (ifVersion is null) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        else if (!blind) request.Headers.TryAddWithoutValidation("If-Match", ifVersion);
        using var res = await SyncTargets.SendAsync(http, request, What, ct);
        if (res.StatusCode is HttpStatusCode.PreconditionFailed) throw new SyncPreconditionException();
        if (conditional && res.StatusCode is HttpStatusCode.NotImplemented or HttpStatusCode.BadRequest)
        {
            // Some WebDAV servers do not support conditional PUT headers: check the current object first and write unconditionally.
            var current = await GetAsync(name, ct);
            if (ifVersion is null ? current is not null : current is null || current.Version != ifVersion)
                throw new SyncPreconditionException();
            using var plainReq = Request(HttpMethod.Put, Url(name));
            plainReq.Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } };
            using var plainRes = await SyncTargets.SendAsync(http, plainReq, What, ct);
            if (!plainRes.IsSuccessStatusCode) throw SyncTargets.Failure(plainRes, What);
            if (StrongEtag(plainRes) is { } plainEtag) return plainEtag;
            using var plainHead = await SyncTargets.SendAsync(http, Request(HttpMethod.Head, Url(name)), What, ct);
            return StrongEtag(plainHead) ?? SyncTargets.ContentVersion(bytes);
        }
        if (!res.IsSuccessStatusCode) throw SyncTargets.Failure(res, What);
        if (StrongEtag(res) is { } etag) return etag;
        using var head = await SyncTargets.SendAsync(http, Request(HttpMethod.Head, Url(name)), What, ct);
        return StrongEtag(head) ?? SyncTargets.ContentVersion(bytes);
    }

    private async Task EnsureFolderAsync(CancellationToken ct)
    {
        if (c.Folder.Length == 0) return;
        var baseUrl = c.Url.TrimEnd('/');
        var prefix = "";
        foreach (var segment in c.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            prefix += Uri.EscapeDataString(segment) + "/";
            using var res = await SyncTargets.SendAsync(http, Request(new HttpMethod("MKCOL"), $"{baseUrl}/{prefix}"), What, ct);
            if (res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.MethodNotAllowed) continue;
            throw SyncTargets.Failure(res, What);
        }
    }

    public async Task TestAsync(CancellationToken ct)
    {
        var probe = Encoding.UTF8.GetBytes("astra");
        var current = await GetAsync(".astra-test", ct);
        await PutAsync(".astra-test", probe, current?.Version, ct);
        if ((await GetAsync(".astra-test", ct))?.Bytes.SequenceEqual(probe) != true) throw new AdminApiException(502, "The WebDAV server did not return what was written");
    }
}

/// <summary>
/// An object in an S3 bucket (AWS, R2, MinIO, B2, …) over hand-written SigV4 (<see cref="S3Signer"/>). <c>If-Match</c> / <c>If-None-Match</c> go
/// outside the signature (a server that reads them back differently refuses the signed request with 403); a server that does not support them
/// gets a check of the object's ETag just before the write instead (a narrow window, as magpie documents). Port of magpie's <c>s3.go</c> (MIT).
/// </summary>
public sealed class S3Target(SyncTargetConfig c, string? secret, Func<HttpClient> http, Func<DateTimeOffset> clock) : ISyncTarget
{
    private const string What = "the S3 server";
    public string Describe => $"s3://{c.Bucket}/{c.Folder}";

    private string Region => c.Region.Length > 0 ? c.Region : "us-east-1";

    private string Key(string name) => (c.Folder.Length > 0 ? c.Folder + "/" : "") + name;

    /// <summary>Host, escaped path and base URL of an object, in path style or virtual-hosted style.</summary>
    internal (string Url, string Host, string Path) Locate(string name)
    {
        var key = S3Signer.Escape(Key(name), path: true);
        Uri endpoint = c.Url.Length > 0 ? new Uri(c.Url.TrimEnd('/')) : new Uri($"https://s3.{Region}.amazonaws.com");
        var authority = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}";
        var basePath = endpoint.AbsolutePath.TrimEnd('/');
        if (c.PathStyle || c.Bucket.Contains('.'))
        {
            var path = $"{basePath}/{c.Bucket}/{key}";
            return ($"{endpoint.Scheme}://{authority}{path}", authority, path);
        }
        var host = $"{c.Bucket}.{authority}";
        var p = $"{basePath}/{key}";
        return ($"{endpoint.Scheme}://{host}{p}", host, p);
    }

    private HttpRequestMessage Signed(HttpMethod method, string name, byte[]? body, IDictionary<string, string>? unsigned)
    {
        var (url, host, path) = Locate(name);
        var request = new HttpRequestMessage(method, url);
        var payload = S3Signer.Sha256Hex(body ?? []);
        var headers = new Dictionary<string, string> { ["x-amz-content-sha256"] = payload };
        var auth = S3Signer.Sign(method.Method, host, path, null, headers, payload, c.Username, secret ?? "", Region, "s3", clock(), out var amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payload);
        request.Headers.TryAddWithoutValidation("Authorization", auth);
        if (body is not null) request.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/octet-stream") } };
        if (unsigned is not null) foreach (var (k, v) in unsigned) request.Headers.TryAddWithoutValidation(k, v);
        return request;
    }

    public async Task<RemoteFile?> GetAsync(string name, CancellationToken ct)
    {
        using var res = await SyncTargets.SendAsync(http, Signed(HttpMethod.Get, name, null, null), What, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw SyncTargets.Failure(res, What);
        var bytes = await SyncTargets.ReadCappedAsync(res, What, ct);
        return new RemoteFile(bytes, res.Headers.ETag?.Tag ?? SyncTargets.ContentVersion(bytes));
    }

    private async Task<string?> HeadVersionAsync(string name, CancellationToken ct)
    {
        using var res = await SyncTargets.SendAsync(http, Signed(HttpMethod.Head, name, null, null), What, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw SyncTargets.Failure(res, What);
        return res.Headers.ETag?.Tag;
    }

    public async Task<string> PutAsync(string name, byte[] bytes, string? ifVersion, CancellationToken ct)
    {
        var condition = new Dictionary<string, string>();
        if (ifVersion is null) condition["If-None-Match"] = "*";
        else if (!ifVersion.StartsWith("sha256:", StringComparison.Ordinal)) condition["If-Match"] = ifVersion;
        using var res = await SyncTargets.SendAsync(http, Signed(HttpMethod.Put, name, bytes, condition), What, ct);
        if (res.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict) throw new SyncPreconditionException();
        if (res.StatusCode is HttpStatusCode.NotImplemented || (res.StatusCode == HttpStatusCode.BadRequest && condition.Count > 0))
        {
            // No conditional writes here: check the ETag just before an unconditional write.
            var current = await HeadVersionAsync(name, ct);
            if (ifVersion is null ? current is not null : current != ifVersion && !(ifVersion.StartsWith("sha256:", StringComparison.Ordinal))) throw new SyncPreconditionException();
            using var plain = await SyncTargets.SendAsync(http, Signed(HttpMethod.Put, name, bytes, null), What, ct);
            if (!plain.IsSuccessStatusCode) throw SyncTargets.Failure(plain, What);
            return plain.Headers.ETag?.Tag ?? SyncTargets.ContentVersion(bytes);
        }
        if (!res.IsSuccessStatusCode) throw SyncTargets.Failure(res, What);
        return res.Headers.ETag?.Tag ?? SyncTargets.ContentVersion(bytes);
    }

    public async Task TestAsync(CancellationToken ct)
    {
        var probe = Encoding.UTF8.GetBytes("astra");
        var current = await GetAsync(".astra-test", ct);
        await PutAsync(".astra-test", probe, current?.Version, ct);
        if ((await GetAsync(".astra-test", ct))?.Bytes.SequenceEqual(probe) != true) throw new AdminApiException(502, "The S3 server did not return what was written");
    }
}

/// <summary>
/// A private GitHub repository through the contents API with a fine-grained token (Contents read and write). A public repository is refused
/// (its history would hand every sealed version to anyone). The version is the blob sha; a write with a stale sha is a conflict. Port of
/// magpie's <c>github.go</c> in spirit (MIT).
/// </summary>
public sealed class GitHubTarget(SyncTargetConfig c, string? token, Func<HttpClient> http) : ISyncTarget
{
    private const string What = "GitHub";
    private const string Api = "https://api.github.com";
    public string Describe => "github://" + c.Url + (c.Folder.Length > 0 ? "/" + c.Folder : "");

    private HttpRequestMessage Request(HttpMethod method, string path, string? accept = null)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Accept.ParseAdd(accept ?? "application/vnd.github+json");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        request.Headers.UserAgent.ParseAdd("astra-sync");
        if (!string.IsNullOrEmpty(token)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private string ContentsPath(string name) =>
        $"/repos/{c.Url}/contents/{string.Join('/', Key(name).Split('/').Select(Uri.EscapeDataString))}";

    private string Key(string name) => (c.Folder.Length > 0 ? c.Folder + "/" : "") + name;

    private string RefQuery => c.Branch.Length > 0 ? "?ref=" + Uri.EscapeDataString(c.Branch) : "";

    public async Task<RemoteFile?> GetAsync(string name, CancellationToken ct)
    {
        using var res = await SyncTargets.SendAsync(http, Request(HttpMethod.Get, ContentsPath(name) + RefQuery), What, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw SyncTargets.Failure(res, What);
        if (JsonNode.Parse(await SyncTargets.ReadCappedAsync(res, What, ct)) is not JsonObject o) throw new AdminApiException(502, "GitHub answered with something unexpected");
        var sha = SyncTargets.Text(o["sha"]) ?? throw new AdminApiException(502, "GitHub answered with something unexpected");
        var content = SyncTargets.Text(o["content"]);
        if (!string.IsNullOrEmpty(content) && SyncTargets.Text(o["encoding"]) == "base64")
            return new RemoteFile(Convert.FromBase64String(content.Replace("\n", "").Replace("\r", "")), sha);
        // Over 1 MB the API sends no inline content: ask for the raw bytes.
        using var raw = await SyncTargets.SendAsync(http, Request(HttpMethod.Get, ContentsPath(name) + RefQuery, "application/vnd.github.raw+json"), What, ct);
        if (!raw.IsSuccessStatusCode) throw SyncTargets.Failure(raw, What);
        return new RemoteFile(await SyncTargets.ReadCappedAsync(raw, What, ct), sha);
    }

    public async Task<string> PutAsync(string name, byte[] bytes, string? ifVersion, CancellationToken ct)
    {
        var body = new JsonObject { ["message"] = "astra sync", ["content"] = Convert.ToBase64String(bytes) };
        if (ifVersion is not null) body["sha"] = ifVersion;
        if (c.Branch.Length > 0) body["branch"] = c.Branch;
        using var request = Request(HttpMethod.Put, ContentsPath(name));
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await SyncTargets.SendAsync(http, request, What, ct);
        // 409: the branch moved; 422: the sha is stale, or a file is there that this write did not know about.
        if (res.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity) throw new SyncPreconditionException();
        if (!res.IsSuccessStatusCode) throw SyncTargets.Failure(res, What);
        var o = JsonNode.Parse(await SyncTargets.ReadCappedAsync(res, What, ct)) as JsonObject;
        return SyncTargets.Text(o?["content"]?["sha"]) ?? throw new AdminApiException(502, "GitHub answered with something unexpected");
    }

    public async Task TestAsync(CancellationToken ct)
    {
        using var res = await SyncTargets.SendAsync(http, Request(HttpMethod.Get, $"/repos/{c.Url}"), What, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) throw new AdminApiException(502, "GitHub cannot find the repository, or the token has no access to it");
        if (!res.IsSuccessStatusCode) throw SyncTargets.Failure(res, What);
        var o = JsonNode.Parse(await SyncTargets.ReadCappedAsync(res, What, ct)) as JsonObject;
        if (o?["private"] is not JsonValue v || !v.TryGetValue<bool>(out var isPrivate) || !isPrivate)
            throw new AdminApiException(400, "The repository is public: use a private one (its history would hold every encrypted version)");
        var probe = Encoding.UTF8.GetBytes("astra");
        var cur = await GetAsync(".astra-test", ct);
        await PutAsync(".astra-test", probe, cur?.Version, ct);
    }
}
