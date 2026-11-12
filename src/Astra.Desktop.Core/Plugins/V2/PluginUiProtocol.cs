using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Desktop.Core.Plugins.V2;

/// <summary>
/// The v2 plugin UI protocol on the desktop's side (design/plugin-v2 03, 04), the same rules as the web shell's
/// <c>web/src/plugin-ui/protocol.ts</c>: applying a patch, local conditions, responsive values, axis ticks, formatting and
/// calendar dates. tests/fixtures/plugin-ui-behavior holds the cases both shells run.
/// </summary>
public static class PluginUiProtocol
{
    /// <summary>The UI protocol version this shell renders.</summary>
    public const string UiVersion = "1.0";

    /// <summary>The components the desktop renders (reported when a view opens); the tests check them against components.json.</summary>
    public static readonly string[] Components =
    [
        "Page", "Stack", "Grid", "Split", "Scroll", "Box", "Divider", "Spacer", "Sticky",
        "Group", "Row", "Card", "Section", "Tabs", "Tab", "Accordion", "AccordionItem", "Disclosure", "Stepper", "Timeline", "Breadcrumb", "Toolbar", "Boundary",
        "Text", "Heading", "RichText", "Markdown", "Code", "JsonView", "DiffView", "Link", "Icon", "Image", "Avatar", "Badge", "StatusDot", "Kbd", "KV",
        "Alert", "Banner", "EmptyState", "Skeleton", "Spinner", "Progress", "Meter", "Counter", "QRCode",
        "Button", "IconButton", "ButtonGroup", "SplitButton", "CopyButton", "ConfirmButton", "Menu", "ContextMenu",
        "TextField", "PasswordField", "TextArea", "NumberField", "MoneyField", "SearchField", "CodeField", "Switch", "Checkbox", "RadioGroup", "RadioCards",
        "Segmented", "Select", "Combobox", "MultiSelect", "ChipGroup", "TagInput", "Slider", "DatePicker", "DateRangePicker", "TimePicker",
        "KeyValueEditor", "ListEditor", "FileDrop", "Form", "Field", "FieldGroup",
        "Table", "List", "Tree", "Pagination", "LogView", "MetricCard", "Sparkline", "Chart", "Heatmap", "Gauge", "Canvas",
        "Sheet", "Dialog", "Popover",
        "ModelPicker", "ProviderPicker", "AccountPicker", "ClientPicker", "TokenPicker", "ProtocolPicker", "ModelLabel", "ProviderLabel", "ClientLabel",
        "Money", "RequestLink", "TraceLink", "UsageBar",
    ];

    /// <summary>Applies patch ops to a tree (in place on a copy); returns the new root.</summary>
    public static JsonObject ApplyPatch(JsonObject tree, JsonArray ops)
    {
        var root = (JsonObject)tree.DeepClone();
        foreach (var opNode in ops)
        {
            if (opNode is not JsonObject op) continue;
            var path = (op["path"] as JsonArray ?? []).Select(p => p!.GetValue<int>()).ToList();
            var kind = op["op"]?.GetValue<string>();
            if (kind == "replace" && path.Count == 0)
            {
                root = (JsonObject)op["node"]!.DeepClone();
                continue;
            }
            var parent = Walk(root, kind == "replace" ? path.Take(path.Count - 1) : path);
            if (parent is null) continue;
            switch (kind)
            {
                case "replace":
                    ((JsonArray)parent["children"]!)[path[^1]] = op["node"]!.DeepClone();
                    break;
                case "props":
                    if (parent["props"] is not JsonObject props) parent["props"] = props = [];
                    if (op["set"] is JsonObject set) foreach (var (k, v) in set) props[k] = v?.DeepClone();
                    if (op["unset"] is JsonArray unset) foreach (var k in unset) props.Remove(k!.GetValue<string>());
                    break;
                case "insert":
                    if (parent["children"] is not JsonArray kids) parent["children"] = kids = [];
                    kids.Insert(op["index"]!.GetValue<int>(), op["node"]!.DeepClone());
                    break;
                case "remove":
                    var list = (JsonArray)parent["children"]!;
                    list.RemoveAt(op["index"]!.GetValue<int>());
                    if (list.Count == 0) parent.Remove("children");
                    break;
                case "move":
                    var moving = (JsonArray)parent["children"]!;
                    var item = moving[op["from"]!.GetValue<int>()]!;
                    moving.RemoveAt(op["from"]!.GetValue<int>());
                    moving.Insert(op["to"]!.GetValue<int>(), item);
                    break;
            }
        }
        return root;
    }

