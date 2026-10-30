using System.Text.Json.Nodes;

namespace Astra.Desktop.Core.Traces;

/// <summary>
/// Where the trace canvases remember things between runs: one top-level key of <c>desktop-prefs.json</c> (the shell's <c>DesktopPrefs</c>
/// extras). The app attaches it with <see cref="TracePrefs.Use"/>; without one (unit tests, before the Traces page is built) everything
/// lasts the app session only.
/// </summary>
public interface ITracePrefsStore
{
    /// <summary>The stored value of <paramref name="key"/> (a copy), or null.</summary>
    JsonNode? Get(string key);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/> (null removes it) and saves.</summary>
    void Set(string key, JsonNode? value);
}

/// <summary>
/// The persisted state of the trace canvases (canvas.tsx keeps it in localStorage: <c>astra.traces.inspector</c> = "1" / "0" and
/// <c>astra.traces.layout.&lt;key&gt;</c> = <c>{ id: { x, y } }</c>; topology.tsx keeps <c>astra.traces.activity</c> = "0" when the activity
/// feed is hidden). Here all of it lives in one prefs key, <see cref="Key"/>:
/// <c>{ "inspector": bool, "activity": bool, "layouts": { "&lt;key&gt;": { "&lt;node id&gt;": { "x": n, "y": n } } } }</c>.
/// </summary>
public static class TracePrefs
{
    /// <summary>The top-level prefs key.</summary>
    public const string Key = "traces";

    private static readonly object Gate = new();
    private static ITracePrefsStore? _store;

    /// <summary>
    /// Attaches <paramref name="store"/> (null detaches). Attaching a different store forgets the session's layouts (they are read from the
    /// new store when next asked for) and adopts its 详情 switch value when it has one. Attaching the same store again does nothing.
    /// </summary>
    public static void Use(ITracePrefsStore? store)
    {
        lock (Gate)
        {
            if (ReferenceEquals(store, _store)) return;
            _store = store;
        }
        TraceLayouts.Forget();
        _rail = null;
        if (store?.Get(Key) is JsonObject o && o["inspector"] is JsonValue v && v.TryGetValue<bool>(out var on)) TraceInspectorSwitch.Load(on);
    }

    /// <summary>The positions stored under <paramref name="layoutKey"/> (non-finite entries dropped); null when nothing is stored.</summary>
    internal static Dictionary<string, Pt>? ReadLayout(string layoutKey)
    {
        var store = _store;
        if (store?.Get(Key) is not JsonObject o || o["layouts"] is not JsonObject layouts || layouts[layoutKey] is not JsonObject map) return null;
        return ParseLayout(map);
    }

    /// <summary>Stores <paramref name="positions"/> under <paramref name="layoutKey"/> (an empty map removes it).</summary>
    internal static void WriteLayout(string layoutKey, IReadOnlyDictionary<string, Pt> positions)
    {
        Update(o =>
        {
            var layouts = o["layouts"] as JsonObject ?? new JsonObject();
            o["layouts"] = layouts;
            if (positions.Count == 0) layouts.Remove(layoutKey);
            else layouts[layoutKey] = FormatLayout(positions);
            if (layouts.Count == 0) o.Remove("layouts");
        });
    }

    /// <summary>Stores the 详情 switch.</summary>
    internal static void WriteInspector(bool on) => Update(o => o["inspector"] = on);

    /// <summary>Whether the topology shows its activity feed (default on; topology.tsx <c>readRail</c>).</summary>
    public static bool ActivityRail
    {
        get => _rail ??= _store?.Get(Key) is not JsonObject o || o["activity"] is not JsonValue v || !v.TryGetValue<bool>(out var on) || on;
        set
        {
            _rail = value;
            Update(o =>
            {
                if (value) o.Remove("activity");
                else o["activity"] = false;
            });
        }
    }

    private static bool? _rail;

    private static void Update(Action<JsonObject> change)
    {
        var store = _store;
        if (store is null) return;
        var o = store.Get(Key) as JsonObject ?? new JsonObject();
        change(o);
        store.Set(Key, o.Count == 0 ? null : o);
    }

