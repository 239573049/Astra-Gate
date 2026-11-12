using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Astra.Core.Plugins;
using Astra.Plugins.V2.Runtime;
using Astra.Plugins.V2.Ui;

namespace Astra.Plugins.V2;

/// <summary>
/// The v2 plugin host (design/plugin-v2 01): the enabled v2 plugins, each with its main context, and what joins them to
/// the rest of Astra — the host call table, the event bus, the job scheduler, runtime permission requests, the view
/// sessions (<see cref="Ui"/>) and the gateway hooks (<see cref="Gateway"/>). It follows <see cref="PluginCatalog"/>:
/// a change to the list reloads the plugins it touched.
/// </summary>
public sealed class PluginV2Host : IAsyncDisposable
{
    /// <summary>A main context unused this long is unloaded (its views' state stays with the host).</summary>
    public static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(10);

    /// <summary>A plugin whose engine a limit stops this many times within <see cref="FaultWindow"/> is stopped.</summary>
    public const int FaultLimit = 5;

    public static readonly TimeSpan FaultWindow = TimeSpan.FromMinutes(10);

    private readonly PluginCatalog _catalog;
    private readonly IPluginV2Services _services;
    private readonly HostCalls _calls;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly ConcurrentDictionary<string, PluginV2Instance> _plugins = new();
    private readonly ConcurrentDictionary<string, string> _problems = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _subscriptions = new();
    private readonly ConcurrentDictionary<string, PermissionRequest> _requests = new();
    private readonly ConcurrentDictionary<(string, string), DateTimeOffset> _nextJob = new();
    private readonly ConcurrentDictionary<string, JsonObject> _upgrades = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _ticker;
    private long _generation = -1;

    public PluginV2Host(PluginCatalog catalog, IPluginV2Services services)
    {
        _catalog = catalog;
        _services = services;
        _calls = new HostCalls(this, services);
        Ui = new UiSessions(this);
        Gateway = new V2Gateway(this);
        _catalog.Changed += generation => { var refresh = RefreshAsync(); };
        _ticker = Task.Run(TickAsync);
    }

    /// <summary>The view sessions the shells open.</summary>
    public UiSessions Ui { get; }

    /// <summary>The v2 gateway hooks, which the middleware chain runs with v1's.</summary>
    public V2Gateway Gateway { get; }

    internal IPluginV2Services Services => _services;

    /// <summary>Told when a plugin's views should be re-rendered or its contributions changed (the shells refresh their menus).</summary>
    public event Action<string>? PluginChanged;

    /// <summary>The loaded v2 plugins.</summary>
    public async Task<IReadOnlyList<PluginV2Instance>> ListAsync(CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        return [.. _plugins.Values.OrderBy(p => p.SortOrder)];
    }

    /// <summary>The plugin with <paramref name="id"/>, loaded and enabled; null when there is none.</summary>
    public async Task<PluginV2Instance?> FindAsync(string id, CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        return _plugins.TryGetValue(id, out var p) ? p : null;
    }

    /// <summary>Why a v2 plugin (by spec) isn't running: a manifest problem, a version it doesn't run on, ungranted permissions.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ProblemsAsync(CancellationToken ct = default)
    {
        await EnsureAsync(ct);
        return new Dictionary<string, string>(_problems);
    }

