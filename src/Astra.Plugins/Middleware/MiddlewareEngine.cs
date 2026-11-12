using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Astra.Plugins.Js;
using Jaunt;

namespace Astra.Plugins.Middleware;

/// <summary>
/// The gateway middleware in force: every enabled plugin with a middleware module, in the list's order,
/// loaded again when the list or a middleware's file changes (looked at once a second, as magpie does).
/// <see cref="BeginAsync"/> starts one request's run; it is null when there is no middleware, so a
/// gateway without plugins pays nothing.
/// </summary>
public sealed class MiddlewareEngine(PluginCatalog catalog, Action<string, string> log)
{
    /// <summary>The v2 host, whose plugins' gateway hooks run in the same chain (by the list's order); null without v2.</summary>
    public V2.PluginV2Host? V2Host { get; set; }

    /// <summary>The protocol-free views ir hook sets get; null hands them the raw JSON.</summary>
    public IHookViews? Views { get; set; }

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
        return set.Modules.Count == 0 ? null : new MiddlewareRun(set, info(), Views);
    }

    /// <summary>Each middleware plugin's state, by spec: its hooks and counts, or why it didn't load.</summary>
    public async Task<Dictionary<string, MiddlewareState>> StatesAsync(CancellationToken ct = default)
    {
        var set = await CurrentAsync(ct);
        var states = new Dictionary<string, MiddlewareState>();
        foreach (var (spec, error) in set.Errors) states[spec] = new MiddlewareState([], null, error, 0, 0, 0, null);
        foreach (var m in set.Modules)
        {
            var state = m.State();
            // a v2 plugin's several hook sets show as one: the hooks of all of them
            if (states.TryGetValue(m.Spec, out var before) && m.V2Plugin is not null)
                state = state with
                {
                    Hooks = [.. before.Hooks.Concat(state.Hooks).Distinct()],
                    Calls = before.Calls + state.Calls,
                    Failures = before.Failures + state.Failures,
                    LastError = state.LastError ?? before.LastError,
                };
            states[m.Spec] = state;
        }
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

    private async Task<Found> FindAsync(CancellationToken ct)
    {
        var snapshot = await catalog.SnapshotAsync(ct);
        var v1 = snapshot.Plugins.Where(p => p.Entry.Enabled && p.Manifest.MiddlewareFile is not null).ToList();
        IReadOnlyList<(V2.PluginV2Instance Plugin, V2.HookContribution Hook)> v2 = [];
        long v2Generation = 0;
        if (V2Host is { } host)
        {
            v2 = await host.Gateway.HooksAsync(ct);
            v2Generation = host.Gateway.Generation;
        }
        return new Found(snapshot.Generation, v1, v2, v2Generation);
    }

    private sealed record Found(long Generation, List<InstalledPlugin> Plugins,
        IReadOnlyList<(V2.PluginV2Instance Plugin, V2.HookContribution Hook)> V2, long V2Generation);

    // The list's generation, and each middleware file as it is on disk: a plugin under development
    // (a path) is picked up by the next request after a save.
    private static string Stamp(Found found)
    {
        var b = new StringBuilder().Append(found.Generation).Append('/').Append(found.V2Generation);
        foreach (var (p, h) in found.V2) b.Append('|').Append(p.Id).Append('/').Append(h.Id).Append('@').Append(p.Manifest.Version).Append('#').Append(p.OptionsJson?.GetHashCode() ?? 0).Append(p.Granted.Count);
        foreach (var p in found.Plugins)
        {
            var file = p.Manifest.MiddlewareFile!;
            b.Append('|').Append(file);
            var fi = new FileInfo(file);
            if (fi.Exists) b.Append(':').Append(fi.LastWriteTimeUtc.Ticks).Append(':').Append(fi.Length);
        }
        return b.ToString();
    }

    private MiddlewareSet Load(Found found, string stamp)
    {
        var modules = new List<(int Order, MiddlewareModule Module)>();
        var errors = new Dictionary<string, string>();
        foreach (var (plugin, hook) in found.V2)
        {
            try
            {
                modules.Add((plugin.SortOrder, MiddlewareModule.LoadV2(plugin, hook, V2Host!.Gateway, log)));
            }
            catch (Exception e) when (e is JsHookException or JsLoadException or IOException or UnauthorizedAccessException)
            {
                errors[plugin.Spec] = e.Message;
                plugin.Log("error", $"gateway hooks {hook.Id} didn't load: {e.Message}");
            }
        }
        foreach (var p in found.Plugins)
        {
            try
            {
                var options = Encoding.UTF8.GetBytes(p.Entry.OptionsJson is { Length: > 0 } o ? o : "{}");
                modules.Add((p.Entry.SortOrder, MiddlewareModule.Load(p.Entry.Spec, Store.PluginSpec.ShortName(p.Entry.Name), p.Manifest.ModuleRoot,
                    p.Manifest.MiddlewareFile!, options, log)));
            }
            catch (Exception e) when (e is JsHookException or JsLoadException or IOException or UnauthorizedAccessException)
            {
                errors[p.Entry.Spec] = e.Message;
                log("warn", $"middleware {p.Entry.Name} didn't load: {e.Message}");
            }
        }
        return new MiddlewareSet(stamp, [.. modules.OrderBy(m => m.Order).Select(m => m.Module)], errors);
    }
}

