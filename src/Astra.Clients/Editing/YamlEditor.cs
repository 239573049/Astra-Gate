using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Astra.Clients.Editing;

/// <summary>
/// Format-preserving editor for YAML config files whose root is a block mapping (Hermes Agent's
/// <c>config.yaml</c>, MiniMax Code's <c>config.yaml</c>). Values are exchanged as JSON (YAML is read
/// into the JSON data model); entries are located with YamlDotNet source marks and edited as text
/// splices, so comments, ordering and formatting of every other entry stay byte-identical. Written
/// values are rendered as block YAML with double-quoted strings. Every mutation re-parses the result
/// and compares the whole document against the expected one, throwing <see cref="EditorException"/>
/// instead of returning a corrupted document.
/// <para>
/// A document whose root is a block <em>sequence of mappings</em> (DeepSeek Harness's <c>cordis.patch.yml</c>) is addressed
/// with a leading array selector, <c>[id=llm-pi-ai].config.providers.astra</c> (<see cref="JsoncEditor.Selector"/>): the
/// selector picks the row whose members match, the rest of the path walks inside it. A row Astra touches is re-rendered as
/// block YAML (comments inside that one row are not kept); every other row stays byte-identical.
/// </para>
/// </summary>
public sealed partial class YamlEditor
{
    private const string IndentUnit = "  ";

    /// <summary>Creates an editor over YAML text. Blank (or comment-only) text is an empty mapping.</summary>
    public YamlEditor(string text) => Text = text;

    /// <summary>Current document text (after any mutations).</summary>
    public string Text { get; }

    // ---------------------------------------------------------------- queries

    /// <summary>
    /// The value at <paramref name="path"/> (dotted) as JSON text, or null when the key is absent.
    /// A present key with a YAML null value yields the text "null".
    /// </summary>
    public string? GetJsonText(string path)
    {
        var segments = SplitPath(path);
        if (IsRowPath(segments)) return ResolveInRow(LoadRoot(Text), segments, out var present) is { } node ? node.ToJsonString() : present ? "null" : null;
        var entry = Locate(LoadRoot(Text), segments);
        return entry is null ? null : ToJson(entry.Value.Value)?.ToJsonString() ?? "null";
    }

    /// <summary>The value at <paramref name="path"/> as a JSON node (null when absent or YAML null).</summary>
    public JsonNode? Get(string path)
    {
        var segments = SplitPath(path);
        if (IsRowPath(segments)) return ResolveInRow(LoadRoot(Text), segments, out _);
        var entry = Locate(LoadRoot(Text), segments);
        return entry is null ? null : ToJson(entry.Value.Value);
    }

    /// <summary>True when a key exists at this dotted path.</summary>
    public bool Has(string path)
    {
        var segments = SplitPath(path);
        if (IsRowPath(segments))
        {
            _ = ResolveInRow(LoadRoot(Text), segments, out var present);
            return present;
        }
        return Locate(LoadRoot(Text), segments) is not null;
    }

    /// <summary>The whole document in the JSON data model (null for an empty document).</summary>
    public static JsonNode? ParseToJson(string text)
    {
        var root = LoadRoot(text);
        return root is null ? null : ToJson(root);
    }

    // ---------------------------------------------------------------- mutations

    /// <summary>Sets the value at <paramref name="path"/> to the JSON value <paramref name="rawJsonText"/>, creating intermediate mappings.</summary>
    public YamlEditor SetRaw(string path, string rawJsonText)
    {
        if (string.IsNullOrWhiteSpace(rawJsonText)) throw new ArgumentException("Raw JSON text is empty.", nameof(rawJsonText));
        JsonNode? value;
        try
        {
            value = JsonNode.Parse(rawJsonText);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new EditorException($"Value for '{path}' is not valid JSON: {e.Message}");
        }

        var segments = SplitPath(path);
        var root = LoadRoot(Text);
        if (IsRowPath(segments)) return SetRow(root, segments, value, path);
        var expected = DocumentObject(root);
        SetIn(expected, segments, value?.DeepClone());

        var nl = TextTool.NewlineOf(Text);
        string updated;
        if (root is null)
        {
            // Empty or comment-only document: append the new top-level entry.
            var prefix = Text.Length == 0 || Text.EndsWith('\n') ? "" : nl;
            updated = Text + prefix + RenderEntry(segments[0], Nest(segments[1..], value), "", nl) + nl;
        }
        else
        {
            if (root is not YamlMappingNode rootMap) throw new EditorException("The YAML document root is not a mapping.");
            if (rootMap.Style == MappingStyle.Flow) throw new EditorException("A flow-style YAML root mapping is not supported.");
            updated = SetCore(Text, rootMap, segments, value, nl);
        }
        return Verify(updated, expected, $"set '{path}'");
    }