    /// <summary>Re-reads the list when it moved on since the last look.</summary>
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        if (Interlocked.Read(ref _generation) == _catalog.Generation) return;
        await RefreshAsync(ct);
    }

    /// <summary>Reloads the plugins whose entry, manifest or grants changed; stops the ones gone or switched off.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            var snapshot = await _catalog.SnapshotAsync(ct);
            var seen = new HashSet<string>();
            _problems.Clear();
            foreach (var installed in snapshot.Plugins)
            {
                if (!installed.Manifest.IsV2) continue;
                if (installed.Manifest.V2Error is { } error)
                {
                    _problems[installed.Entry.Spec] = error;
                    continue;
                }
                var manifest = installed.Manifest.V2!;
                if (!installed.Entry.Enabled) continue;
                if (!manifest.RunsOn(_services.HostVersion))
                {
                    _problems[installed.Entry.Spec] = $"needs Astra {manifest.EnginesAstra}; this is {_services.HostVersion}";
                    continue;
                }
                if (!seen.Add(manifest.Id))
                {
                    _problems[installed.Entry.Spec] = $"another plugin already has the id {manifest.Id}";
                    continue;
                }
                var granted = await _services.Data.GrantsAsync(manifest.Id, ct);
                var missing = manifest.Permissions.Where(p => !granted.Contains(p)).ToList();
                if (missing.Count > 0)
                {
                    seen.Remove(manifest.Id);
                    _problems[installed.Entry.Spec] = "waiting for permissions: " + string.Join(", ", missing);
                    continue;
                }
                var stamp = Stamp(installed, granted);
                if (_plugins.TryGetValue(manifest.Id, out var existing) && existing.Stamp == stamp)
                {
                    existing.SortOrder = installed.Entry.SortOrder;
                    if (existing.OptionsJson != installed.Entry.OptionsJson)
                    {
                        // settings changed: the plugin hears of it; the views and hooks keep running (hooks reload with the new options)
                        existing.OptionsJson = installed.Entry.OptionsJson;
                        Publish("plugin.configChanged", existing.Config(), existing.Id);
                    }
                    continue;
                }
                var instance = new PluginV2Instance(this, installed, manifest, granted, stamp);
                if (_upgrades.TryRemove(manifest.Id, out var upgrade))
                {
                    instance.Main.Upgrade = upgrade;
                    instance.Reason = "upgrade";
                }
                if (existing is not null)
                {
                    _plugins[manifest.Id] = instance;
                    // a dev plugin reloaded on save keeps its open views: they render again on the new code, with their state (06 §2)
                    var hot = existing.Dev && instance.Dev;
                    await StopAsync(existing, keepSessions: hot);
                    if (existing.Manifest.Version != manifest.Version) instance.Reason = "update";
                    if (hot)
                    {
                        instance.Log("info", "reloaded");
                        _ = Ui.ReloadAsync(manifest.Id);
                    }
                }
                else _plugins[manifest.Id] = instance;
                ScheduleJobs(instance);
                PluginChanged?.Invoke(manifest.Id);
                if (manifest.Activation.Contains("onStartup"))
                    _ = ActivateQuietlyAsync(instance);
            }
            foreach (var gone in _plugins.Keys.Where(id => !seen.Contains(id)).ToList())
                if (_plugins.TryRemove(gone, out var old))
                {
                    await StopAsync(old);
                    PluginChanged?.Invoke(gone);
                }
            Watch(snapshot.Plugins.Where(i => i.Manifest.IsV2 && i.Entry.Enabled && i.Entry.Source == PluginSources.Path).Select(i => i.Entry.Spec));
            Gateway.Invalidate();
            Interlocked.Exchange(ref _generation, snapshot.Generation);
        }
        finally
        {
            _refresh.Release();
        }
    }

    private static string Stamp(InstalledPlugin installed, IReadOnlyList<string> granted)
    {
        var m = installed.Manifest.V2!;
        var stamp = $"{installed.Entry.Spec}|{m.Version}|{string.Join(',', granted.Order(StringComparer.Ordinal))}";
        // a dev folder changes in any file a module imports, so all of them count; an installed package only in its entry files
        var files = installed.Entry.Source == PluginSources.Path ? DevFiles(m.Root) : new[] { m.Main, m.Gateway, Path.Combine(m.Root, PluginV2Versions.ManifestFile) }.OfType<string>();
        foreach (var file in files)
            if (file is not null && new FileInfo(file) is { Exists: true } fi) stamp += $"|{fi.LastWriteTimeUtc.Ticks}:{fi.Length}";
        return stamp;
    }

    // The files of a dev plugin's folder (sorted; no node_modules / .git / dist; at most 2000).
    internal static IEnumerable<string> DevFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(f => !IgnoredDev(Path.GetRelativePath(root, f)))
                .Order(StringComparer.Ordinal).Take(2000).ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static bool IgnoredDev(string relative) =>
        relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0] is "node_modules" or ".git" or "dist" || relative.EndsWith(".tgz", StringComparison.Ordinal);

    // ---- dev plugins: reload on save (06 §2)

    private readonly ConcurrentDictionary<string, (FileSystemWatcher Watcher, Timer Debounce)> _watchers = new();

    /// <summary>How long a burst of saves settles before a dev plugin reloads.</summary>
    internal static TimeSpan ReloadDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    private void Watch(IEnumerable<string> roots)
    {
        var want = roots.Where(Directory.Exists).ToHashSet(StringComparer.Ordinal);
        foreach (var root in _watchers.Keys.Where(r => !want.Contains(r)).ToList())
            if (_watchers.TryRemove(root, out var w))
            {
                w.Watcher.Dispose();
                w.Debounce.Dispose();
            }
        foreach (var root in want.Where(r => !_watchers.ContainsKey(r)))
        {
            try
            {
                var debounce = new Timer(_ =>
                {
                    _catalog.Invalidate();
                    _ = RefreshQuietlyAsync();
                });
                var watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                void Changed(object? sender, FileSystemEventArgs e)
                {
                    if (!IgnoredDev(Path.GetRelativePath(root, e.FullPath))) debounce.Change(ReloadDelay, Timeout.InfiniteTimeSpan);
                }
                watcher.Changed += Changed;
                watcher.Created += Changed;
                watcher.Deleted += Changed;
                watcher.Renamed += (s, e) => Changed(s, e);
                watcher.EnableRaisingEvents = true;
                _watchers[root] = (watcher, debounce);
            }
            catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
            {
                // no watcher (too many open, a network folder): the plugin still runs, it just doesn't reload on save
            }
        }
    }

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or PluginCallException)
        {
        }
    }

    private async Task StopAsync(PluginV2Instance p, bool keepSessions = false)
    {
        if (!keepSessions) await Ui.CloseAllAsync(p.Id);
        _calls.CloseStreams(p.Id);
        foreach (var sub in _subscriptions.Values) sub.TryRemove(p.Id, out _);
        foreach (var key in _nextJob.Keys.Where(k => k.Item1 == p.Id).ToList()) _nextJob.TryRemove(key, out _);
        foreach (var r in _requests.Values.Where(r => r.PluginId == p.Id).ToList()) Answer(r.Id, false);
        await p.StopAsync();
    }

    private async Task ActivateQuietlyAsync(PluginV2Instance p)
    {
        try
        {
            await p.Main.RunAsync(new JsonObject { ["op"] = "event", ["name"] = "__activate" });
        }
        catch (PluginCallException e)
        {
            p.Fail("activate", e.Message);
        }
    }

    /// <summary>
    /// Marks the next activation of <paramref name="pluginId"/> as an upgrade from a v1 plugin: its activate gets
    /// <c>ctx.reason = "upgrade"</c> and <c>ctx.upgrade = { from: … }</c> (design/plugin-v2 07 §3.3).
    /// </summary>
    public void SetUpgrade(string pluginId, JsonObject from) => _upgrades[pluginId] = new JsonObject { ["from"] = from.DeepClone() };

    /// <summary>Activates a plugin now (its activate runs), throwing what it failed with.</summary>
    public async Task ActivateAsync(string pluginId, CancellationToken ct = default)
    {
        var p = await FindAsync(pluginId, ct) ?? throw new PluginCallException("not_found", (await ProblemsAsync(ct)).Values.FirstOrDefault() ?? $"{pluginId} is not running");
        if (p.Manifest.Main is null) return;
        await p.Main.RunAsync(new JsonObject { ["op"] = "event", ["name"] = "__activate" }, TimeSpan.FromSeconds(30), ct: ct);
    }

    // ---- what the host call table and the sessions use

    internal HostCalls Calls => _calls;

    internal void Log(PluginV2Instance p, string level, string message) => p.Log(level, message);

    internal async Task ActivityAsync(PluginV2Instance p, string call, string? target, string outcome)
    {
        try
        {
            await _services.Data.AddActivityAsync(new PluginActivity(p.Id, DateTimeOffset.UtcNow, call, target, outcome));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            p.Log("warn", "activity log: " + e.Message);
        }
    }

    internal void StorageChanged(PluginV2Instance p, string key, string? valueJson) => Gateway.StorageChanged(p.Id, key, valueJson);

    /// <summary>Host events a plugin may subscribe to, and the permission each needs.</summary>
    public static readonly IReadOnlyDictionary<string, string?> HostEvents = new Dictionary<string, string?>
    {
        ["request.completed"] = PluginPermissions.RequestsRead,
        ["provider.changed"] = PluginPermissions.ProvidersRead,
        ["account.changed"] = PluginPermissions.ProvidersRead,
        ["settings.changed"] = null,
        ["alert.fired"] = null,
        ["plugin.configChanged"] = null,
    };

    internal string? Subscribe(PluginV2Instance p, string name)
    {
        if (name.Contains(':'))
        {
            var (owner, ev) = (name[..name.IndexOf(':')], name[(name.IndexOf(':') + 1)..]);
            if (owner != p.Id && !PluginPermissions.Holds(p.Granted, PluginPermissions.Events))
                return "subscribing to another plugin's events needs the permission \"events\"";
            _ = ev;
        }
        else if (!HostEvents.TryGetValue(name, out var need)) return $"\"{name}\" is not an event of this Astra";
        else if (need is not null && !PluginPermissions.Holds(p.Granted, need)) return $"{name} needs the permission \"{need}\"";
        _subscriptions.GetOrAdd(name, _ => new()).TryAdd(p.Id, 0);
        return null;
    }

    /// <summary>
    /// Delivers an event to the plugins subscribed to it: a host event (<c>request.completed</c>, …) or a plugin's own
    /// (<c>acme.guard:hit</c>). Best effort and unordered across names; a plugin that isn't loaded is woken for it.
    /// </summary>
    /// <summary>Whether some plugin listens for <paramref name="name"/> (callers skip building a payload otherwise).</summary>
    public bool HasSubscribers(string name) => _subscriptions.TryGetValue(name, out var s) && !s.IsEmpty;

    public void Publish(string name, JsonNode? payload, string? fromPluginId = null)
    {
        if (!_subscriptions.TryGetValue(name, out var subs)) return;
        var json = payload?.ToJsonString();
        foreach (var id in subs.Keys)
        {
            if (!_plugins.TryGetValue(id, out var p)) continue;
            if (name == "plugin.configChanged" && fromPluginId != id) continue;
            _ = DeliverAsync(p, name, json);
        }
    }

    private static async Task DeliverAsync(PluginV2Instance p, string name, string? json)
    {
        try
        {
            p.Main.Reason = "event";
            await p.Main.RunAsync(new JsonObject { ["op"] = "event", ["name"] = name, ["payload"] = json is null ? null : JsonNode.Parse(json) },
                TimeSpan.FromSeconds(30));
        }
        catch (PluginCallException e)
        {
            p.Fail("event " + name, e.Message);
        }
    }

    // ---- 2.1: providers and clients (design/plugin-v2 08)

    /// <summary>How long a provider's prepare / a client's connect may take (they sit on a request or a client change).</summary>
    public static TimeSpan ProviderCallLimit { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs provider <paramref name="providerId"/>'s <c>what</c> (prepare, models, refresh, usage) in plugin
    /// <paramref name="pluginId"/>: for prepare, <paramref name="request"/> is the request the host is about to send.
    /// </summary>
    /// <exception cref="PluginCallException">No such running plugin or provider, or the plugin failed.</exception>
    public async Task<JsonNode?> ProviderCallAsync(string pluginId, string providerId, string what, JsonObject? request, JsonObject? account, CancellationToken ct)
    {
        var p = await FindAsync(pluginId, ct) ?? throw new PluginCallException("not_found", $"{pluginId} is not running");
        if (p.Manifest.Providers.All(x => x.Id != providerId)) throw new PluginCallException("not_found", $"{pluginId} has no provider {providerId}");
        p.Main.Reason = "provider";
        var op = new JsonObject { ["op"] = "provider." + what, ["provider"] = providerId, ["account"] = account?.DeepClone() };
        if (request is not null) op["request"] = request.DeepClone();
        return await p.Main.RunAsync(op, what == "prepare" ? ProviderCallLimit : TimeSpan.FromSeconds(30), ct: ct);
    }

    /// <summary>Runs one of a provider's reply operations (<c>provider.response.start|frame|end|dispose</c>, 08 §2.2): <paramref name="op"/> is the whole operation.</summary>
    /// <exception cref="PluginCallException">No such running plugin or provider, the plugin failed or took longer than <paramref name="limit"/>.</exception>
    public async Task<JsonNode?> ProviderReplyCallAsync(string pluginId, string providerId, JsonObject op, TimeSpan limit, CancellationToken ct)
    {
        var p = await FindAsync(pluginId, ct) ?? throw new PluginCallException("not_found", $"{pluginId} is not running");
        if (p.Manifest.Providers.All(x => x.Id != providerId)) throw new PluginCallException("not_found", $"{pluginId} has no provider {providerId}");
        p.Main.Reason = "provider";
        return await p.Main.RunAsync(op, limit, ct: ct);
    }

    /// <summary>Runs client <paramref name="clientId"/>'s connect: the keys to write beside the model, or null.</summary>
    public async Task<JsonObject?> ConnectClientAsync(string pluginId, string clientId, JsonObject input, CancellationToken ct)
    {
        var p = await FindAsync(pluginId, ct) ?? throw new PluginCallException("not_found", $"{pluginId} is not running");
        p.Main.Reason = "client";
        return await p.Main.RunAsync(new JsonObject { ["op"] = "client.connect", ["client"] = clientId, ["input"] = input.DeepClone() }, ProviderCallLimit, ct: ct) as JsonObject;
    }

    /// <summary>Runs a command: <c>&lt;pluginId&gt;:&lt;command&gt;</c>.</summary>
    public async Task<JsonNode?> ExecuteCommandAsync(string fullId, JsonNode? args, JsonObject? client, CancellationToken ct)
    {
        var colon = fullId.LastIndexOf(':');
        if (colon <= 0) throw new PluginCallException("not_found", $"\"{fullId}\" is not <plugin>:<command>");
        var p = await FindAsync(fullId[..colon], ct) ?? throw new PluginCallException("not_found", $"no plugin {fullId[..colon]}");
        var id = fullId[(colon + 1)..];
        if (p.Manifest.Commands.All(c => c.Id != id)) throw new PluginCallException("not_found", $"{p.Id} has no command \"{id}\"");
        p.Main.Reason = "command";
        return await p.Main.RunAsync(new JsonObject { ["op"] = "command", ["id"] = id, ["args"] = args?.DeepClone(), ["client"] = client?.DeepClone() },
            client: client, ct: ct);
    }

    internal async Task RunJobAsync(PluginV2Instance p, string id, CancellationToken ct, int attempt = 1)
    {
        p.Main.Reason = "job";
        await p.Main.RunAsync(new JsonObject
        {
            ["op"] = "job", ["id"] = id, ["scheduledAt"] = DateTimeOffset.UtcNow.ToString("O"), ["attempt"] = attempt,
        }, TimeSpan.FromMinutes(10), ct: ct);
    }

    private void ScheduleJobs(PluginV2Instance p)
    {
        foreach (var job in p.Manifest.Jobs)
            _nextJob[(p.Id, job.Id)] = NextRun(job, DateTimeOffset.UtcNow);
    }

    private static DateTimeOffset NextRun(JobContribution job, DateTimeOffset after)
    {
        if (job.Every is { } every) return after + every;
        if (job.Cron is { } text && Cron.TryParse(text, out var cron) && cron.Next(after.LocalDateTime) is { } next)
            return new DateTimeOffset(DateTime.SpecifyKind(next, DateTimeKind.Local));
        return DateTimeOffset.MaxValue;
    }

    // Jobs that are due, and idle main contexts to unload. A missed run (the server was busy or asleep) runs once.
    private async Task TickAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var ((pluginId, jobId), due) in _nextJob)
                {
                    if (due > now || !_plugins.TryGetValue(pluginId, out var p)) continue;
                    var job = p.Manifest.Jobs.FirstOrDefault(j => j.Id == jobId);
                    if (job is null) continue;
                    _nextJob[(pluginId, jobId)] = NextRun(job, now);
                    _ = RunScheduledAsync(p, jobId);
                }
                foreach (var p in _plugins.Values)
                {
                    if (p.MainCreated && p.Main.Loaded && p.Main.Idle > IdleUnload && !Ui.HasSessions(p.Id) && !HasSubscriptions(p.Id))
                        await p.Main.UnloadIfIdleAsync();
                    if (p.Healthy && p.RecentFaults() >= FaultLimit) await Disable(p, "its engine was stopped by a limit too often");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private bool HasSubscriptions(string pluginId) => _subscriptions.Values.Any(s => s.ContainsKey(pluginId));

    private async Task RunScheduledAsync(PluginV2Instance p, string jobId)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                await RunJobAsync(p, jobId, _stop.Token, attempt);
                return;
            }
            catch (PluginCallException e)
            {
                p.Fail("job " + jobId, e.Message);
                if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(30 * attempt), _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task Disable(PluginV2Instance p, string why)
    {
        p.Healthy = false;
        p.Log("error", "stopped: " + why);
        _services.Notify(p.Id, p.Manifest.DisplayName("en"), "stopped: " + why, "danger");
        await StopAsync(p);
    }

    // ---- runtime permission requests (permissions.request)

    /// <summary>The requests waiting for the user.</summary>
    public IReadOnlyList<PermissionRequest> PendingRequests => [.. _requests.Values.OrderBy(r => r.At)];

    /// <summary>Told when a request is added or answered (the shells show the host's own dialog).</summary>
    public event Action<PermissionRequest>? RequestsChanged;

    internal async Task<bool> RequestPermissionsAsync(PluginV2Instance p, List<string> asked, CancellationToken ct)
    {
        var allowed = p.Manifest.OptionalPermissions.Concat(p.Manifest.Permissions).ToHashSet();
        var unknown = asked.Where(a => !allowed.Contains(a)).ToList();
        if (unknown.Count > 0)
            throw new PluginCallException("invalid_argument", "not in optionalPermissions: " + string.Join(", ", unknown));
        var missing = asked.Where(a => !p.Granted.Contains(a)).ToList();
        if (missing.Count == 0) return true;
        var request = new PermissionRequest(Guid.NewGuid().ToString("n"), p.Id, p.Manifest.DisplayName("en"), missing, DateTimeOffset.UtcNow);
        _requests[request.Id] = request;
        RequestsChanged?.Invoke(request);
        await ActivityAsync(p, "permissions.request", string.Join(" ", missing), "asked");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            var ok = await request.Answer.Task.WaitAsync(timeout.Token);
            return ok;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _requests.TryRemove(request.Id, out _);
        }
    }

    /// <summary>The user's answer to a runtime permission request; true when there was such a request.</summary>
    public bool Answer(string requestId, bool allow)
    {
        if (!_requests.TryGetValue(requestId, out var r)) return false;
        if (allow && _plugins.TryGetValue(r.PluginId, out var p))
        {
            var granted = p.Granted.Concat(r.Permissions).Distinct().ToList();
            _services.Data.SetGrantsAsync(p.Id, granted).GetAwaiter().GetResult();
            p.Granted = granted;
            _ = ActivityAsync(p, "permissions.request", string.Join(" ", r.Permissions), "granted");
        }
        r.Answer.TrySetResult(allow);
        RequestsChanged?.Invoke(r);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var (watcher, debounce) in _watchers.Values)
        {
            watcher.Dispose();
            await debounce.DisposeAsync();
        }
        _watchers.Clear();
        try
        {
            await _ticker;
        }
        catch (OperationCanceledException)
        {
        }
        foreach (var p in _plugins.Values) await p.StopAsync();
    }
}

