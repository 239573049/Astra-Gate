using System.Globalization;

namespace Astra.Plugins.V2;

/// <summary>
/// The semver a v2 manifest uses: <c>x.y.z</c> with an optional <c>-pre</c> (build metadata is ignored), compared by
/// precedence, and the npm-style ranges <c>engines</c> writes: comparators (<c>&gt;=1.2.0 &lt;2</c>), <c>^</c>, <c>~</c>,
/// <c>x</c> wildcards and <c>||</c>. A partial version (<c>1.2</c>) reads with zeros.
/// </summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string? Pre) : IComparable<SemVer>
{
    public static bool TryParse(string? text, out SemVer v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('v', 'V');
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        string? pre = null;
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0) return false;
        }
        var parts = s.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        var n = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return false;
        v = new SemVer(n[0], n[1], n[2], pre);
        return true;
    }

    /// <summary>Whether <paramref name="text"/> is a full <c>x.y.z[-pre]</c> version, as a manifest's <c>version</c> must be.</summary>
    public static bool IsFull(string? text) => TryParse(text, out _) && text!.Split('-', 2)[0].Split('+', 2)[0].Count(c => c == '.') == 2;

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Pre == other.Pre) return 0;
        if (Pre is null) return 1;
        if (other.Pre is null) return -1;
        var a = Pre.Split('.');
        var b = other.Pre.Split('.');
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            var an = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ai);
            var bn = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bi);
            c = an && bn ? ai.CompareTo(bi) : an ? -1 : bn ? 1 : string.CompareOrdinal(a[i], b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    /// <summary>This version without its prerelease part.</summary>
    public SemVer Release => this with { Pre = null };

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (Pre is null ? "" : "-" + Pre);

    /// <summary>
    /// Whether <paramref name="version"/> satisfies <paramref name="range"/> by precedence; an empty range allows anything and
    /// an unreadable one allows nothing. (The host compares its own <c>-preview.N</c> as its release, see <see cref="Release"/>.)
    /// </summary>
    public static bool Satisfies(SemVer version, string? range)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Trim() is "*" or "x") return true;
        foreach (var alternative in range.Split("||"))
        {
            var ok = true;
            var any = false;
            foreach (var token in Tokens(alternative))
            {
                any = true;
                if (!Comparator(token, out var low, out var high)) return false;
                if ((low is { } l && version < l) || (high is { } h && version >= h)) ok = false;
            }
            if (any && ok) return true;
        }
        return false;
    }

    // "1.2 - 2.0" is not supported; ">= 1.2" (a space after the operator) is joined back up.
    private static IEnumerable<string> Tokens(string alternative)
    {
        var parts = alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            if (p is ">=" or "<=" or ">" or "<" or "=" or "^" or "~" && i + 1 < parts.Length) p += parts[++i];
            yield return p;
        }
    }

    // One comparator as [low, high): low inclusive, high exclusive (null = unbounded).
    private static bool Comparator(string token, out SemVer? low, out SemVer? high)
    {
        low = high = null;
        string op = token.StartsWith(">=", StringComparison.Ordinal) ? ">=" : token.StartsWith("<=", StringComparison.Ordinal) ? "<="
            : token[0] is '>' or '<' or '=' or '^' or '~' ? token[..1] : "";
        var rest = token[op.Length..].TrimStart('v');
        if (rest is "*" or "x" or "X") return op is "" or ">=" or "=";
        var parts = rest.Split('-', 2)[0].Split('.');
        var wild = parts.Length < 3 || parts.Any(p => p is "x" or "X" or "*");
        var given = parts.TakeWhile(p => p is not ("x" or "X" or "*")).Count();
        if (!TryParse(string.Join('.', parts.Take(given).DefaultIfEmpty("0")) + (rest.Contains('-') && !wild ? "-" + rest.Split('-', 2)[1] : ""), out var v))
            return false;
        SemVer Next() => given switch
        {
            <= 1 => new SemVer(v.Major + 1, 0, 0, "0"),
            2 => new SemVer(v.Major, v.Minor + 1, 0, "0"),
            _ => new SemVer(v.Major, v.Minor, v.Patch + 1, "0"),
        };
        switch (op)
        {
            case "" or "=":
                low = v;
                high = wild ? Next() : v.Pre is null ? new SemVer(v.Major, v.Minor, v.Patch + 1, "0") : v with { Pre = v.Pre + ".\uffff" };
                return true;
            case ">=":
                low = v;
                return true;
            case ">":
                low = wild ? Next() : new SemVer(v.Major, v.Minor, v.Patch + 1, "0");
                if (!wild && v.Pre is not null) low = new SemVer(v.Major, v.Minor, v.Patch, v.Pre + ".0");
                return true;
            case "<":
                high = v.Pre is null ? v with { Pre = "0" } : v;
                return true;
            case "<=":
                high = wild ? Next() : new SemVer(v.Major, v.Minor, v.Patch + 1, "0");
                return true;
            case "~":
                low = v;
                high = given <= 1 ? new SemVer(v.Major + 1, 0, 0, "0") : new SemVer(v.Major, v.Minor + 1, 0, "0");
                return true;
            case "^":
                low = v;
                high = v.Major > 0 || given <= 1 ? new SemVer(v.Major + 1, 0, 0, "0")
                    : v.Minor > 0 || given <= 2 ? new SemVer(0, v.Minor + 1, 0, "0")
                    : new SemVer(0, 0, v.Patch + 1, "0");
                return true;
        }
        return false;
    }
}
