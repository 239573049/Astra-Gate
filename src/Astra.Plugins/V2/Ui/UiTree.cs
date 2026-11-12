using System.Text.Json.Nodes;

namespace Astra.Plugins.V2.Ui;

/// <summary>
/// Checks and normalises a view's tree before it reaches a shell (design/plugin-v2 03 §3): unknown components become a
/// placeholder, unknown or ill-typed props are dropped, events become <c>{ action, args }</c> naming one of the view's
/// actions, <c>Markdown</c> is parsed into ordinary nodes, sibling keys are made unique, and the tree's size is capped.
/// What it drops it reports as warnings (the plugin page and the dev inspector show them); the shells can then trust
/// what they get.
/// </summary>
public static class UiTree
{
    /// <summary>The most nodes a tree may have.</summary>
    public const int MaxNodes = 5000;

    /// <summary>The most bytes a tree may serialise to.</summary>
    public const int MaxBytes = 1024 * 1024;

    /// <summary>The most bytes one action's args may be.</summary>
    public const int MaxArgsBytes = 16 * 1024;

    /// <summary>The normalised tree; throws <see cref="UiTreeException"/> when it is too large to send.</summary>
    public static JsonObject Normalize(JsonNode? tree, IReadOnlySet<string> actions, UiSchema schema, List<string> warnings, string? slot = null)
    {
        var state = new State(actions, schema, warnings);
        JsonObject root;
        if (tree is JsonArray list) root = Node(schema, "Stack", new JsonObject(), list);
        else if (tree is JsonObject o) root = o;
        else if (tree is null) root = Node(schema, "Stack", new JsonObject(), null);
        else
        {
            warnings.Add("render returned neither a node nor a list of nodes");
            root = Node(schema, "Stack", new JsonObject(), null);
        }
        var result = state.Visit(root, "root");
        if (slot is not null && schema.SlotRoots(slot) is { } allowed && !allowed.Contains(result["type"]!.GetValue<string>()))
            throw new UiTreeException($"the {slot} slot takes only {string.Join(", ", allowed)} at its root");
        if (state.Count > MaxNodes) throw new UiTreeException($"the view has {state.Count} nodes; the most is {MaxNodes}");
        var bytes = result.ToJsonString().Length;
        if (bytes > MaxBytes) throw new UiTreeException($"the view is {bytes / 1024} KiB; the most is {MaxBytes / 1024} KiB — use a data channel for large lists");
        return result;
    }

    private static JsonObject Node(UiSchema schema, string type, JsonObject props, JsonArray? children)
    {
        _ = schema;
        var n = new JsonObject { ["type"] = type, ["props"] = props };
        if (children is not null) n["children"] = children.DeepClone();
        return n;
    }

    private sealed class State(IReadOnlySet<string> actions, UiSchema schema, List<string> warnings)
    {
        public int Count;