/// <summary>A permission a plugin asked for at run time, waiting for the user.</summary>
public sealed record PermissionRequest(string Id, string PluginId, string PluginName, IReadOnlyList<string> Permissions, DateTimeOffset At)
{
    internal TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>A loaded, enabled v2 plugin: its manifest, grants, settings, log and main context.</summary>
public sealed class PluginV2Instance
{
    private readonly PluginV2Host _host;
    private readonly Lazy<PluginMain> _main;
    private readonly ConcurrentQueue<PluginLogLine> _lines = new();
    private readonly ConcurrentQueue<DateTimeOffset> _faults = new();
    private JsonObject? _messages;
    private long _minute;
    private int _linesThisMinute;

    internal PluginV2Instance(PluginV2Host host, InstalledPlugin installed, PluginV2Manifest manifest, IReadOnlyList<string> granted, string stamp)
    {
        _host = host;
        Installed = installed;
        Manifest = manifest;
        Granted = granted;
        Stamp = stamp;
        SortOrder = installed.Entry.SortOrder;
        Dev = installed.Entry.Source == PluginSources.Path;
        OptionsJson = installed.Entry.OptionsJson;
        _main = new Lazy<PluginMain>(() => new PluginMain(this, (m, n, a) => host.Calls.Sync(m.Plugin, n, a), (m, n, a, ct) => host.Calls.AsyncCall(m.Plugin, n, a, ct), (level, line) => Log(level, line)));
    }