    /// <summary>Reads one layout map; entries without finite <c>x</c> / <c>y</c> are dropped (canvas.tsx <c>readLayout</c>).</summary>
    public static Dictionary<string, Pt> ParseLayout(JsonObject map)
    {
        var result = new Dictionary<string, Pt>(StringComparer.Ordinal);
        foreach (var (id, node) in map)
            if (node is JsonObject p && Num(p["x"]) is { } x && Num(p["y"]) is { } y) result[id] = new Pt(x, y);
        return result;
    }

    /// <summary>Writes one layout map.</summary>
    public static JsonObject FormatLayout(IReadOnlyDictionary<string, Pt> positions)
    {
        var map = new JsonObject();
        foreach (var (id, p) in positions.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (double.IsFinite(p.X) && double.IsFinite(p.Y)) map[id] = new JsonObject { ["x"] = p.X, ["y"] = p.Y };
        return map;
    }

    private static double? Num(JsonNode? node)
        => node is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
}

/// <summary>
/// Where the user dragged the nodes of a canvas, kept under a layout key (the topology) and persisted through <see cref="TracePrefs"/>
/// (canvas.tsx <c>readLayout</c> / <c>writeLayout</c>); canvases without a key keep their own map while they are open.
/// </summary>
public static class TraceLayouts
{
    private static readonly Dictionary<string, Dictionary<string, Pt>> Cached = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>The positions remembered under <paramref name="key"/> (a copy; empty when none or no key).</summary>
    public static Dictionary<string, Pt> Read(string? key)
    {
        if (key is null) return new Dictionary<string, Pt>(StringComparer.Ordinal);
        lock (Gate)
        {
            if (!Cached.TryGetValue(key, out var map))
                Cached[key] = map = TracePrefs.ReadLayout(key) ?? new Dictionary<string, Pt>(StringComparer.Ordinal);
            return new Dictionary<string, Pt>(map, StringComparer.Ordinal);
        }
    }

    /// <summary>Remembers <paramref name="positions"/> under <paramref name="key"/> (an empty map forgets the key) and persists them.</summary>
    public static void Write(string? key, IReadOnlyDictionary<string, Pt> positions)
    {
        if (key is null) return;
        lock (Gate) Cached[key] = new Dictionary<string, Pt>(positions, StringComparer.Ordinal);
        TracePrefs.WriteLayout(key, positions);
    }

    /// <summary>Drops the in-memory copies (a new store was attached).</summary>
    internal static void Forget()
    {
        lock (Gate) Cached.Clear();
    }
}

/// <summary>
/// Whether clicking a node opens its details panel (the canvas's 详情 switch): off by default, one setting for every trace canvas,
/// persisted through <see cref="TracePrefs"/> (canvas.tsx <c>useInspectorSwitch</c>, localStorage <c>astra.traces.inspector</c>).
/// </summary>
public static class TraceInspectorSwitch
{
    private static bool _on;

    /// <summary>The current value.</summary>
    public static bool On => _on;

    /// <summary>Raised after the value changed.</summary>
    public static event Action? Changed;

    /// <summary>Sets the value, stores it and tells every open canvas.</summary>
    public static void Set(bool on)
    {
        if (_on == on) return;
        _on = on;
        TracePrefs.WriteInspector(on);
        Changed?.Invoke();
    }

    /// <summary>Adopts a stored value without writing it back.</summary>
    internal static void Load(bool on)
    {
        if (_on == on) return;
        _on = on;
        Changed?.Invoke();
    }
}

/// <summary>
/// An animated viewport change (React Flow's <c>fitView / zoomIn / zoomOut / setCenter</c> with a <c>duration</c>): d3-zoom's
/// transition, i.e. <c>d3.interpolateZoom</c> (van Wijk &amp; Nuij smooth zoom, ρ = √2) over the view's centre and width, eased with
/// d3's default <c>easeCubicInOut</c>. <see cref="At"/> takes the elapsed fraction of the duration.
/// </summary>
public sealed class ViewportTween
{
    private const double Rho = 1.4142135623730951;
    private const double Rho2 = 2;
    private const double Rho4 = 4;
    private readonly Viewport _from;
    private readonly Viewport _to;
    private readonly double _cx;
    private readonly double _cy;
    private readonly double _w;
    private readonly Func<double, (double Ux, double Uy, double W)> _path;

