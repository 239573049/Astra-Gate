using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Clients.Editing;

/// <summary>
/// Format-preserving editor for JSON and JSONC documents (comments and trailing commas allowed).
/// Tokens are located with <see cref="Utf8JsonReader"/> (byte spans; commas/colons/comments never
/// produce tokens) and all mutations are surgical byte splices, so comments, formatting and ordering
/// elsewhere in the file stay byte-identical. Every mutation re-parses and verifies the expected
/// value, throwing <see cref="EditorException"/> instead of returning a corrupted document.
/// Key paths are dotted member names; a segment written as <c>[key=value&amp;key2=value2]</c> selects the
/// element of an array whose object has those string members (see <see cref="Selector"/>), e.g.
/// <c>providers.[name=astra]</c>. Setting a selector path that matches nothing appends the element.
/// </summary>
public sealed class JsoncEditor
{
    private const string DefaultUnit = "  ";

    private readonly byte[] _data;

    /// <summary>Creates an editor over JSON/JSONC text. Blank text is treated as an empty object.</summary>
    public JsoncEditor(string text) : this(Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(text) ? "{}" : text)) { }

    private JsoncEditor(byte[] data) => _data = data;

    /// <summary>Current document text (after any mutations).</summary>
    public string Text => Encoding.UTF8.GetString(_data);

    private JsonDocumentOptions Options => new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    // ---------------------------------------------------------------- queries

    /// <summary>The JSON node at <paramref name="path"/> (dotted), or null when absent.</summary>
    public JsonNode? Get(string path)
    {
        var member = Navigate(Build(), SplitPath(path));
        if (member is null) return null;
        var node = member.Value.Value;
        var slice = Encoding.UTF8.GetString(_data, (int)node.Start, checked((int)(node.End - node.Start)));
        return JsonNode.Parse(slice, nodeOptions: new JsonNodeOptions(), documentOptions: Options);
    }

    /// <summary>True when a value exists at this dotted path.</summary>
    public bool Has(string path) => Navigate(Build(), SplitPath(path)) is not null;

    // ---------------------------------------------------------------- mutations

    /// <summary>Sets the value at <paramref name="path"/> to <paramref name="value"/>, creating intermediate objects.</summary>
    public JsoncEditor Set(string path, JsonNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetRaw(path, value.ToJsonString());
    }

    /// <summary>
    /// Sets the value at <paramref name="path"/> to the raw JSON source text <paramref name="rawJsonText"/>,
    /// spliced verbatim (used to restore original formatting byte-for-byte).
    /// </summary>
    public JsoncEditor SetRaw(string path, string rawJsonText)
    {
        if (string.IsNullOrWhiteSpace(rawJsonText)) throw new ArgumentException("Raw JSON text is empty.", nameof(rawJsonText));
        _ = JsonNode.Parse(rawJsonText, nodeOptions: new JsonNodeOptions(), documentOptions: Options)
            ?? throw new EditorException($"Raw JSON text for '{path}' is not a valid JSON value.");

        var segments = SplitPath(path);
        var data = _data;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (IsSelector(segments[i]))
            {
                // Array elements are never created implicitly: only whole elements are appended.
                if (Navigate(Build(data), segments[..(i + 1)]) is null)
                    throw new EditorException($"Cannot set '{path}': no array element matches '{segments[i]}'.");
                continue;
            }
            data = EnsureContainer(data, segments[..(i + 1)], array: IsSelector(segments[i + 1]));
        }

        var editor = new JsoncEditor(data);
        var model = editor.Build();
        var parentSegments = segments[..^1];
        var parent = parentSegments.Length == 0
            ? model.Root
            : Navigate(model, parentSegments)?.Value ?? throw new EditorException($"Self-check failed: parent of '{path}' missing.");
        var last = segments[^1];
        var selector = IsSelector(last);
        if (parent.Kind != (selector ? NodeKind.Array : NodeKind.Object))
            throw new EditorException($"Cannot set '{last}' inside a non-{(selector ? "array" : "object")} at '{string.Join(".", parentSegments)}'.");

        Node? existing;
        if (selector) existing = FindElement(model, parent, last);
        else
        {
            var idx = FindMember(model, parent, last);
            existing = idx >= 0 ? parent.Members[idx].Value : null;
        }
        byte[] result;
        if (existing is not null)
        {
            result = Splice(editor._data, (int)existing.Start, (int)existing.End, Encoding.UTF8.GetBytes(rawJsonText));
        }
        else
        {
            var (at, end, insert) = BuildInsert(parent, selector ? null : last, Encoding.UTF8.GetBytes(rawJsonText), editor._data);
            result = Splice(editor._data, at, end, insert);
        }

        var verify = new JsoncEditor(result);
        var current = verify.Get(path) ?? throw new EditorException($"Self-check failed: '{path}' missing after set.");
        var expected = JsonNode.Parse(rawJsonText, nodeOptions: new JsonNodeOptions(), documentOptions: verify.Options);
        if (!JsonNode.DeepEquals(current, expected))
            throw new EditorException($"Self-check failed: value at '{path}' differs after set.");
        return verify;
    }

    /// <summary>Removes the value at <paramref name="path"/>. No-op when absent.</summary>
    public JsoncEditor Remove(string path)
    {
        var model = Build();
        var member = Navigate(model, SplitPath(path));
        if (member is null) return this;

        var start = (int)member.Value.NameStart;
        var end = (int)member.Value.Value.End;

        // A member whose value is followed only by an optional comma and whitespace to the end of its
        // line owns that line: remove it whole. Any comma the removal strands before '}' is a trailing
        // comma, which JSONC allows — leaving it keeps restores byte-identical for files that use
        // trailing-comma style. Files without any trailing comma may be read by strict JSON parsers
        // (e.g. Copilot CLI's providers.json), so there the stranded comma is removed too.
        var lineEnd = LineEnd(_data, end);
        var afterValue = end;
        if (afterValue < lineEnd && _data[afterValue] == (byte)',') afterValue++;
        var strandedComma = -1;
        if (AtLineContentStart(_data, start) && OnlyWhitespace(_data, afterValue, lineEnd))
        {
            start = LineStart(_data, start);
            end = lineEnd;
            var before = SkipWsBackward(_data, start);
            var next = SkipWsForward(_data, end);
            if (before >= 0 && _data[before] == (byte)',' && next < _data.Length && _data[next] is (byte)'}' or (byte)']'
                && !UsesTrailingCommas(_data))
                strandedComma = before;
        }
        else
        {
            var p = SkipWsForward(_data, end);
            if (p < _data.Length && _data[p] == (byte)',') end = p + 1;
            else
            {
                var q = SkipWsBackward(_data, start);
                if (q >= 0 && _data[q] == (byte)',') start = q;
            }
        }

        var spliced = Splice(_data, start, end, []);
        if (strandedComma >= 0) spliced = Splice(spliced, strandedComma, strandedComma + 1, []);
        var verify = new JsoncEditor(spliced);
        if (verify.Has(path))
            throw new EditorException($"Self-check failed: '{path}' still present after remove.");
        _ = verify.Build(); // a successful full re-parse proves the commas are still balanced
        return verify;
    }

    // ---------------------------------------------------------------- container scaffolding

    /// <summary>Guarantees that the object (or array) at <paramref name="segments"/> exists (creating it as <c>{}</c> / <c>[]</c>).</summary>
    private static byte[] EnsureContainer(byte[] data, string[] segments, bool array)
    {
        var kind = array ? NodeKind.Array : NodeKind.Object;
        var model = Build(data);
        var parentSegments = segments[..^1];
        var parent = parentSegments.Length == 0 ? model.Root : Navigate(model, parentSegments)?.Value;
        if (parent is null || parent.Kind != NodeKind.Object)
            throw new EditorException($"Cannot create '{string.Join(".", segments)}': parent is missing or not an object.");
        var idx = FindMember(model, parent, segments[^1]);
        if (idx >= 0)
        {
            if (parent.Members[idx].Value.Kind != kind)
                throw new EditorException($"'{string.Join(".", segments)}' already exists and is not an {(array ? "array" : "object")}.");
            return data;
        }
        var (at, end, insert) = BuildInsert(parent, segments[^1], array ? "[]"u8.ToArray() : "{}"u8.ToArray(), data);
        return Splice(data, at, end, insert);
    }

    /// <summary>
    /// Computes the splice region and text for appending a new member to an object, or (with a null
    /// <paramref name="name"/>) a new element to an array.
    /// </summary>
    private static (int At, int End, byte[] Text) BuildInsert(Node parent, string? name, byte[] valueBytes, byte[] data)
    {
        var nl = UsesCrlf(data) ? "\r\n" : "\n";
        var memberSource = (name is null ? "" : JsonSerializer.Serialize(name) + ": ") + Encoding.UTF8.GetString(valueBytes);
        var open = (int)parent.Start;
        var close = (int)parent.End - 1; // index of '}' or ']'
        var lastContent = SkipWsBackward(data, close); // last non-whitespace char before the closing bracket
        var multiline = ContainsNewline(data, open, close);
        var count = parent.Kind == NodeKind.Array ? parent.Elements.Count : parent.Members.Count;
        var memberIndent = count > 0
            ? IndentBefore(data, (int)(parent.Kind == NodeKind.Array ? parent.Elements[0].Start : parent.Members[0].NameStart))
            : IndentBefore(data, open) + DefaultUnit;

        if (count == 0)
        {
            // Replace the (possibly whitespace-only) interior of the empty object.
            var inner = multiline
                ? nl + memberIndent + memberSource + nl + IndentBefore(data, open)
                : " " + memberSource + " ";
            return (open + 1, close, Encoding.UTF8.GetBytes(inner));
        }
        if (lastContent >= 0 && data[lastContent] == (byte)',')
        {
            // A trailing comma is already there: reuse it.
            var text2 = multiline ? nl + memberIndent + memberSource : " " + memberSource;
            return (lastContent + 1, lastContent + 1, Encoding.UTF8.GetBytes(text2));
        }
        var text = multiline
            ? Encoding.UTF8.GetBytes("," + nl + memberIndent + memberSource)
            : Encoding.UTF8.GetBytes(", " + memberSource);
        return (lastContent + 1, lastContent + 1, text);
    }

    // ---------------------------------------------------------------- document model

    private enum NodeKind { Object, Array, Value }

    private sealed class Node
    {
        public required NodeKind Kind { get; init; }
        public required long Start { get; init; }
        public long End { get; set; }
        public List<Member> Members { get; } = [];

        /// <summary>Array elements (arrays only).</summary>
        public List<Node> Elements { get; } = [];
    }

    private struct Member
    {
        public long NameStart;
        public long NameEnd;
        public Node Value;
    }

    private sealed record Model(Node Root, byte[] Data);

    private Model Build() => Build(_data);

    private static Model Build(byte[] data)
    {
        var tokens = Tokenize(data);
        var pos = 0;
        var root = ParseValue(tokens, ref pos);
        if (pos != tokens.Count)
            throw new EditorException("JSON document has trailing content after the root value.");
        return new Model(root, data);
    }

    private static Node ParseValue(IReadOnlyList<Token> tokens, ref int pos)
    {
        if (pos >= tokens.Count) throw new EditorException("Unexpected end of JSON document.");
        var t = tokens[pos];
        switch (t.Kind)
        {
            case JsonTokenType.StartObject:
            {
                var node = new Node { Kind = NodeKind.Object, Start = t.Start };
                pos++;
                while (true)
                {
                    if (pos >= tokens.Count) throw new EditorException("Unterminated JSON object.");
                    if (tokens[pos].Kind == JsonTokenType.EndObject) { pos++; break; }
                    if (tokens[pos].Kind != JsonTokenType.PropertyName)
                        throw new EditorException("Unexpected token inside a JSON object.");
                    var name = tokens[pos];
                    pos++; // (the ':' never becomes a token)
                    var value = ParseValue(tokens, ref pos);
                    node.Members.Add(new Member { NameStart = name.Start, NameEnd = name.End, Value = value });
                }
                node.End = tokens[pos - 1].End;
                return node;
            }
            case JsonTokenType.StartArray:
            {
                var node = new Node { Kind = NodeKind.Array, Start = t.Start };
                pos++;
                while (true)
                {
                    if (pos >= tokens.Count) throw new EditorException("Unterminated JSON array.");
                    if (tokens[pos].Kind == JsonTokenType.EndArray) { pos++; break; }
                    node.Elements.Add(ParseValue(tokens, ref pos));
                }
                node.End = tokens[pos - 1].End;
                return node;
            }
            case JsonTokenType.EndObject or JsonTokenType.EndArray or JsonTokenType.PropertyName or JsonTokenType.None:
                throw new EditorException($"Unexpected JSON token {t.Kind}.");
            default:
                pos++;
                return new Node { Kind = NodeKind.Value, Start = t.Start, End = t.End };
        }
    }

    private readonly record struct Token(JsonTokenType Kind, long Start, long End);

    private static List<Token> Tokenize(byte[] data)
    {
        var reader = new Utf8JsonReader(data, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var tokens = new List<Token>();
        try
        {
            while (reader.Read())
            {
                var end = reader.BytesConsumed;
                // The reader consumes the ':' together with a property name (the span may also cover
                // whitespace before the colon); the member name span must end at the closing quote.
                while (end > reader.TokenStartIndex && IsWs(data[end - 1])) end--;
                if (reader.TokenType == JsonTokenType.PropertyName && end > reader.TokenStartIndex && data[end - 1] == (byte)':')
                    end--;
                tokens.Add(new Token(reader.TokenType, reader.TokenStartIndex, end));
            }
        }
        catch (JsonException e)
        {
            throw new EditorException($"Invalid JSON document: {e.Message}");
        }
        if (tokens.Count == 0) throw new EditorException("JSON document is empty.");
        return tokens;
    }

    private static (long NameStart, long NameEnd, Node Value)? Navigate(Model model, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return null;
        Node current = model.Root;
        (long, long, Node) result = default;
        for (var i = 0; i < segments.Count; i++)
        {
            if (IsSelector(segments[i]))
            {
                // An array element has no member name: its "name" span is empty at the element start.
                if (current.Kind != NodeKind.Array || FindElement(model, current, segments[i]) is not { } element) return null;
                result = (element.Start, element.Start, element);
                current = element;
                continue;
            }
            if (current.Kind != NodeKind.Object) return null;
            var idx = FindMember(model, current, segments[i]);
            if (idx < 0) return null;
            var m = current.Members[idx];
            result = (m.NameStart, m.NameEnd, m.Value);
            current = m.Value;
        }
        return result;
    }

    /// <summary>The first object element of an array that matches a <c>[key=value&amp;…]</c> selector.</summary>
    private static Node? FindElement(Model model, Node array, string selector)
    {
        foreach (var element in array.Elements)
        {
            if (element.Kind != NodeKind.Object) continue;
            var slice = Encoding.UTF8.GetString(model.Data, (int)element.Start, checked((int)(element.End - element.Start)));
            var node = JsonNode.Parse(slice, nodeOptions: new JsonNodeOptions(),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (ElementMatches(node, selector)) return element;
        }
        return null;
    }

    private static int FindMember(Model model, Node parent, string name)
    {
        for (var i = 0; i < parent.Members.Count; i++)
        {
            var m = parent.Members[i];
            var (s, e) = ((int)m.NameStart, (int)m.NameEnd);
            var nameText = Encoding.UTF8.GetString(model.Data, s, e - s);
            string decoded;
            try
            {
                decoded = JsonSerializer.Deserialize<string>(nameText) ?? "";
            }
            catch (JsonException)
            {
                continue;
            }
            if (string.Equals(decoded, name, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static string[] SplitPath(string path)
    {
        var parts = SplitKeyPath(path);
        if (parts.Length == 0) throw new ArgumentException("Empty key path.", nameof(path));
        return parts;
    }

    // ---------------------------------------------------------------- key paths and array selectors

    /// <summary>Splits a dotted key path; dots inside a <c>[…]</c> selector segment do not split.</summary>
    public static string[] SplitKeyPath(string path)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        foreach (var c in path)
        {
            if (c == '[') depth++;
            else if (c == ']') depth = Math.Max(0, depth - 1);
            if (c == '.' && depth == 0)
            {
                if (current.ToString().Trim() is { Length: > 0 } part) parts.Add(part);
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        if (current.ToString().Trim() is { Length: > 0 } tail) parts.Add(tail);
        return [.. parts];
    }

    /// <summary>
    /// Builds an array-element selector segment such as <c>[provider=astra&amp;id=gpt-5]</c>. Keys and values
    /// must not contain '[', ']', '=' or '&amp;'.
    /// </summary>
    public static string Selector(params (string Key, string Value)[] conditions)
    {
        if (conditions.Length == 0) throw new ArgumentException("A selector needs at least one condition.", nameof(conditions));
        foreach (var (key, value) in conditions)
        {
            if (key.Length == 0 || key.IndexOfAny(SelectorReserved) >= 0 || value.IndexOfAny(SelectorReserved) >= 0)
                throw new EditorException($"'{key}={value}' cannot be used in an array selector.");
        }
        return "[" + string.Join("&", conditions.Select(c => c.Key + "=" + c.Value)) + "]";
    }

    /// <summary>True when the path segment is an array-element selector.</summary>
    public static bool IsSelector(string segment) => segment.Length >= 2 && segment[0] == '[' && segment[^1] == ']';

    /// <summary>True when <paramref name="element"/> is an object whose string members satisfy the selector.</summary>
    public static bool ElementMatches(JsonNode? element, string selector)
    {
        if (element is not JsonObject o) return false;
        foreach (var condition in selector[1..^1].Split('&'))
        {
            var eq = condition.IndexOf('=');
            if (eq <= 0) throw new EditorException($"Invalid array selector '{selector}'.");
            var (key, value) = (condition[..eq], condition[(eq + 1)..]);
            if (o[key] is not JsonValue v || !v.TryGetValue(out string? s) || !string.Equals(s, value, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static readonly char[] SelectorReserved = ['[', ']', '=', '&'];

    // ---------------------------------------------------------------- byte helpers

    /// <summary>True when the document contains at least one trailing comma (JSONC style).</summary>
    private static bool UsesTrailingCommas(byte[] data)
    {
        var reader = new Utf8JsonReader(data, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = false });
        try
        {
            while (reader.Read()) { }
            return false;
        }
        catch (JsonException)
        {
            return true; // the document parses with trailing commas allowed, so they are the difference
        }
    }

    private static byte[] Splice(byte[] data, int start, int end, byte[] replacement)
    {
        if (start < 0 || end < start || end > data.Length) throw new ArgumentOutOfRangeException(nameof(end), "Invalid splice region.");
        var result = new byte[data.Length - (end - start) + replacement.Length];
        Buffer.BlockCopy(data, 0, result, 0, start);
        Buffer.BlockCopy(replacement, 0, result, start, replacement.Length);
        Buffer.BlockCopy(data, end, result, start + replacement.Length, data.Length - end);
        return result;
    }

    private static bool UsesCrlf(byte[] data)
    {
        for (var i = 1; i < data.Length; i++)
            if (data[i] == (byte)'\n' && data[i - 1] == (byte)'\r') return true;
        return false;
    }

    private static int LineStart(byte[] data, int offset)
    {
        var i = Math.Min(Math.Max(offset, 0), data.Length);
        while (i > 0 && data[i - 1] != (byte)'\n') i--;
        return i;
    }

    private static int LineEnd(byte[] data, int offset)
    {
        var i = Math.Min(Math.Max(offset, 0), data.Length);
        while (i < data.Length && data[i] != (byte)'\n') i++;
        return i < data.Length ? i + 1 : i;
    }

    private static bool AtLineContentStart(byte[] data, int offset)
    {
        for (var i = LineStart(data, offset); i < offset; i++)
            if (!IsWs(data[i])) return false;
        return true;
    }

    private static bool OnlyWhitespace(byte[] data, int start, int end)
    {
        for (var i = start; i < end && i < data.Length; i++)
            if (!IsWs(data[i])) return false;
        return true;
    }

    private static bool ContainsNewline(byte[] data, int start, int end)
    {
        for (var i = Math.Max(start, 0); i < end && i < data.Length; i++)
            if (data[i] == (byte)'\n') return true;
        return false;
    }

    private static bool IsWs(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static int SkipWsForward(byte[] data, int offset)
    {
        var i = Math.Max(offset, 0);
        while (i < data.Length && IsWs(data[i])) i++;
        return i;
    }

    private static int SkipWsBackward(byte[] data, int offset)
    {
        var i = Math.Min(offset, data.Length) - 1;
        while (i >= 0 && IsWs(data[i])) i--;
        return i;
    }

    private static string IndentBefore(byte[] data, int offset)
    {
        var start = LineStart(data, offset);
        var i = start;
        while (i < offset && data[i] is (byte)' ' or (byte)'\t') i++;
        return Encoding.UTF8.GetString(data, start, i - start);
    }
}
