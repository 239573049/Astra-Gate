using System.Text;
using System.Text.RegularExpressions;

namespace Astra.Clients.Editing;

/// <summary>
/// The entries of a TOML array of tables (<c>[[models]]</c>) that one client owns, found by a key they carry — the selector
/// <c>models[provider=astra]</c> names every <c>[[models]]</c> entry whose <c>provider</c> is the string <c>"astra"</c>. The entries are
/// located by a line scan (an entry runs from its <c>[[name]]</c> header to the next table header), which is exact for the documents
/// clients write themselves; every mutation re-parses the result and fails when it is not valid TOML any more.
/// </summary>
public static class TomlArrayEntries
{
    private static readonly Regex Selector = new(@"^(?<name>[A-Za-z0-9_.-]+)\[(?<key>[A-Za-z0-9_-]+)=(?<value>[^\]]*)\]$", RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="keyPath"/> is an array-entry selector.</summary>
    public static bool IsSelector(string keyPath) => Selector.IsMatch(keyPath);

    private static (string Name, string Key, string Value) Parse(string keyPath)
    {
        var m = Selector.Match(keyPath);
        if (!m.Success) throw new EditorException($"'{keyPath}' is not an array-entry selector (name[key=value]).");
        return (m.Groups["name"].Value, m.Groups["key"].Value, m.Groups["value"].Value);
    }

    /// <summary>True when at least one entry matches.</summary>
    public static bool Has(string text, string keyPath) => Spans(text, keyPath).Count > 0;

    /// <summary>
    /// Replaces the matching entries with <paramref name="entries"/> (each a list of key / raw TOML value text, written in order),
    /// appended at the end of the document after the old ones are removed.
    /// </summary>
    public static string Set(string text, string keyPath, IReadOnlyList<IReadOnlyList<KeyValuePair<string, string>>> entries)
    {
        var (name, _, _) = Parse(keyPath);
        var without = RemoveSpans(text, Spans(text, keyPath));
        var nl = TextTool.NewlineOf(text);
        var sb = new StringBuilder(without);
        if (sb.Length > 0 && sb[^1] != '\n') sb.Append(nl);
        foreach (var entry in entries)
        {
            if (sb.Length > 0 && !EndsWithBlankLine(sb.ToString())) sb.Append(nl);
            sb.Append("[[").Append(name).Append("]]").Append(nl);
            foreach (var (key, value) in entry) sb.Append(key).Append(" = ").Append(value).Append(nl);
        }
        return Checked(sb.ToString(), keyPath, expectEntries: entries.Count > 0);
    }

    /// <summary>Removes the matching entries (no-op when there are none).</summary>
    public static string Remove(string text, string keyPath)
    {
        var spans = Spans(text, keyPath);
        return spans.Count == 0 ? text : Checked(RemoveSpans(text, spans), keyPath, expectEntries: false);
    }

    private static string Checked(string updated, string keyPath, bool expectEntries)
    {
        // Parse through the TOML model layer: a broken document throws here instead of being written.
        _ = TomlEditor.Validated(updated);
        if (Has(updated, keyPath) != expectEntries)
            throw new EditorException($"Self-check failed: entries '{keyPath}' {(expectEntries ? "missing" : "still present")} after edit.");
        return updated;
    }

    private static string RemoveSpans(string text, List<(int Start, int End)> spans)
    {
        for (var i = spans.Count - 1; i >= 0; i--) text = text[..spans[i].Start] + text[spans[i].End..];
        return text;
    }

    /// <summary>The character range of every matching entry: header line through the line before the next table header.</summary>
    private static List<(int Start, int End)> Spans(string text, string keyPath)
    {
        var (name, key, value) = Parse(keyPath);
        var lines = new List<(int Start, int End, string Content)>();
        for (var offset = 0; offset < text.Length;)
        {
            var next = text.IndexOf('\n', offset);
            var end = next < 0 ? text.Length : next + 1;
            lines.Add((offset, end, text[offset..end].Trim()));
            offset = end;
        }
        var result = new List<(int, int)>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!HeaderOf(lines[i].Content, name)) continue;
            var j = i + 1;
            var matches = false;
            for (; j < lines.Count && !lines[j].Content.StartsWith('['); j++)
                if (KeyEquals(lines[j].Content, key, value)) matches = true;
            if (matches) result.Add((lines[i].Start, j < lines.Count ? lines[j].Start : text.Length));
            i = j - 1;
        }
        return result;
    }

    private static bool HeaderOf(string content, string name)
    {
        var c = content;
        var hash = c.IndexOf('#');
        if (hash >= 0) c = c[..hash].TrimEnd();
        return c == "[[" + name + "]]";
    }

    private static bool KeyEquals(string content, string key, string value)
    {
        var m = Regex.Match(content, "^" + Regex.Escape(key) + @"\s*=\s*""(?<v>(?:[^""\\]|\\.)*)""");
        return m.Success && m.Groups["v"].Value == value;
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
}