    public string Id => Manifest.Id;

    public string Spec => Installed.Entry.Spec;

    public InstalledPlugin Installed { get; }

    public PluginV2Manifest Manifest { get; }

    /// <summary>Granted permissions (install, upgrade, or a runtime request).</summary>
    public IReadOnlyList<string> Granted { get; internal set; }

    /// <summary>A plugin loaded from a folder (development): loopback http allowed, reloaded on save.</summary>
    public bool Dev { get; }

    public int SortOrder { get; internal set; }

    public bool Healthy { get; internal set; } = true;

    internal string Stamp { get; }

    /// <summary>The settings as stored (options_json); kept current without a restart.</summary>
    public string? OptionsJson { get; internal set; }

    internal string Reason
    {
        set => Main.Reason = value;
    }

    internal PluginMain Main => _main.Value;

    internal bool MainCreated => _main.IsValueCreated;

    /// <summary>The newest log lines (console, astra.log, failures), oldest first.</summary>
    public IReadOnlyList<PluginLogLine> Lines => [.. _lines];

    /// <summary>The last failure of a hook, view, command, job or event, for the plugin page.</summary>
    public string? LastError { get; private set; }

    /// <summary>Whether the main context is loaded now.</summary>
    public bool Running => MainCreated && Main.Loaded;

