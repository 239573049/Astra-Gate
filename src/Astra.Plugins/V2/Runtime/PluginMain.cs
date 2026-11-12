using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Astra.Plugins.Js;
using Jaunt;

namespace Astra.Plugins.V2.Runtime;

/// <summary>
/// A v2 plugin's main context (design/plugin-v2 01 §3): one engine, driven like an actor. Every touch of the engine is a
/// work item on one queue, run one at a time — starting an operation (activate, a view's init / render / action, a
/// command, a job, an event) or settling a host call it is waiting on. While an operation awaits a host call (an HTTP
/// request, storage), other operations run. The engine holds no session data (views' state lives with the host), so
/// it can be replaced at any time: a limit hit faults it, a worn one is retired, an idle one is unloaded, and the next
/// operation loads the module again and calls activate.
/// </summary>
internal sealed class PluginMain : IAsyncDisposable
{
    /// <summary>CPU time one slice of JS may take (an operation's start, or a continuation after a host call).</summary>
    public static readonly TimeSpan SliceLimit = TimeSpan.FromSeconds(1);

    /// <summary>How long a render may take.</summary>
    public static readonly TimeSpan RenderLimit = TimeSpan.FromMilliseconds(250);

    /// <summary>Loading the module (and the boot) may take this long.</summary>
    public static readonly TimeSpan LoadLimit = TimeSpan.FromSeconds(2);

    /// <summary>An operation's wall-clock limit unless the caller gives one (it may await host calls).</summary>
    public static readonly TimeSpan DefaultWall = TimeSpan.FromSeconds(60);

    private readonly PluginV2Instance _plugin;
    private readonly Func<PluginMain, string, string?, HostCallResult> _sync;
    private readonly Func<PluginMain, string, string?, CancellationToken, Task<HostCallResult>> _async;
    private readonly Action<string, string> _log;
    private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;
    private readonly Dictionary<long, TaskCompletionSource<JsonNode?>> _ops = [];
    private readonly Dictionary<long, CancellationTokenSource> _tickets = [];
    private readonly SemaphoreSlim _activation = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private JsRuntime? _rt;
    private JsValue _dispatch, _settle, _adopt;
    private int _generation;
    private long _nextOp, _nextTicket;
    private bool _active;
    private long _lastUsed = Stopwatch.GetTimestamp();
    private string _reason = "startup";

    private readonly string? _entry;
    private readonly string _context;
    private readonly IReadOnlyDictionary<string, (string Id, Jaunt.JsModule Module)> _modules;

    /// <param name="entry">The module to load instead of the manifest's main (a test file, for <c>astra plugin test</c>).</param>
    /// <param name="context">The boot's context: "main", or "test" (06 §4: session-bound calls answered in the engine).</param>
    /// <param name="modules">The virtual modules it may import (default: <see cref="V2Assets.VirtualModules"/>).</param>
    public PluginMain(PluginV2Instance plugin, Func<PluginMain, string, string?, HostCallResult> sync,
        Func<PluginMain, string, string?, CancellationToken, Task<HostCallResult>> async, Action<string, string> log,
        string? entry = null, string context = "main", IReadOnlyDictionary<string, (string Id, Jaunt.JsModule Module)>? modules = null)
    {
        _entry = entry;
        _context = context;
        _modules = modules ?? V2Assets.VirtualModules;
        _plugin = plugin;
        _sync = sync;
        _async = async;
        _log = log;
        _loop = Task.Run(LoopAsync);
    }

    public PluginV2Instance Plugin => _plugin;

    /// <summary>Whether an engine is loaded and activated now.</summary>
    public bool Loaded => Volatile.Read(ref _active);

    /// <summary>Operations and host calls in flight.</summary>
    public int Busy
    {
        get
        {
            lock (_ops) return _ops.Count + _tickets.Count;
        }
    }

