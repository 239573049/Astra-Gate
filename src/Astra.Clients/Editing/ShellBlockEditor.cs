namespace Astra.Clients.Editing;

/// <summary>
/// Line-based editor for Astra-managed blocks in a POSIX shell rc file (<c>~/.zshrc</c>, <c>~/.bashrc</c>). A block is
/// delimited by <c># &gt;&gt;&gt; astra &lt;id&gt; &gt;&gt;&gt;</c> and <c># &lt;&lt;&lt; astra &lt;id&gt; &lt;&lt;&lt;</c> lines; everything outside
/// the markers is preserved as-is. Block bodies are exchanged with "\n" line endings. Every mutation is verified by
/// re-reading the block (throws <see cref="EditorException"/> on mismatch).
/// </summary>
public sealed class ShellBlockEditor
{
    private readonly string _text;

    public ShellBlockEditor(string text) => _text = text;

    /// <summary>Current document text (after any mutations).</summary>
    public string Text => _text;

    public static string BeginMarker(string id) => $"# >>> astra {id} >>>";

    public static string EndMarker(string id) => $"# <<< astra {id} <<<";

    /// <summary>The block body (lines joined by "\n", without the markers), or null when the block is absent.</summary>
    public string? Get(string id)
    {
        var block = Find(id);
        return block is null ? null : BodyOf(_text, block.Value);
    }

    /// <summary>True when the block exists.</summary>
    public bool Has(string id) => Find(id) is not null;

    /// <summary>
    /// Sets the block body. Replaces the block in place when it exists; otherwise appends it at the end of the file.
    /// </summary>
    public ShellBlockEditor Set(string id, string body)
    {
        ValidateId(id);
        var normalized = NormalizeBody(body);
        var nl = TextTool.NewlineOf(_text);
        var block = BeginMarker(id) + nl + string.Join(nl, normalized.Split('\n')) + nl + EndMarker(id) + nl;
        var found = Find(id);
        if (found is { } span)
            return Verify(_text[..span.Start] + block + _text[span.End..], id, normalized);
        var prefix = _text.Length == 0 || _text.EndsWith('\n') ? "" : nl;
        return Verify(_text + prefix + block, id, normalized);
    }

    /// <summary>Removes the block including its markers. No-op when absent.</summary>
    public ShellBlockEditor Remove(string id)
    {
        ValidateId(id);
        if (Find(id) is not { } span) return this;
        return Verify(_text[..span.Start] + _text[span.End..], id, null);
    }

    // ---------------------------------------------------------------- helpers

    private readonly record struct Span(int Start, int End, int BodyStart, int BodyEnd);

    /// <summary>The character range of the whole block (begin marker line through end marker line, terminators included).</summary>
    private Span? Find(string id)
    {
        var begin = BeginMarker(id);
        var end = EndMarker(id);
        var start = -1;
        var bodyStart = -1;
        var offset = 0;
        while (offset < _text.Length)
        {
            var next = _text.IndexOf('\n', offset);
            var lineEnd = next < 0 ? _text.Length : next + 1;
            var content = _text[offset..lineEnd].TrimEnd('\r', '\n');
            if (start < 0)
            {
                if (content == begin)
                {
                    start = offset;
                    bodyStart = lineEnd;
                }
            }
            else if (content == end)
            {
                return new Span(start, lineEnd, bodyStart, offset);
            }
            offset = lineEnd;
        }
        if (start >= 0) throw new EditorException($"Unterminated Astra block '{id}' (missing '{end}').");
        return null;
    }

    private static string BodyOf(string text, Span span)
    {
        var body = text[span.BodyStart..span.BodyEnd];
        return body.Replace("\r\n", "\n").TrimEnd('\n');
    }

    private static string NormalizeBody(string body)
    {
        var normalized = body.Replace("\r\n", "\n").TrimEnd('\n');
        if (normalized.Contains('\r')) throw new EditorException("Shell block bodies must not contain carriage returns.");
        return normalized;
    }

    private ShellBlockEditor Verify(string updated, string id, string? expected)
    {
        var editor = new ShellBlockEditor(updated);
        var actual = editor.Get(id);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new EditorException($"Shell block '{id}' did not verify after the edit.");
        return editor;
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Any(c => char.IsWhiteSpace(c) || c is '>' or '<'))
            throw new ArgumentException("Shell block ids must be non-empty and contain no whitespace or angle brackets.", nameof(id));
    }
}
