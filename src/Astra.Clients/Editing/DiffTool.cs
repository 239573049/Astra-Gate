using System.Text;

namespace Astra.Clients.Editing;

/// <summary>Minimal unified diff generator used for change previews.</summary>
public static class DiffTool
{
    private const int Context = 3;

    /// <summary>
    /// A unified diff (with @@ hunks) between two versions of a file, using LF line endings in the output.
    /// </summary>
    public static string Unified(string filePath, string before, string after)
    {
        var a = SplitLines(before);
        var b = SplitLines(after);
        var ops = DiffOps(a, b); // (kind ' '/'-'/'+', text)
        var sb = new StringBuilder();
        sb.Append("--- a/").Append(filePath).Append('\n');
        sb.Append("+++ b/").Append(filePath).Append('\n');

        var changeIdx = new List<int>();
        for (var i = 0; i < ops.Count; i++)
            if (ops[i].Kind != ' ') changeIdx.Add(i);
        if (changeIdx.Count == 0) return sb.ToString();

        // Group change indices into runs separated by more than 2*Context equal lines.
        var groups = new List<(int Start, int End)>();
        var gStart = changeIdx[0];
        var gPrev = changeIdx[0];
        foreach (var ci in changeIdx.Skip(1))
        {
            if (ci - gPrev - 1 > 2 * Context) { groups.Add((gStart, gPrev + 1)); gStart = ci; }
            gPrev = ci;
        }
        groups.Add((gStart, gPrev + 1));

        var lineA = 0;
        var lineB = 0;
        var opLines = new (string Text, char Kind, int APos, int BPos)[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            opLines[i] = (ops[i].Text, ops[i].Kind, lineA, lineB);
            if (ops[i].Kind != '+') lineA++;
            if (ops[i].Kind != '-') lineB++;
        }

        foreach (var (cs, ce) in groups)
        {
            var start = Math.Max(cs - Context, 0);
            var end = Math.Min(ce + Context, ops.Count);
            var aStart = start < ops.Count ? opLines[start].APos : lineA;
            var bStart = start < ops.Count ? opLines[start].BPos : lineB;
            var aLen = 0;
            var bLen = 0;
            for (var i = start; i < end; i++)
            {
                if (ops[i].Kind != '+') aLen++;
                if (ops[i].Kind != '-') bLen++;
            }
            sb.Append($"@@ -{aStart + 1},{aLen} +{bStart + 1},{bLen} @@\n");
            for (var i = start; i < end; i++)
            {
                sb.Append(ops[i].Kind).Append(ops[i].Text).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            lines.Add(text[start..end]);
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    private static List<(char Kind, string Text)> DiffOps(List<string> a, List<string> b)
    {
        // LCS over lines (config files are small; O(n*m) is fine).
        var table = new int[a.Count + 1, b.Count + 1];
        for (var i = a.Count - 1; i >= 0; i--)
            for (var j = b.Count - 1; j >= 0; j--)
                table[i, j] = string.Equals(a[i], b[j], StringComparison.Ordinal)
                    ? table[i + 1, j + 1] + 1
                    : Math.Max(table[i + 1, j], table[i, j + 1]);

        var ops = new List<(char Kind, string Text)>();
        int i2 = 0, j2 = 0;
        while (i2 < a.Count && j2 < b.Count)
        {
            if (string.Equals(a[i2], b[j2], StringComparison.Ordinal)) { ops.Add((' ', a[i2])); i2++; j2++; }
            else if (table[i2 + 1, j2] >= table[i2, j2 + 1]) { ops.Add(('-', a[i2])); i2++; }
            else { ops.Add(('+', b[j2])); j2++; }
        }
        while (i2 < a.Count) { ops.Add(('-', a[i2])); i2++; }
        while (j2 < b.Count) { ops.Add(('+', b[j2])); j2++; }
        return ops;
    }
}
