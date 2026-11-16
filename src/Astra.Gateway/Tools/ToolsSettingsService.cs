using Astra.Core.Tools;
using Astra.Data;

namespace Astra.Gateway.Tools;

/// <summary>
/// Owns <see cref="ToolsSettings"/> the way <c>SmartRoutingService</c> owns the routing settings: one snapshot a request reads
/// once, replaced as a whole under its own <c>settings</c> key (a null database keeps them in memory: tests). Keys inside it stay protected; this class never decrypts.
/// </summary>
public sealed class ToolsSettingsService(AstraDatabase? db = null)
{
    private readonly SemaphoreSlim _writes = new(1, 1);
    private volatile ToolsSettings _current = new();

    /// <summary>The active settings; shared, so never changed by the caller.</summary>
    public ToolsSettings Current => _current;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (db is null) return;
        var loaded = (await db.Settings.GetAsync<ToolsSettings>(ToolsSettings.StorageKey, ct) ?? new ToolsSettings()).Clone();
        Normalize(loaded);
        _current = loaded;
    }

    /// <summary>Applies <paramref name="change"/> to a copy, normalizes it, stores and activates it; changes are applied one at a time.</summary>
    public async Task<ToolsSettings> UpdateAsync(Action<ToolsSettings> change, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct);
        try
        {
            var next = _current.Clone();
            change(next);
            Normalize(next);
            if (db is not null) await db.Settings.SetAsync(ToolsSettings.StorageKey, next, ct);
            _current = next;
            return next.Clone();
        }
        finally
        {
            _writes.Release();
        }
    }

    private static void Normalize(ToolsSettings s)
    {
        s.Search.ResultCount = Math.Clamp(s.Search.ResultCount, 1, 20);
        if (!SearchModes.All.Contains(s.Search.Mode)) s.Search.Mode = SearchModes.Auto;
        s.Fetch.MaxBytes = Math.Clamp(s.Fetch.MaxBytes, 64 * 1024, 20 * 1024 * 1024);
        s.Fetch.TimeoutSeconds = Math.Clamp(s.Fetch.TimeoutSeconds, 3, 120);
        s.Fetch.MaxChars = Math.Clamp(s.Fetch.MaxChars, 1000, 100_000);
        s.Image.Model = s.Image.Model.Trim();
        s.Image.Size = s.Image.Size.Trim();
        // One entry per vendor, the first one wins.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        s.Search.Apis.RemoveAll(a => a.Vendor.Length == 0 || !seen.Add(a.Vendor));
        foreach (var api in s.Search.Apis)
        {
            if (api.Pick != SearchPicks.Order) api.Pick = SearchPicks.Turns;
            api.Url = string.IsNullOrWhiteSpace(api.Url) ? null : api.Url.Trim();
        }
    }
}
