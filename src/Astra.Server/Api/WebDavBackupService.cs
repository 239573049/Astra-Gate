using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Data;

namespace Astra.Server.Api;

/// <summary>The WebDAV target of provider backups, stored under the settings key <see cref="WebDavBackupService.StorageKey"/>.</summary>
public sealed class WebDavSettings
{
    public string Url { get; set; } = "";
    public string Username { get; set; } = "";

    /// <summary>The password, sealed by <see cref="ISecretProtector"/>; never returned by the API.</summary>
    public string? PasswordProtected { get; set; }

    /// <summary>Folder (one path segment) under <see cref="Url"/> that holds the backup files.</summary>
    public string Folder { get; set; } = WebDavBackupService.DefaultFolder;
}

/// <summary>
/// Manual cloud backup of the provider backup file over WebDAV (issue #33, <c>.plan/provider-backup.md</c>). Upload writes
/// <c>providers-latest.json</c> and a timestamped copy; restore reads the latest one and hands it to the ordinary preview and
/// import, so nothing is overwritten silently. Only a file without keys or with passphrase-sealed keys is ever uploaded (plaintext
/// keys must not land on a third party's server). No directory listing, no redirects (the credentials must not follow one), TLS
/// is validated, and the credentials never appear in an error. Everything but reading the status is for this machine only (the
/// endpoints check): the admin chooses an address Astra then connects to.
/// </summary>
public sealed class WebDavBackupService(AstraDatabase db, ProviderBackupService backup, ISecretProtector secrets, IHttpClientFactory http, ILogger<WebDavBackupService> log)
{
    public const string StorageKey = "webdav_backup";
    public const string HttpClientName = "astra-webdav";
    public const string DefaultFolder = "astra";
    public const string LatestName = "providers-latest.json";
    private const int MaxBytes = 16 * 1024 * 1024;

    public async Task<JsonObject> StatusAsync(CancellationToken ct)
    {
        var s = await LoadAsync(ct);
        return new JsonObject
        {
            ["configured"] = s.Url.Length > 0, ["url"] = s.Url, ["username"] = s.Username, ["hasPassword"] = s.PasswordProtected is not null, ["folder"] = s.Folder,
        };
    }

    /// <summary>Saves the address and login. An omitted password keeps the stored one, unless the address or user changed (then it is dropped).</summary>
    public async Task<JsonObject> SaveAsync(JsonObject body, CancellationToken ct)
    {
        var current = await LoadAsync(ct);
        var url = Text(body["url"])?.Trim().TrimEnd('/') ?? "";
        var username = Text(body["username"])?.Trim() ?? "";
        var folder = Text(body["folder"])?.Trim() is { Length: > 0 } f ? f : DefaultFolder;
        if (url.Length == 0) throw new AdminApiException(400, "The WebDAV address is required");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0)
            throw new AdminApiException(400, "The WebDAV address must be an http(s) URL without credentials, query or fragment");
        if (folder.Length > 64 || !folder.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') || folder is "." or "..")
            throw new AdminApiException(400, "The folder may only contain letters, digits, '.', '_' and '-'");
        if (username.Length > 200 || username.Any(char.IsControl) || username.Contains(':')) throw new AdminApiException(400, "Invalid user name");

        var sameLogin = string.Equals(current.Url, url, StringComparison.Ordinal) && string.Equals(current.Username, username, StringComparison.Ordinal);
        var next = new WebDavSettings { Url = url, Username = username, Folder = folder, PasswordProtected = sameLogin ? current.PasswordProtected : null };
        if (Text(body["password"]) is { Length: > 0 } password)
        {
            if (password.Any(char.IsControl) || password.Length > 500) throw new AdminApiException(400, "Invalid password");
            next.PasswordProtected = secrets.Protect(password);
        }
        await db.Settings.SetAsync(StorageKey, next, ct);
        return await StatusAsync(ct);
    }

    public async Task ForgetAsync(CancellationToken ct) => await db.Settings.SetAsync(StorageKey, new WebDavSettings(), ct);

    /// <summary>Creates the folder and checks that a file can be written and read back: what "back up" will need.</summary>
    public async Task<JsonObject> TestAsync(CancellationToken ct)
    {
        var s = await RequireAsync(ct);
        await EnsureFolderAsync(s, ct);
        var probe = Encoding.UTF8.GetBytes("astra");
        await PutAsync(s, ".astra-test", probe, ct);
        var back = await GetAsync(s, ".astra-test", ct);
        if (back is null || !back.SequenceEqual(probe)) throw new AdminApiException(502, "The WebDAV server did not return what was written");
        return new JsonObject { ["ok"] = true };
    }

