using System.Diagnostics;
using System.Text.Json.Nodes;
using Astra.Core;
using Astra.Plugins.Runtime;

namespace Astra.Plugins.Host;

/// <summary>
/// The plugin host as the rest of Astra uses it (magpie's host.go get/start/Restart): started on first need
/// with the enabled plugins that run in it, started again when the plugin list or Astra's proxy changes —
/// the old host retires, finishing what it has in flight — and its RPCs wrapped. Its unsolicited events come
/// out as <see cref="Changed"/> (a sign-in saved or refreshed) and <see cref="SignIn"/> (what a plugin said of
/// an account's sign-in).
/// </summary>
public sealed class PluginHost(
    PluginCatalog catalog, BunRuntime bun, HostAssets assets, AstraPaths paths,
    Func<IEnumerable<string>?>? toolPath, Action<string, string> log, ModelsDevCatalog? modelsDev = null) : IAsyncDisposable
{
    private static readonly TimeSpan ProxyLookEvery = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProxyLookNone = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _startLock = new(1, 1);
    private Running? _current;
    private long _lastProxyLook;
    private long _generation;

    /// <summary>A sign-in was saved, refreshed or removed, or a new host started: the providers may differ.</summary>
    public event Action? Changed;

    /// <summary>A plugin said what became of an account's sign-in: (provider, account, "expired" | "kept" | "renewed").</summary>
    public event Action<string, string, string>? SignIn;

    /// <summary>Moves every time a host starts (cached loader options and listings are per host).</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>How the host fared: running, the plugins it loaded (and why some didn't), the last start error.</summary>
    public PluginHostStatus Status
    {
        get
        {
            var r = _current;
            List<PluginHostMessage> recent;
            lock (_recent) recent = [.. _recent.Reverse()];
            return new PluginHostStatus(r?.Client.Alive == true, r?.Loaded ?? [], _lastError,
                r?.Client.Alive == false ? r.Client.GoneReason : null, recent);
        }
    }

    private string? _lastError;

    /// <summary>Whether any enabled plugin runs in the host (so starting one is worth it).</summary>
    public async Task<bool> HasPluginsAsync(CancellationToken ct = default) => (await HostPluginsAsync(ct)).Count > 0;

    public async Task<JsonNode?> CallAsync(string method, JsonObject parameters, CancellationToken ct = default)
    {
        var host = await GetAsync(ct);
        return await host.CallAsync(method, parameters, ct);
    }

    public async Task<HttpResponseMessage> FetchAsync(HostFetchRequest request, CancellationToken ct = default)
    {
        var host = await GetAsync(ct);
        return await host.FetchAsync(request, ct);
    }

    /// <summary>Stops the host; the next call starts one with the plugins as they are then.</summary>
    public async Task RestartAsync()
    {
        await _startLock.WaitAsync();
        try
        {
            if (_current is { } r)
            {
                _current = null;
                Retire(r.Client);
            }
        }
        finally
        {
            _startLock.Release();
        }
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_current is { } r) await r.Client.DisposeAsync();
        _current = null;
    }

    private async Task<PluginHostClient> GetAsync(CancellationToken ct)
    {
        var r = _current;
        if (r is not null && r.Client.Alive && r.Generation == catalog.Generation && !ProxyMoved(r)) return r.Client;
        await _startLock.WaitAsync(ct);
        try
        {
            r = _current;
            if (r is not null && r.Client.Alive && r.Generation == catalog.Generation && !ProxyMoved(r)) return r.Client;
            if (r is not null)
            {
                _current = null;
                Retire(r.Client);
            }
            _current = await StartAsync(ct);
            Interlocked.Increment(ref _generation);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _lastError = e.Message;
            throw e as PluginHostException ?? new PluginHostException("starting plugins: " + e.Message);
        }
        finally
        {
            _startLock.Release();
        }
        Changed?.Invoke();
        return _current!.Client;
    }

    private async Task<Running> StartAsync(CancellationToken ct)
    {
        var generation = catalog.Generation;
        var plugins = await HostPluginsAsync(ct);
        var snapshot = await catalog.SnapshotAsync(ct);
        var exe = await bun.EnsureAsync(ct);
        var (hostJs, piJs) = assets.Ensure();
        // the providers' models.dev entries, as OpenCode has them (the copy there is when it can't be fetched)
        var modelsDevPath = modelsDev is null ? null : await modelsDev.EnsureAsync(log, ct);
        Directory.CreateDirectory(paths.PluginsDir);
        var env = ChildEnvironment.Build(forHost: true, toolPath?.Invoke());
        var proxy = ChildEnvironment.Proxy().Proxy;
        var client = PluginHostClient.Start(exe, hostJs, paths.PluginsDir, env, OnEvent, line => log("info", "plugin: " + line));

        var items = new JsonArray();
        foreach (var p in plugins)
        {
            var item = new JsonObject { ["spec"] = p.Entry.Spec, ["target"] = p.Target };
            if (p.Entry.OptionsJson is { Length: > 0 } o && JsonNode.Parse(o) is JsonObject options) item["options"] = options;
            items.Add((JsonNode)item);
        }
        var prefer = new JsonObject();
        foreach (var (id, spec) in snapshot.Preferences) prefer[id] = spec;
        var init = new JsonObject
        {
            ["authPath"] = paths.PluginAuthFile,
            ["modelsDevPath"] = modelsDevPath ?? (File.Exists(paths.PluginModelsDevFile) ? paths.PluginModelsDevFile : ""),
            ["piPath"] = piJs,
            ["directory"] = paths.PluginsDir,
            ["config"] = new JsonObject(),
            ["prefer"] = prefer,
            ["plugins"] = items,
        };
        JsonNode? result;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            result = await client.CallAsync("init", init, timeout.Token);
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
        var loaded = new List<PluginHostLoaded>();
        foreach (var node in result?["plugins"] as JsonArray ?? [])
        {
            if (node is not JsonObject o) continue;
            var provides = (o["provides"] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList() ?? [];
            var servedBy = new Dictionary<string, string>();
            if (o["servedBy"] is JsonObject sb)
                foreach (var (k, v) in sb) servedBy[k] = v?.GetValue<string>() ?? "";
            var entry = new PluginHostLoaded(o["spec"]?.GetValue<string>() ?? "", o["error"]?.GetValue<string>(), provides, servedBy);
            if (entry.Error is not null) log("warn", $"plugin {entry.Spec} didn't load: {entry.Error}");
            loaded.Add(entry);
        }
        _lastError = null;
        Interlocked.Exchange(ref _lastProxyLook, Stopwatch.GetTimestamp());
        return new Running(client, generation, proxy, loaded);
    }

    // The enabled plugins that run in the host: not those only middleware or an agent, which run in Astra's
    // own process (magpie's InMagpieOnly).
    private async Task<List<InstalledPlugin>> HostPluginsAsync(CancellationToken ct) =>
        (await catalog.SnapshotAsync(ct)).Plugins
        .Where(p => p.Entry.Enabled && !p.Manifest.InProcessOnly && p.Manifest.NotPluginReason is null && (File.Exists(p.Target) || Directory.Exists(p.Target)))
        .ToList();

    // The host reads its proxy once, when it starts: a proxy set since (the system's, at login before the proxy
    // app is up; or Settings changed) needs a new host. Looked at every 15 s, every 2 s while it has none.
    private bool ProxyMoved(Running r)
    {
        var every = r.Proxy is null ? ProxyLookNone : ProxyLookEvery;
        var last = Interlocked.Read(ref _lastProxyLook);
        if (Stopwatch.GetElapsedTime(last) < every) return false;
        Interlocked.Exchange(ref _lastProxyLook, Stopwatch.GetTimestamp());
        return ChildEnvironment.Proxy().Proxy != r.Proxy;
    }

    // A host taken out of service finishes what it has in flight, then stops.
    private void Retire(PluginHostClient client) => _ = Task.Run(async () =>
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 600;
        while (client.Alive && client.InFlight > 0 && Stopwatch.GetTimestamp() < deadline) await Task.Delay(500);
        await client.DisposeAsync();
    });

    private void OnEvent(JsonObject m)
    {
        var name = m["event"]?.GetValue<string>();
        switch (name)
        {
            case "auth":
                Changed?.Invoke();
                break;
            case "signIn":
                SignIn?.Invoke(m["provider"]?.GetValue<string>() ?? "", m["account"]?.GetValue<string>() ?? "", m["said"]?.GetValue<string>() ?? "");
                break;
            case "log":
                log(m["level"]?.GetValue<string>() ?? "info", "plugin: " + m["message"]?.GetValue<string>());
                break;
            case "toast":
                log("info", $"plugin: {m["title"]?.GetValue<string>()} {m["message"]?.GetValue<string>()}");
                Remember(new PluginHostMessage(DateTimeOffset.UtcNow, m["variant"]?.GetValue<string>() ?? "info",
                    m["title"]?.GetValue<string>() ?? "", m["message"]?.GetValue<string>() ?? ""));
                break;
            case "recovered":
                log("warn", "plugin sign-ins were read from their last good copy: " + m["said"]?.GetValue<string>());
                Remember(new PluginHostMessage(DateTimeOffset.UtcNow, "warning", "plugin-auth.json", m["said"]?.GetValue<string>() ?? ""));
                break;
        }
    }

    // What plugins showed the user (magpie's toasts), newest last; the Plugins page lists them.
    private readonly Queue<PluginHostMessage> _recent = new();

    private void Remember(PluginHostMessage message)
    {
        lock (_recent)
        {
            _recent.Enqueue(message);
            while (_recent.Count > 20) _recent.Dequeue();
        }
    }

    private sealed record Running(PluginHostClient Client, long Generation, string? Proxy, List<PluginHostLoaded> Loaded);
}

/// <summary>How one plugin fared when the host loaded it: the provider ids it signs in to, and those of them another
/// plugin serves instead (id → that plugin's spec).</summary>
public sealed record PluginHostLoaded(string Spec, string? Error, List<string> Provides, Dictionary<string, string> ServedBy);

/// <summary>The host as the Plugins page shows it.</summary>
/// <param name="StartError">Why the last start failed.</param>
/// <param name="GoneReason">Why the host stopped, when it did on its own.</param>
/// <param name="Recent">What plugins showed lately (their toasts), newest first.</param>
public sealed record PluginHostStatus(bool Running, List<PluginHostLoaded> Plugins, string? StartError, string? GoneReason, List<PluginHostMessage> Recent);

/// <summary>A message a plugin showed the user (OpenCode's <c>tui.showToast</c>).</summary>
/// <param name="Variant">info, success, warning or error.</param>
public sealed record PluginHostMessage(DateTimeOffset At, string Variant, string Title, string Message);
