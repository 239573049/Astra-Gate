using System.Text.Json.Nodes;
using Astra.Core.Plugins;

namespace Astra.Plugins.V2;

/// <summary>
/// What the v2 host needs from the server that Astra.Plugins can't reach itself (it references Astra.Core only):
/// secret protection, the HTTP client plugins fetch through, notifications, the read-only data views and logging.
/// The server implements it (<c>PluginV2HostServices</c>); tests pass a fake.
/// </summary>
public interface IPluginV2Services
{
    /// <summary>This Astra's version (for <c>engines.astra</c> and <c>astra.version</c>).</summary>
    string HostVersion { get; }

    IPluginDataStore Data { get; }

    string Protect(string plaintext);

    string Unprotect(string protectedValue);

    /// <summary>
    /// A client for <c>http.fetch</c>: no automatic redirects (the host follows them, checking each origin), no cookies,
    /// Astra's proxy settings.
    /// </summary>
    HttpClient Http();

    /// <summary>A notification from a plugin (<c>notifications.send</c>): shown in the console's feed and as a desktop notification.</summary>
    void Notify(string pluginId, string title, string? body, string tone);

    /// <summary>
    /// A read-only data view (<c>data.*</c>): <paramref name="what"/> is <c>requests.query</c>, <c>requests.stats</c>,
    /// <c>providers.list</c>, <c>models.list</c> or <c>tokens.list</c>. Never secrets, bodies or client IPs.
    /// </summary>
    ValueTask<JsonNode?> QueryAsync(string what, JsonNode? args, CancellationToken ct);

    /// <summary>A line of a plugin's log (rate limited by the caller).</summary>
    void Log(string pluginId, string level, string message);

    /// <summary>The provider accounts of v2 providers (2.1, design/plugin-v2 08 §2); null on a host that has none.</summary>
    IPluginProviderAccounts? ProviderAccounts => null;
}

/// <summary>Where a v2 provider's accounts live (the server: provider_accounts rows of the plugin's provider row).</summary>
public interface IPluginProviderAccounts
{
    /// <summary>The accounts, as the plugin sees them: key, displayName, email, plan, status, enabled.</summary>
    Task<JsonArray> ListAsync(string pluginId, string providerId, CancellationToken ct);

    /// <summary>Adds the account with this key, or merges the fields given into it.</summary>
    Task SetAsync(string pluginId, string providerId, JsonObject account, CancellationToken ct);

    Task<bool> RemoveAsync(string pluginId, string providerId, string key, CancellationToken ct);
}
