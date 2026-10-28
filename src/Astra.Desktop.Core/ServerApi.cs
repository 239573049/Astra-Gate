using System.Net.Http.Headers;
using System.Text.Json;

namespace Astra.Desktop.Core;

/// <summary>GET /api/version, tolerant: a missing field is null.</summary>
public sealed record ServerVersionInfo(string? Version, string? ApiVersion);

/// <summary>The few unauthenticated server calls the shell makes before the typed admin client exists. Port of desktop/src/api.ts.</summary>
public static class ServerApi
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static string BaseUrl(int port) => $"http://127.0.0.1:{port}";

    /// <summary>GET /api/health — true only when the server answers {"status":"ok"}.</summary>
    public static async Task<bool> CheckHealthAsync(string baseUrl, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync(baseUrl, "/api/health", timeout, ct);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == "ok";
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return false;
        }
    }

    /// <summary>GET /api/version; null when the server cannot be read.</summary>
    public static async Task<ServerVersionInfo?> FetchVersionAsync(string baseUrl, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var doc = await GetJsonAsync(baseUrl, "/api/version", timeout, ct);
            var o = doc.RootElement;
            return o.ValueKind == JsonValueKind.Object ? new ServerVersionInfo(ServerFiles.Str(o, "version"), ServerFiles.Str(o, "apiVersion")) : new ServerVersionInfo(null, null);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>POST /api/admin/shutdown with the runtime token. False on any failure; callers verify the shutdown through health.</summary>
    public static async Task<bool> RequestShutdownAsync(string baseUrl, string? runtimeToken, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/admin/shutdown");
            request.Headers.Add("X-Astra-Admin", "1");
            if (!string.IsNullOrEmpty(runtimeToken)) request.Headers.Add("X-Astra-Runtime-Token", runtimeToken);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            using var response = await Http.SendAsync(request, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<JsonDocument> GetJsonAsync(string baseUrl, string path, TimeSpan timeout, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var response = await Http.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
    }
}
