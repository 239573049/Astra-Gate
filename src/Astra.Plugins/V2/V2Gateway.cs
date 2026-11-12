using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Astra.Plugins.V2;

/// <summary>
/// What the gateway's middleware chain needs of the v2 host (design/plugin-v2 02 §3): the hook sets to load, a
/// generation that moves when they change, the <c>ctx.shared</c> values (the storage keys a hook set <c>reads</c>, kept in
/// memory so a hook reads them synchronously and they follow every write), and the counters hooks bump.
/// </summary>
public sealed class V2Gateway
{
    private readonly PluginV2Host _host;
    private readonly ConcurrentDictionary<(string Plugin, string Key), string?> _shared = new();
    private readonly ConcurrentDictionary<(string Plugin, string Name), long> _counters = new();
    private long _generation;

    internal V2Gateway(PluginV2Host host) => _host = host;

    /// <summary>Moves when the v2 plugins with hooks may have changed (the middleware set reloads).</summary>
    public long Generation => Interlocked.Read(ref _generation);

    internal void Invalidate() => Interlocked.Increment(ref _generation);

    /// <summary>The enabled v2 plugins and their hook sets, in the list's order.</summary>
    public async Task<IReadOnlyList<(PluginV2Instance Plugin, HookContribution Hook)>> HooksAsync(CancellationToken ct = default)
    {
        var plugins = await _host.ListAsync(ct);
        var list = new List<(PluginV2Instance, HookContribution)>();
        foreach (var p in plugins.Where(p => p.Healthy && p.Manifest.Gateway is not null))
        {
            foreach (var h in p.Manifest.Hooks)
            {
                list.Add((p, h));
                foreach (var key in h.Reads)
                    if (!_shared.ContainsKey((p.Id, key)))
                        _shared[(p.Id, key)] = await _host.Services.Data.GetAsync(p.Id, key, ct);
            }
        }
        return list;
    }

    /// <summary>The <c>ctx.shared</c> object for a hook set: each key it reads, as stored (null when unset).</summary>
    public string SharedJson(string pluginId, IReadOnlyList<string> reads)
    {
        if (reads.Count == 0) return "{}";
        var o = new JsonObject();
        foreach (var key in reads)
        {
            var json = _shared.TryGetValue((pluginId, key), out var v) ? v : null;
            try
            {
                o[key] = json is null ? null : JsonNode.Parse(json);
            }
            catch (System.Text.Json.JsonException)
            {
                o[key] = null;
            }
        }
        return o.ToJsonString();
    }

    internal void StorageChanged(string pluginId, string key, string? valueJson)
    {
        if (_shared.ContainsKey((pluginId, key))) _shared[(pluginId, key)] = valueJson;
    }

    /// <summary><c>ctx.count(name, n)</c>: a counter the plugin page shows.</summary>
    public void Count(string pluginId, string name, long n) => _counters.AddOrUpdate((pluginId, name), n, (_, v) => v + n);

    public IReadOnlyDictionary<string, long> Counters(string pluginId) =>
        _counters.Where(c => c.Key.Plugin == pluginId).ToDictionary(c => c.Key.Name, c => c.Value);

    /// <summary><c>ctx.emit(name, payload)</c> from a hook: the plugin's own event, delivered asynchronously.</summary>
    public void Emit(PluginV2Instance p, string name, string? payloadJson)
    {
        if (p.Manifest.Events.All(e => e.Name != name) || !PluginPermissions.Holds(p.Granted, PluginPermissions.Events))
        {
            p.Log("warn", $"ctx.emit({name}) ignored: declare it in contributes.events and ask for the permission \"events\"");
            return;
        }
        _host.Publish(p.Id + ":" + name, payloadJson is null ? null : JsonNode.Parse(payloadJson), p.Id);
    }

    internal PluginV2Host Host => _host;
}