        public JsonObject Visit(JsonObject raw, string where)
        {
            Count++;
            if (Count > MaxNodes) return new JsonObject { ["type"] = "Spacer", ["props"] = new JsonObject() };
            var type = raw["type"] is JsonValue tv && tv.TryGetValue<string>(out var t) ? t : "";
            var key = raw["key"] is JsonValue kv ? kv.ToString() : null;
            var component = schema.Find(type);
            if (component is null)
            {
                if (raw["props"] is JsonObject fp && fp["fallback"] is JsonObject fallback) return Visit(fallback, where);
                Warn($"{where}: unknown component \"{type}\"");
                return Placeholder(key, $"unknown component {type}");
            }
            var props = raw["props"] as JsonObject ?? [];
            if (type == "Markdown")
            {
                var text = props["value"] is JsonValue mv && mv.TryGetValue<string>(out var md) ? md : "";
                var link = props["onLink"] is { } ol ? Action(ol, $"{where}.Markdown.onLink")?["action"]?.GetValue<string>() : null;
                var max = props["maxBlocks"] is JsonValue bv && bv.TryGetValue<int>(out var b) ? b : 2000;
                var size = props["size"] is JsonValue sv && sv.TryGetValue<string>(out var sz) ? sz : "md";
                var expanded = Visit(MarkdownUi.Render(text, link, max, size), where + ".Markdown");
                if (key is not null) expanded["key"] = key;
                return expanded;
            }

            var outProps = new JsonObject();
            foreach (var (name, value) in props)
            {
                if (value is null) continue;
                if (!component.Props.TryGetValue(name, out var kind))
                {
                    Warn($"{where}.{type}: unknown prop \"{name}\"");
                    continue;
                }
                var checkedValue = Prop(kind, value, $"{where}.{type}.{name}");
                if (checkedValue is not null) outProps[name] = checkedValue;
            }
            foreach (var need in component.Required)
                if (!outProps.ContainsKey(need))
                {
                    Warn($"{where}.{type}: {need} is required");
                    if (need is "a11yLabel" or "alt")
                        outProps[need] = outProps["icon"]?.ToString() ?? outProps["label"]?.ToString() ?? type;
                }

            var node = new JsonObject { ["type"] = type };
            if (key is not null) node["key"] = key;
            node["props"] = outProps;

            if (raw["children"] is JsonArray kids && kids.Count > 0)
            {
                if (component.Children == "none")
                {
                    if (type is "Text" or "Heading" && !outProps.ContainsKey("value"))
                        outProps["value"] = string.Concat(kids.OfType<JsonObject>().Select(k => k["props"]?["value"]?.ToString() ?? ""));
                    else Warn($"{where}.{type}: takes no children");
                }
                else
                {
                    if (component.Children == "text")
                    {
                        if (!outProps.ContainsKey("value"))
                            outProps["value"] = string.Concat(kids.OfType<JsonObject>().Select(k => k["props"]?["value"]?.ToString() ?? ""));
                    }
                    else
                    {
                        var children = Children(kids, $"{where}.{type}");
                        if (component.Children == "one" && children.Count != 1) Warn($"{where}.{type}: takes one child, has {children.Count}");
                        if (component.Children == "two" && children.Count != 2) Warn($"{where}.{type}: takes two children, has {children.Count}");
                        if (children.Count > 0) node["children"] = children;
                    }
                }
            }
            return node;
        }

        private JsonArray Children(JsonArray kids, string where)
        {
            var result = new JsonArray();
            var keys = new HashSet<string>();
            var i = 0;
            foreach (var kid in kids)
            {
                if (kid is not JsonObject o)
                {
                    if (kid is JsonValue v && v.TryGetValue<string>(out var s))
                        result.AddNode(new JsonObject { ["type"] = "Text", ["props"] = new JsonObject { ["value"] = s } });
                    i++;
                    continue;
                }
                var child = Visit(o, $"{where}[{i++}]");
                if (child["key"] is JsonValue k && !keys.Add(k.ToString()))
                {
                    Warn($"{where}: two children have the key \"{k}\"");
                    var n = 2;
                    while (!keys.Add(k + "~" + n)) n++;
                    child["key"] = k + "~" + n;
                }
                result.AddNode(child);
            }
            return result;
        }

        private JsonNode? Prop(string kind, JsonNode value, string where)
        {
            switch (kind)
            {
                case "string":
                case "text":
                    if (value is JsonValue sv && sv.TryGetValue<string>(out var s)) return s.Length > 100_000 ? s[..100_000] : s;
                    if (value is JsonValue nv && nv.GetValueKind() is System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) return value.ToString();
                    Warn($"{where}: expected text");
                    return null;
                case "number":
                    if (value is JsonValue n && n.GetValueKind() == System.Text.Json.JsonValueKind.Number) return value.DeepClone();
                    Warn($"{where}: expected a number");
                    return null;
                case "boolean":
                    if (value is JsonValue b && b.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) return value.DeepClone();
                    Warn($"{where}: expected true or false");
                    return null;
                case "json":
                    return value.DeepClone();
                case "node":
                    if (value is JsonObject o) return Visit(o, where);
                    Warn($"{where}: expected a node");
                    return null;
                case "nodes":
                    if (value is JsonArray a) return Children(a, where);
                    if (value is JsonObject single) return new JsonArray(Visit(single, where));
                    Warn($"{where}: expected nodes");
                    return null;
                case "action":
                    return Action(value, where);
            }
            if (kind.StartsWith("enum:", StringComparison.Ordinal))
            {
                if (value is JsonValue ev && ev.TryGetValue<string>(out var e) && kind[5..].Split('|').Contains(e)) return e;
                Warn($"{where}: expected one of {kind[5..].Replace("|", ", ")}");
                return null;
            }
            if (kind.StartsWith("token:", StringComparison.Ordinal))
            {
                var token = kind[6..];
                if (token == "icon")
                    return value is JsonValue iv && iv.TryGetValue<string>(out var icon) && icon.Length is > 0 and <= 200 ? icon : null;
                if (token == "width" && value is JsonValue wv && wv.GetValueKind() == System.Text.Json.JsonValueKind.Number) return value.DeepClone();
                if (token == "width" && value is JsonObject responsive) return responsive.DeepClone();
                if (value is JsonValue tv && (tv.GetValueKind() == System.Text.Json.JsonValueKind.Number ? tv.ToString() : tv.TryGetValue<string>(out var str) ? str : null) is { } tok
                    && schema.Token(token).Contains(tok))
                    return tok;
                if (value is JsonObject byWidth && token is "gap") return byWidth.DeepClone();
                Warn($"{where}: not a {token} token");
                return null;
            }
            return value.DeepClone();
        }