    public async Task<JsonObject> UploadAsync(JsonObject body, CancellationToken ct)
    {
        var mode = Text(body["secrets"]) ?? ProviderBackupService.Modes.None;
        if (mode == ProviderBackupService.Modes.Plaintext)
            throw new AdminApiException(400, "Plaintext keys are never uploaded; use no keys or a passphrase");
        var s = await RequireAsync(ct);
        var file = await backup.ExportAsync(body, local: true, ct);
        var bytes = Encoding.UTF8.GetBytes(file.ToJsonString());
        if (bytes.Length > MaxBytes) throw new AdminApiException(400, "The backup is too large to upload");
        await EnsureFolderAsync(s, ct);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'");
        await PutAsync(s, $"providers-{stamp}.json", bytes, ct);
        await PutAsync(s, LatestName, bytes, ct);
        log.LogInformation("Provider backup uploaded to WebDAV: {Count} provider(s), secrets={Mode}", (file["providers"] as JsonArray)?.Count ?? 0, mode);
        return new JsonObject { ["uploaded"] = (file["providers"] as JsonArray)?.Count ?? 0, ["name"] = $"providers-{stamp}.json" };
    }

    /// <summary>The latest uploaded backup, as the file the preview / import calls take. Nothing is written locally.</summary>
    public async Task<JsonObject> LatestAsync(CancellationToken ct)
    {
        var s = await RequireAsync(ct);
        var bytes = await GetAsync(s, LatestName, ct) ?? throw new AdminApiException(404, "There is no backup on the WebDAV server yet");
        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject file) throw new FormatException();
            if (Text(file["secrets"]) == ProviderBackupService.Modes.Plaintext) throw new AdminApiException(400, "The backup on the server holds plaintext keys; it was not made by Astra");
            return file;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException)
        {
            throw new AdminApiException(502, "The file on the WebDAV server is not a backup");
        }
    }

    // ------------------------------------------------------------------ WebDAV

    private async Task<WebDavSettings> LoadAsync(CancellationToken ct) => await db.Settings.GetAsync<WebDavSettings>(StorageKey, ct) ?? new WebDavSettings();

    private async Task<WebDavSettings> RequireAsync(CancellationToken ct)
    {
        var s = await LoadAsync(ct);
        if (s.Url.Length == 0) throw new AdminApiException(409, "WebDAV is not set up yet");
        return s;
    }

    private string FolderUrl(WebDavSettings s) => $"{s.Url}/{s.Folder}";

    private HttpRequestMessage Request(WebDavSettings s, HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (s.Username.Length > 0 || s.PasswordProtected is not null)
        {
            string? password = null;
            if (s.PasswordProtected is not null)
            {
                try { password = secrets.Unprotect(s.PasswordProtected); }
                catch (System.Security.Cryptography.CryptographicException) { throw new AdminApiException(409, "The stored WebDAV password cannot be read; enter it again"); }
            }
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{s.Username}:{password}")));
        }
        return request;
    }

    /// <summary>MKCOL; an existing folder (405) is fine. Parent folders are not created: the address must already exist.</summary>
    private async Task EnsureFolderAsync(WebDavSettings s, CancellationToken ct)
    {
        using var res = await SendAsync(Request(s, new HttpMethod("MKCOL"), FolderUrl(s) + "/"), ct);
        if (res.IsSuccessStatusCode || res.StatusCode == HttpStatusCode.MethodNotAllowed) return;
        throw Failure(res);
    }

    private async Task PutAsync(WebDavSettings s, string name, byte[] bytes, CancellationToken ct)
    {
        using var request = Request(s, HttpMethod.Put, $"{FolderUrl(s)}/{name}");
        request.Content = new ByteArrayContent(bytes) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
        using var res = await SendAsync(request, ct);
        if (!res.IsSuccessStatusCode) throw Failure(res);
    }

    private async Task<byte[]?> GetAsync(WebDavSettings s, string name, CancellationToken ct)
    {
        using var res = await SendAsync(Request(s, HttpMethod.Get, $"{FolderUrl(s)}/{name}"), ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw Failure(res);
        if (res.Content.Headers.ContentLength > MaxBytes) throw new AdminApiException(502, "The file on the WebDAV server is too large");
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes) throw new AdminApiException(502, "The file on the WebDAV server is too large");
        }
        return buffer.ToArray();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var client = http.CreateClient(HttpClientName);
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            // the address is the admin's own, but keep the exception text (it may carry the URL) out of the answer
            log.LogWarning("WebDAV request failed: {Kind}", e.GetType().Name);
            throw new AdminApiException(502, "Could not connect to the WebDAV server");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AdminApiException(504, "The WebDAV server did not answer in time");
        }
    }

    private static AdminApiException Failure(HttpResponseMessage res) => res.StatusCode switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new AdminApiException(502, $"The WebDAV server refused the login (HTTP {(int)res.StatusCode})"),
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther
            => new AdminApiException(502, "The WebDAV server redirected the request; use its final address"),
        _ => new AdminApiException(502, $"The WebDAV server answered HTTP {(int)res.StatusCode}"),
    };

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
