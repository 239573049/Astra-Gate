using Astra.Core;
using Astra.Data;

namespace Astra.Gateway.Pipeline;

/// <summary>Cached <see cref="AppSettings"/>; the gateway reads <see cref="Current"/> on every request.</summary>
public sealed class SettingsService(AstraDatabase db)
{
    private volatile AppSettings? _current;

    public AppSettings Current => _current ?? throw new InvalidOperationException("SettingsService not loaded");

    public async Task LoadAsync(CancellationToken ct = default) =>
        _current = await db.Settings.GetAsync<AppSettings>(AppSettings.StorageKey, ct) ?? new AppSettings();

    public async Task<AppSettings> UpdateAsync(Action<AppSettings> mutate, CancellationToken ct = default)
    {
        var next = Current.Clone();
        mutate(next);
        await db.Settings.SetAsync(AppSettings.StorageKey, next, ct);
        _current = next;
        return next;
    }
}
