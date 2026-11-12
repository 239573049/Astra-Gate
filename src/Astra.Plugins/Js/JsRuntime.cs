using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using Jaunt;

namespace Astra.Plugins.Js;

/// <summary>
/// One JavaScript engine running one plugin module: magpie's moejs runtime, on Jaunt. The host talks
/// to it only in JSON text and <see cref="JsValue"/>s — no CLR objects cross over (Jaunt has no reflection
/// interop at all), which keeps it Native-AOT safe and keeps plugins away from .NET. The only global it
/// adds is <c>console</c>; there is no fetch, no timers and no Node API, as in magpie.
/// An engine is not thread-safe: a pool hands each one to a single request at a time.
/// </summary>
/// <remarks>
/// Jaunt's host API reads no property of a JS object and makes no function, so a small bridge script
/// (<see cref="Bridge"/>) does that inside the engine: it hands its helpers to the host through one native
/// function and then deletes that global, so plugins never see it. A limit hit (deadline, recursion,
/// memory) faults a Jaunt engine for good, unlike a thrown error: <see cref="IsFaulted"/> tells the owner
/// to drop this runtime and build another.
/// </remarks>
internal sealed class JsRuntime : IDisposable
{
    /// <summary>The longest any one execution may take, whatever its caller's limit: the safety net for
    /// the host's own reads (an export behind a getter, a toJSON), which have no limit of their own.</summary>
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(5);

    /// <summary>Above this many bytes allocated over its life, a pooled engine is retired (Jaunt's count only grows).</summary>
    private const long RetireBytes = 128L * 1024 * 1024;

    private const int OpRegister = 0, OpLog = 1, OpSettle = 2, OpCall = 3;

    private readonly JsEngine _engine;
    private readonly JsModuleLoader _loader;
    private readonly Action<string, string> _log;
    private readonly List<JsValue> _registered = [];
    private readonly List<HostFunction> _functions = [];
    private readonly JsFunction _get, _set, _call, _text, _errorText, _function;
    private readonly JsValue _pending;
    private JsModuleInstance? _module;
    private HashSet<string>? _exportNames;
    private int _settled;
    private JsValue _settledValue;
    private TimeSpan _cachedLimit;
    private JsExecutionOptions? _cachedOptions;

    /// <param name="log">Receives (level, line) for each console call: log/info → "info", debug, warn, error.</param>
    /// <param name="consoleLevels">The console methods to define; agents get no debug in magpie.</param>
    public JsRuntime(JsModuleLoader loader, Action<string, string> log, IReadOnlyList<string>? consoleLevels = null)
    {
        _loader = loader;
        _log = log;
        _engine = new JsEngine(new JsEngineOptions
        {
            Limits = new JsLimits
            {
                Timeout = Ceiling,
                MaxStackDepth = 512,
                MaxTrackedBytes = 4 * RetireBytes,
            },
        });
        _engine.SetFunction(BridgeGlobal, Host, 2);
        var levels = string.Join(",", (consoleLevels ?? ["log", "info", "debug", "warn", "error"]).Select(l => "\"" + JsonEncodedText.Encode(l).Value + "\""));
        _engine.Evaluate(Bridge.Replace("LEVELS", "[" + levels + "]", StringComparison.Ordinal));
        _get = JsFunction.FromValue(_engine, _registered[0]);
        _set = JsFunction.FromValue(_engine, _registered[1]);
        _call = JsFunction.FromValue(_engine, _registered[2]);
        _text = JsFunction.FromValue(_engine, _registered[3]);
        _errorText = JsFunction.FromValue(_engine, _registered[4]);
        _function = JsFunction.FromValue(_engine, _registered[5]);
        _pending = _registered[6];
        _registered.Clear();
    }

    /// <summary>True once a limit stopped this engine: it can't run anything again, so drop it.</summary>
    public bool IsFaulted => _engine.IsFaulted;

    /// <summary>True when the engine should not go back to a pool: faulted, or near the end of its allocation budget.</summary>
    public bool Worn => _engine.IsFaulted || _engine.GetStatistics().TrackedBytes > RetireBytes;

    public void Dispose() => _engine.Dispose();

