using System.Diagnostics;
using System.Text;
using Astra.Plugins.Js;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Astra.Plugins.Middleware;

/// <summary>
/// The gateway middleware in force: every enabled plugin with a middleware module, in the list's order,
/// loaded again when the list or a middleware's file changes (looked at once a second, as magpie does).
/// <see cref="BeginAsync"/> starts one request's run; it is null when there is no middleware, so a
/// gateway without plugins pays nothing.
/// </summary>
public sealed class MiddlewareEngine(PluginCatalog catalog, Action<string, string> log)
{
    /// <summary>How long a hook may run before it is stopped and its input kept as it was.</summary>
    public static readonly TimeSpan RequestLimit = TimeSpan.FromMilliseconds(250);

    /// <summary>The limit for one event of a stream.</summary>
    public static readonly TimeSpan EventLimit = TimeSpan.FromMilliseconds(50);

    /// <summary>The limit for loading a middleware (evaluating its module).</summary>
    public static readonly TimeSpan LoadLimit = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private volatile MiddlewareSet? _current;
    private long _nextCheck;

    /// <summary>Starts a request's middleware; null when there is none.</summary>
    public async ValueTask<MiddlewareRun?> BeginAsync(MiddlewareInfo info, CancellationToken ct = default)
    {
        var set = await CurrentAsync(ct);
        return set.Modules.Count == 0 ? null : new MiddlewareRun(set, info);
    }

    /// <summary>Each middleware plugin's state, by spec: its hooks and counts, or why it didn't load.</summary>
    public async Task<Dictionary<string, MiddlewareState>> StatesAsync(CancellationToken ct = default)
    {
        var set = await CurrentAsync(ct);
        var states = new Dictionary<string, MiddlewareState>();
        foreach (var (spec, error) in set.Errors) states[spec] = new MiddlewareState([], null, error, 0, 0, 0, null);
        foreach (var m in set.Modules) states[m.Spec] = m.State();
        return states;
    }

    /// <summary>Drops the middleware loaded, so the next request loads it again.</summary>
    public void Reload() => _current = null;