    private static JsonObject? Walk(JsonObject root, IEnumerable<int> path)
    {
        var node = root;
        foreach (var i in path)
        {
            if (node["children"] is not JsonArray kids || i < 0 || i >= kids.Count || kids[i] is not JsonObject child) return null;
            node = child;
        }
        return node;
    }

    // ---------- local conditions (03 §6)

    /// <summary>Evaluates a local condition over the local state bag.</summary>
    public static bool EvalWhen(JsonNode? expr, IReadOnlyDictionary<string, JsonNode?> local) => Truthy(Value(expr, local));

    /// <summary><c>visible</c> / <c>disabled</c>: a boolean, or <c>{ when: expr }</c>.</summary>
    public static bool Flag(JsonNode? v, IReadOnlyDictionary<string, JsonNode?> local, bool fallback)
    {
        if (v is null) return fallback;
        if (v.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return v.GetValue<bool>();
        if (v is JsonObject o && o.ContainsKey("when")) return EvalWhen(o["when"], local);
        return fallback;
    }

    private static JsonNode? Value(JsonNode? expr, IReadOnlyDictionary<string, JsonNode?> local)
    {
        if (expr is not JsonArray a || a.Count == 0 || a[0] is not JsonValue head || !head.TryGetValue<string>(out var op)) return expr;
        JsonNode? Arg(int i) => i < a.Count ? Value(a[i], local) : null;
        return op switch
        {
            "local" => local.TryGetValue(a.Count > 1 ? a[1]?.ToString() ?? "" : "", out var v) ? v : null,
            "eq" => B(Same(Arg(1), Arg(2))),
            "ne" => B(!Same(Arg(1), Arg(2))),
            "and" => B(a.Skip(1).All(x => Truthy(Value(x, local)))),
            "or" => B(a.Skip(1).Any(x => Truthy(Value(x, local)))),
            "not" => B(!Truthy(Arg(1))),
            "in" => B(Arg(2) is JsonArray list && list.Any(x => Same(x, Arg(1)))),
            "empty" => B(Arg(1) switch { null => true, JsonArray l => l.Count == 0, JsonValue s when s.TryGetValue<string>(out var str) => str.Length == 0, _ => false }),
            "gt" => B(Num(Arg(1)) > Num(Arg(2))),
            "lt" => B(Num(Arg(1)) < Num(Arg(2))),
            _ => expr,
        };
    }

    private static double Num(JsonNode? n) => ToDouble(n);

    /// <summary>A JSON number as a double, however it was made (parsed, or created from an int / long / decimal); NaN otherwise.</summary>
    public static double ToDouble(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

    private static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    private static bool Truthy(JsonNode? n) => n switch
    {
        null => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => ToDouble(v) != 0,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>().Length > 0,
        _ => true,
    };

    private static JsonNode B(bool b) => JsonValue.Create(b);

    // ---------- responsive values (05 §1)

    /// <summary>compact (&lt; 480), regular, wide (≥ 960): the view container's width class.</summary>
    public static string WidthClass(double px) => px < 480 ? "compact" : px >= 960 ? "wide" : "regular";

    /// <summary>A value that may be <c>{ compact, regular, wide }</c>, resolved for <paramref name="width"/>.</summary>
    public static JsonNode? Responsive(JsonNode? v, string width)
    {
        if (v is not JsonObject o || !(o.ContainsKey("compact") || o.ContainsKey("regular") || o.ContainsKey("wide"))) return v;
        return o[width] ?? (width is "wide" or "compact" ? o["regular"] : null) ?? o["compact"] ?? o["regular"] ?? o["wide"];
    }

    // ---------- tokens

    /// <summary>The spacing scale's steps, in px.</summary>
    public static double Gap(string? token) => token switch { "0" => 0, "1" => 4, "2" => 8, "3" => 12, "4" => 16, "6" => 24, "8" => 32, _ => 12 };

    /// <summary>A width token in px (fill and auto are NaN: the layout decides).</summary>
    public static double Width(string? token) => token switch { "xs" => 120, "sm" => 200, "md" => 320, "lg" => 480, "xl" => 720, _ => double.NaN };

    /// <summary>The data palette (05 §2), the same eight colours as the web.</summary>
    public static readonly string[] Palette = ["#0a84ff", "#30d158", "#ff9f0a", "#bf5af2", "#ff375f", "#64d2ff", "#ffd60a", "#ac8e68"];

    // ---------- chart ticks (04 §7)

    /// <summary>The "nice numbers" value-axis ticks (the web's <c>niceTicks</c>; same results for the same input).</summary>
    public static List<double> NiceTicks(double min, double max, int count = 4)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max)) return [0];
        if (min == max)
        {
            if (min == 0) return [0, 1];
            max = min > 0 ? min * 2 : 0;
            min = min > 0 ? 0 : min * 2;
        }
        var span = NiceNum(max - min, false);
        var step = NiceNum(span / Math.Max(1, count), true);
        var lo = Math.Floor(min / step) * step;
        var hi = Math.Ceiling(max / step) * step;
        var ticks = new List<double>();
        for (var v = lo; v <= hi + step / 2; v += step) ticks.Add(double.Parse(v.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        return ticks;
    }

    private static double NiceNum(double range, bool round)
    {
        var exp = Math.Floor(Math.Log10(range));
        var f = range / Math.Pow(10, exp);
        var nf = round ? (f < 1.5 ? 1 : f < 3 ? 2 : f < 7 ? 5 : 10) : f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10;
        return nf * Math.Pow(10, exp);
    }

    // ---------- formatting (05 §9)

    /// <summary>A value as the <c>format</c> token says, in <paramref name="locale"/>.</summary>
    public static string Format(string? kind, JsonNode? v, string locale)
    {
        if (v is null) return "";
        var culture = Culture(locale);
        var isNum = v.GetValueKind() == JsonValueKind.Number || (v is JsonValue sv && sv.TryGetValue<string>(out var s0) && double.TryParse(s0, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        var n = isNum ? (v.GetValueKind() == JsonValueKind.Number ? ToDouble(v) : double.Parse(v.GetValue<string>(), CultureInfo.InvariantCulture)) : double.NaN;
        string Plain() => v is JsonValue pv && pv.TryGetValue<string>(out var str) ? str : v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? v.ToString() : v.ToJsonString();
        switch (kind)
        {
            case "number":
                return isNum ? n.ToString("#,0.##########", culture) : Plain();
            case "compact":
            case "tokens":
                return isNum ? Compact(n, culture) : Plain();
            case "percent":
                return isNum ? (n * 100).ToString("0.#", culture) + "%" : Plain();
            case "money":
            {
                if (!isNum) return Plain();
                var usd = n / 1e9;
                var digits = Math.Abs(usd) >= 1 ? 2 : Math.Abs(usd) >= 0.01 ? 4 : 6;
                return "$" + usd.ToString("F" + digits, CultureInfo.InvariantCulture);
            }
            case "bytes":
            {
                if (!isNum) return Plain();
                string[] units = ["B", "KB", "MB", "GB", "TB"];
                var x = n;
                var u = 0;
                while (Math.Abs(x) >= 1024 && u < units.Length - 1)
                {
                    x /= 1024;
                    u++;
                }
                return (u == 0 ? x.ToString(CultureInfo.InvariantCulture) : x.ToString("0.0", CultureInfo.InvariantCulture)) + " " + units[u];
            }
            case "duration":
                if (!isNum) return Plain();
                return n < 1000 ? $"{Math.Round(n)} ms" : n < 60_000 ? (n / 1000).ToString("0.0", CultureInfo.InvariantCulture) + " s" : $"{Math.Floor(n / 60_000)} min {Math.Round(n % 60_000 / 1000)} s";
            case "date":
                return DateOf(v) is { } d ? d.ToString("d", culture) : Plain();
            case "time":
                return DateOf(v) is { } t ? t.ToString("t", culture) : Plain();
            case "datetime":
                return DateOf(v) is { } dt ? dt.ToString("g", culture) : Plain();
            case "relative":
            {
                if (DateOf(v) is not { } at) return Plain();
                var diff = at - DateTime.Now;
                var zh = locale.StartsWith("zh", StringComparison.Ordinal);
                var abs = diff.Duration();
                var (amount, unit) = abs.TotalSeconds < 60 ? ((int)abs.TotalSeconds, zh ? "秒" : "s") : abs.TotalMinutes < 60 ? ((int)abs.TotalMinutes, zh ? "分钟" : "min")
                    : abs.TotalHours < 24 ? ((int)abs.TotalHours, zh ? "小时" : "h") : ((int)abs.TotalDays, zh ? "天" : "d");
                return diff.TotalSeconds <= 0 ? (zh ? $"{amount} {unit}前" : $"{amount} {unit} ago") : (zh ? $"{amount} {unit}后" : $"in {amount} {unit}");
            }
            default:
                return Plain();
        }
    }

    private static string Compact(double n, CultureInfo culture)
    {
        var abs = Math.Abs(n);
        if (culture.Name.StartsWith("zh", StringComparison.Ordinal))
            return abs >= 1e8 ? (n / 1e8).ToString("0.#", culture) + "亿" : abs >= 1e4 ? (n / 1e4).ToString("0.#", culture) + "万" : n.ToString("0.#", culture);
        return abs >= 1e9 ? (n / 1e9).ToString("0.#", culture) + "B" : abs >= 1e6 ? (n / 1e6).ToString("0.#", culture) + "M" : abs >= 1e3 ? (n / 1e3).ToString("0.#", culture) + "K" : n.ToString("0.#", culture);
    }

    private static CultureInfo Culture(string locale)
    {
        try
        {
            return CultureInfo.GetCultureInfo(locale.StartsWith("zh", StringComparison.Ordinal) ? "zh-CN" : "en-US");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    /// <summary>A calendar date (YYYY-MM-DD, that day) or an instant (local time).</summary>
    public static DateTime? DateOf(JsonNode? v)
    {
        if (v is not JsonValue s || !s.TryGetValue<string>(out var text)) return null;
        if (text.Length == 10 && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return day.ToDateTime(TimeOnly.MinValue);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.LocalDateTime : null;
    }

    // ---------- calendar dates (04 §7)

    /// <summary>"today", "-30d", "+1y" or a YYYY-MM-DD → that local date.</summary>
    public static DateOnly? ResolveDate(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Length >= 10 && DateOnly.TryParseExact(v[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (v == "today") return today;
        if (v.Length < 3 || !int.TryParse(v[..^1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return null;
        return v[^1] switch { 'd' => today.AddDays(n), 'w' => today.AddDays(7 * n), 'm' => today.AddMonths(n), 'y' => today.AddYears(n), _ => null };
    }

    /// <summary>A named preset range (today, 7d, 30d, thisMonth, …) as calendar dates, both ends included.</summary>
    public static (DateOnly From, DateOnly To)? Preset(string name)
    {
        var t = DateOnly.FromDateTime(DateTime.Now);
        return name switch
        {
            "today" => (t, t),
            "yesterday" => (t.AddDays(-1), t.AddDays(-1)),
            "7d" => (t.AddDays(-6), t),
            "30d" => (t.AddDays(-29), t),
            "90d" => (t.AddDays(-89), t),
            "thisMonth" => (new DateOnly(t.Year, t.Month, 1), t),
            "lastMonth" => (new DateOnly(t.Year, t.Month, 1).AddMonths(-1), new DateOnly(t.Year, t.Month, 1).AddDays(-1)),
            _ => null,
        };
    }

    /// <summary>A calendar date as the protocol writes it.</summary>
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
