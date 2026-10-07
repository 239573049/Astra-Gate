namespace Astra.Core.Privacy;

/// <summary>
/// Restores redaction placeholders in a (streaming) response before it reaches the client.
/// Chunks may split a placeholder anywhere, so a small tail is held back until it can no
/// longer be the prefix of a placeholder. With an empty map the restorer is a pass-through.
/// </summary>
public sealed class ChunkRestorer
{
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly int _holdback;
    private string _pending = "";

    public ChunkRestorer(IEnumerable<PrivacyRedaction> redactions)
    {
        foreach (var r in redactions) _map[r.Placeholder] = r.Original;
        _holdback = _map.Count == 0 ? 0 : _map.Keys.Max(k => k.Length) - 1;
    }

    /// <summary>Convenience overload for a plain placeholder → original map.</summary>
    public ChunkRestorer(IReadOnlyDictionary<string, string> placeholderMap)
    {
        foreach (var (placeholder, original) in placeholderMap) _map[placeholder] = original;
        _holdback = _map.Count == 0 ? 0 : _map.Keys.Max(k => k.Length) - 1;
    }

    public IReadOnlyDictionary<string, string> Map => _map;

    /// <summary>Feeds one upstream chunk; returns the text that is safe to write to the client now.</summary>
    public string Push(string chunk)
    {
        if (_map.Count == 0) return chunk;
        lock (_gate)
        {
            _pending += chunk;
            foreach (var (placeholder, original) in _map) _pending = _pending.Replace(placeholder, original);
            if (_pending.Length <= _holdback) return "";
            var emit = _pending[..^_holdback];
            _pending = _pending[^_holdback..];
            return emit;
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
}
