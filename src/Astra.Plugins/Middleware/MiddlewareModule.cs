using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Astra.Plugins.Js;
using Jaunt;

namespace Astra.Plugins.Middleware;

/// <summary>
/// One loaded gateway middleware: its module (parsed once), which hooks it has, the stream events its
/// onEvent wants, and a pool of engines with the module evaluated — a request takes one for all its
/// hooks, so they share <c>ctx.state</c>, and module globals persist across requests as in magpie — until
/// the engine is retired: one a limit stopped (Jaunt can't run it again), or one that has allocated a lot.
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

    /// <summary>Astra's own hook (not magpie's): the request as it goes to one upstream, after translation.</summary>
    public bool HasUpstream { get; private set; }

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
        m.HasUpstream = Has(first.OnUpstream);
        if (!m.HasRequest && !m.HasEvent && !m.HasResponse && !m.HasUpstream)
            throw new JsHookException("exports none of onRequest, onEvent, onResponse and onUpstream");
        var events = first.Runtime.Export("events");
        if (events.IsArray && JsonNode.Parse(first.Runtime.ToJson(events) ?? "[]") is JsonArray names)
        {
            m.Events = [];
            foreach (var v in names)
                if (v is JsonValue s && s.TryGetValue<string>(out var ev)) m.Events.Add(ev);
        }
        m._pool.Add(first);
        return m;
    }

    public MiddlewareInstance Get() => _pool.TryTake(out var i) ? i : NewInstance();

    public void Put(MiddlewareInstance instance)
    {
        instance.Run = null;
        if (!instance.Runtime.Worn && _pool.Count < MaxPooled) _pool.Add(instance);
        else instance.Runtime.Dispose();
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
        if (HasUpstream) hooks.Add("onUpstream");
        var calls = Interlocked.Read(ref _calls);
        return new MiddlewareState(hooks, Events?.Order(StringComparer.Ordinal).ToList(), null, calls,
            calls > 0 ? Interlocked.Read(ref _nanos) / (double)calls / 1e3 : 0, Interlocked.Read(ref _failures), _lastError);
    }

    private MiddlewareInstance NewInstance()
    {
        var rt = new JsRuntime(_loader, Say);
        try
        {
            rt.Load(MiddlewareEngine.LoadLimit);
            return new MiddlewareInstance(rt, Hook(rt, "onRequest"), Hook(rt, "onEvent"), Hook(rt, "onResponse"), Hook(rt, "onUpstream"));
        }
        catch
        {
            rt.Dispose();
            throw;
        }
    }

    // The export, or default's member, when it is a function.
    private static JsValue Hook(JsRuntime rt, string name)
    {
        var v = rt.Export(name);
        return v.IsFunction ? v : JsValue.Undefined;
    }

    private static bool Has(JsValue hook) => !hook.IsUndefined;
}

/// <summary>
/// One engine of a middleware, with its hooks looked up and the host functions its ctx gets (made once per
/// engine; they act for the request holding it, <see cref="Run"/>).
/// </summary>
internal sealed class MiddlewareInstance
{
    public MiddlewareInstance(JsRuntime runtime, JsValue onRequest, JsValue onEvent, JsValue onResponse, JsValue onUpstream)
    {
        Runtime = runtime;
        OnRequest = onRequest;
        OnEvent = onEvent;
        OnResponse = onResponse;
        OnUpstream = onUpstream;
        Reject = runtime.Function("reject", 2, args => Run?.Reject(args) ?? JsValue.Undefined);
        Sha256 = runtime.Function("sha256", 1, MiddlewareRun.Sha256);
        SetHeader = runtime.Function("setHeader", 2, args => Run?.SetHeader(args, remove: false) ?? JsValue.Undefined);
        RemoveHeader = runtime.Function("removeHeader", 1, args => Run?.SetHeader(args, remove: true) ?? JsValue.Undefined);
    }

    public JsRuntime Runtime { get; }
    public JsValue OnRequest { get; }
    public JsValue OnEvent { get; }
    public JsValue OnResponse { get; }
    public JsValue OnUpstream { get; }
    public JsValue Reject { get; }
    public JsValue Sha256 { get; }
    public JsValue SetHeader { get; }
    public JsValue RemoveHeader { get; }

    /// <summary>The request this engine is serving; null while pooled.</summary>
    public MiddlewareRun? Run { get; set; }
}

/// <summary>A middleware as the Plugins page shows it (magpie's middleware.State).</summary>
/// <param name="Hooks">The hooks it has: onRequest, onEvent, onResponse, onUpstream.</param>
/// <param name="Events">The events its onEvent wants; null is every one.</param>
/// <param name="Error">Why it didn't load.</param>
/// <param name="AvgMicros">Average µs a call, JSON in and out included.</param>
public sealed record MiddlewareState(
    List<string> Hooks, List<string>? Events, string? Error, long Calls, double AvgMicros, long Failures, string? LastError);
