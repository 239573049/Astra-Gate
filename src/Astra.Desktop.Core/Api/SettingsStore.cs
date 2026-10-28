using Astra.Api.Contracts;

namespace Astra.Desktop.Core.Api;

/// <summary>
/// The server settings (GET /api/settings; web <c>useSettings</c>, key ['settings']). The shell reads <c>gatewayBaseUrl</c> from it
/// for the sidebar's address line; the Settings page appends its mutations (<c>PUT /api/settings</c> then <c>Invalidate("settings")</c>).
/// </summary>
public sealed partial class SettingsStore
{
    /// <summary>The settings; key ['settings'].</summary>
    public Query<SettingsDto> Settings { get; }

    /// <summary>Creates the store over the shared client and cache.</summary>
    public SettingsStore(QueryClient queries, ApiClient api)
    {
        Settings = queries.Query(QueryKeys.Settings, async ct => await api.GetAsync<SettingsDto>("/api/settings", ct)
            ?? throw new ApiException(0, "empty settings response"));
    }

    /// <summary>
    /// The address the sidebar shows: the gateway base URL without its scheme (web ServiceStatus), else <paramref name="fallback"/> (the
    /// address the app itself reached the server on) while the settings are not loaded or carry no URL.
    /// </summary>
    public static string? DisplayAddress(string? gatewayBaseUrl, string? fallback)
    {
        if (string.IsNullOrWhiteSpace(gatewayBaseUrl)) return fallback;
        var url = gatewayBaseUrl.Trim().TrimEnd('/');
        foreach (var scheme in new[] { "https://", "http://" })
            if (url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return url[scheme.Length..];
        return url;
    }
}
