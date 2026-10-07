using System.Text;

namespace Astra.Clients.Editing;

/// <summary>
/// Line-based, format-preserving editor for .env files: get/set/remove KEY=value pairs while
/// preserving all other lines, comments, ordering, quoting and line endings. Every mutation is
/// verified by re-reading the result (throws <see cref="EditorException"/> on mismatch).
/// </summary>
public sealed class DotEnvEditor
{
    private readonly string _text;

    public DotEnvEditor(string text) => _text = text;

    /// <summary>Current document text (after any mutations).</summary>
    public string Text => _text;

    /// <summary>The logical (unquoted) value of <paramref name="key"/>, or null when absent.</summary>
    public string? Get(string key)
    {
        foreach (var (line, _) in EnumerateLines(_text))
        {
            if (!TrySplit(line, key, out var valueText)) continue;
            return Unquote(valueText);
        }
        return null;
    }

    /// <summary>The raw value source text (as written after '='), or null when absent. Used for byte-exact restores.</summary>
    public string? GetRaw(string key)
    {
        foreach (var (line, _) in EnumerateLines(_text))
        {
            if (!TrySplit(line, key, out var valueText)) continue;
            return valueText;
        }
        return null;
    }

    /// <summary>
    /// Sets the key to the raw value source text <paramref name="rawValueText"/>, spliced verbatim after
    /// the '=' (used for byte-exact restores). The raw text must be what <see cref="Get"/> can parse back.
    /// </summary>
    public DotEnvEditor SetRaw(string key, string rawValueText)
    {
        ValidateKey(key);
        if (string.IsNullOrWhiteSpace(rawValueText)) throw new ArgumentException("Raw value text is empty.", nameof(rawValueText));
        var lines = SplitKeepingTerminators(_text);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!TrySplit(lines[i], key, out _)) continue;
            var eq = IndexOfValueStart(lines[i]);
            var term = lines[i].EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : lines[i].EndsWith('\n') ? "\n" : "";
            lines[i] = lines[i][..eq] + rawValueText + term;
            return VerifyRaw(string.Concat(lines), key, rawValueText);
        }
        var nl = TextTool.NewlineOf(_text);
        var prefix = _text.Length == 0 ? "" : _text.EndsWith('\n') ? "" : nl;
        return VerifyRaw(_text + prefix + key + "=" + rawValueText + nl, key, rawValueText);
    }

    /// <summary>True when the key exists (commented lines do not count).</summary>
    public bool Has(string key)
    {
        foreach (var (line, _) in EnumerateLines(_text))
            if (TrySplit(line, key, out _)) return true;
        return false;
    }

    /// <summary>
    /// Sets the key to <paramref name="value"/>. The value is written raw when it only contains safe
    /// characters, and double-quoted (with escaping) otherwise. Creates the key at end of file when absent.
    /// </summary>
    public DotEnvEditor Set(string key, string value)
    {
        ValidateKey(key);
        if (value.Contains('\n') || value.Contains('\r')) throw new EditorException("Multi-line values are not supported in .env files.");
        var valueText = FormatValue(value);

        var lines = SplitKeepingTerminators(_text);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!TrySplit(lines[i], key, out _)) continue;
            var eq = IndexOfValueStart(lines[i]);
            var term = lines[i].EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : lines[i].EndsWith('\n') ? "\n" : "";
            lines[i] = lines[i][..eq] + valueText + term;
            var updated = string.Concat(lines);
            return Verify(updated, key, value);
        }

        var nl = TextTool.NewlineOf(_text);
        var prefix = _text.Length == 0 ? "" : _text.EndsWith('\n') ? "" : nl;
        var appended = prefix + key + "=" + valueText + nl;
        return Verify(_text + appended, key, value);
    }

    /// <summary>Removes the key's line (when the value was the last line, the extra newline goes with it). No-op when absent.</summary>
    public DotEnvEditor Remove(string key)
    {
        ValidateKey(key);
        var lines = SplitKeepingTerminators(_text);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!TrySplit(lines[i], key, out _)) continue;
            lines.RemoveAt(i);
            return VerifyRemoved(string.Concat(lines), key);
        }
        return this;
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<(string Text, int Start)> EnumerateLines(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var nl = text.IndexOf('\n', start);
            var end = nl < 0 ? text.Length : nl + 1;
            yield return (text[start..end], start);
            start = end;
        }
    }

    private static List<string> SplitKeepingTerminators(string text)
    {
        var list = new List<string>();
        foreach (var (line, _) in EnumerateLines(text)) list.Add(line);
        return list;
    }

    private static bool TrySplit(string line, string key, out string valueText)
    {
        valueText = "";
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#')) return false;
        if (trimmed.StartsWith("export", StringComparison.Ordinal)
            && (trimmed.Length == 6 || char.IsWhiteSpace(trimmed[6])))
            trimmed = trimmed[6..].TrimStart();
        var eq = trimmed.IndexOf('=');
        if (eq <= 0) return false;
        var name = trimmed[..eq].TrimEnd();
        if (!string.Equals(name, key, StringComparison.Ordinal)) return false;
        valueText = trimmed[(eq + 1)..].TrimEnd('\r', '\n').Trim();
        return true;
    }

    private static int IndexOfValueStart(string line)
    {
        var eq = line.IndexOf('=');
        return eq + 1;
    }

    private static void ValidateKey(string key)
    {
        if (key.Length == 0 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '.'))
            throw new EditorException($"Invalid .env key '{key}'.");
    }

    /// <summary>Quotes a value only when needed (safe characters stay unquoted).</summary>
    private static string FormatValue(string value)
    {
        if (value.Length == 0) return "\"\"";
        var safe = true;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '/' or ':' or '+' or '@') continue;
            safe = false;
            break;
        }
        if (safe) return value;
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string Unquote(string valueText)
    {
        if (valueText.Length >= 2 && valueText[0] == '"' && valueText[^1] == '"')
        {
            var sb = new StringBuilder(valueText.Length);
            for (var i = 1; i < valueText.Length - 1; i++)
            {
                var c = valueText[i];
                if (c == '\\' && i + 1 < valueText.Length - 1)
                {
                    var n = valueText[++i];
                    switch (n)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': break;
                        default: sb.Append(n); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
        if (valueText.Length >= 2 && valueText[0] == '\'' && valueText[^1] == '\'')
            return valueText[1..^1];
        return valueText;
    }

    private DotEnvEditor Verify(string updated, string key, string expectedLogical)
    {
        var actual = new DotEnvEditor(updated).Get(key);
        if (actual != expectedLogical)
            throw new EditorException($"Self-check failed: {key}={actual ?? "<absent>"}, expected {expectedLogical}.");
        return new DotEnvEditor(updated);
    }

    private DotEnvEditor VerifyRaw(string updated, string key, string expectedRaw)
    {
        var editor = new DotEnvEditor(updated);
        if (editor.GetRaw(key) != expectedRaw)
            throw new EditorException($"Self-check failed: {key}={editor.GetRaw(key) ?? "<absent>"}, expected {expectedRaw}.");
        return new DotEnvEditor(updated);
    }

    private DotEnvEditor VerifyRemoved(string updated, string key)
    {
        if (new DotEnvEditor(updated).Has(key))
            throw new EditorException($"Self-check failed: {key} still present after remove.");
        return new DotEnvEditor(updated);
    }
}