    /// <summary>Evaluates the entry module within <paramref name="limit"/>; a failure is the load error.</summary>
    public void Load(TimeSpan limit)
    {
        using var deadline = new CancellationTokenSource(limit);
        try
        {
            var entry = _loader.CompileEntry();
            try
            {
                _module = _engine.LoadModule(entry, _loader, _loader.Entry, deadline.Token);
            }
            catch (JsAsyncRequiredException)
            {
                // top-level await: nothing ran yet, so load it again on the path that pumps promises
                _module = _engine.LoadModuleAsync(entry, _loader, _loader.Entry, deadline.Token).AsTask().GetAwaiter().GetResult();
            }
            _exportNames = [.. _module.ExportNames];
        }
        catch (Exception e) when (LoadFailure(e) is { } inner)
        {
            // the loader refused an import, or a file could not be read: its own exception is the load error
            if (inner is JsLoadException) throw new JsHookException(inner.Message);
            ExceptionDispatchInfo.Throw(inner);
        }
        catch (JsException e)
        {
            throw new JsHookException(ErrorText(e));
        }
        catch (Exception e) when (Describe(e) is { } text)
        {
            throw new JsHookException(text);
        }
    }

    /// <summary>
    /// The export <paramref name="name"/>, or the member of that name on <c>export default</c>:
    /// magpie reads hooks both ways, the named export first. Undefined when neither has it.
    /// </summary>
    public JsValue Export(string name)
    {
        var module = _module ?? throw new InvalidOperationException("the module is not loaded");
        var v = Named(module, name);
        if (!v.IsUndefined) return v;
        var d = Named(module, "default");
        return d.IsObject ? Get(d, name) : JsValue.Undefined;
    }

    private JsValue Named(JsModuleInstance module, string name)
    {
        if (_exportNames is null || !_exportNames.Contains(name)) return JsValue.Undefined;
        try
        {
            return module.GetValue(name);
        }
        catch (JsException)
        {
            // still in its temporal dead zone
            return JsValue.Undefined;
        }
    }

    /// <summary>
    /// Calls <paramref name="fn"/> with the time it has. A returned promise is settled before this
    /// returns — a hook has nothing to wait on, so an async one has finished by then unless it waits on
    /// a promise that never settles, which is a failure.
    /// </summary>
    public JsValue Call(JsValue fn, TimeSpan limit, params JsValue[] args)
    {
        var callArgs = new JsValue[args.Length + 1];
        callArgs[0] = fn;
        args.CopyTo(callArgs, 1);
        _settled = 0;
        try
        {
            var v = Run(limit, x => x.Invoke(_call, callArgs));
            if (v != _pending) return v;
            return _settled switch
            {
                1 => _settledValue,
                2 => throw new JsHookException("rejected: " + Text(_settledValue)),
                _ => throw new JsHookException("returned a promise that never settled"),
            };
        }
        finally
        {
            _settledValue = default;
        }
    }

    /// <summary>The loaded module's export names.</summary>
    public IReadOnlyCollection<string> ExportNames => _exportNames ?? (IReadOnlyCollection<string>)[];

    /// <summary>Evaluates a script within <paramref name="limit"/> and returns its completion value.</summary>
    public JsValue Evaluate(string script, TimeSpan limit) => Run(limit, x => x.Evaluate(script));

    /// <summary>Bytes the engine has allocated over its life (Jaunt's count only grows).</summary>
    public long TrackedBytes => _engine.GetStatistics().TrackedBytes;

    /// <summary>JSON text as a value of this engine.</summary>
    public JsValue ParseJson(string json) => Run(null, x => x.ParseJson(json));

