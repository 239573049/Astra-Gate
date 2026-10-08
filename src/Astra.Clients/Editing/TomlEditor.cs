using System.Text;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Astra.Clients.Editing;

/// <summary>
/// Format-preserving editor for TOML documents. The syntax tree is only used to <em>locate</em> spans;
/// all mutations are surgical text splices on the original string, so comments, ordering, whitespace,
/// line endings and missing trailing newlines are preserved outside the edited region.
/// Every mutation re-parses the result and verifies the expected value, throwing
/// <see cref="EditorException"/> (and never writing the file) on mismatch.
/// </summary>
public sealed class TomlEditor
{
    private readonly string _text;

    public TomlEditor(string text) => _text = text;

    /// <summary>Current document text (after any mutations).</summary>
    public string Text => _text;

    private static DocumentSyntax Parse(string text)
    {
        var doc = SyntaxParser.Parse(text);
        if (doc.HasErrors)
        {
            var first = doc.Diagnostics.FirstOrDefault();
            throw new EditorException($"Invalid TOML document: {first?.Message ?? "parse error"}");
        }
        return doc;
    }

    // ---------------------------------------------------------------- queries

    /// <summary>Raw TOML source text of the value at <paramref name="path"/> (dotted), or null when absent.</summary>
    public string? GetValue(string path)
    {
        var found = Find(Parse(_text), SplitPath(path));
        if (found is null) return null;
        var v = found.Value.Kv.Value!;
        return _text[v.Span.Offset..(v.Span.Offset + v.Span.Length)];
    }

    /// <summary>Decoded string value at <paramref name="path"/>, or null when absent or not a TOML string.</summary>
    public string? GetStringValue(string path)
    {
        var found = Find(Parse(_text), SplitPath(path));
        return found?.Kv.Value is StringValueSyntax s ? s.Value : null;
    }

    /// <summary>Boolean value at <paramref name="path"/>, or null when absent or not a boolean.</summary>
    public bool? GetBoolValue(string path) => GetValue(path) switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    /// <summary>True when a key/value pair exists at this dotted path.</summary>
    public bool KeyExists(string path) => Find(Parse(_text), SplitPath(path)) is not null;

    /// <summary>True when a table header (<c>[a.b]</c> or <c>[[a.b]]</c>) with this dotted name exists.</summary>
    public bool TableExists(string path)
    {
        var segments = SplitPath(path);
        foreach (var t in Parse(_text).Tables)
            if (SegmentsEqual(NameSegments(t), segments))
                return true;
        return false;
    }

    // ---------------------------------------------------------------- mutations

    /// <summary>Sets the key to <paramref name="tomlValueText"/> (raw TOML value source, e.g. <c>"x"</c> or <c>true</c>).</summary>
    public TomlEditor Set(string path, string tomlValueText)
    {
        var segments = SplitPath(path);
        ValidateBareSegments(segments);
        if (string.IsNullOrWhiteSpace(tomlValueText)) throw new ArgumentException("Value text is empty.", nameof(tomlValueText));
        var doc = Parse(_text);
        var found = Find(doc, segments);
        var updated = found is not null
            ? SpliceValue(_text, found.Value.Kv, tomlValueText)
            : Insert(_text, doc, segments, tomlValueText);
        return VerifySet(updated, segments, tomlValueText);
    }

    /// <summary>Sets the key to a TOML basic string.</summary>
    public TomlEditor SetString(string path, string value) => Set(path, TomlValueText.ForString(value));

    /// <summary>Sets the key to a TOML boolean.</summary>
    public TomlEditor SetBool(string path, bool value) => Set(path, value ? "true" : "false");

