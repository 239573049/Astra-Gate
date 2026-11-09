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
    public ValueTask<MiddlewareRun?> BeginAsync(MiddlewareInfo info, CancellationToken ct = default) => BeginAsync(() => info, ct);

    /// <summary>
    /// Starts a request's middleware, building its info only when there is one: the gateway's reads the model and
    /// stream flag by scanning the whole body, which a large agent request should not pay for without a plugin.
    /// </summary>
    public async ValueTask<MiddlewareRun?> BeginAsync(Func<MiddlewareInfo> info, CancellationToken ct = default)
    {
        var set = await CurrentAsync(ct);
        return set.Modules.Count == 0 ? null : new MiddlewareRun(set, info());
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

/// <summary>Where one attempt of a request goes, for onUpstream (<c>ctx.upstream</c>).</summary>
/// <param name="Protocol">The upstream's API, named as ctx.protocol names the agent's: anthropic, chat, responses or gemini.</param>
/// <param name="Model">The model id sent upstream.</param>
public sealed record MiddlewareUpstream(string Protocol, string ProviderId, string ProviderName, string Model, bool Stream);

/// <summary>A header an onUpstream sets on the upstream request; a null value removes it.</summary>
public sealed record MiddlewareHeader(string Name, string? Value)
{
    // Credentials and transport headers stay the gateway's: a plugin never sees or replaces a key, and never breaks framing.
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "x-api-key", "api-key", "x-goog-api-key", "cookie", "host", "connection",
        "content-length", "content-type", "content-encoding", "transfer-encoding", "te", "upgrade", "expect",
    };

    /// <summary>Why a plugin may not set this header; null when it may.</summary>
    public static string? Refusal(string name, string? value)
    {
        if (name.Length == 0 || name.Length > 256 || !name.All(c => c is > ' ' and < '\x7f' and not (':' or '(' or ')' or ',' or '/' or ';' or '<' or '=' or '>' or '?' or '@' or '[' or '\\' or ']' or '{' or '}' or '"')))
            return "not a header name";
        if (Reserved.Contains(name) || name.StartsWith("x-astra-", StringComparison.OrdinalIgnoreCase))
            return "a header the gateway keeps";
        if (value is not null && (value.Length > 8192 || value.Any(c => c is '\r' or '\n' or '\0')))
            return "not a header value";
        return null;
    }
}

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
    // Set only while an onUpstream runs: its target, and the headers it set (ctx.setHeader / ctx.removeHeader).
    private MiddlewareUpstream? _upstream;
    private List<MiddlewareHeader>? _headers;
    private int _current = -1;

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

    /// <summary>Whether some middleware wants the request as it goes upstream (onUpstream).</summary>
    public bool WantsUpstream => _set.Modules.Any(m => m.HasUpstream);

    /// <summary>
    /// Runs each onUpstream on <paramref name="body"/> — the JSON one attempt sends <paramref name="upstream"/>, in the
    /// upstream's API, after translation and the privacy guard — with <c>ctx.upstream</c> set. What one returns is what
    /// the next is given. Returns the body when some hook changed it (null otherwise) and the headers the hooks set, in
    /// order; a hook that fails keeps neither its body nor its headers. Runs again for each attempt (a pool's next member).
    /// </summary>
    public (byte[]? Body, List<MiddlewareHeader> Headers) Upstream(MiddlewareUpstream upstream, byte[] body)
    {
        byte[]? changed = null;
        var headers = new List<MiddlewareHeader>();
        for (var i = 0; i < _set.Modules.Count; i++)
        {
            var m = _set.Modules[i];
            if (!m.HasUpstream) continue;
            _upstream = upstream;
            _headers = [];
            var (output, error) = Apply(i, Hook.Upstream, MiddlewareEngine.RequestLimit, changed ?? body, null);
            var set = _headers;
            _upstream = null;
            _headers = null;
            if (error == Dropped) m.Fail("onUpstream", "returned null; a request can't be dropped here (ctx.reject is onRequest's)");
            else if (error is not null) m.Fail("onUpstream", error);
            else
            {
                if (output is not null) changed = output;
                headers.AddRange(set);
            }
        }
        return (changed, headers);
    }

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
        Upstream,
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
            if (hook == Hook.Upstream && _upstream is { } u)
                ctx.Set("upstream", rt.ParseJson(new StringBuilder("{\"protocol\":").Append(Quote(u.Protocol))
                    .Append(",\"provider\":{\"id\":").Append(Quote(u.ProviderId)).Append(",\"name\":").Append(Quote(u.ProviderName))
                    .Append("},\"model\":").Append(Quote(u.Model)).Append(",\"stream\":").Append(u.Stream ? "true" : "false").Append('}').ToString()));
            var fn = hook switch
            {
                Hook.Request => instance.OnRequest,
                Hook.Event => instance.OnEvent,
                Hook.Upstream => instance.OnUpstream,
                _ => instance.OnResponse,
            };
            _current = i;
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
            _current = -1;
            m.Count(Stopwatch.GetElapsedTime(started).Ticks * 100);
        }
    }

    // Middleware i's engine for this request and its ctx: the request's info, the plugin's options, a
    // state of its own, reject, sha256, and the header setters onUpstream uses.
    private (MiddlewareInstance, ObjectInstance) Acquire(int i)
    {
        if (_instances[i] is { } existing) return (existing, _contexts[i]!);
        var m = _set.Modules[i];
        var instance = m.Get();
        var rt = instance.Runtime;
        var json = new StringBuilder(_info).Append(",\"options\":").Append(Encoding.UTF8.GetString(m.Options)).Append(",\"state\":{}}").ToString();
        var ctx = rt.ParseJson(json).AsObject();
        ctx.Set("reject", rt.Function("reject", 2, Reject));
        ctx.Set("sha256", rt.Function("sha256", 1, Sha256));
        ctx.Set("setHeader", rt.Function("setHeader", 2, args => SetHeader(args, remove: false)));
        ctx.Set("removeHeader", rt.Function("removeHeader", 1, args => SetHeader(args, remove: true)));
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

    // ctx.sha256(text | bytes): the hex SHA-256 of a string's UTF-8 bytes, or of an array of byte values (0–255, taken
    // modulo 256) for a digest over raw bytes, such as a few bytes cut out of UTF-8 text. The engine has no crypto of its own.
    private static JsValue Sha256(JsValue[] args)
    {
        byte[] data;
        if (args.Length > 0 && args[0].IsArray())
        {
            var array = args[0].AsArray();
            var length = (int)Math.Clamp(Jint.Runtime.TypeConverter.ToNumber(array.Get("length")), 0, 16 * 1024 * 1024);
            data = new byte[length];
            for (var i = 0; i < length; i++)
            {
                var v = array.Get(i);
                data[i] = v.IsNumber() ? (byte)((long)v.AsNumber() & 0xff) : (byte)0;
            }
        }
        else data = Encoding.UTF8.GetBytes(args.Length > 0 && args[0].IsString() ? args[0].AsString() : "");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
    }

    // ctx.setHeader(name, value) / ctx.removeHeader(name): only inside onUpstream; anything else is ignored with a line in
    // the log, so a plugin written against the wrong hook does nothing rather than fail the request.
    private JsValue SetHeader(JsValue[] args, bool remove)
    {
        var m = _current >= 0 ? _set.Modules[_current] : null;
        var name = args.Length > 0 && args[0].IsString() ? args[0].AsString().Trim() : "";
        string? value = remove ? null : args.Length > 1 && !args[1].IsUndefined() && !args[1].IsNull() ? Jint.Runtime.TypeConverter.ToString(args[1]) : null;
        var verb = remove ? "removeHeader" : "setHeader";
        if (_headers is null)
            m?.Say("warn", $"ctx.{verb} works in onUpstream only");
        else if (!remove && value is null)
            m?.Say("warn", $"ctx.setHeader({name}) without a value; use ctx.removeHeader");
        else if (MiddlewareHeader.Refusal(name, value) is { } why)
            m?.Say("warn", $"ctx.{verb}({name}) ignored: {why}");
        else
            _headers.Add(new MiddlewareHeader(name, value));
        return JsValue.Undefined;
    }

    private static string Quote(string s) => "\"" + System.Text.Json.JsonEncodedText.Encode(s).Value + "\"";
}
