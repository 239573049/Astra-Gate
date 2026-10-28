using System.Globalization;
using System.Text.RegularExpressions;

namespace Astra.Desktop.Core.Clients;

/// <summary>How a line of a unified diff is coloured (web <c>DiffView</c>).</summary>
public enum DiffLineKind
{
    /// <summary>Unchanged line (or anything else).</summary>
    Context,

    /// <summary>"+" line: green on a green tint.</summary>
    Added,

    /// <summary>"-" line: red on a red tint.</summary>
    Removed,

    /// <summary>"@@ … @@" hunk header: muted.</summary>
    Hunk,

    /// <summary>"--- a" / "+++ b" file header: plain.</summary>
    FileHeader,
}

/// <summary>One line of a unified diff with the old / new line numbers it stands at (null where it has none).</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLine, int? NewLine);

/// <summary>
/// Splits the server's unified diff (<c>FileDiffDto.UnifiedDiff</c>) into coloured lines. The colouring is the web's <c>DiffView</c>
/// (ClientsPage.tsx): "+"/"-" but not "+++"/"---", "@@" muted. The line numbers are the desktop's addition (plan inventory GAP-6 asks
/// for a gutter): they follow the hunk headers, so they stay empty for a diff without any.
/// </summary>
public static partial class DiffLines
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex HunkHeader();

    /// <summary>The lines of <paramref name="diff"/> (split on '\n', like the web; a trailing '\r' is dropped).</summary>
    public static List<DiffLine> Parse(string? diff)
    {
        var result = new List<DiffLine>();
        int? oldLine = null, newLine = null;
        var raws = (diff ?? "").Split('\n');
        for (var i = 0; i < raws.Length; i++)
        {
            var raw = raws[i];
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            // The diff ends with a newline: the empty piece after it is drawn (as the web does) but stands at no line.
            if (line.Length == 0 && i == raws.Length - 1 && i > 0)
                result.Add(new DiffLine(DiffLineKind.Context, line, null, null));
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                var m = HunkHeader().Match(line);
                if (m.Success)
                {
                    oldLine = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                    newLine = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                result.Add(new DiffLine(DiffLineKind.Hunk, line, null, null));
            }
            else if (line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal))
                result.Add(new DiffLine(DiffLineKind.FileHeader, line, null, null));
            else if (line.StartsWith('+'))
                result.Add(new DiffLine(DiffLineKind.Added, line, null, newLine++));
            else if (line.StartsWith('-'))
                result.Add(new DiffLine(DiffLineKind.Removed, line, oldLine++, null));
            else
                result.Add(new DiffLine(DiffLineKind.Context, line, oldLine++, newLine++));
        }
        return result;
    }
}