    /// <summary>
    /// Appends a brand-new table <c>[path]</c> at end of file with the given items (raw TOML value texts).
    /// Throws when a header with the same name already exists.
    /// </summary>
    public TomlEditor InsertTable(string path, IReadOnlyList<KeyValuePair<string, string>> items)
    {
        var segments = SplitPath(path);
        ValidateBareSegments(segments);
        foreach (var item in items) ValidateBareSegments([item.Key]);
        if (TableExists(path)) throw new EditorException($"Table [{path}] already exists; use Set to update its keys.");
        var nl = TextTool.NewlineOf(_text);
        var tail = _text.Length > 0 && !_text.EndsWith('\n') ? _text + nl : _text;
        var blank = tail.Length > 0 && !EndsWithBlankLine(tail) ? nl : "";
        var sb = new StringBuilder();
        sb.Append(blank).Append('[').Append(string.Join(".", segments)).Append(']').Append(nl);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Value)) throw new ArgumentException($"Value for '{item.Key}' is empty.", nameof(items));
            sb.Append(item.Key).Append(" = ").Append(item.Value).Append(nl);
        }
        var updated = tail + sb.ToString();
        return VerifyTableInserted(updated, segments, items);
    }

    private TomlEditor VerifyTableInserted(string updated, IReadOnlyList<string> segments, IReadOnlyList<KeyValuePair<string, string>> items)
    {
        var editor = new TomlEditor(updated);
        if (!editor.TableExists(string.Join(".", segments)))
            throw new EditorException($"Self-check failed: table [{string.Join(".", segments)}] missing after insert.");
        foreach (var item in items)
        {
            var expected = updated;
            var found = Find(Parse(updated), segments.Concat([item.Key]).ToArray());
            if (found is null)
                throw new EditorException($"Self-check failed: '{item.Key}' missing in [{string.Join(".", segments)}] after insert.");
            var v = found.Value.Kv.Value!;
            if (expected.Substring(v.Span.Offset, v.Span.Length) != item.Value)
                throw new EditorException($"Self-check failed: '{item.Key}' differs after table insert.");
        }
        // Model-layer check: the document must deserialize and contain the table.
        Navigate(ToModel(updated), segments);
        return editor;
    }

    /// <summary>Removes the key/value pair (its whole line). No-op when the key is absent.</summary>
    public TomlEditor Remove(string path)
    {
        var doc = Parse(_text);
        var found = Find(doc, SplitPath(path));
        if (found is null) return this;
        var span = found.Value.Kv.Span;
        var start = TextTool.AtLineContentStart(_text, span.Offset) ? TextTool.LineStart(_text, span.Offset) : span.Offset;
        var end = span.Offset + span.Length;
        // A pair's span stops short of its line terminator when another node follows; consume it so
        // no empty line is left behind (never when the span already ends with one).
        var endsWithTerminator = end > 0 && (_text[end - 1] == '\n' || _text[end - 1] == '\r');
        if (!endsWithTerminator && end < _text.Length && (_text[end] == '\r' || _text[end] == '\n'))
        {
            end++;
            if (end < _text.Length && _text[end - 1] == '\r' && _text[end] == '\n') end++;
        }
        var updated = TextTool.Splice(_text, start, end, "");
        return VerifyRemoved(updated, path);
    }

    /// <summary>
    /// Removes a whole table: its header line and every item belonging to it (up to the next header or EOF).
    /// No-op when the table is absent.
    /// </summary>
    public TomlEditor RemoveTable(string path)
    {
        var segments = SplitPath(path);
        foreach (var t in Parse(_text).Tables)
        {
            if (!SegmentsEqual(NameSegments(t), segments)) continue;
            var span = t.Span;
            var start = TextTool.AtLineContentStart(_text, span.Offset) ? TextTool.LineStart(_text, span.Offset) : span.Offset;
            // Absorb blank lines that immediately precede the header so no stray blank line is left between
            // the previous table and whatever follows the removed one.
            while (start > 0 && _text[start - 1] == '\n')
            {
                var prevEnd = start - 1;
                var prevStart = TextTool.LineStart(_text, prevEnd);
                if (!string.IsNullOrWhiteSpace(_text[prevStart..prevEnd])) break;
                start = prevStart;
            }
            var end = span.Offset + span.Length;
            // A table whose last item lacks a line terminator (EOF) stops short of it; consume one
            // newline so no blank line remains — but never when the span already ends with one.
            if (end < _text.Length && _text[end] == '\n' && !(end > 0 && (_text[end - 1] == '\n' || _text[end - 1] == '\r')))
                end++;
            var updated = TextTool.Splice(_text, start, end, "");
            return VerifyTableRemoved(updated, segments);
        }
        return this;
    }

    // ---------------------------------------------------------------- mutation mechanics

    private static string SpliceValue(string text, KeyValueSyntax kv, string newValue)
    {
        var v = kv.Value ?? throw new EditorException("Key/value pair has no value node.");
        return TextTool.Splice(text, v.Span.Offset, v.Span.Offset + v.Span.Length, newValue);
    }

    private string Insert(string text, DocumentSyntax doc, IReadOnlyList<string> segments, string value)
    {
        var nl = TextTool.NewlineOf(text);
        var line = $"{segments[^1]} = {value}";

        // 1. Exact table header for the parent path exists: append the pair inside it.
        var table = ExactTable(doc, segments.Take(segments.Count - 1).ToArray());
        if (table is not null)
        {
            var at = table.Items.ChildrenCount > 0
                ? ItemEnd(text, table.Items.GetChild(table.Items.ChildrenCount - 1) as KeyValueSyntax)
                : HeaderEnd(text, table);
            return InsertLine(text, at, line, nl);
        }

        // 2. No table: create "[a.b]" at end of file (only for paths with a table part).
        if (segments.Count > 1)
        {
            var header = "[" + string.Join(".", segments.Take(segments.Count - 1)) + "]";
            var tail = text.Length > 0 && !text.EndsWith('\n') ? text + nl : text;
            var blank = tail.Length > 0 && !EndsWithBlankLine(tail) ? nl : "";
            return tail + blank + header + nl + line + nl;
        }

        // 3. Top-level key: after the last top-level pair, else before the first table, else at end of file.
        var top = doc.KeyValues;
        if (top.ChildrenCount > 0)
            return InsertLine(text, ItemEnd(text, top.GetChild(top.ChildrenCount - 1) as KeyValueSyntax), line, nl);
        if (doc.Tables.ChildrenCount > 0)
            return InsertLine(text, (doc.Tables.GetChild(0) as TableSyntaxBase)!.Span.Offset, line, nl);
        var tail2 = text.Length > 0 && !text.EndsWith('\n') ? text + nl : text;
        return tail2 + line + nl;
    }

    /// <summary>
    /// End of <paramref name="kv"/>'s line: the span stops short of the line terminator when another
    /// node follows (the terminator rides on the next node as leading trivia), so consume it — the new
    /// line belongs after it, before any comment lines that follow (those stay with what they document).
    /// </summary>
    private static int ItemEnd(string text, KeyValueSyntax? kv)
    {
        if (kv is null) return 0;
        var end = Math.Min(kv.Span.Offset + kv.Span.Length, text.Length);
        // Skip the pair's own line terminator when the span stops short of it; when the span already
        // ends with the terminator, the position is already the start of the next line.
        if (end < text.Length && text[end - 1] != '\n' && text[end - 1] != '\r')
        {
            if (text[end] == '\r') end++;
            if (end < text.Length && text[end] == '\n') end++;
        }
        return end;
    }

    private static int HeaderEnd(string text, TableSyntaxBase table)
    {
        if (table.EndOfLineToken is not null)
            return Math.Min(table.EndOfLineToken.Span.Offset + table.EndOfLineToken.Span.Length, text.Length);
        return TextTool.LineEnd(text, table.Span.Offset);
    }

    private string InsertLine(string text, int at, string line, string nl)
    {
        var prefix = at > 0 && at <= text.Length && text[at - 1] != '\n' ? nl : "";
        return TextTool.Splice(text, at, at, prefix + line + nl);
    }

    private static bool EndsWithBlankLine(string text)
    {
        var i = text.Length - 1;
        while (i >= 0 && text[i] == '\r') i--;
        if (i < 0 || text[i] != '\n') return false;
        i--;
        while (i >= 0 && text[i] == '\r') i--;
        return i < 0 || text[i] == '\n';
    }

    // ---------------------------------------------------------------- lookup helpers

    private static TableSyntaxBase? ExactTable(DocumentSyntax doc, IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return null;
        foreach (var t in doc.Tables)
            if (SegmentsEqual(NameSegments(t), segments))
                return t;
        return null;
    }

    private static IReadOnlyList<string> SplitPath(string path)
    {
        var parts = path.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) throw new ArgumentException("Empty key path.", nameof(path));
        return parts;
    }

    private static void ValidateBareSegments(IReadOnlyList<string> segments)
    {
        foreach (var s in segments)
            if (s.Length == 0 || s.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-'))
                throw new EditorException($"Key segment '{s}' is not a bare TOML key; quoted keys are not supported by this editor.");
    }

    private static IReadOnlyList<string> NameSegments(TableSyntaxBase t)
    {
        var list = new List<string>();
        if (t.Name?.Key is not null) list.Add(Unquote(t.Name.Key.ToString().Trim()));
        if (t.Name != null)
            foreach (var d in t.Name.DotKeys)
                if (d.Key is not null) list.Add(Unquote(d.Key.ToString().Trim()));
        return list;
    }

    private static IReadOnlyList<string> KeySegments(KeySyntax? key)
    {
        var list = new List<string>();
        if (key?.Key is not null) list.Add(Unquote(key.Key.ToString().Trim()));
        if (key != null)
            foreach (var d in key.DotKeys)
                if (d.Key is not null) list.Add(Unquote(d.Key.ToString().Trim()));
        return list;
    }

    private static string Unquote(string key) =>
        key.Length >= 2 && key[0] == '"' && key[^1] == '"'
            ? key[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal)
            : key;

    /// <summary>
    /// Locates the key/value pair for a dotted path: the longest matching table header provides the context,
    /// the remaining segments must match the pair's (possibly dotted) key.
    /// </summary>
    private static (KeyValueSyntax Kv, TableSyntaxBase? Owner)? Find(DocumentSyntax doc, IReadOnlyList<string> segments)
    {
        foreach (var kv in doc.KeyValues)
            if (SegmentsEqual(KeySegments(kv.Key), segments))
                return (kv, null);

        foreach (var t in doc.Tables)
        {
            var name = NameSegments(t);
            if (name.Count == 0 || name.Count >= segments.Count) continue;
            var ok = true;
            for (var i = 0; i < name.Count; i++)
                if (!string.Equals(name[i], segments[i], StringComparison.Ordinal)) { ok = false; break; }
            if (!ok) continue;
            var rest = segments.Skip(name.Count).ToArray();
            foreach (var item in t.Items)
                if (SegmentsEqual(KeySegments(item.Key), rest)) return (item, t);
        }
        return null;
    }

    private static bool SegmentsEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        return true;
    }

    // ---------------------------------------------------------------- self-checks

    private TomlEditor VerifySet(string updated, IReadOnlyList<string> segments, string expectedRaw)
    {
        var doc = Parse(updated);
        var found = Find(doc, segments) ?? throw new EditorException($"Self-check failed: '{string.Join(".", segments)}' missing after set.");
        var v = found.Kv.Value ?? throw new EditorException($"Self-check failed: '{string.Join(".", segments)}' has no value after set.");
        var actualRaw = updated[v.Span.Offset..(v.Span.Offset + v.Span.Length)];
        if (actualRaw != expectedRaw)
            throw new EditorException($"Self-check failed: '{string.Join(".", segments)}' = {actualRaw}, expected {expectedRaw}.");

        // Verify through the model layer as well (catches structural breakage such as duplicate keys).
        Navigate(ToModel(updated), segments);
        return new TomlEditor(updated);
    }

    private TomlEditor VerifyRemoved(string updated, string path)
    {
        if (Find(Parse(updated), SplitPath(path)) is not null)
            throw new EditorException($"Self-check failed: '{path}' still present after remove.");
        return new TomlEditor(updated);
    }

    private TomlEditor VerifyTableRemoved(string updated, IReadOnlyList<string> segments)
    {
        foreach (var t in Parse(updated).Tables)
            if (SegmentsEqual(NameSegments(t), segments))
                throw new EditorException($"Self-check failed: table [{string.Join(".", segments)}] still present after remove.");
        return new TomlEditor(updated);
    }

    private static TomlTable ToModel(string text)
    {
        try
        {
            return TomlSerializer.Deserialize(text, ClientTomlContext.Default.TomlTable)
                   ?? throw new EditorException("TOML document deserialized to null.");
        }
        catch (Exception e) when (e is not EditorException)
        {
            throw new EditorException($"TOML document is not a valid model: {e.Message}");
        }
    }

    private static void Navigate(TomlTable table, IReadOnlyList<string> segments)
    {
        object current = table;
        foreach (var s in segments)
        {
            if (current is not TomlTable t || !t.TryGetValue(s, out current!))
                throw new EditorException($"Self-check failed: '{string.Join(".", segments)}' not found in TOML model.");
        }
    }
}

/// <summary>Renders .NET values as TOML value source text.</summary>
public static class TomlValueText
{
    /// <summary>A TOML basic string with escapes for control characters, backslash and quote.</summary>
    public static string ForString(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\t': sb.Append("\\t"); break;
                case '\n': sb.Append("\\n"); break;
                case '\f': sb.Append("\\f"); break;
                case '\r': sb.Append("\\r"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }
}
