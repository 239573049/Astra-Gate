using System.Diagnostics;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Json;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;

namespace Astra.Plugins.Js;

/// <summary>
/// One JavaScript engine running one plugin module: magpie's moejs runtime, on Jint. The host talks
/// to it only in JSON text and <see cref="JsValue"/>s — no CLR objects cross over (no AllowClr, no
/// interop wrappers), which keeps it Native-AOT safe and keeps plugins away from .NET. The only
/// global it adds is <c>console</c>; there is no fetch, no timers and no Node API, as in magpie.
/// An engine is not thread-safe: a pool hands each one to a single request at a time.
/// </summary>
internal sealed class JsRuntime
{
    private readonly Engine _engine;
    private readonly DeadlineConstraint _deadline = new();
    private readonly JsModuleLoader _loader;
    private ObjectInstance? _exports;

    /// <param name="log">Receives (level, line) for each console call: log/info → "info", debug, warn, error.</param>
    /// <param name="consoleLevels">The console methods to define; agents get no debug in magpie.</param>
    public JsRuntime(JsModuleLoader loader, Action<string, string> log, IReadOnlyList<string>? consoleLevels = null)
    {
        _loader = loader;
        _engine = new Engine(o => o
            .EnableModules(loader)
            .Constraint(_deadline)
            .LimitRecursion(512)
            .LimitMemory(256L * 1024 * 1024)
            .RegexTimeoutInterval(TimeSpan.FromMilliseconds(200)));

        var console = new JsObject(_engine);
        foreach (var name in consoleLevels ?? ["log", "info", "debug", "warn", "error"])
        {
            var level = name == "log" ? "info" : name;
            console.FastSetDataProperty(name, new ClrFunction(_engine, name, (_, args) =>
            {
                var parts = new string[args.Length];
                for (var i = 0; i < args.Length; i++) parts[i] = Text(args[i]);
                log(level, string.Join(' ', parts));
                return JsValue.Undefined;
            }));
        }
        _engine.SetValue("console", console);
    }

    /// <summary>Evaluates the entry module within <paramref name="limit"/>; a failure is the load error.</summary>
    public void Load(TimeSpan limit)
    {
        _exports = Run(limit, () => _engine.Modules.Import(_loader.Entry));
    }

    /// <summary>
    /// The export <paramref name="name"/>, or the member of that name on <c>export default</c>:
    /// magpie reads hooks both ways, the named export first. Undefined when neither has it.
    /// </summary>
    public JsValue Export(string name)
    {
        var exports = _exports ?? throw new InvalidOperationException("the module is not loaded");
        var v = exports.Get(name);
        if (!v.IsUndefined()) return v;
        var d = exports.Get("default");
        return d.IsObject() ? d.AsObject().Get(name) : JsValue.Undefined;
    }

    /// <summary>
    /// Calls <paramref name="fn"/> with the time it has. A returned promise is settled before this
    /// returns — a hook has nothing to wait on, so an async one has finished by then unless it waits on
    /// a promise that never settles, which is a failure.
    /// </summary>
    public JsValue Call(JsValue fn, TimeSpan limit, params JsValue[] args) =>
        Run(limit, () =>
        {
            var v = _engine.Invoke(fn, JsValue.Undefined, args);
            if (!v.IsPromise()) return v;
            using var settle = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
            try
            {
                return v.UnwrapIfPromise(settle.Token);
            }
            catch (PromiseRejectedException e)
            {
                throw new JsHookException("rejected: " + Text(e.RejectedValue));
            }
            catch (OperationCanceledException)
            {
                throw new JsHookException("returned a promise that never settled");
            }
        });

    /// <summary>JSON text as a value of this engine.</summary>
    public JsValue ParseJson(string json) => new JsonParser(_engine).Parse(json);

    /// <summary>UTF-8 JSON as a value of this engine (no string in between).</summary>
    public JsValue ParseJson(ReadOnlySpan<byte> utf8) => new JsonParser(_engine).Parse(utf8);

