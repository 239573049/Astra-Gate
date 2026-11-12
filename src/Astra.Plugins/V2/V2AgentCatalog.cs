using System.Text.Json.Nodes;
using Astra.Core.Plugins;
using Astra.Plugins.Agents;
using Astra.Plugins.V2.Runtime;

namespace Astra.Plugins.V2;

/// <summary>
/// The agents the client layer sees (design/plugin-v2 08 §3): v1 agent plugins first, then the clients running v2 plugins
/// declare (<c>contributes.clients</c>), as the same descriptor — so a v2 client is written by <c>PluginClientAdapter</c> through
/// <c>ClientConfigApplier</c> like a v1 one, and restore-all needs no plugin code. A v2 client whose id a v1 agent already
/// has is left out (v1 wins) and reported in <see cref="Errors"/>.
/// </summary>
public sealed class V2AgentCatalog : IAgentPluginCatalog
{
    private readonly AgentPluginCatalog _v1;
    private readonly PluginV2Host _v2;
    private volatile Snapshot _v2Clients = new([], new Dictionary<string, string>());
    private long _v2Generation;

    private sealed record Snapshot(List<(AgentPluginDescriptor Descriptor, string PluginId)> Clients, IReadOnlyDictionary<string, string> Errors);

    public V2AgentCatalog(AgentPluginCatalog v1, PluginV2Host v2)
    {
        _v1 = v1;
        _v2 = v2;
        _v2.PluginChanged += changed => _ = ReloadV2Async();
    }

    public long Generation => _v1.Generation * 1_000_003 + Interlocked.Read(ref _v2Generation);

    public IReadOnlyList<AgentPluginDescriptor> List()
    {
        var v1 = _v1.List();
        var taken = v1.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        return [.. v1, .. _v2Clients.Clients.Where(c => !taken.Contains(c.Descriptor.Id)).Select(c => c.Descriptor)];
    }

    public IReadOnlyDictionary<string, string> Errors
    {
        get
        {
            var all = new Dictionary<string, string>(_v1.Errors);
            foreach (var (k, v) in _v2Clients.Errors) all[k] = v;
            var taken = _v1.List().Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var (d, plugin) in _v2Clients.Clients.Where(c => taken.Contains(c.Descriptor.Id)))
                all[d.Spec] = $"client {d.Id} of {plugin}: a v1 agent plugin already provides it";
            return all;
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _v1.RefreshAsync(ct);
        await ReloadV2Async(ct);
    }

    public JsonObject? Connect(string agentId, JsonObject input)
    {
        if (_v1.List().Any(a => a.Id == agentId)) return _v1.Connect(agentId, input);
        var hit = _v2Clients.Clients.FirstOrDefault(c => c.Descriptor.Id == agentId);
        if (hit.Descriptor is null) throw new InvalidOperationException($"no client {agentId}");
        try
        {
            // the client layer plans synchronously; connect is a short call into the plugin's engine
            return _v2.ConnectClientAsync(hit.PluginId, agentId, input, CancellationToken.None).WaitAsync(PluginV2Host.ProviderCallLimit + TimeSpan.FromSeconds(1))
                .GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is PluginCallException or TimeoutException)
        {
            throw new InvalidOperationException($"{hit.PluginId}: connect failed: {e.Message}", e);
        }
    }

    private async Task ReloadV2Async(CancellationToken ct = default)
    {
        var list = new List<(AgentPluginDescriptor, string)>();
        var errors = new Dictionary<string, string>();
        try
        {
            foreach (var p in await _v2.ListAsync(ct))
                foreach (var c in p.Manifest.Clients)
                    list.Add((new AgentPluginDescriptor
                    {
                        Spec = p.Spec, Id = c.Id, Name = LocalText.Resolve(c.Name, "en") ?? c.Id, Config = c.Config, Format = c.Format, Model = c.Model,
                        Prefix = c.Prefix, Bin = c.Bin, Dir = c.Dir, Ua = c.Ua, Icon = c.Icon, Notice = c.Notice, HasConnect = true,
                    }, p.Id));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            errors["v2"] = e.Message;
        }
        _v2Clients = new Snapshot(list, errors);
        Interlocked.Increment(ref _v2Generation);
    }
}