    /// <summary>UTF-8 JSON as a value of this engine.</summary>
    public JsValue ParseJson(ReadOnlySpan<byte> utf8)
    {
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(utf8);
        }
        catch (DecoderFallbackException)
        {
            throw new JsHookException("SyntaxError: not UTF-8");
        }
        return ParseJson(text);
    }

    /// <summary>A value as UTF-8 JSON (JSON.stringify), or null when it has no JSON form.</summary>
    public byte[]? ToJsonBytes(JsValue value) => ToJson(value) is { } s ? Encoding.UTF8.GetBytes(s) : null;

    /// <summary>A value as JSON text (JSON.stringify), or null when it has no JSON form.</summary>
    public string? ToJson(JsValue value)
    {
        if (value.IsUndefined || value.IsFunction || value.IsSymbol) return null;
        try
        {
            return Run(null, x => x.ToJsonString(value));
        }
        catch (JsConversionException)
        {
            return null;
        }
    }

    /// <summary>A new plain object of this engine, for building a hook's ctx.</summary>
    public JsValue NewObject() => ParseJson("{}");

    /// <summary>The property <paramref name="key"/> of <paramref name="target"/> (undefined for null and undefined).</summary>
    public JsValue Get(JsValue target, string key) => Run(null, x => x.Invoke(_get, target, key));

    /// <summary>Sets the property <paramref name="key"/> of <paramref name="target"/>.</summary>
    public void Set(JsValue target, string key, JsValue value) => Run(null, x => x.Invoke(_set, target, key, value));

    /// <summary>
    /// A host function JS can call, e.g. ctx.reject. It lives as long as the engine, so make the ones a
    /// runtime needs once rather than per request.
    /// </summary>
    public JsValue Function(string name, int length, HostFunction fn)
    {
        _functions.Add(fn);
        return Run(null, x => x.Invoke(_function, _functions.Count - 1, name, length));
    }

    /// <summary>A value as console.log prints it in magpie: a string as it is, anything else as JSON.</summary>
    public string Text(JsValue v)
    {
        if (v.IsString) return v.AsString();
        if (_engine.IsFaulted) return v.ToString();
        try
        {
            var t = Run(null, x => x.Invoke(_text, v));
            return t.IsString ? t.AsString() : "?";
        }
        catch (JsHookException)
        {
            return "?";
        }
    }

    // Runs body in one execution under the limit (the ceiling when null); every failure comes out as a
    // JsHookException carrying the text magpie shows for it ("TypeError: x is not a function", "took too long", ...).
    private T Run<T>(TimeSpan? limit, Func<JsExecution, T> body)
    {
        try
        {
            using var x = _engine.BeginExecution(limit is { } l ? Options(l) : null);
            var v = body(x);
            x.Complete();
            return v;
        }
        catch (JsException e)
        {
            throw new JsHookException(ErrorText(e));
        }
        catch (Exception e) when (Describe(e) is { } text)
        {
            throw new JsHookException(text);
        }
    }

    private JsExecutionOptions Options(TimeSpan limit)
    {
        if (_cachedOptions is null || _cachedLimit != limit)
        {
            _cachedOptions = new JsExecutionOptions { Timeout = limit };
            _cachedLimit = limit;
        }
        return _cachedOptions;
    }

    // "<name>: <message>" for a thrown Error, as moejs's Exception.Name()/Message() reads; the value as
    // text for anything else thrown.
    private string ErrorText(JsException e)
    {
        if (_engine.IsFaulted) return e.Message;
        try
        {
            using var x = _engine.BeginExecution();
            var t = x.Invoke(_errorText, e.ThrownValue);
            x.Complete();
            return t.IsString ? t.AsString() : e.Message;
        }
        catch (Exception)
        {
            return e.Message;
        }
    }

    // The text of a failure that is not a thrown JS value; null for one that isn't the plugin's doing.
    private static string? Describe(Exception e) => e switch
    {
        JsLimitExceededException { Kind: JsLimitKind.Timeout } or OperationCanceledException => "took too long",
        JsLimitExceededException { Kind: JsLimitKind.StackDepth } l => "RangeError: " + l.Message,
        JsLimitExceededException l => "limit exceeded: " + l.Message,
        JsCompileException c => "SyntaxError: " + c.Message,
        JsConversionException c => c.Message,
        JsAsyncRequiredException a => a.Message,
        JsEngineFaultedException f => "engine stopped: " + f.Message,
        JsHookException h => h.Message,
        _ => null,
    };

    // The exception a module load failed with when it came from the loader, which Jaunt wraps as an engine fault.
    private static Exception? LoadFailure(Exception e)
    {
        for (var inner = e; inner is not null; inner = inner.InnerException)
            if (inner is JsLoadException or IOException or UnauthorizedAccessException) return inner;
        return null;
    }

    // The bridge's one door into the host: register (a helper, at start), log (console), settle (a hook's
    // promise), call (a host function made by Function).
    private JsValue Host(ref JsCallContext c)
    {
        switch (c.GetInt32(0))
        {
            case OpRegister:
                _registered.Add(c[1]);
                break;
            case OpLog:
                _log(c.GetString(1) ?? "info", c.GetString(2) ?? "");
                break;
            case OpSettle:
                _settled = c.GetInt32(1);
                _settledValue = c[2];
                break;
            case OpCall:
                var id = c.GetInt32(1);
                var fn = (uint)id < (uint)_functions.Count ? _functions[id] : null;
                return fn is null ? JsValue.Undefined : fn(new HostArgs(c));
        }
        return JsValue.Undefined;
    }

    private const string BridgeGlobal = "__astraHost";

    // Evaluated once per engine, before any plugin code. It keeps its own copies of what it uses, so a plugin
    // that replaces JSON.stringify or Promise.prototype.then can't change what the host reads.
    private const string Bridge = """
        (() => {
          'use strict';
          const host = globalThis.__astraHost;
          delete globalThis.__astraHost;
          const stringify = JSON.stringify, toStr = String, isPromise = (v) => v instanceof Promise;
          const then = Promise.prototype.then, define = Object.defineProperty, apply = Reflect.apply;
          const text = (v) => {
            if (typeof v === 'string') return v;
            try { const j = stringify(v); if (typeof j === 'string' && j.length > 0) return j; } catch {}
            try { return toStr(v); } catch { return '?'; }
          };
          const pending = {};
          host(0, (o, k) => (o === null || o === undefined ? undefined : o[k]));
          host(0, (o, k, v) => { o[k] = v; });
          host(0, (fn, ...args) => {
            const r = apply(fn, undefined, args);
            if (!isPromise(r)) return r;
            apply(then, r, [(v) => { host(2, 1, v); }, (e) => { host(2, 2, e); }]);
            return pending;
          });
          host(0, text);
          host(0, (e) => (e !== null && typeof e === 'object' && typeof e.name === 'string' && typeof e.message === 'string')
            ? e.name + ': ' + e.message : 'Error: ' + text(e));
          host(0, (id, name, length) => {
            const f = function (...args) { return host(3, id, ...args); };
            define(f, 'name', { value: name });
            define(f, 'length', { value: length });
            return f;
          });
          host(0, pending);
          const console = {};
          for (const name of LEVELS) {
            const level = name === 'log' ? 'info' : name;
            console[name] = (...args) => { host(1, level, args.map(text).join(' ')); };
          }
          globalThis.console = console;
        })()
        """;
}

