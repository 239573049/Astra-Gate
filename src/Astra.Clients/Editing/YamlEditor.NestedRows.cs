using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Astra.Clients.Editing;

/// <summary>
/// Rows of a block sequence under a top-level mapping key, addressed as <c>providers.[id=astra]</c> (Raycast AI's
/// <c>providers.yaml</c>): the key picks the sequence, the selector the row (<see cref="JsoncEditor.Selector"/>). Only whole rows are read, set
/// and removed. The sequence is found by scanning lines — the key at column 0, its items at one indentation — so every other line of the file
/// stays byte-identical; the row Astra writes is rendered as block YAML (comments inside that one row are not kept). Removing the last row
/// removes the key too. Every edit re-parses the result and compares the whole document with the expected one.
/// </summary>
public sealed partial class YamlEditor
{
    private static bool IsNestedRowPath(string[] segments) =>
        segments.Length == 2 && !JsoncEditor.IsSelector(segments[0]) && JsoncEditor.IsSelector(segments[1]);

    private JsonNode? NestedRowGet(string[] segments, out bool present)
    {
        present = false;
        if (DocumentObject(LoadRoot(Text))[segments[0]] is not JsonArray list) return null;
        foreach (var item in list)
        {
            if (!JsoncEditor.ElementMatches(item, segments[1])) continue;
            present = true;
            return item?.DeepClone();
        }
        return null;
    }

    /// <summary>Where the sequence under a top-level key sits in the text.</summary>
    /// <param name="KeyStart">Start of the key's line.</param>
    /// <param name="KeyEnd">End of the key's line (past its newline).</param>
    /// <param name="Inline">The key's value is on its own line (<c>[]</c>, a flow list) or it has no items: the whole entry is rewritten.</param>
    /// <param name="ItemIndent">The indentation of the items' dashes.</param>
    /// <param name="Items">The text region of each item, from its dash line to the end of its last content line.</param>
    private sealed record SequenceScan(int KeyStart, int KeyEnd, bool Inline, int ItemIndent, List<(int Start, int End)> Items);

    private SequenceScan? ScanSequence(string key)
    {
        var lines = new List<(int Start, int End)>();
        for (var i = 0; i < Text.Length;)
        {
            var end = TextTool.LineEnd(Text, i);
            lines.Add((i, end));
            i = end;
        }
        string Content(int index) => Text[lines[index].Start..lines[index].End].TrimEnd('\r', '\n');

        var keyPattern = new Regex("^(" + Regex.Escape(key) + "|\"" + Regex.Escape(key) + "\"|'" + Regex.Escape(key) + "')[ \\t]*:(?<rest>.*)$");
        var k = -1;
        string rest = "";
        for (var i = 0; i < lines.Count; i++)
        {
            var match = keyPattern.Match(Content(i));
            if (!match.Success) continue;
            k = i;
            rest = match.Groups["rest"].Value;
            break;
        }
        if (k < 0) return null;
        var hash = rest.IndexOf(" #", StringComparison.Ordinal);
        if (rest.TrimStart().StartsWith('#')) rest = "";
        else if (hash >= 0) rest = rest[..hash];
        if (rest.Trim().Length > 0) return new SequenceScan(lines[k].Start, lines[k].End, true, 2, []);

        var itemIndent = -1;
        var items = new List<(int Start, int End)>();
        for (var i = k + 1; i < lines.Count; i++)
        {
            var content = Content(i);
            var trimmed = content.TrimStart(' ');
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            var indent = content.Length - trimmed.Length;
            var dash = trimmed == "-" || trimmed.StartsWith("- ", StringComparison.Ordinal);
            if (itemIndent < 0)
            {
                if (!dash) break;
                itemIndent = indent;
            }
            if (indent < itemIndent || (indent == itemIndent && !dash)) break;
            if (indent == itemIndent) items.Add((lines[i].Start, lines[i].End));
            else items[^1] = (items[^1].Start, lines[i].End);
        }
        return new SequenceScan(lines[k].Start, lines[k].End, items.Count == 0, itemIndent < 0 ? 2 : itemIndent, items);
    }

