using System.Globalization;
using System.Text.Json.Nodes;

namespace Astra.Providers.Quota;

/// <summary>
/// Evaluates the declarative extractor of a balance / quota query (the AOT-safe replacement for
/// cc-switch's JavaScript <c>extractor(response)</c>). A node is one of:
/// <list type="bullet">
/// <item>a string — a path into the response (<c>data.balance</c>, <c>items[0].total</c>); relative to
/// the current element inside a plan's <c>each</c>, <c>$.</c> forces the response root and <c>@</c> is the element itself;</item>
/// <item>a number or boolean — a constant;</item>
/// <item>an array — candidates, the first one that is present wins (<c>a || b</c>);</item>
/// <item>an object with one operator: <c>$const</c>, <c>$var</c>, <c>$scale</c> (+ <c>by</c>), <c>$add</c>,
/// <c>$sub</c>, <c>$mul</c>, <c>$div</c>, <c>$pctUsed</c> (<c>{used, total}</c>), <c>$eq</c>, <c>$not</c>,
/// <c>$case</c> (<c>{on, cases, default}</c>) and <c>$time</c> (epoch seconds / millis / date string → ISO 8601).</item>
/// </list>
/// A value that cannot be found is absent (null) — never 0. Arithmetic over an absent or non-finite
/// operand is absent too, so a missing upstream field can never turn into a made-up number.
/// Pure functions over <see cref="JsonNode"/>: no I/O, no reflection.
/// </summary>
public static class QuotaExpression
{
    private static readonly HashSet<string> Operators =
        ["$const", "$var", "$scale", "$add", "$sub", "$mul", "$div", "$pctUsed", "$eq", "$not", "$case", "$time"];

    /// <summary>Variables (<c>$var</c>), the response root and the current element of an evaluation.</summary>
    public sealed record Scope(JsonNode? Root, JsonNode? Current, IReadOnlyDictionary<string, string> Vars)
    {
        public Scope At(JsonNode? element) => this with { Current = element };
    }

    /// <summary>Evaluates <paramref name="spec"/>; null means "absent".</summary>
    public static JsonNode? Evaluate(JsonNode? spec, Scope scope)
    {
        switch (spec)
        {
            case null:
                return null;
            case JsonValue value when value.TryGetValue<string>(out var path):
                return Resolve(path, scope)?.DeepClone();
            case JsonValue value:
                return value.DeepClone();
            case JsonArray candidates:
                foreach (var candidate in candidates)
                    if (Evaluate(candidate, scope) is { } hit) return hit;
                return null;
            case JsonObject op:
                return Operate(op, scope);
            default:
                return null;
        }
    }

