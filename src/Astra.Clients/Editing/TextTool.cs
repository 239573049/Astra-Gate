namespace Astra.Clients.Editing;

/// <summary>Low-level text utilities shared by the format-preserving editors.</summary>
public static class TextTool
{
    /// <summary>Returns true when the text uses CRLF line endings (any CRLF wins over LF).</summary>
    public static bool UsesCrlf(string text) => text.Contains("\r\n", StringComparison.Ordinal);

    /// <summary>The newline used by <paramref name="text"/> ("\r\n" or "\n").</summary>
    public static string NewlineOf(string text) => UsesCrlf(text) ? "\r\n" : "\n";

    /// <summary>Offset of the first character of the line containing <paramref name="offset"/>.</summary>
    public static int LineStart(string text, int offset)
    {
        var i = Math.Min(Math.Max(offset, 0), text.Length);
        while (i > 0 && text[i - 1] != '\n') i--;
        return i;
    }

    /// <summary>Offset just past the end of the line containing <paramref name="offset"/> (past the '\n').</summary>
    public static int LineEnd(string text, int offset)
    {
        var i = Math.Min(Math.Max(offset, 0), text.Length);
        while (i < text.Length && text[i] != '\n') i++;
        return i < text.Length ? i + 1 : i;
    }

    /// <summary>Whitespace ("indentation") between the start of the line containing <paramref name="offset"/> and <paramref name="offset"/>.</summary>
    public static string IndentBefore(string text, int offset)
    {
        var start = LineStart(text, offset);
        var i = start;
        while (i < offset && (text[i] == ' ' || text[i] == '\t')) i++;
        return text[start..i];
    }

    /// <summary>True when <paramref name="text"/> is null, empty or whitespace only.</summary>
    public static bool IsBlank(string? text) => string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// Replaces the region [<paramref name="start"/>, <paramref name="end"/>) in <paramref name="text"/> with
    /// <paramref name="replacement"/> and returns the new string.
    /// </summary>
    public static string Splice(string text, int start, int end, string replacement)
    {
        if (start < 0 || end < start || end > text.Length) throw new ArgumentOutOfRangeException(nameof(end));
        return text[..start] + replacement + text[end..];
    }

    /// <summary>True when the offset is preceded (within the same line) only by whitespace or nothing.</summary>
    public static bool AtLineContentStart(string text, int offset)
    {
        var start = LineStart(text, offset);
        for (var i = start; i < offset; i++)
            if (!char.IsWhiteSpace(text[i])) return false;
        return true;
    }
}