/// <summary>A host function made by <see cref="JsRuntime.Function"/>.</summary>
internal delegate JsValue HostFunction(HostArgs args);

/// <summary>The arguments of one call of a host function; valid only during that call.</summary>
internal readonly ref struct HostArgs
{
    // the bridge passes (op, id, ...args): the function's own arguments start at 2
    private const int Skip = 2;
    private readonly JsCallContext _call;

    public HostArgs(JsCallContext call) => _call = call;

    public int Length => Math.Max(0, _call.ArgumentCount - Skip);

    /// <summary>Argument <paramref name="i"/>; undefined when missing.</summary>
    public JsValue this[int i] => i < 0 ? JsValue.Undefined : _call[i + Skip];

    /// <summary>An own data property of an object, e.g. an array's "length" or "3"; no getter runs.</summary>
    public JsValue Own(JsValue target, string key) => _call.GetOwnDataProperty(target, key);
}

/// <summary>A hook or a load that failed; the message is what the plugin page shows as its last error.</summary>
internal sealed class JsHookException(string message) : Exception(message);

internal static class JsText
{
    /// <summary>Cuts a log line to magpie's 2000 characters.</summary>
    public static string Clip(string s) => s.Length > 2000 ? new StringBuilder(s, 0, 2000, 2001).Append('…').ToString() : s;
}