    private async ValueTask<MiddlewareSet> CurrentAsync(CancellationToken ct)
    {
        var s = _current;
        var now = Stopwatch.GetTimestamp();
        var next = Interlocked.Read(ref _nextCheck);
        if (s is not null && (now < next || Interlocked.CompareExchange(ref _nextCheck, now + Stopwatch.Frequency, next) != next))
            return s;
        var found = await FindAsync(ct);
        var stamp = Stamp(found);
        if (s is not null && s.Stamp == stamp) return s;
        await _loadLock.WaitAsync(ct);
        try
        {
            if (_current is { } again && again.Stamp == stamp) return again;
            var loaded = Load(found, stamp);
            _current = loaded;
            return loaded;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task<(long Generation, List<InstalledPlugin> Plugins)> FindAsync(CancellationToken ct)
    {
        var snapshot = await catalog.SnapshotAsync(ct);
        return (snapshot.Generation, snapshot.Plugins.Where(p => p.Entry.Enabled && p.Manifest.MiddlewareFile is not null).ToList());
    }

    // The list's generation, and each middleware file as it is on disk: a plugin under development
    // (a path) is picked up by the next request after a save.
    private static string Stamp((long Generation, List<InstalledPlugin> Plugins) found)
    {
        var b = new StringBuilder().Append(found.Generation);
        foreach (var p in found.Plugins)
        {
            var file = p.Manifest.MiddlewareFile!;
            b.Append('|').Append(file);
            var fi = new FileInfo(file);
            if (fi.Exists) b.Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(':').Append(fi.Length);
        }
        return b.ToString();
    }

    private MiddlewareSet Load((long Generation, List<InstalledPlugin> Plugins) found, string stamp)
    {
        var modules = new List<MiddlewareModule>();
        var errors = new Dictionary<string, string>();
        foreach (var p in found.Plugins)
        {
            try
            {
                var options = Encoding.UTF8.GetBytes(p.Entry.OptionsJson is { Length: > 0 } o ? o : "{}");
                modules.Add(MiddlewareModule.Load(p.Entry.Spec, Store.PluginSpec.ShortName(p.Entry.Name), p.Manifest.ModuleRoot,
                    p.Manifest.MiddlewareFile!, options, log));
            }
            catch (Exception e) when (e is JsHookException or JsLoadException or IOException or UnauthorizedAccessException)
            {
                errors[p.Entry.Spec] = e.Message;
                log("warn", $"middleware {p.Entry.Name} didn't load: {e.Message}");
            }
        }
        return new MiddlewareSet(stamp, modules, errors);
    }
}

/// <summary>The middleware loaded at one stamp.</summary>
internal sealed record MiddlewareSet(string Stamp, List<MiddlewareModule> Modules, Dictionary<string, string> Errors);

/// <summary>What a request's hooks are told of it, in ctx.</summary>
/// <param name="Protocol">The agent's API: anthropic, chat, responses or gemini.</param>
/// <param name="Agent">The client that sent it; null for a direct call.</param>
public sealed record MiddlewareInfo(string Protocol, string? Model, bool Stream, string Path, string? Agent);

/// <summary>A request a middleware turned away (ctx.reject): the agent gets it in its API's error shape.</summary>
public sealed record MiddlewareReject(int Status, string Message);

/// <summary>
/// The middleware of one request (magpie's middleware.Run). Each middleware's hooks run in one engine for
/// the whole request, taken from its pool on first use and given back by <see cref="Dispose"/>.
/// </summary>
public sealed class MiddlewareRun : IDisposable
{
    private readonly MiddlewareSet _set;
    private readonly string _info;
    private readonly MiddlewareInstance?[] _instances;
    private readonly ObjectInstance?[] _contexts;
    private MiddlewareReject? _rejected;

    internal MiddlewareRun(MiddlewareSet set, MiddlewareInfo info)
    {
        _set = set;
        var b = new StringBuilder("{\"protocol\":").Append(Quote(info.Protocol))
            .Append(",\"model\":").Append(Quote(info.Model ?? ""))
            .Append(",\"stream\":").Append(info.Stream ? "true" : "false")
            .Append(",\"path\":").Append(Quote(info.Path));
        if (!string.IsNullOrEmpty(info.Agent)) b.Append(",\"agent\":").Append(Quote(info.Agent));
        _info = b.ToString();
        _instances = new MiddlewareInstance?[set.Modules.Count];
        _contexts = new ObjectInstance?[set.Modules.Count];
    }

    /// <summary>Whether some middleware wants the reply's stream events.</summary>
    public bool WantsEvents => _set.Modules.Any(m => m.HasEvent);

    /// <summary>Whether some middleware wants a whole JSON reply.</summary>
    public bool WantsResponse => _set.Modules.Any(m => m.HasResponse);

    /// <summary>
    /// Runs each onRequest on <paramref name="body"/> (the agent's JSON, in its own API), in turn: what one
    /// returns is what the next is given; undefined leaves it as it was, and so does a hook that throws
    /// or takes too long. A non-null reject is a request a hook turned away.
    /// </summary>
    public (byte[] Body, MiddlewareReject? Reject) Request(byte[] body)
    {
        for (var i = 0; i < _set.Modules.Count; i++)
        {
            var m = _set.Modules[i];
            if (!m.HasRequest) continue;
            var (output, error) = Apply(i, Hook.Request, MiddlewareEngine.RequestLimit, body, null);
            if (error is not null)
            {
                m.Fail("onRequest", error);
                continue;
            }
            if (_rejected is not null) return (body, _rejected);
            if (output is not null) body = output;
        }
        return (body, null);
    }

    /// <summary>
    /// Runs each onEvent that wants it on one complete event of the stream (with its blank line). Returns
    /// the bytes to write — the event as it was when nothing changed it — or null when a hook dropped it.
    /// </summary>
    public byte[]? Event(byte[] ev)
    {
        if (!SseEventFramer.TrySplit(ev, out var name, out var start, out var end)) return ev;
        byte[]? data = null;
        for (var i = 0; i < _set.Modules.Count; i++)
        {
            var m = _set.Modules[i];
            if (!m.HasEvent || (m.Events is not null && !m.Events.Contains(name))) continue;
            var (output, error) = Apply(i, Hook.Event, MiddlewareEngine.EventLimit, data ?? ev[start..end], null);
            if (error == Dropped) return null;
            if (error is not null) m.Fail("onEvent", error);
            else if (output is not null) data = output;
        }
        if (data is null) return ev;
        var result = new byte[start + data.Length + (ev.Length - end)];
        ev.AsSpan(0, start).CopyTo(result);
        data.CopyTo(result, start);
        ev.AsSpan(end).CopyTo(result.AsSpan(start + data.Length));
        return result;
    }

    /// <summary>Runs each onResponse on a whole JSON reply, with <c>ctx.status</c> set; returns the body to write.</summary>
    public byte[] Response(byte[] body, int status)
    {
        for (var i = 0; i < _set.Modules.Count; i++)
        {
            var m = _set.Modules[i];
            if (!m.HasResponse) continue;
            var (output, error) = Apply(i, Hook.Response, MiddlewareEngine.RequestLimit, body, status);
            if (error == Dropped) m.Fail("onResponse", "returned null; a whole reply can't be dropped");
            else if (error is not null) m.Fail("onResponse", error);
            else if (output is not null) body = output;
        }
        return body;
    }

    /// <summary>Gives the engines back to their pools.</summary>
    public void Dispose()
    {
        for (var i = 0; i < _instances.Length; i++)
        {
            if (_instances[i] is { } instance) _set.Modules[i].Put(instance);
            _instances[i] = null;
            _contexts[i] = null;
        }
    }

    private const string Dropped = "dropped";

    private enum Hook
    {
        Request,
        Event,
        Response,
    }

    // Calls a hook on JSON data: the JSON it returned, null for undefined (keep), or an error — Dropped
    // for null. A call's cost is all it costs the request: the JSON read into the engine and written
    // back, which is most of it on a large body, as well as the hook.
    private (byte[]? Output, string? Error) Apply(int i, Hook hook, TimeSpan limit, ReadOnlySpan<byte> data, int? status)
    {
        var m = _set.Modules[i];
        var started = Stopwatch.GetTimestamp();
        try
        {
            var (instance, ctx) = Acquire(i);
            if (status is { } s) ctx.Set("status", s);
            var rt = instance.Runtime;
            var fn = hook switch
            {
                Hook.Request => instance.OnRequest,
                Hook.Event => instance.OnEvent,
                _ => instance.OnResponse,
            };
            var result = rt.Call(fn, limit, rt.ParseJson(data), ctx);
            if (result.IsUndefined()) return (null, null);
            if (result.IsNull()) return (null, Dropped);
            if (!result.IsObject()) return (null, "returned neither an object, null nor undefined");
            return (rt.ToJsonBytes(result), null);
        }
        catch (JsHookException e)
        {
            return (null, e.Message);
        }
        catch (Jint.Runtime.JavaScriptException e)
        {
            // a JSON.parse of the data itself (it was not JSON after all)
            return (null, "SyntaxError: " + e.Message);
        }
        finally
        {
            m.Count(Stopwatch.GetElapsedTime(started).Ticks * 100);
        }
    }

    // Middleware i's engine for this request and its ctx: the request's info, the plugin's options, a
    // state of its own and reject.
    private (MiddlewareInstance, ObjectInstance) Acquire(int i)
    {
        if (_instances[i] is { } existing) return (existing, _contexts[i]!);
        var m = _set.Modules[i];
        var instance = m.Get();
        var rt = instance.Runtime;
        var json = new StringBuilder(_info).Append(",\"options\":").Append(Encoding.UTF8.GetString(m.Options)).Append(",\"state\":{}}").ToString();
        var ctx = rt.ParseJson(json).AsObject();
        ctx.Set("reject", rt.Function("reject", 2, Reject));
        _instances[i] = instance;
        _contexts[i] = ctx;
        return (instance, ctx);
    }

    // ctx.reject(status, message): onRequest turns the request away. Only the first call counts.
    private JsValue Reject(JsValue[] args)
    {
        var status = 403;
        if (args.Length > 0 && args[0].IsNumber() && args[0].AsNumber() is var n && n >= 400 && n <= 599) status = (int)n;
        var message = args.Length > 1 && args[1].IsString() ? args[1].AsString() : "turned away by a gateway middleware";
        _rejected ??= new MiddlewareReject(status, message);
        return JsValue.Undefined;
    }

    private static string Quote(string s) => "\"" + System.Text.Json.JsonEncodedText.Encode(s).Value + "\"";
}