    /// <summary>How long since an operation last ran.</summary>
    public TimeSpan Idle => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUsed));

    /// <summary>The last time an engine was lost to a limit, and why (the plugin page shows it).</summary>
    public string? LastFault { get; private set; }

    /// <summary>Engines lost to limits; several in a short time make the host stop the plugin.</summary>
    public int Faults { get; private set; }

    /// <summary>Why the next activation happens: startup, view, command, job, event, upgrade, restart.</summary>
    public string Reason
    {
        set => _reason = value;
    }

    /// <summary>Extra context the next activate gets (ctx.upgrade on the first run after an upgrade).</summary>
    public JsonNode? Upgrade { get; set; }

    /// <summary>
    /// Runs one operation (<c>{ op: … }</c>) and returns its JSON result. Activates the plugin first when it isn't.
    /// </summary>
    /// <exception cref="PluginCallException">The plugin threw, a limit stopped it, or it took longer than <paramref name="wall"/>.</exception>
    public async Task<JsonNode?> RunAsync(JsonObject op, TimeSpan? wall = null, TimeSpan? slice = null, JsonObject? client = null, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _lastUsed, Stopwatch.GetTimestamp());
        await EnsureActiveAsync(client, ct);
        return await OpAsync(op, wall ?? DefaultWall, slice ?? SliceLimit, ct);
    }

    /// <summary>Unloads the engine when nothing is in flight (idle unload, a retired engine); true when it did.</summary>
    public async Task<bool> UnloadIfIdleAsync()
    {
        if (!await _activation.WaitAsync(0)) return false;
        try
        {
            if (Busy > 0 || !_active) return false;
            await PostAsync(() =>
            {
                lock (_ops)
                    if (_ops.Count + _tickets.Count > 0) return;
                Drop(null);
            });
            return !_active;
        }
        finally
        {
            _activation.Release();
        }
    }

    /// <summary>Stops the plugin: deactivate (briefly), then drop the engine.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_active)
        {
            try
            {
                await OpAsync(new JsonObject { ["op"] = "deactivate" }, TimeSpan.FromSeconds(2), SliceLimit, CancellationToken.None);
            }
            catch (PluginCallException)
            {
            }
        }
        await PostAsync(() => Drop(null));
        _stopping.Cancel();
        _queue.Writer.TryComplete();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EnsureActiveAsync(JsonObject? client, CancellationToken ct)
    {
        if (Volatile.Read(ref _active) && _rt is { IsFaulted: false }) return;
        await _activation.WaitAsync(ct);
        try
        {
            if (_active && _rt is { IsFaulted: false }) return;
            await PostAsync(Load);
            var activate = new JsonObject
            {
                ["op"] = "activate",
                ["reason"] = _reason,
                ["plugin"] = new JsonObject { ["id"] = _plugin.Id, ["version"] = _plugin.Manifest.Version },
            };
            if (Upgrade is not null) activate["upgrade"] = Upgrade.DeepClone();
            if (client is not null) activate["client"] = client.DeepClone();
            await OpAsync(activate, TimeSpan.FromSeconds(30), SliceLimit, ct);
            Upgrade = null;
            _reason = "restart";
            Volatile.Write(ref _active, true);
        }
        finally
        {
            _activation.Release();
        }
    }

    // On the loop: a new engine with the boot run and the module loaded, its exports handed to the boot.
    private void Load()
    {
        Drop(null);
        var main = _entry ?? _plugin.Manifest.Main ?? throw new PluginCallException("not_supported", "the plugin has no main module");
        var loader = new JsModuleLoader(_plugin.Manifest.Root, main, "a v2 plugin", _modules);
        var rt = new JsRuntime(loader, (level, line) => _log(level, line));
        try
        {
            var gen = ++_generation;
            var boot = rt.Evaluate("(" + V2Assets.Boot + ")", LoadLimit);
            var call = rt.Function("call", 2, a => Call(gen, a));
            var sync = rt.Function("sync", 2, a => Sync(a));
            var done = rt.Function("done", 3, a => Done(a));
            var api = rt.Call(boot, LoadLimit, call, sync, done, _context);
            _dispatch = rt.Get(api, "dispatch");
            _settle = rt.Get(api, "settle");
            _adopt = rt.Get(api, "adopt");
            rt.Load(LoadLimit);
            foreach (var name in rt.ExportNames)
                rt.Call(_adopt, LoadLimit, name, rt.Export(name));
            _rt = rt;
        }
        catch (Exception e)
        {
            rt.Dispose();
            var message = e is JsHookException or JsLoadException ? e.Message : e.GetType().Name + ": " + e.Message;
            throw new PluginCallException("load_failed", message);
        }
    }

    private async Task<JsonNode?> OpAsync(JsonObject op, TimeSpan wall, TimeSpan slice, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextOp);
        op["opId"] = id;
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_ops) _ops[id] = tcs;
        var json = op.ToJsonString();
        await PostAsync(() =>
        {
            var rt = _rt ?? throw new PluginCallException("unavailable", "the plugin is not loaded");
            Slice(rt, () => rt.Call(_dispatch, slice, json));
        }).ContinueWith(t =>
        {
            if (t.Exception?.InnerException is { } e) tcs.TrySetException(e is PluginCallException ? e : new PluginCallException("failed", e.Message));
        }, TaskScheduler.Default);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wall);
        try
        {
            return await tcs.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PluginCallException("timeout", $"{op["op"]} took longer than {wall.TotalSeconds:0.#} s");
        }
        finally
        {
            lock (_ops) _ops.Remove(id);
        }
    }

    // Runs a slice of JS; a limit that stops the engine fails everything waiting on it, and drops it.
    private void Slice(JsRuntime rt, Action body)
    {
        try
        {
            body();
        }
        catch (JsHookException e) when (rt.IsFaulted)
        {
            LastFault = e.Message;
            Faults++;
            Drop(new PluginCallException("limit", e.Message));
            throw new PluginCallException("limit", e.Message);
        }
        catch (JsHookException e)
        {
            throw new PluginCallException("failed", e.Message);
        }
        if (rt.Worn && !rt.IsFaulted)
        {
            // past its allocation budget: let what is in flight finish, then the idle check replaces it
            _log("debug", "engine worn; it will be replaced when idle");
        }
    }

    private void Drop(PluginCallException? why)
    {
        Volatile.Write(ref _active, false);
        _generation++;
        List<TaskCompletionSource<JsonNode?>> waiting;
        List<CancellationTokenSource> tickets;
        lock (_ops)
        {
            waiting = why is null ? [] : [.. _ops.Values];
            tickets = [.. _tickets.Values];
            _tickets.Clear();
        }
        foreach (var t in tickets) t.Cancel();
        foreach (var w in waiting) w.TrySetException(why!);
        _rt?.Dispose();
        _rt = null;
    }

    // ---- the three doors

    private JsValue Call(int generation, HostArgs a)
    {
        var name = a[0].IsString ? a[0].AsString() : "";
        var args = a[1].IsString ? a[1].AsString() : null;
        var ticket = Interlocked.Increment(ref _nextTicket);
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        lock (_ops) _tickets[ticket] = cts;
        _ = RunHostCallAsync(generation, ticket, name, args, cts);
        return (double)ticket;
    }

    private async Task RunHostCallAsync(int generation, long ticket, string name, string? args, CancellationTokenSource cts)
    {
        HostCallResult result;
        try
        {
            result = await _async(this, name, args, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = HostCallResult.Fail("cancelled", "cancelled");
        }
        catch (Exception e)
        {
            result = HostCallResult.Fail("unavailable", e.Message);
        }
        lock (_ops)
        {
            if (!_tickets.Remove(ticket)) return;
        }
        cts.Dispose();
        _ = PostAsync(() =>
        {
            if (generation != _generation || _rt is not { } rt) return;
            try
            {
                Slice(rt, () => rt.Call(_settle, SliceLimit, (double)ticket, result.Ok, result.Json ?? ""));
            }
            catch (PluginCallException)
            {
                // a continuation that threw rejects its own operation through done(); a fault already failed them all
            }
        });
    }

    private JsValue Sync(HostArgs a)
    {
        var name = a[0].IsString ? a[0].AsString() : "";
        var args = a[1].IsString ? a[1].AsString() : null;
        HostCallResult r;
        try
        {
            r = _sync(this, name, args);
        }
        catch (Exception e)
        {
            r = HostCallResult.Fail("unavailable", e.Message);
        }
        return r.Ok ? "{\"result\":" + (r.Json is { Length: > 0 } j ? j : "null") + "}" : "{\"error\":" + r.Json + "}";
    }

    private JsValue Done(HostArgs a)
    {
        var id = a[0].IsNumber ? (long)a[0].AsNumber() : -1;
        var ok = a[1].IsBoolean && a[1].AsBoolean();
        var json = a[2].IsString ? a[2].AsString() : "null";
        TaskCompletionSource<JsonNode?>? tcs;
        lock (_ops) _ops.TryGetValue(id, out tcs);
        if (tcs is null) return JsValue.Undefined;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            node = null;
        }
        if (ok) tcs.TrySetResult(node);
        else
        {
            var o = node as JsonObject;
            var code = o?["code"]?.GetValue<string>() ?? "failed";
            var name = o?["name"]?.GetValue<string>() ?? "Error";
            var message = o?["message"]?.GetValue<string>() ?? "failed";
            tcs.TrySetException(new PluginCallException(code, name is "Error" or "AstraError" ? message : name + ": " + message, o?["details"]?.DeepClone()));
        }
        return JsValue.Undefined;
    }

    // ---- the loop

    private Task PostAsync(Action work)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(() =>
            {
                try
                {
                    work();
                    tcs.TrySetResult();
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
            }))
            tcs.TrySetException(new PluginCallException("unavailable", "the plugin was stopped"));
        return tcs.Task;
    }

    private async Task LoopAsync()
    {
        await foreach (var work in _queue.Reader.ReadAllAsync())
            work();
    }
}

/// <summary>A host call's outcome as the boot reads it: ok with JSON, or an error object { code, message }.</summary>
internal readonly record struct HostCallResult(bool Ok, string? Json)
{
    public static HostCallResult Value(JsonNode? node) => new(true, node?.ToJsonString() ?? "null");

    public static HostCallResult Raw(string json) => new(true, json);

    public static HostCallResult Fail(string code, string message) =>
        new(false, new JsonObject { ["code"] = code, ["message"] = message }.ToJsonString());
}

/// <summary>An operation a v2 plugin couldn't do: <see cref="Code"/> is the error code, the message what to show.</summary>
public sealed class PluginCallException(string code, string message, JsonNode? details = null) : Exception(message)
{
    public string Code { get; } = code;

    public JsonNode? Details { get; } = details;
}
