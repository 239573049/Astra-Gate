using System.Collections.Concurrent;
using Astra.Plugins.Js;
using Jint;
using Jint.Native;

namespace Astra.Plugins.Middleware;

/// <summary>
/// One loaded gateway middleware: its module (parsed once), which hooks it has, the stream events its
/// onEvent wants, and a pool of engines with the module evaluated — a request takes one for all its
/// hooks, so they share <c>ctx.state</c>, and module globals persist across requests as in magpie.
/// </summary>
internal sealed class MiddlewareModule
{
    private const int LinesAMinute = 60;
    private const int MaxPooled = 64;

    private readonly JsModuleLoader _loader;
    private readonly Action<string, string> _log;
    private readonly ConcurrentBag<MiddlewareInstance> _pool = [];
    private long _calls, _nanos, _failures, _logged;
    private string? _lastError;

    private MiddlewareModule(string spec, string name, JsModuleLoader loader, byte[] options, Action<string, string> log)
    {
        Spec = spec;
        Name = name;
        _loader = loader;
        Options = options;
        _log = log;
    }

    public string Spec { get; }
    public string Name { get; }

    /// <summary>The plugin's options as JSON: <c>ctx.options</c>.</summary>
    public byte[] Options { get; }

    public bool HasRequest { get; private set; }
    public bool HasEvent { get; private set; }
    public bool HasResponse { get; private set; }

    /// <summary>The stream events onEvent is called for, by name; null is every one.</summary>
    public HashSet<string>? Events { get; private set; }

    /// <summary>Compiles the module and evaluates it once (that engine is the pool's first); throws with the load error.</summary>
    public static MiddlewareModule Load(string spec, string name, string root, string file, byte[] options, Action<string, string> log)
    {
        var loader = new JsModuleLoader(root, file, "a middleware");
        var m = new MiddlewareModule(spec, name, loader, options, log);
        var first = m.NewInstance();
        m.HasRequest = Has(first.OnRequest);
        m.HasEvent = Has(first.OnEvent);
        m.HasResponse = Has(first.OnResponse);
        if (!m.HasRequest && !m.HasEvent && !m.HasResponse)
            throw new JsHookException("exports none of onRequest, onEvent and onResponse");
        var events = first.Runtime.Export("events");
        if (events.IsArray())
        {
            m.Events = [];
            foreach (var v in events.AsArray())
                if (v.IsString()) m.Events.Add(v.AsString());
        }
        m._pool.Add(first);
        return m;
    }

    public MiddlewareInstance Get() => _pool.TryTake(out var i) ? i : NewInstance();

    public void Put(MiddlewareInstance instance)
    {
        if (_pool.Count < MaxPooled) _pool.Add(instance);
    }

    public void Count(long nanos)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _nanos, nanos);
    }

    /// <summary>A hook that failed: its input goes on as it was; counted, kept as the last error, logged.</summary>
    public void Fail(string hook, string error)
    {
        Interlocked.Increment(ref _failures);
        var msg = hook + ": " + error;
        _lastError = msg;
        Say("error", msg);
    }

    /// <summary>A line the middleware logs — at most 60 a minute, so one that logs every event doesn't fill the log.</summary>
    public void Say(string level, string msg)
    {
        var minute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        while (true)
        {
            var v = Interlocked.Read(ref _logged);
            long n = v & 0xffff, at = v >> 16;
            if (at != minute) n = 0;
            if (n >= LinesAMinute) return;
            if (Interlocked.CompareExchange(ref _logged, minute << 16 | (n + 1), v) == v) break;
        }
        _log(level, $"middleware {Name} [{level}]: {JsText.Clip(msg)}");
    }

    public MiddlewareState State()
    {
        var hooks = new List<string>();
        if (HasRequest) hooks.Add("onRequest");
        if (HasEvent) hooks.Add("onEvent");
        if (HasResponse) hooks.Add("onResponse");
        var calls = Interlocked.Read(ref _calls);
        return new MiddlewareState(hooks, Events?.Order(StringComparer.Ordinal).ToList(), null, calls,
            calls > 0 ? Interlocked.Read(ref _nanos) / (double)calls / 1e3 : 0, Interlocked.Read(ref _failures), _lastError);
    }

    private MiddlewareInstance NewInstance()
    {
        var rt = new JsRuntime(_loader, Say);
        rt.Load(MiddlewareEngine.LoadLimit);
        return new MiddlewareInstance(rt, Hook(rt, "onRequest"), Hook(rt, "onEvent"), Hook(rt, "onResponse"));
    }

    // The export, or default's member, when it is a function.
    private static JsValue Hook(JsRuntime rt, string name)
    {
        var v = rt.Export(name);
        return v is Jint.Native.Function.Function ? v : JsValue.Undefined;
    }

    private static bool Has(JsValue hook) => !hook.IsUndefined();
}

/// <summary>One engine of a middleware, with its hooks looked up.</summary>
internal sealed record MiddlewareInstance(JsRuntime Runtime, JsValue OnRequest, JsValue OnEvent, JsValue OnResponse);

/// <summary>A middleware as the Plugins page shows it (magpie's middleware.State).</summary>
/// <param name="Hooks">The hooks it has: onRequest, onEvent, onResponse.</param>
/// <param name="Events">The events its onEvent wants; null is every one.</param>
/// <param name="Error">Why it didn't load.</param>
/// <param name="AvgMicros">Average µs a call, JSON in and out included.</param>
public sealed record MiddlewareState(
    List<string> Hooks, List<string>? Events, string? Error, long Calls, double AvgMicros, long Failures, string? LastError);