        public JsonObject? Action(JsonNode value, string where)
        {
            string? name = null;
            JsonNode? args = null;
            if (value is JsonValue v && v.TryGetValue<string>(out var s)) name = s;
            else if (value is JsonObject o)
            {
                name = o["action"] is JsonValue av && av.TryGetValue<string>(out var a) ? a : null;
                args = o["args"];
            }
            if (name is null)
            {
                Warn($"{where}: an event takes an action name or {{ action, args }}");
                return null;
            }
            if (!actions.Contains(name))
            {
                Warn($"{where}: the view has no action \"{name}\"");
                return null;
            }
            var result = new JsonObject { ["action"] = name };
            if (args is not null)
            {
                if (args.ToJsonString().Length > MaxArgsBytes) Warn($"{where}: args larger than {MaxArgsBytes / 1024} KiB are dropped");
                else result["args"] = args.DeepClone();
            }
            return result;
        }

        private static JsonObject Placeholder(string? key, string message)
        {
            var n = new JsonObject { ["type"] = "Alert" };
            if (key is not null) n["key"] = key;
            n["props"] = new JsonObject { ["tone"] = "warning", ["message"] = message };
            return n;
        }

        private void Warn(string message)
        {
            if (warnings.Count < 50) warnings.Add(message);
        }
    }
}

/// <summary>A tree that can't be sent (too large, wrong root for its slot).</summary>
public sealed class UiTreeException(string message) : Exception(message);

/// <summary>
/// The patch between two normalised trees (design/plugin-v2 03 §3.3): <c>props</c> (set / unset), <c>insert</c>,
/// <c>remove</c>, <c>move</c> and <c>replace</c>, each at a path of child indexes from the root, applied in order. Children
/// are matched by key (an unkeyed child by its position and type). The shells apply exactly this algorithm's output.
/// </summary>
public static class UiDiff
{
    public static JsonArray Diff(JsonObject before, JsonObject after)
    {
        var ops = new JsonArray();
        Node(before, after, [], ops);
        return ops;
    }

    private static void Node(JsonObject a, JsonObject b, List<int> path, JsonArray ops)
    {
        if (Type(a) != Type(b) || Key(a) != Key(b))
        {
            ops.AddNode(new JsonObject { ["op"] = "replace", ["path"] = Path(path), ["node"] = b.DeepClone() });
            return;
        }
        var pa = a["props"] as JsonObject ?? [];
        var pb = b["props"] as JsonObject ?? [];
        var set = new JsonObject();
        var unset = new JsonArray();
        foreach (var (k, v) in pb)
            if (!pa.TryGetPropertyValue(k, out var old) || !JsonNode.DeepEquals(old, v))
                set[k] = v?.DeepClone();
        foreach (var (k, _) in pa)
            if (!pb.ContainsKey(k)) unset.AddNode(k);
        if (set.Count > 0 || unset.Count > 0)
        {
            var op = new JsonObject { ["op"] = "props", ["path"] = Path(path) };
            if (set.Count > 0) op["set"] = set;
            if (unset.Count > 0) op["unset"] = unset;
            ops.AddNode(op);
        }
        Children(a["children"] as JsonArray ?? [], b["children"] as JsonArray ?? [], path, ops);
    }

