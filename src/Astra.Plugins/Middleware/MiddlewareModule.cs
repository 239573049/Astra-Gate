using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Astra.Plugins.Js;
using Astra.Plugins.V2;
using Astra.Plugins.V2.Runtime;
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

    /// <summary>For a v2 hook set: the plugin, the set's contribution, and the host. Null for a v1 middleware.</summary>
    public PluginV2Instance? V2Plugin { get; private set; }

    public HookContribution? V2Hook { get; private set; }

    private V2Gateway? _v2Gateway;

    /// <summary>A v2 hook set's onText (text deltas of the reply, ir view).</summary>
    public bool HasText { get; private set; }

    /// <summary>A v2 hook set's onComplete (read-only, after the reply).</summary>
    public bool HasComplete { get; private set; }

    /// <summary>Whether the hooks get the protocol-free view (ir) rather than the client's own JSON.</summary>
    public bool IrView => V2Hook?.View == "ir";

    /// <summary>What this module may do with what it is given; v1 middleware may do all of it (magpie's rules).</summary>
    public bool CanRewriteRequest => V2Plugin is null || PluginPermissions.Holds(V2Plugin.Granted, PluginPermissions.RequestRewrite);
    public bool CanReject => V2Plugin is null || PluginPermissions.Holds(V2Plugin.Granted, PluginPermissions.RequestReject);
    public bool CanRewriteResponse => V2Plugin is null || PluginPermissions.Holds(V2Plugin.Granted, PluginPermissions.ResponseRewrite);
    public bool CanSetHeaders => V2Plugin is null || PluginPermissions.Holds(V2Plugin.Granted, PluginPermissions.UpstreamHeaders);

    /// <summary>Whether this module wants a request: a v2 hook set's match; v1 middleware wants every one.</summary>
    public bool Wants(MiddlewareInfo info) => V2Hook is null || V2Hook.Match.Matches(info.Protocol, info.Model, info.Agent, info.Stream);

    /// <summary>A v2 hook set's ctx.shared, as JSON.</summary>
    public string SharedJson => V2Plugin is null || V2Hook is null || _v2Gateway is null ? "{}" : _v2Gateway.SharedJson(V2Plugin.Id, V2Hook.Reads);

    internal V2Gateway? Gateway => _v2Gateway;

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

    /// <summary>
    /// Loads one hook set of a v2 plugin's gateway module: the module (with the <c>astra</c> import) evaluated after the
    /// boot, its <c>gateway.defineHooks</c> object read, and the set named <paramref name="hook"/> taken from it.
    /// </summary>
    public static MiddlewareModule LoadV2(PluginV2Instance plugin, HookContribution hook, V2Gateway gateway, Action<string, string> log)
    {
        var manifest = plugin.Manifest;
        var loader = new JsModuleLoader(manifest.Root, manifest.Gateway!, "a v2 plugin", V2Assets.VirtualModules);
        var options = System.Text.Encoding.UTF8.GetBytes(plugin.Config().ToJsonString());
        var m = new MiddlewareModule(plugin.Spec, manifest.Id + (manifest.Hooks.Count > 1 ? "/" + hook.Id : ""), loader, options,
            (level, line) => { plugin.Log(level, line); log(level, line); })
        {
            V2Plugin = plugin,
            V2Hook = hook,
            _v2Gateway = gateway,
        };
        var first = m.NewInstance();
        m.HasRequest = hook.On.Contains("request") && Has(first.OnRequest);
        m.HasEvent = hook.On.Contains("event") && Has(first.OnEvent);
        m.HasResponse = hook.On.Contains("response") && Has(first.OnResponse);
        m.HasUpstream = hook.On.Contains("upstream") && Has(first.OnUpstream);
        m.HasText = hook.On.Contains("text") && Has(first.OnText);
        m.HasComplete = hook.On.Contains("complete") && Has(first.OnComplete);
        foreach (var on in hook.On)
        {
            var has = on switch
            {
                "request" => m.HasRequest, "event" => m.HasEvent, "response" => m.HasResponse, "upstream" => m.HasUpstream,
                "text" => m.HasText, _ => m.HasComplete,
            };
            if (!has) throw new JsHookException($"hook set {hook.Id} lists \"{on}\" but defines no on{char.ToUpperInvariant(on[0])}{on[1..]}");
        }
        if (hook.Events is { } events) m.Events = [.. events];
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
        if (HasText) hooks.Add("onText");
        if (HasComplete) hooks.Add("onComplete");
        var calls = Interlocked.Read(ref _calls);
        return new MiddlewareState(hooks, Events?.Order(StringComparer.Ordinal).ToList(), null, calls,
            calls > 0 ? Interlocked.Read(ref _nanos) / (double)calls / 1e3 : 0, Interlocked.Read(ref _failures), _lastError);
    }

    private MiddlewareInstance NewInstance()
    {
        var rt = new JsRuntime(_loader, Say);
        try
        {
            if (V2Plugin is { } plugin && V2Hook is { } hook)
            {
                var boot = rt.Evaluate("(" + V2Assets.Boot + ")", MiddlewareEngine.LoadLimit);
                var calls = _v2Gateway!.Host.Calls;
                var call = rt.Function("call", 2, _ => -1);
                var sync = rt.Function("sync", 2, a =>
                {
                    var r = calls.Sync(plugin, a[0].IsString ? a[0].AsString() : "", a[1].IsString ? a[1].AsString() : null, gateway: true);
                    return r.Ok ? "{\"result\":" + (r.Json is { Length: > 0 } j ? j : "null") + "}" : "{\"error\":" + r.Json + "}";
                });
                var done = rt.Function("done", 3, _ => JsValue.Undefined);
                var api = rt.Call(boot, MiddlewareEngine.LoadLimit, call, sync, done, "gateway");
                var adopt = rt.Get(api, "adopt");
                rt.Load(MiddlewareEngine.LoadLimit);
                foreach (var name in rt.ExportNames) rt.Call(adopt, MiddlewareEngine.LoadLimit, name, rt.Export(name));
                var hooks = rt.Call(rt.Get(api, "hooks"), MiddlewareEngine.LoadLimit);
                if (!hooks.IsObject) throw new JsHookException("the gateway module exports no gateway.defineHooks({...}) (as default or as hooks)");
                var set = rt.Get(hooks, hook.Id);
                if (!set.IsObject) throw new JsHookException($"gateway.defineHooks has no hook set \"{hook.Id}\"");
                JsValue Fn(string n)
                {
                    var v = rt.Get(set, n);
                    return v.IsFunction ? v : JsValue.Undefined;
                }
                return new MiddlewareInstance(rt, Fn("onRequest"), Fn("onEvent"), Fn("onResponse"), Fn("onUpstream"), Fn("onText"), Fn("onComplete"));
            }
            rt.Load(MiddlewareEngine.LoadLimit);
            return new MiddlewareInstance(rt, Hook(rt, "onRequest"), Hook(rt, "onEvent"), Hook(rt, "onResponse"), Hook(rt, "onUpstream"),
                JsValue.Undefined, JsValue.Undefined);
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
    public MiddlewareInstance(JsRuntime runtime, JsValue onRequest, JsValue onEvent, JsValue onResponse, JsValue onUpstream,
        JsValue onText, JsValue onComplete)
    {
        Runtime = runtime;
        OnRequest = onRequest;
        OnEvent = onEvent;
        OnResponse = onResponse;
        OnUpstream = onUpstream;
        OnText = onText;
        OnComplete = onComplete;
        Emit = runtime.Function("emit", 2, args => Run?.Emit(args) ?? JsValue.Undefined);
        CountFn = runtime.Function("count", 2, args => Run?.CountMetric(args) ?? JsValue.Undefined);
        HoldBack = runtime.Function("holdBack", 1, args => Run?.HoldBack(args) ?? JsValue.Undefined);
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
    public JsValue OnText { get; }
    public JsValue OnComplete { get; }
    public JsValue Emit { get; }
    public JsValue CountFn { get; }
    public JsValue HoldBack { get; }
    public JsValue Reject { get; }
    public JsValue Sha256 { get; }
    public JsValue SetHeader { get; }
    public JsValue RemoveHeader { get; }

    /// <summary>The request this engine is serving; null while pooled.</summary>
    public MiddlewareRun? Run { get; set; }
}

/// <summary>A middleware as the Plugins page shows it (magpie's middleware.State).</summary>
/// <param name="Hooks">The hooks it has: onRequest, onEvent, onResponse, onUpstream (and a v2 set's onText, onComplete).</param>
/// <param name="Events">The events its onEvent wants; null is every one.</param>
/// <param name="Error">Why it didn't load.</param>
/// <param name="AvgMicros">Average µs a call, JSON in and out included.</param>
public sealed record MiddlewareState(
    List<string> Hooks, List<string>? Events, string? Error, long Calls, double AvgMicros, long Failures, string? LastError);
