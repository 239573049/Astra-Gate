using System.Text.Encodings.Web;
using System.Text.Json;

namespace Astra.Core.Privacy;

/// <summary>
/// Restores redaction placeholders in a (streaming) piece of text before it reaches the client.
/// Chunks may split a placeholder anywhere, so a tail that is still the beginning of a placeholder is held back until
/// it completes or can no longer be one (the next <see cref="Push"/> or <see cref="Flush"/>). Only such a tail is held:
/// ordinary text goes out at once. With an empty map the restorer is a pass-through.
///
/// <paramref name="jsonEscape"/> is for text that is itself a piece of a JSON document — a tool call's arguments arrive
/// as fragments of one, in a string value of the stream's own JSON — where the original goes back as it is written inside
/// a JSON string (quotes, backslashes and control characters escaped) rather than raw, so the arguments stay valid JSON.
/// </summary>
public sealed class ChunkRestorer
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly int _maxKey;
    private readonly bool _jsonEscape;
    private string _pending = "";

    public ChunkRestorer(IEnumerable<PrivacyRedaction> redactions, bool jsonEscape = false)
    {
        foreach (var r in redactions) _map[r.Placeholder] = jsonEscape ? EscapeJson(r.Original) : r.Original;
        _jsonEscape = jsonEscape;
        _maxKey = _map.Count == 0 ? 0 : _map.Keys.Max(k => k.Length);
    }

    /// <summary>Convenience overload for a plain placeholder → original map.</summary>
    public ChunkRestorer(IReadOnlyDictionary<string, string> placeholderMap, bool jsonEscape = false)
    {
        foreach (var (placeholder, original) in placeholderMap) _map[placeholder] = jsonEscape ? EscapeJson(original) : original;
        _jsonEscape = jsonEscape;
        _maxKey = _map.Count == 0 ? 0 : _map.Keys.Max(k => k.Length);
    }

    public IReadOnlyDictionary<string, string> Map => _map;

    /// <summary>Whether this restorer escapes originals for the inside of a JSON string.</summary>
    public bool JsonEscape => _jsonEscape;

    /// <summary>A value as written inside a JSON string, without the quotes.</summary>
    public static string EscapeJson(string value) =>
        JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).Value;

    /// <summary>Feeds one upstream chunk; returns the text that is safe to write to the client now.</summary>
    public string Push(string chunk)
    {
        if (_map.Count == 0) return chunk;
        lock (_gate)
        {
            var text = _pending.Length == 0 ? chunk : _pending + chunk;
            foreach (var (placeholder, original) in _map)
                if (text.Contains(placeholder, StringComparison.Ordinal)) text = text.Replace(placeholder, original, StringComparison.Ordinal);
            var hold = HeldTail(text);
            _pending = text[^hold..];
            return text[..^hold];
        }
    }

    /// <summary>Ends the stream; returns whatever was still held back.</summary>
    public string Flush()
    {
        lock (_gate)
        {
            var rest = _pending;
            _pending = "";
            return rest;
        }
    }

    // How much of the end of text is the beginning of some placeholder (a proper prefix: the whole one was just replaced).
    private int HeldTail(string text)
    {
        for (var start = Math.Max(0, text.Length - (_maxKey - 1)); start < text.Length; start++)
        {
            var tail = text.AsSpan(start);
            foreach (var key in _map.Keys)
                if (key.Length > tail.Length && key.AsSpan().StartsWith(tail, StringComparison.Ordinal)) return tail.Length;
        }
        return 0;
    }
}
