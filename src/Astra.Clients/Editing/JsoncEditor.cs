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
            data = EnsureObject(data, segments[..(i + 1)]);

        var editor = new JsoncEditor(data);
        var model = editor.Build();
        var parentSegments = segments[..^1];
        var parent = parentSegments.Length == 0
            ? model.Root
            : Navigate(model, parentSegments)?.Value ?? throw new EditorException($"Self-check failed: parent of '{path}' missing.");
        if (parent.Kind != NodeKind.Object)
            throw new EditorException($"Cannot set '{segments[^1]}' inside a non-object at '{string.Join(".", parentSegments)}'.");

        var idx = FindMember(model, parent, segments[^1]);
        byte[] result;
        if (idx >= 0)
        {
            var m = parent.Members[idx];
            result = Splice(editor._data, (int)m.Value.Start, (int)m.Value.End, Encoding.UTF8.GetBytes(rawJsonText));
        }
        else
        {
            var (at, end, insert) = BuildInsert(model, parent, segments[^1], Encoding.UTF8.GetBytes(rawJsonText), editor._data);
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
        // trailing-comma style.
        var lineEnd = LineEnd(_data, end);
        var afterValue = end;
        if (afterValue < lineEnd && _data[afterValue] == (byte)',') afterValue++;
        if (AtLineContentStart(_data, start) && OnlyWhitespace(_data, afterValue, lineEnd))
        {
            start = LineStart(_data, start);
            end = lineEnd;
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

        var verify = new JsoncEditor(Splice(_data, start, end, []));
        if (verify.Has(path))
            throw new EditorException($"Self-check failed: '{path}' still present after remove.");
        _ = verify.Build(); // a successful full re-parse proves the commas are still balanced
        return verify;
    }

    // ---------------------------------------------------------------- object scaffolding

    /// <summary>Guarantees that the object at <paramref name="segments"/> exists (creating it as <c>{}</c>).</summary>
    private static byte[] EnsureObject(byte[] data, string[] segments)
    {
        var model = Build(data);
        var parentSegments = segments[..^1];
        var parent = parentSegments.Length == 0 ? model.Root : Navigate(model, parentSegments)?.Value;
        if (parent is null || parent.Kind != NodeKind.Object)
            throw new EditorException($"Cannot create '{string.Join(".", segments)}': parent is missing or not an object.");
        if (FindMember(model, parent, segments[^1]) >= 0)
        {
            var existing = parent.Members[FindMember(model, parent, segments[^1])].Value;
            if (existing.Kind != NodeKind.Object)
                throw new EditorException($"'{string.Join(".", segments)}' already exists and is not an object.");
            return data;
        }
        var (at, end, insert) = BuildInsert(model, parent, segments[^1], "{}"u8.ToArray(), data);
        return Splice(data, at, end, insert);
    }

    /// <summary>Computes the splice region and text for appending a new member to an object.</summary>
    private static (int At, int End, byte[] Text) BuildInsert(Model model, Node parent, string name, byte[] valueBytes, byte[] data)
    {
        var nl = UsesCrlf(data) ? "\r\n" : "\n";
        var memberSource = JsonSerializer.Serialize(name) + ": " + Encoding.UTF8.GetString(valueBytes);
        var open = (int)parent.Start;
        var close = (int)parent.End - 1; // index of '}'
        var lastContent = SkipWsBackward(data, close); // last non-whitespace char before '}'
        var multiline = ContainsNewline(data, open, close);
        var memberIndent = parent.Members.Count > 0
            ? IndentBefore(data, (int)parent.Members[0].NameStart)
            : IndentBefore(data, open) + DefaultUnit;

        if (parent.Members.Count == 0)
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
                    _ = ParseValue(tokens, ref pos);
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
            if (current.Kind != NodeKind.Object) return null;
            var idx = FindMember(model, current, segments[i]);
            if (idx < 0) return null;
            var m = current.Members[idx];
            result = (m.NameStart, m.NameEnd, m.Value);
            current = m.Value;
        }
        return result;
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
        var parts = path.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Empty key path.", nameof(path));
        return parts;
    }

    // ---------------------------------------------------------------- byte helpers

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
