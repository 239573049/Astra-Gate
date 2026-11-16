using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace Astra.Core.Privacy;

/// <summary>
/// Makes the placeholders the privacy guard puts in place of sensitive values, and remembers what it has masked.
///
/// A placeholder is <c>{{KIND_xxxxxxxx}}</c>: the category as upper-case words and 40 bits of a keyed hash
/// (HMAC-SHA256 over category and value, with a per-install key) in lower-case base32. The same value therefore
/// always gets the same placeholder — in every request of a conversation, and across restarts when the key is
/// persisted — which keeps the model's view of a conversation consistent and the provider's prompt cache valid,
/// while the key means a placeholder reveals nothing about its value and cannot be brute-forced offline from a log.
/// The format follows magpie's (MIT): braces and a word-like body are what models copy back unmangled.
///
/// "Once masked, always masked": a value masked once is remembered in memory (bounded, FIFO, this process only,
/// never written anywhere) and masked wherever it shows up later, even where no rule would match it on its own
/// (a name or an id inside a sentence). The remembered values are what the guard already holds per request to restore
/// responses, kept a little longer.
/// </summary>
public sealed class PlaceholderMint
{
    /// <summary>The most distinct values remembered; the oldest go first.</summary>
    public const int MaxKnown = 4096;

    /// <summary>Shorter values are never remembered: they would match inside ordinary words.</summary>
    public const int MinKnownLength = 6;

    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    private readonly byte[] _key;
    private readonly object _gate = new();
    private readonly Dictionary<string, KnownValue> _known = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private KnownSet? _set;
    private int _generation;

    /// <param name="key">The HMAC key (32 random bytes persisted per install); a fresh random one when null.</param>
    public PlaceholderMint(byte[]? key = null) => _key = key is { Length: > 0 } ? (byte[])key.Clone() : RandomNumberGenerator.GetBytes(32);

    /// <summary>A new random key, the form the guard persists (protected) per install.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    /// <summary>The placeholder kind of a category: <c>credit_card</c> → <c>CREDIT_CARD</c>.</summary>
    public static string KindOf(string category)
    {
        var kind = new StringBuilder(category.Length);
        foreach (var c in category) kind.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        return kind.Length == 0 ? "VALUE" : kind.ToString();
    }

    /// <summary>The placeholder for <paramref name="value"/> of <paramref name="category"/>: stable for one key.</summary>
    public string Placeholder(string category, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(category) + 1 + Encoding.UTF8.GetByteCount(value);
        Span<byte> buffer = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        var n = Encoding.UTF8.GetBytes(category, buffer);
        buffer[n++] = 0;
        Encoding.UTF8.GetBytes(value, buffer[n..]);
        Span<byte> mac = stackalloc byte[HMACSHA256.HashSizeInBytes];
        HMACSHA256.HashData(_key, buffer, mac);

        // 5 bytes = 40 bits = 8 base32 digits
        ulong bits = 0;
        for (var i = 0; i < 5; i++) bits = (bits << 8) | mac[i];
        var kind = KindOf(category);
        var sb = new StringBuilder(2 + kind.Length + 1 + 8 + 2);
        sb.Append("{{").Append(kind).Append('_');
        for (var shift = 35; shift >= 0; shift -= 5) sb.Append(Alphabet[(int)((bits >> shift) & 31)]);
        return sb.Append("}}").ToString();
    }

    /// <summary>True when <paramref name="text"/> is one of this format's placeholders (or an older <c>[REDACTED:…]</c> one).</summary>
    public static bool LooksLikePlaceholder(string text) =>
        text.StartsWith("{{", StringComparison.Ordinal) && text.EndsWith("}}", StringComparison.Ordinal) ||
        text.StartsWith("[REDACTED:", StringComparison.Ordinal) && text.EndsWith(']');

    /// <summary>Remembers values that were just masked, so later text holding them is masked too.</summary>
    public void Remember(IEnumerable<PrivacyRedaction> redactions)
    {
        lock (_gate)
        {
            foreach (var r in redactions)
            {
                if (r.Original.Length < MinKnownLength || _known.ContainsKey(r.Original)) continue;
                _known[r.Original] = new KnownValue(r.Original, r.Category, r.RuleId);
                _order.Enqueue(r.Original);
                while (_known.Count > MaxKnown) _known.Remove(_order.Dequeue());
                _set = null;
                _generation++;
            }
        }
    }

    /// <summary>How many distinct values are remembered.</summary>
    public int KnownCount
    {
        get { lock (_gate) return _known.Count; }
    }

    /// <summary>Whether a remembered value occurs in <paramref name="text"/> (as a word of its own).</summary>
    public bool ContainsKnown(string text) => Snapshot().Set?.Occurs(text) == true;

    /// <summary>
    /// The remembered values as a searchable snapshot (null when nothing is remembered) with its generation, which
    /// bumps whenever the set changes — a "this text is clean" verdict is only good for the generation it was made in.
    /// </summary>
    internal (KnownSet? Set, int Generation) Snapshot()
    {
        lock (_gate)
        {
            if (_known.Count == 0) return (null, _generation);
            return (_set ??= new KnownSet(_known.Values), _generation);
        }
    }

    /// <summary>What the mint remembers about one masked value.</summary>
    internal sealed record KnownValue(string Value, string Category, string RuleId);

    /// <summary>An immutable multi-string index over the remembered values.</summary>
    internal sealed class KnownSet
    {
        private readonly SearchValues<string> _search;
        private readonly Dictionary<char, KnownValue[]> _byFirst;

        public KnownSet(IEnumerable<KnownValue> values)
        {
            var all = values.ToArray();
            _search = SearchValues.Create(all.Select(v => v.Value).ToArray(), StringComparison.Ordinal);
            _byFirst = all.GroupBy(v => v.Value[0])
                .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Value.Length).ToArray());
        }

        /// <summary>Whether any remembered value occurs in <paramref name="text"/> (allocation-free).</summary>
        public bool Occurs(ReadOnlySpan<char> text)
        {
            var offset = 0;
            while (offset < text.Length)
            {
                var i = text[offset..].IndexOfAny(_search);
                if (i < 0) return false;
                i += offset;
                if (MatchAt(text, i) is not null) return true;
                offset = i + 1;
            }
            return false;
        }

        /// <summary>Every non-overlapping occurrence, left to right, the longest value first at a position.</summary>
        public List<(int Index, int Length, KnownValue Value)> Find(string text)
        {
            var found = new List<(int, int, KnownValue)>();
            var offset = 0;
            while (offset < text.Length)
            {
                var i = text.AsSpan(offset).IndexOfAny(_search);
                if (i < 0) break;
                i += offset;
                if (MatchAt(text, i) is { } hit)
                {
                    found.Add((i, hit.Value.Length, hit));
                    offset = i + hit.Value.Length;
                }
                else offset = i + 1;
            }
            return found;
        }

        private KnownValue? MatchAt(ReadOnlySpan<char> text, int index)
        {
            if (!_byFirst.TryGetValue(text[index], out var candidates)) return null;
            foreach (var candidate in candidates)
            {
                var value = candidate.Value;
                if (!text[index..].StartsWith(value, StringComparison.Ordinal)) continue;
                // A word is a word of its own: no letter, digit or '_' beside a value that starts / ends like one.
                if (index > 0 && IsWord(value[0]) && IsWord(text[index - 1])) continue;
                var end = index + value.Length;
                if (end < text.Length && IsWord(value[^1]) && IsWord(text[end])) continue;
                return candidate;
            }
            return null;
        }

        private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