    /// <summary>Removes the key at <paramref name="path"/>. No-op when absent.</summary>
    public YamlEditor Remove(string path)
    {
        var segments = SplitPath(path);
        var root = LoadRoot(Text);
        if (IsRowPath(segments)) return RemoveRow(root, segments, path);
        if (Locate(root, segments) is not { } entry) return this;
        var expected = DocumentObject(root);
        RemoveIn(expected, segments);
        var nl = TextTool.NewlineOf(Text);

        string updated;
        var keyStart = Idx(entry.Key.Start);
        var parent = entry.Parent;
        if (parent.Style != MappingStyle.Flow && parent.Children.Count == 1 && entry.ParentEntry is { } owner)
        {
            // Removing the only child would leave "parent:" (a YAML null): write "parent: {}" instead.
            updated = ReplaceEntryValue(Text, owner.Key, owner.Value, new JsonObject(), nl);
        }
        else if (parent.Style != MappingStyle.Flow && TextTool.AtLineContentStart(Text, keyStart))
        {
            var start = TextTool.LineStart(Text, keyStart);
            var end = TextTool.LineEnd(Text, Math.Max(ContentEnd(entry.Key, entry.Value) - 1, keyStart));
            updated = TextTool.Splice(Text, start, end, "");
        }
        else if (entry.ParentEntry is { } holder)
        {
            // Flow mappings and "- key:" sequence items are rebuilt from the data model.
            var rebuilt = ToJson(holder.Value) as JsonObject ?? throw new EditorException($"Cannot remove '{path}'.");
            rebuilt.Remove(segments[^1]);
            updated = ReplaceEntryValue(Text, holder.Key, holder.Value, rebuilt, nl);
        }
        else
        {
            throw new EditorException($"Cannot remove '{path}' from this YAML layout.");
        }
        return Verify(updated, expected, $"remove '{path}'");
    }

    // ---------------------------------------------------------------- editing core