    /// <summary>The settings: options_json over the configuration schema's defaults (secret settings live in secrets, not here).</summary>
    public JsonObject Config()
    {
        var config = new JsonObject();
        if (Manifest.Configuration is { } schema)
            foreach (var prop in schema.Properties.Where(p => !p.Secret && p.Default is not null))
                config[prop.Name] = prop.Default!.DeepClone();
        if (OptionsJson is { Length: > 0 } json)
        {
            try
            {
                if (JsonNode.Parse(json) is JsonObject o)
                    foreach (var (k, v) in o)
                        if (Manifest.Configuration?.Properties.FirstOrDefault(p => p.Name == k) is not { Secret: true })
                            config[k] = v?.DeepClone();
            }
            catch (JsonException)
            {
            }
        }
        return config;
    }

    /// <summary>One setting's value as text (for net:{config.x}); null when unset or not text.</summary>
    public string? Setting(string name) => Config()[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The plugin's translations: locales/&lt;lang&gt;.json, as { lang: { key: text } }.</summary>
    public JsonObject Messages()
    {
        if (_messages is not null) return _messages;
        var all = new JsonObject();
        var dir = Path.Combine(Manifest.Root, "locales");
        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    if (new FileInfo(file).Length < 1024 * 1024 && JsonNode.Parse(File.ReadAllText(file)) is JsonObject o)
                        all[Path.GetFileNameWithoutExtension(file)] = o;
                }
                catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
                {
                    Log("warn", $"locales/{Path.GetFileName(file)}: {e.Message}");
                }
            }
        return _messages = all;
    }