/// <summary>The middleware loaded at one stamp.</summary>
internal sealed record MiddlewareSet(string Stamp, List<MiddlewareModule> Modules, Dictionary<string, string> Errors);

/// <summary>What a request's hooks are told of it, in ctx.</summary>
/// <param name="Protocol">The agent's API: anthropic, chat, responses or gemini.</param>
/// <param name="Agent">The client that sent it; null for a direct call.</param>
/// <param name="TokenId">The gateway token the request came with (v2 hooks' ctx.token); null for none.</param>
public sealed record MiddlewareInfo(string Protocol, string? Model, bool Stream, string Path, string? Agent,
    string? TokenId = null, string? TokenName = null, string? RequestId = null);

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
/// the whole request, taken from its pool on first use and given back by <see cref="Dispose"/>. An engine a
/// limit stopped is dropped at once, so that middleware's next hook in the request gets a fresh one (and a
/// fresh <c>ctx.state</c>).
/// </summary>
public sealed class MiddlewareRun : IDisposable
{
    private readonly MiddlewareSet _set;
    private readonly MiddlewareInfo _infoRecord;
    private readonly string _info;
    private readonly IHookViews? _views;
    private readonly bool[] _wants;
    private readonly MiddlewareInstance?[] _instances;
    private readonly JsValue[] _contexts;
    private MiddlewareReject? _rejected;
    // Set only while an onUpstream runs: its target, and the headers it set (ctx.setHeader / ctx.removeHeader).
    private MiddlewareUpstream? _upstream;
    private List<MiddlewareHeader>? _headers;
    private int _current = -1;
    // onText: per module, the characters a hook held back (ctx.holdBack) and how many it wants held
    private readonly string[] _held;
    private readonly int[] _holdBack;
    private byte[]? _lastTextEvent;

    internal MiddlewareRun(MiddlewareSet set, MiddlewareInfo info, IHookViews? views = null)
    {
        _set = set;
        _infoRecord = info;
        _views = views;
        var b = new StringBuilder("{\"protocol\":").Append(Quote(info.Protocol))
            .Append(",\"model\":").Append(Quote(info.Model ?? ""))
            .Append(",\"stream\":").Append(info.Stream ? "true" : "false")
            .Append(",\"path\":").Append(Quote(info.Path));
        if (!string.IsNullOrEmpty(info.Agent)) b.Append(",\"agent\":").Append(Quote(info.Agent));
        _info = b.ToString();
        _wants = [.. set.Modules.Select(m => m.Wants(info))];
        _instances = new MiddlewareInstance?[set.Modules.Count];
        _contexts = new JsValue[set.Modules.Count];
        _held = [.. set.Modules.Select(_ => "")];
        _holdBack = new int[set.Modules.Count];
    }