    private static void Children(JsonArray oldKids, JsonArray newKids, List<int> path, JsonArray ops)
    {
        var current = oldKids.Select((n, i) => (Key: ChildKey((JsonObject)n!, i), Node: (JsonObject)n!)).ToList();
        var wanted = newKids.Select((n, i) => (Key: ChildKey((JsonObject)n!, i), Node: (JsonObject)n!)).ToList();
        var wantedKeys = wanted.Select(w => w.Key).ToHashSet();
        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (wantedKeys.Contains(current[i].Key)) continue;
            ops.AddNode(new JsonObject { ["op"] = "remove", ["path"] = Path(path), ["index"] = i });
            current.RemoveAt(i);
        }
        for (var j = 0; j < wanted.Count; j++)
        {
            var (key, node) = wanted[j];
            if (j < current.Count && current[j].Key == key)
            {
                Node(current[j].Node, node, [.. path, j], ops);
                continue;
            }
            var from = current.FindIndex(j, c => c.Key == key);
            if (from > j)
            {
                ops.AddNode(new JsonObject { ["op"] = "move", ["path"] = Path(path), ["from"] = from, ["to"] = j });
                var moving = current[from];
                current.RemoveAt(from);
                current.Insert(j, moving);
                Node(moving.Node, node, [.. path, j], ops);
                continue;
            }
            ops.AddNode(new JsonObject { ["op"] = "insert", ["path"] = Path(path), ["index"] = j, ["node"] = node.DeepClone() });
            current.Insert(j, (key, node));
        }
    }

    /// <summary>Applies <paramref name="ops"/> to <paramref name="tree"/> (what a shell does; the tests use it to check a diff).</summary>
    public static JsonObject Apply(JsonObject tree, JsonArray ops)
    {
        var root = (JsonObject)tree.DeepClone();
        foreach (var opNode in ops)
        {
            var op = (JsonObject)opNode!;
            var path = ((JsonArray)op["path"]!).Select(p => p!.GetValue<int>()).ToList();
            JsonObject parent = root;
            foreach (var index in path) parent = (JsonObject)((JsonArray)parent["children"]!)[index]!;
            switch (op["op"]!.GetValue<string>())
            {
                case "replace":
                    if (path.Count == 0) root = (JsonObject)op["node"]!.DeepClone();
                    else
                    {
                        var owner = root;
                        foreach (var index in path.Take(path.Count - 1)) owner = (JsonObject)((JsonArray)owner["children"]!)[index]!;
                        ((JsonArray)owner["children"]!)[path[^1]] = op["node"]!.DeepClone();
                    }
                    break;
                case "props":
                    var props = parent["props"] as JsonObject ?? [];
                    parent["props"] = props;
                    if (op["set"] is JsonObject set) foreach (var (k, v) in set) props[k] = v?.DeepClone();
                    if (op["unset"] is JsonArray unset) foreach (var k in unset) props.Remove(k!.GetValue<string>());
                    break;
                case "insert":
                    if (parent["children"] is not JsonArray kids) parent["children"] = kids = [];
                    kids.Insert(op["index"]!.GetValue<int>(), op["node"]!.DeepClone());
                    break;
                case "remove":
                    ((JsonArray)parent["children"]!).RemoveAt(op["index"]!.GetValue<int>());
                    if (((JsonArray)parent["children"]!).Count == 0) parent.Remove("children");
                    break;
                case "move":
                    var list = (JsonArray)parent["children"]!;
                    var item = list[op["from"]!.GetValue<int>()]!;
                    list.RemoveAt(op["from"]!.GetValue<int>());
                    list.Insert(op["to"]!.GetValue<int>(), item);
                    break;
            }
        }
        return root;
    }

    private static string Type(JsonObject n) => n["type"]?.GetValue<string>() ?? "";

    private static string? Key(JsonObject n) => n["key"]?.ToString();

    private static string ChildKey(JsonObject n, int index) => Key(n) is { } k ? "k:" + k : $"#{index}:{Type(n)}";

    private static JsonArray Path(List<int> path) => new([.. path.Select(p => (JsonNode?)p)]);
}