    /// <summary>A value as UTF-8 JSON (JSON.stringify), or null when it has no JSON form.</summary>
    public byte[]? ToJsonBytes(JsValue value)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        return new JsonSerializer(_engine).Serialize(value, buffer) ? buffer.WrittenSpan.ToArray() : null;
    }

    /// <summary>A value as JSON text (JSON.stringify), or null when it has no JSON form.</summary>
    public string? ToJson(JsValue value)
    {
        var s = new JsonSerializer(_engine).Serialize(value);
        return s.IsString() ? s.AsString() : null;
    }

    /// <summary>A new plain object of this engine, for building a hook's ctx.</summary>
    public JsObject NewObject() => new(_engine);

    /// <summary>A host function JS can call, e.g. ctx.reject.</summary>
    public JsValue Function(string name, int length, Func<JsValue[], JsValue> fn) =>
        new ClrFunction(_engine, name, (_, args) => fn(args), length);

    /// <summary>A value as console.log prints it in magpie: a string as it is, anything else as JSON.</summary>
    public string Text(JsValue v)
    {
        if (v.IsString()) return v.AsString();
        try
        {
            if (ToJson(v) is { Length: > 0 } json) return json;
        }
        catch (JavaScriptException)
        {
            // a cycle or a throwing toJSON: fall back to its string form
        }
        try
        {
            return TypeConverter.ToString(v);
        }
        catch (JavaScriptException)
        {
            return "?";
        }
    }

    // Runs body under the deadline; every failure comes out as a JsHookException carrying the text
    // magpie shows for it ("TypeError: x is not a function", "took too long", ...).
    private T Run<T>(TimeSpan limit, Func<T> body)
    {
        _deadline.Arm(limit);
        try
        {
            return body();
        }
        catch (JsDeadlineException)
        {
            _engine.Advanced.ResetCallStack();
            throw new JsHookException("took too long");
        }
        catch (JavaScriptException e)
        {
            _engine.Advanced.ResetCallStack();
            throw new JsHookException(ErrorText(e.Error, e.Message));
        }
        catch (JsLoadException e)
        {
            throw new JsHookException(e.Message);
        }
        catch (JintException e)
        {
            // recursion, memory, regex timeouts and the like
            _engine.Advanced.ResetCallStack();
            throw new JsHookException(e.GetType().Name.Replace("Exception", "") + ": " + e.Message);
        }
        finally
        {
            _deadline.Disarm();
        }
    }

    // "<name>: <message>" for a thrown Error, as moejs's Exception.Name()/Message() reads; the value as
    // text for anything else thrown.
    private string ErrorText(JsValue error, string fallback)
    {
        if (error.IsObject())
        {
            var o = error.AsObject();
            var name = o.Get("name");
            var message = o.Get("message");
            if (name.IsString() && message.IsString())
                return name.AsString() + ": " + message.AsString();
        }
        return error.IsUndefined() ? fallback : "Error: " + Text(error);
    }
}

/// <summary>A hook or a load that failed; the message is what the plugin page shows as its last error.</summary>
internal sealed class JsHookException(string message) : Exception(message);

internal sealed class JsDeadlineException() : Exception("took too long");

/// <summary>
/// Stops a running call at its deadline. Jint checks constraints between statements (amortized), so
/// a <c>while (true) {}</c> is stopped too; the deadline is per call, armed by <see cref="JsRuntime"/>,
/// not per engine execution, so Jint's own Reset does nothing.
/// </summary>
internal sealed class DeadlineConstraint : Constraint
{
    private long _deadline;

    public override bool IsAmortizable => true;

    public void Arm(TimeSpan limit) =>
        _deadline = Stopwatch.GetTimestamp() + (long)(limit.TotalSeconds * Stopwatch.Frequency);

    public void Disarm() => _deadline = 0;

    public override void Check()
    {
        var d = _deadline;
        if (d != 0 && Stopwatch.GetTimestamp() >= d) throw new JsDeadlineException();
    }

    public override void Reset()
    {
    }
}

internal static class JsText
{
    /// <summary>Cuts a log line to magpie's 2000 characters.</summary>
    public static string Clip(string s) => s.Length > 2000 ? new StringBuilder(s, 0, 2000, 2001).Append('…').ToString() : s;
}