    /// <summary>Numeric view of a node: numbers and numeric strings ("110.00"); null when absent or not finite.</summary>
    public static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        double d;
        if (v.TryGetValue<double>(out var n)) d = n;
        // In-memory values keep their CLR type (an int stays an int), unlike parsed JSON elements.
        else if (v.TryGetValue<long>(out var l)) d = l;
        else if (v.TryGetValue<int>(out var i)) d = i;
        else if (v.TryGetValue<decimal>(out var m)) d = (double)m;
        else if (v.TryGetValue<float>(out var f)) d = f;
        else if (v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) d = parsed;
        else return null;
        return double.IsFinite(d) ? d : null;
    }

    /// <summary>Text view of a node (strings as-is, numbers / booleans formatted invariantly); null when absent.</summary>
    public static string? Text(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s.Length == 0 ? null : s;
        if (v.TryGetValue<bool>(out var b)) return b ? "true" : "false";
        return Number(v) is { } d ? d.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Truthiness for <c>isValid</c> / <c>when</c>: booleans, numbers (≠ 0), "true"/"false"; null when absent.</summary>
    public static bool? Truth(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<bool>(out var b)) return b;
        if (v.TryGetValue<string>(out var s))
            return s.Trim().ToLowerInvariant() switch { "true" => true, "false" => false, _ => Number(v) is { } n ? n != 0 : null };
        return Number(v) is { } d ? d != 0 : null;
    }

    /// <summary>Looks a path up from the current element (or the root with <c>$.</c>).</summary>
    public static JsonNode? Resolve(string path, Scope scope)
    {
        path = path.Trim();
        if (path is "@" or "") return scope.Current;
        if (path == "$") return scope.Root;
        var node = scope.Current;
        if (path.StartsWith("$.", StringComparison.Ordinal))
        {
            node = scope.Root;
            path = path[2..];
        }
        foreach (var segment in SafeSegments(path))
        {
            if (segment.Index is { } index)
            {
                if (node is not JsonArray array || index < 0 || index >= array.Count) return null;
                node = array[index];
            }
            else
            {
                if (node is not JsonObject o || !o.TryGetPropertyValue(segment.Name!, out var next)) return null;
                node = next;
            }
            if (node is null) return null;
        }
        return node;
    }

    /// <summary>Checks a node's shape; appends human-readable problems (path-prefixed) to <paramref name="errors"/>.</summary>
    public static void Validate(JsonNode? spec, string where, List<string> errors)
    {
        switch (spec)
        {
            case null or JsonValue:
                if (spec is JsonValue v && v.TryGetValue<string>(out var path) && !IsValidPath(path))
                    errors.Add($"{where}: 无效的路径 \"{path}\"");
                return;
            case JsonArray candidates:
                if (candidates.Count == 0) errors.Add($"{where}: 候选列表不能为空");
                for (var i = 0; i < candidates.Count; i++) Validate(candidates[i], $"{where}[{i}]", errors);
                return;
            case JsonObject op:
                ValidateOperator(op, where, errors);
                return;
        }
    }

    // ------------------------------------------------------------------ operators

    private static JsonNode? Operate(JsonObject op, Scope scope)
    {
        var name = OperatorName(op);
        switch (name)
        {
            case "$const":
                return op["$const"]?.DeepClone();
            case "$var":
                return Text(op["$var"]) is { } key && scope.Vars.TryGetValue(key, out var value) && value.Length > 0
                    ? JsonValue.Create(value)
                    : null;
            case "$scale":
                return Num(Number(Evaluate(op["$scale"], scope)) * Number(op["by"]));
            case "$add":
            {
                if (op["$add"] is not JsonArray terms || terms.Count == 0) return null;
                double sum = 0;
                foreach (var term in terms)
                {
                    if (Number(Evaluate(term, scope)) is not { } d) return null;
                    sum += d;
                }
                return Num(sum);
            }
            case "$sub":
                return Binary(op["$sub"], scope, (a, b) => a - b);
            case "$mul":
                return Binary(op["$mul"], scope, (a, b) => a * b);
            case "$div":
                return Binary(op["$div"], scope, (a, b) => b == 0 ? null : a / b);
            case "$pctUsed":
            {
                if (op["$pctUsed"] is not JsonObject args) return null;
                if (Number(Evaluate(args["used"], scope)) is not { } used || Number(Evaluate(args["total"], scope)) is not { } total || total <= 0)
                    return null;
                return Num(Math.Clamp(used / total * 100, 0, 100));
            }
            case "$eq":
            {
                if (op["$eq"] is not JsonArray pair || pair.Count != 2) return null;
                var a = Evaluate(pair[0], scope);
                var b = Evaluate(pair[1], scope);
                return a is null || b is null ? null : JsonValue.Create(Same(a, b));
            }
            case "$not":
                return Truth(Evaluate(op["$not"], scope)) is { } t ? JsonValue.Create(!t) : null;
            case "$case":
            {
                if (op["$case"] is not JsonObject args) return null;
                var on = Text(Evaluate(args["on"], scope));
                if (on is not null && args["cases"] is JsonObject cases && cases.TryGetPropertyValue(on, out var hit))
                    return hit?.DeepClone();
                return args["default"]?.DeepClone();
            }
            case "$time":
                return Time(Evaluate(op["$time"], scope)) is { } iso ? JsonValue.Create(iso) : null;
            default:
                return null;
        }
    }

    private static JsonNode? Binary(JsonNode? args, Scope scope, Func<double, double, double?> f)
    {
        if (args is not JsonArray pair || pair.Count != 2) return null;
        if (Number(Evaluate(pair[0], scope)) is not { } a || Number(Evaluate(pair[1], scope)) is not { } b) return null;
        return Num(f(a, b));
    }

    private static JsonNode? Num(double? d) => d is { } v && double.IsFinite(v) ? JsonValue.Create(v) : null;

    /// <summary>Epoch seconds (&lt; 1e12) or millis, or a parsable date string, as ISO 8601 UTC; 0 / negative = no time.</summary>
    public static string? Time(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s))
        {
            if (Number(v) is { } numeric) return FromEpoch(numeric);
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                : null;
        }
        return Number(v) is { } d ? FromEpoch(d) : null;
    }

    private static string? FromEpoch(double value)
    {
        if (value <= 0) return null;
        var ms = value < 1e12 ? value * 1000 : value;
        if (ms > 253402300799999) return null; // beyond year 9999
        return DateTimeOffset.FromUnixTimeMilliseconds((long)ms).ToString("o", CultureInfo.InvariantCulture);
    }

    /// <summary>JSON equality where numbers compare numerically and "1" equals 1.</summary>
    public static bool Same(JsonNode a, JsonNode b)
    {
        if (Number(a) is { } x && Number(b) is { } y) return x == y;
        if (a is JsonValue va && b is JsonValue vb && va.TryGetValue<bool>(out var ba) && vb.TryGetValue<bool>(out var bb)) return ba == bb;
        return JsonNode.DeepEquals(a, b) || (Text(a) is { } ta && ta == Text(b));
    }

    private static string? OperatorName(JsonObject op) =>
        op.Select(kv => kv.Key).FirstOrDefault(k => k.StartsWith('$'));

    private static void ValidateOperator(JsonObject op, string where, List<string> errors)
    {
        var ops = op.Select(kv => kv.Key).Where(k => k.StartsWith('$')).ToList();
        if (ops.Count != 1)
        {
            errors.Add($"{where}: 运算对象必须且只能有一个以 $ 开头的运算符");
            return;
        }
        var name = ops[0];
        if (!Operators.Contains(name))
        {
            errors.Add($"{where}: 未知运算符 {name}");
            return;
        }
        var allowed = name switch
        {
            "$scale" => new[] { "$scale", "by" },
            _ => [name],
        };
        foreach (var key in op.Select(kv => kv.Key))
            if (!allowed.Contains(key)) errors.Add($"{where}: {name} 不支持参数 {key}");
        var arg = op[name];
        switch (name)
        {
            case "$const":
                break;
            case "$var":
                if (Text(arg) is null) errors.Add($"{where}: $var 需要变量名");
                break;
            case "$scale":
                Validate(arg, $"{where}.$scale", errors);
                if (Number(op["by"]) is null) errors.Add($"{where}: $scale 需要数字 by");
                break;
            case "$add":
                if (arg is not JsonArray { Count: > 0 } terms) errors.Add($"{where}: $add 需要非空数组");
                else for (var i = 0; i < terms.Count; i++) Validate(terms[i], $"{where}.$add[{i}]", errors);
                break;
            case "$sub" or "$mul" or "$div" or "$eq":
                if (arg is not JsonArray { Count: 2 } pair) errors.Add($"{where}: {name} 需要两个元素的数组");
                else for (var i = 0; i < 2; i++) Validate(pair[i], $"{where}.{name}[{i}]", errors);
                break;
            case "$pctUsed":
                if (arg is not JsonObject pct || !pct.ContainsKey("used") || !pct.ContainsKey("total"))
                    errors.Add($"{where}: $pctUsed 需要 {{used, total}}");
                else
                {
                    Validate(pct["used"], $"{where}.$pctUsed.used", errors);
                    Validate(pct["total"], $"{where}.$pctUsed.total", errors);
                }
                break;
            case "$not" or "$time":
                Validate(arg, $"{where}.{name}", errors);
                break;
            case "$case":
                if (arg is not JsonObject c || c["cases"] is not JsonObject) errors.Add($"{where}: $case 需要 {{on, cases, default}}");
                else Validate(c["on"], $"{where}.$case.on", errors);
                break;
        }
    }

    // ------------------------------------------------------------------ paths

    private readonly record struct Segment(string? Name, int? Index);

    private static bool IsValidPath(string path)
    {
        path = path.Trim();
        if (path is "@" or "$") return true;
        if (path.StartsWith("$.", StringComparison.Ordinal)) path = path[2..];
        if (path.Length == 0) return false;
        try
        {
            return Segments(path).Count > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Segments of a path; a malformed path yields a segment that can never match (it finds nothing).</summary>
    private static List<Segment> SafeSegments(string path)
    {
        try
        {
            return Segments(path);
        }
        catch (FormatException)
        {
            return [new Segment("\0", null)];
        }
    }

    /// <summary>Splits <c>a.b[0].c</c> into name / index segments. A trailing <c>[*]</c> is accepted and ignored.</summary>
    private static List<Segment> Segments(string path)
    {
        var result = new List<Segment>();
        foreach (var part in path.Split('.'))
        {
            if (part.Length == 0) throw new FormatException("empty path segment");
            var bracket = part.IndexOf('[');
            var name = bracket < 0 ? part : part[..bracket];
            if (name.Length > 0) result.Add(new Segment(name, null));
            var rest = bracket < 0 ? "" : part[bracket..];
            while (rest.Length > 0)
            {
                var close = rest.IndexOf(']');
                if (rest[0] != '[' || close < 0) throw new FormatException("bad index");
                var inner = rest[1..close];
                if (inner == "*")
                {
                    // "items[*]" means "the array itself" — iteration is the plan's "each".
                }
                else if (int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    result.Add(new Segment(null, index));
                else throw new FormatException("bad index");
                rest = rest[(close + 1)..];
            }
        }
        return result;
    }
}
