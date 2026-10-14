using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Astra.Clients.Editing;

/// <summary>
/// For clients that read their config with a strict JSON parser (Electron's <c>JSON.parse</c>): finds what JSONC allows
/// but strict JSON does not — comments and trailing commas — and removes exactly that. Every other character, whitespace
/// included, stays as it was, and a repair is only returned when the result is strict JSON with the same content.
/// </summary>
public static class StrictJson
{
    /// <summary>Problem code: a comma right before a closing <c>}</c> or <c>]</c>.</summary>
    public const string TrailingComma = "trailing-comma";

    /// <summary>Problem code: a <c>//</c> or <c>/* */</c> comment.</summary>
    public const string Comment = "comment";

    /// <summary>True when the text parses as strict JSON (no comments, no trailing commas).</summary>
    public static bool IsStrict(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The first thing that keeps a JSONC document from being strict JSON, as a problem code and 1-based line; null when
    /// the text is strict already, or is not JSONC either (then nobody can tell what the file should say).
    /// </summary>
    public static (string Problem, int Line)? FindProblem(string text)
    {
        if (Scan(text) is not { Count: > 0 } removals || !Verify(text, Apply(text, removals))) return null;
        return (removals[0].Problem, LineOf(text, removals[0].Start));
    }

    /// <summary>The text without its comments and trailing commas; null when <see cref="FindProblem"/> finds nothing.</summary>
    public static string? Repair(string text)
    {
        if (Scan(text) is not { Count: > 0 } removals) return null;
        var repaired = Apply(text, removals);
        return Verify(text, repaired) ? repaired : null;
    }

    private readonly record struct Removal(int Start, int End, string Problem);

    /// <summary>The ranges to remove, in document order; null when a comment is never closed.</summary>
    private static List<Removal>? Scan(string text)
    {
        var removals = new List<Removal>();
        var pendingComma = -1;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                pendingComma = -1;
                i = SkipString(text, i);
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] is '/' or '*')
            {
                var end = text[i + 1] == '/' ? LineCommentEnd(text, i) : BlockCommentEnd(text, i);
                if (end < 0) return null;
                var (start, stop) = CommentRange(text, i, end);
                removals.Add(new Removal(start, stop, Comment));
                i = end; // a comment between a comma and the closing bracket keeps the comma pending
                continue;
            }
            if (c == ',') pendingComma = i;
            else if (c is '}' or ']' && pendingComma >= 0)
            {
                removals.Add(new Removal(pendingComma, pendingComma + 1, TrailingComma));
                pendingComma = -1;
            }
            else if (!char.IsWhiteSpace(c)) pendingComma = -1;
            i++;
        }
        removals.Sort((a, b) => a.Start.CompareTo(b.Start));
        return removals;
    }

    /// <summary>Index just past the string literal that opens at <paramref name="i"/>.</summary>
    private static int SkipString(string text, int i)
    {
        for (var j = i + 1; j < text.Length; j++)
        {
            if (text[j] == '\\') j++;
            else if (text[j] == '"') return j + 1;
        }
        return text.Length;
    }

    /// <summary>End of a <c>//</c> comment: its line break (which stays), or the end of the text.</summary>
    private static int LineCommentEnd(string text, int i)
    {
        var n = text.IndexOf('\n', i);
        if (n < 0) return text.Length;
        return n > i && text[n - 1] == '\r' ? n - 1 : n;
    }

    /// <summary>Index just past the <c>*/</c> of a block comment, or -1 when it is never closed.</summary>
    private static int BlockCommentEnd(string text, int i)
    {
        var n = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return n < 0 ? -1 : n + 2;
    }

    /// <summary>
    /// A comment's range, widened to its whole line (break included) when nothing else is on that line, to the blanks
    /// before it when it ends a line, and to the blanks after it inside a line, so a repair leaves no empty lines,
    /// trailing spaces or doubled blanks behind.
    /// </summary>
    private static (int Start, int End) CommentRange(string text, int start, int end)
    {
        var lineStart = start;
        while (lineStart > 0 && text[lineStart - 1] is ' ' or '\t') lineStart--;
        var lineEnd = end;
        while (lineEnd < text.Length && text[lineEnd] is ' ' or '\t') lineEnd++;
        var aloneFromStart = lineStart == 0 || text[lineStart - 1] == '\n';
        var endsLine = lineEnd == text.Length || text[lineEnd] is '\r' or '\n';
        if (!endsLine) return lineStart < start ? (start, lineEnd) : (start, end);
        if (!aloneFromStart) return (lineStart, lineEnd);
        if (lineEnd < text.Length && text[lineEnd] == '\r') lineEnd++;
        if (lineEnd < text.Length && text[lineEnd] == '\n') lineEnd++;
        return (lineStart, lineEnd);
    }

    private static string Apply(string text, List<Removal> removals)
    {
        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var removal in removals)
        {
            if (removal.Start > cursor) builder.Append(text, cursor, removal.Start - cursor);
            cursor = Math.Max(cursor, removal.End);
        }
        if (cursor < text.Length) builder.Append(text, cursor, text.Length - cursor);
        return builder.ToString();
    }

    /// <summary>True when the repaired text is strict JSON holding exactly what the original JSONC text held.</summary>
    private static bool Verify(string original, string repaired)
    {
        try
        {
            var before = JsonNode.Parse(original, nodeOptions: new JsonNodeOptions(),
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var after = JsonNode.Parse(repaired);
            return IsStrict(repaired) && JsonNode.DeepEquals(before, after);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            return false; // not JSONC at all, or duplicate member names
        }
    }

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }
}