    private YamlEditor NestedRowSet(string[] segments, JsonNode? value, string path)
    {
        var row = value as JsonObject ?? throw new EditorException($"'{path}' must be set to a whole mapping.");
        var key = segments[0];
        var nl = TextTool.NewlineOf(Text);
        var expected = DocumentObject(LoadRoot(Text));
        var list = expected[key] as JsonArray;
        if (list is null && expected[key] is not null) throw new EditorException($"Cannot set '{path}': '{key}' is not a sequence.");
        var index = -1;
        if (list is not null)
            for (var i = 0; i < list.Count; i++)
                if (JsoncEditor.ElementMatches(list[i], segments[1])) { index = i; break; }
        var count = list?.Count ?? 0;
        if (list is null) expected[key] = list = [];
        if (index >= 0) list[index] = row.DeepClone();
        else list.Add(row.DeepClone());

        var scan = ScanSequence(key);
        string updated;
        if (scan is null)
        {
            updated = Text + (Text.Length == 0 || Text.EndsWith('\n') ? "" : nl) + key + ":" + nl + BlockItem(row, "  ", nl) + nl;
        }
        else if (scan.Inline)
        {
            // The entry is rewritten whole: the rows it holds now, then this one in its place.
            var rows = list.Select(r => r as JsonObject ?? throw new EditorException($"Cannot set '{path}': '{key}' holds a row that is not a mapping.")).ToList();
            updated = TextTool.Splice(Text, scan.KeyStart, scan.KeyEnd, RenderSequence(key, rows, new string(' ', scan.ItemIndent), nl) + nl);
        }
        else
        {
            if (scan.Items.Count != count) throw new EditorException($"Cannot set '{path}': the sequence under '{key}' has a layout Astra does not edit.");
            var indent = new string(' ', scan.ItemIndent);
            if (index >= 0)
            {
                var (start, end) = scan.Items[index];
                var hadNewline = end > 0 && Text[end - 1] == '\n';
                updated = TextTool.Splice(Text, start, end, BlockItem(row, indent, nl) + (hadNewline ? nl : ""));
            }
            else
            {
                var at = scan.Items[^1].End;
                updated = TextTool.Splice(Text, at, at, (at > 0 && Text[at - 1] == '\n' ? "" : nl) + BlockItem(row, indent, nl) + nl);
            }
        }
        return Verify(updated, expected, $"set '{path}'");
    }

    private YamlEditor NestedRowRemove(string[] segments, string path)
    {
        var key = segments[0];
        var expected = DocumentObject(LoadRoot(Text));
        if (expected[key] is not JsonArray list) return this;
        var index = -1;
        for (var i = 0; i < list.Count; i++)
            if (JsoncEditor.ElementMatches(list[i], segments[1])) { index = i; break; }
        if (index < 0) return this;
        var count = list.Count;
        list.RemoveAt(index);
        var empty = list.Count == 0;
        if (empty) expected.Remove(key);

        var scan = ScanSequence(key) ?? throw new EditorException($"Cannot remove '{path}': '{key}' was not found.");
        var nl = TextTool.NewlineOf(Text);
        string updated;
        if (scan.Inline)
        {
            var rest = empty ? "" : RenderSequence(key, list.Select(r => r as JsonObject ?? throw new EditorException($"Cannot remove '{path}': '{key}' holds a row that is not a mapping.")).ToList(), "  ", nl) + nl;
            updated = TextTool.Splice(Text, scan.KeyStart, scan.KeyEnd, rest);
        }
        else
        {
            if (scan.Items.Count != count) throw new EditorException($"Cannot remove '{path}': the sequence under '{key}' has a layout Astra does not edit.");
            updated = empty
                ? TextTool.Splice(Text, scan.KeyStart, scan.Items[^1].End, "")
                : TextTool.Splice(Text, scan.Items[index].Start, scan.Items[index].End, "");
        }
        return Verify(updated, expected, $"remove '{path}'");
    }

    // ---------------------------------------------------------------- block rendering

    private static string RenderSequence(string key, List<JsonObject> rows, string indent, string nl) =>
        RenderKey(key) + ":" + string.Concat(rows.Select(r => nl + BlockItem(r, indent, nl)));

    /// <summary>A sequence item at <paramref name="indent"/>: a mapping's first member rides on the dash line, nested collections are block style.</summary>
    private static string BlockItem(JsonNode? item, string indent, string nl)
    {
        switch (item)
        {
            case JsonObject { Count: > 0 } o:
            {
                var lines = BlockMapLines(o, indent + IndentUnit, nl);
                return indent + "- " + lines[(indent.Length + IndentUnit.Length)..];
            }
            case JsonObject:
                return indent + "- {}";
            case JsonArray:
                return indent + "- " + item.ToJsonString();
            default:
                return indent + "- " + RenderScalar(item);
        }
    }

    private static string BlockMapLines(JsonObject map, string indent, string nl)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var (name, child) in map)
        {
            if (!first) sb.Append(nl);
            first = false;
            sb.Append(indent).Append(RenderKey(name)).Append(':').Append(BlockValue(child, indent, nl));
        }
        return sb.ToString();
    }

    /// <summary>The text that follows "key:" in a block mapping at <paramref name="indent"/>.</summary>
    private static string BlockValue(JsonNode? value, string indent, string nl)
    {
        switch (value)
        {
            case JsonObject { Count: > 0 } o:
                return nl + BlockMapLines(o, indent + IndentUnit, nl);
            case JsonArray { Count: > 0 } a:
                return string.Concat(a.Select(item => nl + BlockItem(item, indent + IndentUnit, nl)));
            case JsonObject:
                return " {}";
            case JsonArray:
                return " []";
            default:
                return " " + RenderScalar(value);
        }
    }
}
