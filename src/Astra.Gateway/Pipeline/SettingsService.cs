using System.Net;
using System.Security.Cryptography;
using Astra.Core;
using Astra.Core.Clients;
using Astra.Data;

namespace Astra.Gateway.Pipeline;

/// <summary>Cached <see cref="AppSettings"/>; the gateway reads <see cref="Current"/> on every request.</summary>
public sealed class SettingsService(AstraDatabase db, ISecretProtector secrets)
{
    private volatile AppSettings? _current;

    public AppSettings Current => _current ?? throw new InvalidOperationException("SettingsService not loaded");

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var loaded = await db.Settings.GetAsync<AppSettings>(AppSettings.StorageKey, ct) ?? new AppSettings();
        ApplyProxy(loaded);
        _current = loaded;
    }

    public async Task<AppSettings> UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        var next = Current.Clone();
        mutate(next);
        await db.Settings.SetAsync(AppSettings.StorageKey, next, ct);
        ApplyProxy(next);
        _current = next;
        return next;
    }

    /// <summary>The outbound proxy is process-wide (<see cref="SystemProxy"/>): push the stored choice into it.</summary>
    private void ApplyProxy(AppSettings s)
    {
        NetworkCredential? credentials = null;
        if (!string.IsNullOrEmpty(s.ProxyUsername))
        {
            var password = "";
            if (!string.IsNullOrEmpty(s.ProxyPasswordProtected))
            {
                try
                {
                    password = secrets.Unprotect(s.ProxyPasswordProtected);
                }
                catch (CryptographicException)
                {
                    // Data-protection keys lost (data dir restored elsewhere): the password has to be entered again.
                }
            }
            credentials = new NetworkCredential(s.ProxyUsername, password);
        }
        SystemProxy.Configure(s.ProxyMode, s.ProxyUrl, s.ProxyBypass, credentials);
    }
}