    internal void Log(string level, string line)
    {
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        if (Interlocked.Exchange(ref _minute, minute) != minute) Interlocked.Exchange(ref _linesThisMinute, 0);
        if (Interlocked.Increment(ref _linesThisMinute) > 120) return;
        var clipped = line.Length > 2000 ? line[..2000] + "…" : line;
        _lines.Enqueue(new PluginLogLine(DateTimeOffset.UtcNow, level, clipped));
        while (_lines.Count > 300) _lines.TryDequeue(out _);
        _host.Services.Log(Id, level, clipped);
    }

    internal void Fail(string what, string message)
    {
        LastError = $"{what}: {message}";
        Log("error", LastError);
        if (message.Contains("took too long", StringComparison.Ordinal) || message.Contains("limit", StringComparison.Ordinal))
            _faults.Enqueue(DateTimeOffset.UtcNow);
    }

    internal int RecentFaults()
    {
        var since = DateTimeOffset.UtcNow - PluginV2Host.FaultWindow;
        while (_faults.TryPeek(out var t) && t < since) _faults.TryDequeue(out _);
        return _faults.Count + (MainCreated ? 0 : 0);
    }

    internal async Task StopAsync()
    {
        if (_main.IsValueCreated) await _main.Value.DisposeAsync();
    }
}

/// <summary>A line of a v2 plugin's log.</summary>
public sealed record PluginLogLine(DateTimeOffset At, string Level, string Text);