    private static string SetCore(string text, YamlMappingNode root, string[] segments, JsonNode? value, string nl)
    {
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var found = FindEntry(current, segments[i]);
            var last = i == segments.Length - 1;
            if (found is { } entry)
            {
                if (last) return ReplaceEntryValue(text, entry.Key, entry.Value, value, nl);
                if (entry.Value is YamlMappingNode { Style: not MappingStyle.Flow, Children.Count: > 0 } child)
                {
                    current = child;
                    continue;
                }
                // "key:" (null), "key: {}" or a flow mapping: rebuild this entry's value from the data model.
                var existing = ToJson(entry.Value);
                var rebuilt = existing switch
                {
                    JsonObject o => o,
                    null => new JsonObject(),
                    _ => throw new EditorException($"Cannot set '{string.Join(".", segments)}': '{segments[i]}' is not a mapping."),
                };
                SetIn(rebuilt, segments[(i + 1)..], value?.DeepClone());
                return ReplaceEntryValue(text, entry.Key, entry.Value, rebuilt, nl);
            }
            return InsertEntry(text, current, segments[i], Nest(segments[(i + 1)..], value), nl);
        }
        throw new EditorException("Empty key path.");
    }

    /// <summary>Replaces "key: value" (value block included) with a freshly rendered value, keeping the key text.</summary>
    private static string ReplaceEntryValue(string text, YamlNode key, YamlNode value, JsonNode? newValue, string nl)
    {
        var keyStart = Idx(key.Start);
        var keyEnd = Idx(key.End);
        var regionEnd = TextTool.LineEnd(text, Math.Max(ContentEnd(key, value) - 1, keyStart));
        var hadNewline = regionEnd > 0 && text[regionEnd - 1] == '\n';
        var indent = new string(' ', keyStart - TextTool.LineStart(text, keyStart));
        var replacement = text[keyStart..keyEnd] + ":" + RenderValue(newValue, indent, nl) + (hadNewline ? nl : "");
        return TextTool.Splice(text, keyStart, regionEnd, replacement);
    }

    /// <summary>Appends a new entry after the last entry of a block mapping, at that mapping's indentation.</summary>
    private static string InsertEntry(string text, YamlMappingNode mapping, string key, JsonNode? value, string nl)
    {
        if (mapping.Style == MappingStyle.Flow || mapping.Children.Count == 0)
            throw new EditorException($"Cannot insert '{key}' into an empty or flow-style mapping.");
        var firstKey = mapping.Children.First().Key;
        var firstStart = Idx(firstKey.Start);
        var indent = new string(' ', firstStart - TextTool.LineStart(text, firstStart));
        var lastContent = mapping.Children.Max(p => ContentEnd(p.Key, p.Value));
        var at = TextTool.LineEnd(text, lastContent - 1);
        var prefix = at > 0 && text[at - 1] == '\n' ? "" : nl;
        return TextTool.Splice(text, at, at, prefix + RenderEntry(key, value, indent, nl) + nl);
    }

    /// <summary>Offset just past the last source character of an entry (comments after it excluded).</summary>
    private static int ContentEnd(YamlNode key, YamlNode value) => Math.Max(Idx(key.End) + 1, ContentEnd(value));

    private static int ContentEnd(YamlNode node) => node switch
    {
        YamlMappingNode { Style: not MappingStyle.Flow, Children.Count: > 0 } m =>
            m.Children.Max(p => ContentEnd(p.Key, p.Value)),
        YamlSequenceNode { Style: not SequenceStyle.Flow, Children.Count: > 0 } s => s.Children.Max(ContentEnd),
        YamlScalarNode { Style: ScalarStyle.Plain, Value: "" or null } => 0, // "key:" — no value text
        _ => Idx(node.End),
    };

    // ---------------------------------------------------------------- rendering (JSON → block YAML)

    private static string RenderEntry(string key, JsonNode? value, string indent, string nl) =>
        indent + RenderKey(key) + ":" + RenderValue(value, indent, nl);

    /// <summary>The text that follows "key:" — " scalar", " {}" or a newline-separated nested block.</summary>
    private static string RenderValue(JsonNode? value, string indent, string nl)
    {
        var childIndent = indent + IndentUnit;
        switch (value)
        {
            case JsonObject { Count: > 0 } o:
            {
                var sb = new StringBuilder();
                foreach (var (name, child) in o) sb.Append(nl).Append(RenderEntry(name, child, childIndent, nl));
                return sb.ToString();
            }
            case JsonArray { Count: > 0 } a:
            {
                var sb = new StringBuilder();
                foreach (var item in a)
                {
                    // Nested collections inside sequences are written in flow style (JSON is valid YAML flow).
                    var itemText = item is JsonObject or JsonArray ? item.ToJsonString() : RenderScalar(item);
                    sb.Append(nl).Append(childIndent).Append("- ").Append(itemText);
                }
                return sb.ToString();
            }
            case JsonObject:
                return " {}";
            case JsonArray:
                return " []";
            default:
                return " " + RenderScalar(value);
        }
    }

    private static string RenderScalar(JsonNode? value)
    {
        if (value is null) return "null";
        var element = value.GetValueKind();
        return element switch
        {
            System.Text.Json.JsonValueKind.String => Quote(value.GetValue<string>()),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            System.Text.Json.JsonValueKind.Null => "null",
            _ => value.ToJsonString(),
        };
    }

    private static string RenderKey(string key) =>
        PlainKeyPattern().IsMatch(key) && !Yaml11Booleans.Contains(key)
        && ResolvePlain(key) is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? key
            : Quote(key);

    /// <summary>Words YAML 1.1 readers (PyYAML) resolve to booleans; quoted when used as keys.</summary>
    private static readonly HashSet<string> Yaml11Booleans = new(StringComparer.OrdinalIgnoreCase)
    {
        "y", "n", "yes", "no", "on", "off",
    };

    /// <summary>A YAML double-quoted scalar.</summary>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c is >= '\u007f' and <= '\u009f' || c is '\u2028' or '\u2029' or '\ufeff')
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.\-/]*$")]
    private static partial Regex PlainKeyPattern();

    // ---------------------------------------------------------------- YAML → JSON data model

    private static YamlNode? LoadRoot(string text)
    {
        if (TextTool.IsBlank(text)) return null;
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException e)
        {
            throw new EditorException($"Invalid YAML document: {e.Message}");
        }
        if (stream.Documents.Count == 0) return null;
        if (stream.Documents.Count > 1) throw new EditorException("Multi-document YAML files are not supported.");
        var root = stream.Documents[0].RootNode;
        return root is YamlScalarNode { Style: ScalarStyle.Plain, Value: "" or null } ? null : root;
    }

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlScalarNode s => s.Style == ScalarStyle.Plain ? ResolvePlain(s.Value ?? "") : JsonValue.Create(s.Value ?? ""),
        YamlMappingNode m => MappingToJson(m),
        YamlSequenceNode q => new JsonArray(q.Children.Select(ToJson).ToArray()),
        _ => throw new EditorException($"Unsupported YAML node {node.NodeType}."),
    };

    private static JsonObject MappingToJson(YamlMappingNode mapping)
    {
        var result = new JsonObject();
        foreach (var (key, value) in mapping.Children)
        {
            if (key is not YamlScalarNode k) throw new EditorException("YAML mappings with non-scalar keys are not supported.");
            result[k.Value ?? ""] = ToJson(value);
        }
        return result;
    }

    /// <summary>Plain scalar resolution (YAML 1.2 core schema).</summary>
    private static JsonNode? ResolvePlain(string text)
    {
        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL": return null;
            case "true" or "True" or "TRUE": return JsonValue.Create(true);
            case "false" or "False" or "FALSE": return JsonValue.Create(false);
        }
        if (IntPattern().IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);
        if (FloatPattern().IsMatch(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && double.IsFinite(d))
            return JsonValue.Create(d);
        return JsonValue.Create(text);
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntPattern();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatPattern();

    // ---------------------------------------------------------------- sequence-of-mappings documents (rows)

    private static bool IsRowPath(string[] segments) => JsoncEditor.IsSelector(segments[0]);

    private static JsonArray DocumentArray(YamlNode? root) => root is null
        ? new JsonArray()
        : ToJson(root) as JsonArray ?? throw new EditorException("The YAML document root is not a sequence.");

    /// <summary>The row of the root sequence a selector picks, with its index; null when there is none.</summary>
    private static (YamlSequenceNode Sequence, int Index, YamlMappingNode Row)? FindRow(YamlNode? root, string selector)
    {
        if (root is null) return null;
        if (root is not YamlSequenceNode sequence) throw new EditorException("An array selector needs a YAML document whose root is a sequence.");
        for (var i = 0; i < sequence.Children.Count; i++)
        {
            if (sequence.Children[i] is YamlMappingNode row && JsoncEditor.ElementMatches(ToJson(row), selector)) return (sequence, i, row);
        }
        return null;
    }

    /// <summary>The JSON node at a row path; <paramref name="present"/> tells an absent key from a YAML null.</summary>
    private static JsonNode? ResolveInRow(YamlNode? root, string[] segments, out bool present)
    {
        present = false;
        if (FindRow(root, segments[0]) is not { } found) return null;
        JsonNode? current = ToJson(found.Row);
        foreach (var segment in segments[1..])
        {
            if (current is not JsonObject o || !o.TryGetPropertyValue(segment, out var next)) return null;
            current = next;
        }
        present = true;
        return current;
    }

    private YamlEditor SetRow(YamlNode? root, string[] segments, JsonNode? value, string path)
    {
        var expected = DocumentArray(root);
        var nl = TextTool.NewlineOf(Text);
        var found = FindRow(root, segments[0]);
        var rest = segments[1..];
        JsonObject newRow;
        if (found is { } existing)
        {
            newRow = ToJson(existing.Row) as JsonObject ?? throw new EditorException($"Cannot set '{path}': the row is not a mapping.");
            if (rest.Length == 0) newRow = value as JsonObject ?? throw new EditorException($"'{path}' must be set to a whole mapping.");
            else SetIn(newRow, rest, value?.DeepClone());
            expected[existing.Index] = newRow.DeepClone();
            return Verify(ReplaceRow(existing.Row, newRow, nl), expected, $"set '{path}'");
        }

        if (rest.Length > 0) throw new EditorException($"Cannot set '{path}': no row matches '{segments[0]}'; create the whole row first.");
        newRow = value as JsonObject ?? throw new EditorException($"'{path}' must be set to a whole mapping.");
        expected.Add(newRow.DeepClone());
        if (root is YamlSequenceNode { Style: SequenceStyle.Flow }) throw new EditorException("A flow-style YAML root sequence is not supported.");
        var indent = root is YamlSequenceNode { Children.Count: > 0 } seq ? RowIndent(Text, (YamlMappingNode)seq.Children[0]) : "";
        var rendered = RenderRow(newRow, indent, nl) + nl;
        string updated;
        if (root is YamlSequenceNode { Children.Count: > 0 } sequence)
        {
            var at = TextTool.LineEnd(Text, ContentEnd(sequence.Children[^1]) - 1);
            updated = TextTool.Splice(Text, at, at, (at > 0 && Text[at - 1] == '\n' ? "" : nl) + rendered);
        }
        else
        {
            updated = Text + (Text.Length == 0 || Text.EndsWith('\n') ? "" : nl) + rendered;
        }
        return Verify(updated, expected, $"set '{path}'");
    }

    private YamlEditor RemoveRow(YamlNode? root, string[] segments, string path)
    {
        if (FindRow(root, segments[0]) is not { } found) return this;
        var expected = DocumentArray(root);
        var nl = TextTool.NewlineOf(Text);
        var rest = segments[1..];
        if (rest.Length == 0)
        {
            expected.RemoveAt(found.Index);
            var (start, end, _) = RowRegion(found.Row);
            return Verify(TextTool.Splice(Text, start, end, ""), expected, $"remove '{path}'");
        }

        var row = ToJson(found.Row) as JsonObject ?? throw new EditorException($"Cannot remove '{path}': the row is not a mapping.");
        if (ResolveInRow(root, segments, out var present) is null && !present) return this;
        RemoveIn(row, rest);
        expected[found.Index] = row.DeepClone();
        return Verify(ReplaceRow(found.Row, row, nl), expected, $"remove '{path}'");
    }

    /// <summary>The text span of a row: from its "- " line start to the end of its last content line (newline included).</summary>
    private (int Start, int End, string Indent) RowRegion(YamlMappingNode row)
    {
        if (row.Style == MappingStyle.Flow) throw new EditorException("A flow-style row in a YAML sequence is not supported.");
        var keyStart = Idx(row.Start);
        var lineStart = TextTool.LineStart(Text, keyStart);
        var before = Text[lineStart..keyStart];
        if (!before.EndsWith("- ", StringComparison.Ordinal) || before[..^2].Trim().Length > 0)
            throw new EditorException("A row in a YAML sequence must start on its own '- ' line.");
        return (lineStart, TextTool.LineEnd(Text, Math.Max(ContentEnd(row) - 1, keyStart)), before[..^2]);
    }

    private static string RowIndent(string text, YamlMappingNode row)
    {
        var keyStart = Idx(row.Start);
        var before = text[TextTool.LineStart(text, keyStart)..keyStart];
        return before.EndsWith("- ", StringComparison.Ordinal) ? before[..^2] : "";
    }

    private string ReplaceRow(YamlMappingNode row, JsonObject newRow, string nl)
    {
        var (start, end, indent) = RowRegion(row);
        var hadNewline = end > 0 && Text[end - 1] == '\n';
        return TextTool.Splice(Text, start, end, RenderRow(newRow, indent, nl) + (hadNewline ? nl : ""));
    }

    /// <summary>A mapping as a sequence item: the first member rides on the "- " line.</summary>
    private static string RenderRow(JsonObject row, string indent, string nl)
    {
        if (row.Count == 0) return indent + "- {}";
        var sb = new StringBuilder();
        var first = true;
        foreach (var (name, child) in row)
        {
            var entry = RenderEntry(name, child, indent + IndentUnit, nl);
            if (first) entry = indent + "- " + entry[(indent.Length + IndentUnit.Length)..];
            else sb.Append(nl);
            sb.Append(entry);
            first = false;
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- navigation and helpers

    private readonly record struct Entry(YamlNode Key, YamlNode Value, YamlMappingNode Parent, (YamlNode Key, YamlNode Value)? ParentEntry);

    private static Entry? Locate(YamlNode? root, string[] segments)
    {
        if (root is not YamlMappingNode current) return null;
        (YamlNode, YamlNode)? owner = null;
        for (var i = 0; i < segments.Length; i++)
        {
            if (FindEntry(current, segments[i]) is not { } found) return null;
            if (i == segments.Length - 1) return new Entry(found.Key, found.Value, current, owner);
            if (found.Value is not YamlMappingNode next) return null;
            owner = (found.Key, found.Value);
            current = next;
        }
        return null;
    }

    private static KeyValuePair<YamlNode, YamlNode>? FindEntry(YamlMappingNode mapping, string key)
    {
        foreach (var pair in mapping.Children)
            if (pair.Key is YamlScalarNode k && string.Equals(k.Value, key, StringComparison.Ordinal))
                return pair;
        return null;
    }

    private static JsonObject DocumentObject(YamlNode? root) => root is null
        ? new JsonObject()
        : ToJson(root) as JsonObject ?? throw new EditorException("The YAML document root is not a mapping.");

    private static JsonNode? Nest(string[] segments, JsonNode? value)
    {
        for (var i = segments.Length - 1; i >= 0; i--) value = new JsonObject { [segments[i]] = value };
        return value;
    }

    private static void SetIn(JsonObject target, string[] segments, JsonNode? value)
    {
        if (segments.Length == 0) return;
        var current = target;
        foreach (var segment in segments[..^1])
        {
            if (current[segment] is not JsonObject next)
            {
                next = new JsonObject();
                current[segment] = next;
            }
            current = next;
        }
        current[segments[^1]] = value;
    }

    private static void RemoveIn(JsonObject target, string[] segments)
    {
        JsonNode? current = target;
        foreach (var segment in segments[..^1]) current = (current as JsonObject)?[segment];
        (current as JsonObject)?.Remove(segments[^1]);
    }

    private static YamlEditor Verify(string updated, JsonNode expected, string operation)
    {
        JsonNode actual = expected is JsonArray ? DocumentArray(LoadRoot(updated)) : DocumentObject(LoadRoot(updated));
        if (!JsonNode.DeepEquals(actual, expected))
            throw new EditorException($"Self-check failed after {operation}: the YAML document differs from the expected content.");
        return new YamlEditor(updated);
    }

    private static string[] SplitPath(string path)
    {
        var parts = JsoncEditor.SplitKeyPath(path); // dots inside a [selector] do not split
        if (parts.Length == 0) throw new ArgumentException("Empty key path.", nameof(path));
        return parts;
    }

    private static int Idx(Mark mark) => checked((int)mark.Index);
}