    /// <summary>A tween from <paramref name="from"/> to <paramref name="to"/> in a <paramref name="width"/> × <paramref name="height"/> view.</summary>
    public ViewportTween(Viewport from, Viewport to, double width, double height)
    {
        _from = from;
        _to = to;
        // d3-zoom: p = the extent's centre, w = its larger side; each view is [the world point under p, w / k]
        _cx = width / 2;
        _cy = height / 2;
        _w = Math.Max(Math.Max(width, height), 1);
        var a = from.ToWorld(new Pt(_cx, _cy));
        var b = to.ToWorld(new Pt(_cx, _cy));
        _path = Interpolate(a.X, a.Y, _w / from.Zoom, b.X, b.Y, _w / to.Zoom);
    }

    /// <summary>The view at <paramref name="fraction"/> (0 – 1) of the duration; exactly the target at 1.</summary>
    public Viewport At(double fraction)
    {
        if (fraction <= 0) return _from;
        if (fraction >= 1) return _to;
        var (ux, uy, w) = _path(EaseCubicInOut(fraction));
        var k = _w / w;
        return new Viewport(_cx - ux * k, _cy - uy * k, k);
    }

    /// <summary>d3's <c>easeCubicInOut</c>.</summary>
    public static double EaseCubicInOut(double t)
    {
        t *= 2;
        return (t <= 1 ? t * t * t : (t -= 2) * t * t + 2) / 2;
    }

    // d3-interpolate's interpolateZoom
    private static Func<double, (double, double, double)> Interpolate(double ux0, double uy0, double w0, double ux1, double uy1, double w1)
    {
        var dx = ux1 - ux0;
        var dy = uy1 - uy0;
        var d2 = dx * dx + dy * dy;
        if (d2 < 1e-12)
        {
            var s = Math.Log(w1 / w0) / Rho;
            return t => (ux0 + t * dx, uy0 + t * dy, w0 * Math.Exp(Rho * t * s));
        }
        var d1 = Math.Sqrt(d2);
        var b0 = (w1 * w1 - w0 * w0 + Rho4 * d2) / (2 * w0 * Rho2 * d1);
        var b1 = (w1 * w1 - w0 * w0 - Rho4 * d2) / (2 * w1 * Rho2 * d1);
        var r0 = Math.Log(Math.Sqrt(b0 * b0 + 1) - b0);
        var r1 = Math.Log(Math.Sqrt(b1 * b1 + 1) - b1);
        var span = (r1 - r0) / Rho;
        return t =>
        {
            var s = t * span;
            var coshr0 = Math.Cosh(r0);
            var u = w0 / (Rho2 * d1) * (coshr0 * Math.Tanh(Rho * s + r0) - Math.Sinh(r0));
            return (ux0 + u * dx, uy0 + u * dy, w0 * coshr0 / Math.Cosh(Rho * s + r0));
        };
    }
}

/// <summary>The timeline's smooth reveal of a focused row (<c>scrollIntoView({ block: 'nearest', behavior: 'smooth' })</c>).</summary>
public static class TimelineScroll
{
    /// <summary>How long the smooth scroll takes (ms).</summary>
    public const double SmoothMs = 300;

    /// <summary>
    /// The scroll offset that shows a row at <paramref name="rowTop"/> (content coordinates, <paramref name="rowHeight"/> tall) with the least
    /// movement: unchanged when it is already in view, else aligned to the nearer edge (the top when the row is taller than the view).
    /// </summary>
    public static double Nearest(double offset, double viewport, double rowTop, double rowHeight, double extent)
    {
        var target = offset;
        var bottom = rowTop + rowHeight;
        if (rowTop < offset || rowHeight > viewport) target = rowTop;
        else if (bottom > offset + viewport) target = bottom - viewport;
        return Math.Clamp(target, 0, Math.Max(0, extent - viewport));
    }
}
