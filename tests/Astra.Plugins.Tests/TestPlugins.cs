using Astra.Core;
using Astra.Core.Plugins;

namespace Astra.Plugins.Tests;

/// <summary>An in-memory plugin list and a temp data dir: a <see cref="PluginCatalog"/> without a database.</summary>
internal sealed class TestPlugins
{
    public TestPlugins()
    {
        Home = Directory.CreateTempSubdirectory("astra-plugins-").FullName;
        Paths = new AstraPaths(Home);
        Store = new MemoryPluginStore();
        Catalog = new PluginCatalog(Store, Paths);
    }

    public string Home { get; }
    public AstraPaths Paths { get; }
    public MemoryPluginStore Store { get; }
    public PluginCatalog Catalog { get; }

    /// <summary>A magpie fixture plugin copied into a fresh folder (so a test may change its files).</summary>
    public string Fixture(string name)
    {
        var src = Path.Combine(AppContext.BaseDirectory, "Fixtures", "magpie", name);
        var dest = Path.Combine(Home, "fixtures", name);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var to = Path.Combine(dest, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        return dest;
    }

    /// <summary>A plugin folder made of the given files.</summary>
    public string Folder(string name, params (string Path, string Text)[] files)
    {
        var dir = Path.Combine(Home, "src", name);
        foreach (var (rel, text) in files)
        {
            var to = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.WriteAllText(to, text);
        }
        return dir;
    }

    /// <summary>Adds the plugin at <paramref name="path"/> (a path spec), with options.</summary>
    public async Task AddAsync(string path, string? options = null, bool enabled = true)
    {
        await Store.AddAsync(new PluginEntry { Spec = path, Name = Path.GetFileName(path), OptionsJson = options, Enabled = enabled });
        Catalog.Invalidate();
    }
}

internal sealed class MemoryPluginStore : IPluginListStore
{
    private readonly List<PluginEntry> _list = [];
    private readonly Dictionary<string, string> _prefs = [];

    public Task<IReadOnlyList<PluginEntry>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PluginEntry>>(_list.OrderBy(p => p.SortOrder).ToList());

    public Task<PluginEntry?> GetAsync(string spec, CancellationToken ct = default) => Task.FromResult(_list.FirstOrDefault(p => p.Spec == spec));

    public Task AddAsync(PluginEntry entry, CancellationToken ct = default)
    {
        _list.RemoveAll(p => p.Spec == entry.Spec);
        entry.SortOrder = _list.Count == 0 ? 0 : _list.Max(p => p.SortOrder) + 1;
        _list.Add(entry);
        return Task.CompletedTask;
    }

    public Task ReplaceAsync(string oldSpec, PluginEntry entry, CancellationToken ct = default)
    {
        var i = _list.FindIndex(p => p.Spec == oldSpec);
        entry.SortOrder = i >= 0 ? _list[i].SortOrder : _list.Count;
        if (i >= 0) _list[i] = entry;
        else _list.Add(entry);
        foreach (var k in _prefs.Where(kv => kv.Value == oldSpec).Select(kv => kv.Key).ToList()) _prefs[k] = entry.Spec;
        return Task.CompletedTask;
    }

    public Task<bool> SetEnabledAsync(string spec, bool enabled, CancellationToken ct = default) => Set(spec, p => p.Enabled = enabled);

    public Task<bool> SetOptionsAsync(string spec, string? optionsJson, CancellationToken ct = default) => Set(spec, p => p.OptionsJson = optionsJson);

    public Task<bool> SetInstalledVersionAsync(string spec, string? version, CancellationToken ct = default) => Set(spec, p => p.InstalledVersion = version);

    public Task<bool> RemoveAsync(string spec, CancellationToken ct = default)
    {
        foreach (var k in _prefs.Where(kv => kv.Value == spec).Select(kv => kv.Key).ToList()) _prefs.Remove(k);
        return Task.FromResult(_list.RemoveAll(p => p.Spec == spec) > 0);
    }

    public Task ReorderAsync(IReadOnlyList<string> specs, CancellationToken ct = default)
    {
        var order = specs.Concat(_list.OrderBy(p => p.SortOrder).Select(p => p.Spec)).Distinct().ToList();
        foreach (var p in _list) p.SortOrder = order.IndexOf(p.Spec);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, string>> ListPreferencesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(_prefs));

    public Task SetPreferenceAsync(string providerId, string spec, CancellationToken ct = default)
    {
        _prefs[providerId] = spec;
        return Task.CompletedTask;
    }

    private Task<bool> Set(string spec, Action<PluginEntry> change)
    {
        var p = _list.FirstOrDefault(x => x.Spec == spec);
        if (p is not null) change(p);
        return Task.FromResult(p is not null);
    }
}
