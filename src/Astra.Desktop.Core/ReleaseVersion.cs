namespace Astra.Desktop.Core;

/// <summary>
/// Version ordering for the desktop client: semver precedence, build metadata ignored, a release above its
/// prereleases. One of the places that must agree (AGENTS.md linked update 13): scripts/release-channel.mjs,
/// <c>UpdateCheckService.IsNewer</c>, packages/update-core/src/version.ts, docs/lib/releases.ts and this.
/// </summary>
public static class ReleaseVersion
{
    /// <summary>The desktop app expects the server's <c>apiVersion</c> in the "1.x" family (plan avalonia-client §5.4).</summary>
    public const int ExpectedApiMajor = 1;

    /// <summary>Greater than 0 when <paramref name="a"/> is newer, less than 0 when older, 0 when equal; null when either is unparsable.</summary>
    public static int? Compare(string? a, string? b)
    {
        if (Parse(a) is not { } x || Parse(b) is not { } y) return null;
        for (var i = 0; i < 3; i++)
        {
            if (x.Core[i] != y.Core[i]) return x.Core[i] > y.Core[i] ? 1 : -1;
        }
        // A release ranks above any prerelease of the same core.
        if (x.Pre.Length == 0 || y.Pre.Length == 0) return y.Pre.Length == 0 ? (x.Pre.Length == 0 ? 0 : -1) : 1;
        for (var i = 0; i < Math.Min(x.Pre.Length, y.Pre.Length); i++)
        {
            var (p, q) = (x.Pre[i], y.Pre[i]);
            if (p == q) continue;
            var (np, nq) = (ulong.TryParse(p, out var pv), ulong.TryParse(q, out var qv));
            if (np && nq) return pv > qv ? 1 : -1;
            if (np != nq) return np ? -1 : 1; // numeric identifiers sort below alphanumeric ones
            return string.CompareOrdinal(p, q) > 0 ? 1 : -1;
        }
        return x.Pre.Length.CompareTo(y.Pre.Length);
    }

    /// <summary>True when <paramref name="candidate"/> is strictly newer than <paramref name="current"/>; unparsable input is never newer.</summary>
    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    /// <summary>The major of a version-ish string like "1.4" or "2", or null.</summary>
    public static int? MajorOf(string? version)
    {
        var text = (version ?? "").TrimStart();
        var end = 0;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        if (end == 0 || (end < text.Length && text[end] != '.')) return null;
        return int.TryParse(text.AsSpan(0, end), out var major) ? major : null;
    }

    public static bool IsApiVersionCompatible(string? serverApiVersion, int expectedMajor = ExpectedApiMajor)
        => MajorOf(serverApiVersion) is { } major && major == expectedMajor;

    private static (int[] Core, string[] Pre)? Parse(string? version)
    {
        var text = (version ?? "").Trim().Split('+', 2)[0];
        var dash = text.IndexOf('-');
        var core = (dash < 0 ? text : text[..dash]).TrimStart('v', 'V').Split('.');
        if (core.Length is 0 or > 3) return null;
        var numbers = new int[3];
        for (var i = 0; i < core.Length; i++)
        {
            if (!int.TryParse(core[i], out numbers[i]) || numbers[i] < 0) return null;
        }
        return (numbers, dash < 0 ? [] : text[(dash + 1)..].Split('.'));
    }
}
