using Astra.Core;
using Astra.Core.Plugins;
using Astra.Plugins.Packages;
using Astra.Plugins.Store;

namespace Astra.Plugins;

/// <summary>
/// The installed plugins as everything else reads them: the stored list with each plugin's target and
/// manifest. <see cref="Generation"/> moves on every change made through it, which is what the
/// middleware set, the agent catalog and the plugin host watch to reload — magpie's plugins.json stamp.
/// With <paramref name="off"/> (<c>ASTRA_PLUGINS=off</c>) every plugin reads as switched off, so none runs.
/// </summary>
public sealed class PluginCatalog(IPluginListStore store, AstraPaths paths, bool off = false)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private long _generation = 1;
    private PluginSnapshot? _snapshot;

    /// <summary>Moves on every change to the list (and on <see cref="Invalidate"/>).</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>Told after every change, with the new generation.</summary>
    public event Action<long>? Changed;

    public AstraPaths Paths => paths;

    /// <summary>Plugins are switched off for this server (<c>ASTRA_PLUGINS=off</c>).</summary>
    public bool Off => off;

    public IPluginListStore Store => store;

    /// <summary>Where npm and git plugins are installed.</summary>
    public string PackagesDir => paths.PluginPackagesDir;

    /// <summary>The list as it is now: read from the store once per generation.</summary>
    public async Task<PluginSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        var generation = Generation;
        if (_snapshot is { } s && s.Generation == generation) return s;
        await _lock.WaitAsync(ct);
        try
        {
            generation = Generation;
            if (_snapshot is { } again && again.Generation == generation) return again;
            var entries = await store.ListAsync(ct);
            if (off) foreach (var e in entries) e.Enabled = false;
            var prefer = await store.ListPreferencesAsync(ct);
            var plugins = entries.Select(e =>
            {
                var target = PluginSpec.Target(e.Spec, PackagesDir);
                return new InstalledPlugin(e, target, PackageManifest.Read(target));
            }).ToList();
            return _snapshot = new PluginSnapshot(generation, plugins, prefer);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Re-reads the list and manifests next time (a package was installed or updated on disk).</summary>
    public void Invalidate()
    {
        var g = Interlocked.Increment(ref _generation);
        Changed?.Invoke(g);
    }

    /// <summary>
    /// The plugin <paramref name="name"/> means: the one added as that spec exactly, else the one of that
    /// package, else (with <paramref name="shortName"/>) the one whose short name it is — as the community's
    /// READMEs write it, param-override for @magpie-community/middleware-param-override. A plugin of the
    /// user's own (a folder) beside a third party's is each told apart by its own spec.
    /// </summary>
    public static InstalledPlugin? Find(PluginSnapshot snapshot, string name, bool shortName = false, string? packagesDir = null)
    {
        var bySpec = snapshot.Plugins.FirstOrDefault(p => p.Entry.Spec == name);
        if (bySpec is not null) return bySpec;
        var byName = snapshot.Plugins.FirstOrDefault(p => p.Entry.Name == name
                                                         || (packagesDir is not null && PluginSpec.Name(p.Entry.Spec, packagesDir) == name));
        if (byName is not null || !shortName) return byName;
        return snapshot.Plugins.FirstOrDefault(p => PluginSpec.ShortName(p.Entry.Name) == name);
    }
}

/// <summary>The plugin list at one generation.</summary>
/// <param name="Preferences">The plugin picked (by spec) for each provider id more than one plugin serves.</param>
public sealed record PluginSnapshot(long Generation, IReadOnlyList<InstalledPlugin> Plugins, IReadOnlyDictionary<string, string> Preferences);

/// <summary>A plugin with where it loads from and what it has.</summary>
public sealed record InstalledPlugin(PluginEntry Entry, string Target, PackageManifest Manifest)
{
    /// <summary>The kinds it is (<see cref="PluginKinds"/>), for the kind chips.</summary>
    public IReadOnlyList<string> Kinds
    {
        get
        {
            var kinds = new List<string>();
            if (Manifest.IsPi) kinds.Add(PluginKinds.Pi);
            else if (!Manifest.InProcessOnly && Manifest.HasEntry) kinds.Add(PluginKinds.Provider);
            if (Manifest.MiddlewareFile is not null) kinds.Add(PluginKinds.Middleware);
            if (Manifest.AgentFile is not null) kinds.Add(PluginKinds.Agent);
            return kinds;
        }
    }
}