    /// <summary>Whether some middleware wants the reply's stream events (onEvent, or a v2 set's onText).</summary>
    public bool WantsEvents => Active.Any(m => m.HasEvent || m.HasText);

    /// <summary>Whether some middleware wants a whole JSON reply.</summary>
    public bool WantsResponse => Active.Any(m => m.HasResponse);

    /// <summary>Whether some middleware wants the request as it goes upstream (onUpstream).</summary>
    public bool WantsUpstream => Active.Any(m => m.HasUpstream);

    /// <summary>Whether some v2 hook set wants to hear how the request ended (onComplete).</summary>
    public bool WantsComplete => Active.Any(m => m.HasComplete);

    private IEnumerable<MiddlewareModule> Active => _set.Modules.Where((_, i) => _wants[i]);

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
            if (!_wants[i] || !m.HasUpstream) continue;
            _upstream = upstream;
            _headers = [];
            var input = changed ?? body;
            var (output, error) = ApplyView(i, Hook.Upstream, MiddlewareEngine.RequestLimit, input, null, upstream.Protocol, request: true);
            var set = _headers;
            _upstream = null;
            _headers = null;
            if (error == Dropped) m.Fail("onUpstream", "returned null; a request can't be dropped here (ctx.reject is onRequest's)");
            else if (error is not null) m.Fail("onUpstream", error);
            else if (output is not null && !m.CanRewriteRequest) m.Fail("onUpstream", "changed the request without the permission gateway.request:rewrite");
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
            if (!_wants[i] || !m.HasRequest) continue;
            var (output, error) = ApplyView(i, Hook.Request, MiddlewareEngine.RequestLimit, body, null, _infoRecord.Protocol, request: true);
            if (error is not null)
            {
                m.Fail("onRequest", error);
                continue;
            }
            if (_rejected is not null) return (body, _rejected);
            if (output is not null)
            {
                if (m.CanRewriteRequest) body = output;
                else m.Fail("onRequest", "changed the request without the permission gateway.request:rewrite");
            }
        }
        return (body, null);
    }

    /// <summary>
    /// Runs each onEvent that wants it on one complete event of the stream (with its blank line), then each v2 onText on
    /// the event's text delta. Returns the bytes to write — the event as it was when nothing changed it — or null when a
    /// hook dropped it.
    /// </summary>
    public byte[]? Event(byte[] ev)
    {
        if (!SseEventFramer.TrySplit(ev, out var name, out var start, out var end)) return ev;
        byte[]? data = null;
        for (var i = 0; i < _set.Modules.Count; i++)
        {
            var m = _set.Modules[i];
            if (!_wants[i] || !m.HasEvent || (m.Events is not null && !m.Events.Contains(name))) continue;
            var (output, error) = Apply(i, Hook.Event, MiddlewareEngine.EventLimit, data ?? ev[start..end], null);
            if (error == Dropped)
            {
                if (m.CanRewriteResponse) return null;
                m.Fail("onEvent", "dropped an event without the permission gateway.response:rewrite");
            }
            else if (error is not null) m.Fail("onEvent", error);
            else if (output is not null)
            {
                if (m.CanRewriteResponse) data = output;
                else m.Fail("onEvent", "changed an event without the permission gateway.response:rewrite");
            }
        }
        string? prefix = null;
        if (Active.Any(x => x.HasText)) (data, prefix) = Text(name, data ?? ev[start..end], ev, start, end);
        if (data is null && prefix is null) return ev;
        var body = data ?? ev[start..end];
        var result = new byte[start + body.Length + (ev.Length - end)];
        ev.AsSpan(0, start).CopyTo(result);
        body.CopyTo(result, start);
        ev.AsSpan(end).CopyTo(result.AsSpan(start + body.Length));
        return prefix is null ? result : [.. Encoding.UTF8.GetBytes(prefix), .. result];
    }

    // onText: the event's text delta through each v2 hook set that has one, with what each held back put in front of
    // its next delta; at the end of the text, held text goes out (in this event, or a copy of the last text event before it).
    private (byte[]? Data, string? Prefix) Text(string name, byte[] json, byte[] ev, int start, int end)
    {
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null);
        }
        if (obj is null) return (null, null);
        var protocol = _infoRecord.Protocol;
        var text = TextDeltas.Get(protocol, name, obj);
        var changed = false;
        if (text is not null)
        {
            for (var i = 0; i < _set.Modules.Count; i++)
            {
                var m = _set.Modules[i];
                if (!_wants[i] || !m.HasText) continue;
                var input = _held[i] + text;
                _held[i] = "";
                var output = CallText(i, input);
                if (output is null) output = input;
                else if (!m.CanRewriteResponse)
                {
                    m.Fail("onText", "changed the reply without the permission gateway.response:rewrite");
                    output = input;
                }
                if (_holdBack[i] > 0 && output.Length > 0)
                {
                    var keep = Math.Min(_holdBack[i], output.Length);
                    _held[i] = output[^keep..];
                    output = output[..^keep];
                }
                if (output != text) changed = true;
                text = output;
            }
            _lastTextEvent = ev;
        }
        string? prefix = null;
        if (TextDeltas.Ends(protocol, name, obj) && _held.Any(h => h.Length > 0))
        {
            var rest = string.Concat(_held);
            for (var i = 0; i < _held.Length; i++) _held[i] = "";
            if (text is not null)
            {
                text += rest;
                changed = true;
            }
            else if (_lastTextEvent is { } template && SseEventFramer.TrySplit(template, out var tname, out var ts, out var te)
                     && JsonNode.Parse(template.AsSpan(ts, te - ts)) is JsonObject tobj && TextDeltas.Set(protocol, tname, tobj, rest))
                prefix = Encoding.UTF8.GetString(template.AsSpan(0, ts)) + tobj.ToJsonString() + Encoding.UTF8.GetString(template.AsSpan(te));
        }
        if (!changed) return (null, prefix);
        TextDeltas.Set(protocol, name, obj, text!);
        return (Encoding.UTF8.GetBytes(obj.ToJsonString()), prefix);
    }

    private string? CallText(int i, string text)
    {
        var m = _set.Modules[i];
        var started = Stopwatch.GetTimestamp();
        MiddlewareInstance? held = null;
        try
        {
            var (instance, ctx) = Acquire(i);
            held = instance;
            _current = i;
            var result = instance.Runtime.Call(instance.OnText, MiddlewareEngine.EventLimit, text, ctx);
            if (result.IsUndefined) return null;
            if (result.IsString) return result.AsString();
            m.Fail("onText", "returned neither a string nor undefined");
            return null;
        }
        catch (JsHookException e)
        {
            m.Fail("onText", e.Message);
            return null;
        }
        finally
        {
            _current = -1;
            Release(i, held);
            m.Count(Stopwatch.GetElapsedTime(started).Ticks * 100);
        }
    }

    /// <summary>Runs each onResponse on a whole JSON reply, with <c>ctx.status</c> set; returns the body to write.</summary>
    public byte[] Response(byte[] body, int status)
    {
        for (var i = 0; i < _set.Modules.Count; i++)
        {
            var m = _set.Modules[i];
            if (!_wants[i] || !m.HasResponse) continue;
            var (output, error) = ApplyView(i, Hook.Response, MiddlewareEngine.RequestLimit, body, status, _infoRecord.Protocol, request: false);
            if (error == Dropped) m.Fail("onResponse", "returned null; a whole reply can't be dropped");
            else if (error is not null) m.Fail("onResponse", error);
            else if (output is not null)
            {
                if (m.CanRewriteResponse) body = output;
                else m.Fail("onResponse", "changed the reply without the permission gateway.response:rewrite");
            }
        }
        return body;
    }

    /// <summary>
    /// Tells each v2 onComplete how the request ended: status, usage (the reply's usage object, when known), duration and
    /// the error text. Read-only: what the hooks return is ignored.
    /// </summary>
    public void Complete(int status, JsonObject? usage, long durationMs, string? error)
    {
        if (!WantsComplete) return;
        var info = new JsonObject { ["status"] = status, ["durationMs"] = durationMs };
        if (usage is not null) info["usage"] = usage.DeepClone();
        if (error is not null) info["error"] = error;
        var json = Encoding.UTF8.GetBytes(info.ToJsonString());
        for (var i = 0; i < _set.Modules.Count; i++)
        {
            var m = _set.Modules[i];
            if (!_wants[i] || !m.HasComplete) continue;
            var (_, err) = Apply(i, Hook.Complete, MiddlewareEngine.RequestLimit, json, status);
            if (err is not null && err != Dropped) m.Fail("onComplete", err);
        }
    }

    /// <summary>Gives the engines back to their pools.</summary>
    public void Dispose()
    {
        for (var i = 0; i < _instances.Length; i++)
        {
            if (_instances[i] is { } instance) _set.Modules[i].Put(instance);
            _instances[i] = null;
            _contexts[i] = default;
        }
    }

    private const string Dropped = "dropped";

    private enum Hook
    {
        Request,
        Event,
        Response,
        Upstream,
        Complete,
    }

    // A hook on the view its module asked for: an ir set gets the protocol-free view, and its changes are written back
    // into the protocol's JSON; without views (the relay) or for a body that has none, it gets the JSON itself.
    private (byte[]? Output, string? Error) ApplyView(int i, Hook hook, TimeSpan limit, byte[] data, int? status, string protocol, bool request)
    {
        var m = _set.Modules[i];
        if (!m.IrView || _views is null) return Apply(i, hook, limit, data, status);
        var view = request ? _views.RequestView(data, protocol) : _views.ResponseView(data, protocol);
        if (view is null) return Apply(i, hook, limit, data, status);
        var (output, error) = Apply(i, hook, limit, view, status);
        if (error is not null || output is null) return (null, error);
        var applied = request ? _views.ApplyRequestView(data, protocol, view, output) : _views.ApplyResponseView(data, protocol, view, output);
        return (applied, null);
    }

    // Calls a hook on JSON data: the JSON it returned, null for undefined (keep), or an error — Dropped
    // for null. A call's cost is all it costs the request: the JSON read into the engine and written
    // back, which is most of it on a large body, as well as the hook.
    private (byte[]? Output, string? Error) Apply(int i, Hook hook, TimeSpan limit, ReadOnlySpan<byte> data, int? status)
    {
        var m = _set.Modules[i];
        var started = Stopwatch.GetTimestamp();
        MiddlewareInstance? held = null;
        try
        {
            var (instance, ctx) = Acquire(i);
            held = instance;
            var rt = instance.Runtime;
            if (status is { } s) rt.Set(ctx, "status", s);
            if (hook == Hook.Upstream && _upstream is { } u)
                rt.Set(ctx, "upstream", rt.ParseJson(new StringBuilder("{\"protocol\":").Append(Quote(u.Protocol))
                    .Append(",\"provider\":{\"id\":").Append(Quote(u.ProviderId)).Append(",\"name\":").Append(Quote(u.ProviderName))
                    .Append("},\"model\":").Append(Quote(u.Model)).Append(",\"stream\":").Append(u.Stream ? "true" : "false").Append('}').ToString()));
            var fn = hook switch
            {
                Hook.Request => instance.OnRequest,
                Hook.Event => instance.OnEvent,
                Hook.Upstream => instance.OnUpstream,
                Hook.Complete => instance.OnComplete,
                _ => instance.OnResponse,
            };
            _current = i;
            // a JSON.parse of the data itself fails as a SyntaxError (it was not JSON after all)
            var result = rt.Call(fn, limit, rt.ParseJson(data), ctx);
            if (hook == Hook.Complete || result.IsUndefined) return (null, null);
            if (result.IsNull) return (null, Dropped);
            if (!result.IsObject) return (null, "returned neither an object, null nor undefined");
            return (rt.ToJsonBytes(result), null);
        }
        catch (JsHookException e)
        {
            return (null, e.Message);
        }
        finally
        {
            _current = -1;
            Release(i, held);
            m.Count(Stopwatch.GetElapsedTime(started).Ticks * 100);
        }
    }

    private void Release(int i, MiddlewareInstance? held)
    {
        if (held is { Runtime.IsFaulted: true })
        {
            // a limit stopped it: Jaunt can't run that engine again
            _instances[i] = null;
            _contexts[i] = default;
            _set.Modules[i].Put(held);
        }
    }

    // Middleware i's engine for this request and its ctx: the request's info, the plugin's options, a
    // state of its own, reject, sha256, and the header setters onUpstream uses. A v2 hook set's ctx also has the
    // client, the token, the request id, ctx.shared and emit / count / holdBack.
    private (MiddlewareInstance, JsValue) Acquire(int i)
    {
        if (_instances[i] is { } existing) return (existing, _contexts[i]);
        var m = _set.Modules[i];
        var instance = m.Get();
        instance.Run = this;
        _instances[i] = instance;
        var rt = instance.Runtime;
        var json = new StringBuilder(_info).Append(",\"options\":").Append(Encoding.UTF8.GetString(m.Options)).Append(",\"state\":{}");
        if (m.V2Plugin is not null)
        {
            json.Append(",\"client\":").Append(_infoRecord.Agent is { } agent ? Quote(agent) : "null");
            json.Append(",\"token\":").Append(_infoRecord.TokenId is { } tid
                ? "{\"id\":" + Quote(tid) + ",\"name\":" + Quote(_infoRecord.TokenName ?? "") + "}" : "null");
            if (_infoRecord.RequestId is { } rid) json.Append(",\"requestId\":").Append(Quote(rid));
            json.Append(",\"shared\":").Append(m.SharedJson);
        }
        var ctx = rt.ParseJson(json.Append('}').ToString());
        rt.Set(ctx, "reject", instance.Reject);
        rt.Set(ctx, "sha256", instance.Sha256);
        rt.Set(ctx, "setHeader", instance.SetHeader);
        rt.Set(ctx, "removeHeader", instance.RemoveHeader);
        if (m.V2Plugin is not null)
        {
            rt.Set(ctx, "emit", instance.Emit);
            rt.Set(ctx, "count", instance.CountFn);
            rt.Set(ctx, "holdBack", instance.HoldBack);
        }
        _contexts[i] = ctx;
        return (instance, ctx);
    }

    // ctx.emit(name, payload): a v2 plugin's declared event, delivered to its subscribers after this call.
    internal JsValue Emit(HostArgs args)
    {
        if (_current < 0 || _set.Modules[_current] is not { V2Plugin: { } plugin, Gateway: { } gateway } m) return JsValue.Undefined;
        var name = args[0].IsString ? args[0].AsString() : "";
        var payload = args.Length > 1 && !args[1].IsUndefined ? _instances[_current]?.Runtime.ToJson(args[1]) : null;
        gateway.Emit(plugin, name, payload);
        _ = m;
        return JsValue.Undefined;
    }

    // ctx.count(name, n = 1): a counter on the plugin page.
    internal JsValue CountMetric(HostArgs args)
    {
        if (_current < 0 || _set.Modules[_current] is not { V2Plugin: { } plugin, Gateway: { } gateway }) return JsValue.Undefined;
        var name = args[0].IsString ? args[0].AsString() : "";
        if (name.Length is 0 or > 64) return JsValue.Undefined;
        var n = args.Length > 1 && args[1].IsNumber ? (long)Math.Clamp(args[1].AsNumber(), -1e9, 1e9) : 1;
        gateway.Count(plugin.Id, name, n);
        return JsValue.Undefined;
    }

    // ctx.holdBack(n): onText keeps the last n characters of what it returned for the next delta (to match across deltas).
    internal JsValue HoldBack(HostArgs args)
    {
        if (_current < 0) return JsValue.Undefined;
        _holdBack[_current] = args[0].IsNumber ? (int)Math.Clamp(args[0].AsNumber(), 0, 4096) : 0;
        return JsValue.Undefined;
    }

    // ctx.reject(status, message): onRequest turns the request away. Only the first call counts.
    internal JsValue Reject(HostArgs args)
    {
        var status = 403;
        if (args[0].IsNumber && args[0].AsNumber() is var n && n >= 400 && n <= 599) status = (int)n;
        var message = args[1].IsString ? args[1].AsString() : "turned away by a gateway middleware";
        if (_current >= 0 && !_set.Modules[_current].CanReject)
        {
            _set.Modules[_current].Say("warn", "ctx.reject ignored: it needs the permission gateway.request:reject");
            return JsValue.Undefined;
        }
        _rejected ??= new MiddlewareReject(status, message);
        return JsValue.Undefined;
    }

    // ctx.sha256(text | bytes): the hex SHA-256 of a string's UTF-8 bytes, or of an array of byte values (0–255, taken
    // modulo 256) for a digest over raw bytes, such as a few bytes cut out of UTF-8 text. The engine has no crypto of its own.
    internal static JsValue Sha256(HostArgs args)
    {
        byte[] data;
        if (args[0].IsArray)
        {
            var array = args[0];
            var count = args.Own(array, "length");
            var length = count.IsNumber ? (int)Math.Clamp(count.AsNumber(), 0, 16 * 1024 * 1024) : 0;
            data = new byte[length];
            for (var i = 0; i < length; i++)
            {
                var v = args.Own(array, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                data[i] = v.IsNumber && double.IsFinite(v.AsNumber()) ? (byte)((long)v.AsNumber() & 0xff) : (byte)0;
            }
        }
        else data = Encoding.UTF8.GetBytes(args[0].IsString ? args[0].AsString() : "");
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
    }

    // ctx.setHeader(name, value) / ctx.removeHeader(name): only inside onUpstream; anything else is ignored with a line in
    // the log, so a plugin written against the wrong hook does nothing rather than fail the request.
    internal JsValue SetHeader(HostArgs args, bool remove)
    {
        var m = _current >= 0 ? _set.Modules[_current] : null;
        var name = args[0].IsString ? args[0].AsString().Trim() : "";
        // a primitive as String(v) gives it; an object has no header form of its own
        string? value = remove || args[1].IsNullOrUndefined ? null : args[1].ToString();
        var verb = remove ? "removeHeader" : "setHeader";
        if (_headers is null)
            m?.Say("warn", $"ctx.{verb} works in onUpstream only");
        else if (!remove && value is null)
            m?.Say("warn", $"ctx.setHeader({name}) without a value; use ctx.removeHeader");
        else if (m is { CanSetHeaders: false })
            m.Say("warn", $"ctx.{verb}({name}) ignored: it needs the permission gateway.upstream:headers");
        else if (MiddlewareHeader.Refusal(name, value) is { } why)
            m?.Say("warn", $"ctx.{verb}({name}) ignored: {why}");
        else
            _headers.Add(new MiddlewareHeader(name, value));
        return JsValue.Undefined;
    }

    private static string Quote(string s) => "\"" + System.Text.Json.JsonEncodedText.Encode(s).Value + "\"";
}
