using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Astra.Core.Privacy;

/// <summary>
/// What a regex match must also be to count: checksums (Luhn, the Chinese ID check digit, IBAN mod 97), ranges (SSN,
/// dates, public IPs) and the example values docs print. They cut the false positives a bare pattern has — an order
/// number of 16 digits is not a card, an address range from the RFCs is not a person's. Each takes the matched text
/// (and, for those that need context, the text around it) and returns the length to mask, 0 to reject the match.
/// Ported from magpie's <c>internal/redact</c> (MIT), the rules part only.
/// </summary>
internal static class PrivacyChecks
{
    public static int CreditCard(string text, int index, int length) => Luhn(text.AsSpan(index, length)) ? length : 0;

    /// <summary>The Luhn checksum over a run of digits.</summary>
    public static bool Luhn(ReadOnlySpan<char> digits)
    {
        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var c = digits[i];
            if (c is < '0' or > '9') return false;
            var d = c - '0';
            if (alternate)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            alternate = !alternate;
        }
        return digits.Length > 0 && sum % 10 == 0;
    }

    private static readonly int[] CnIdWeights = [7, 9, 10, 5, 8, 4, 2, 1, 6, 3, 7, 9, 10, 5, 8, 4, 2];
    private const string CnIdCheck = "10X98765432";

    /// <summary>A mainland resident ID: a real birth date and the ISO 7064 mod 11-2 check character.</summary>
    public static int CnId(string text, int index, int length)
    {
        if (length != 18) return 0;
        var v = text.AsSpan(index, length);
        if (!DateTime.TryParseExact(v.Slice(6, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return 0;
        var sum = 0;
        for (var i = 0; i < 17; i++) sum += (v[i] - '0') * CnIdWeights[i];
        return CnIdCheck[sum % 11] == char.ToUpperInvariant(v[17]) ? length : 0;
    }

    /// <summary>A US SSN in the ranges the SSA issues, not the ones printed in ads and examples.</summary>
    public static int Ssn(string text, int index, int length)
    {
        var d = text.Substring(index, length).Replace("-", "").Replace(" ", "");
        if (d.Length != 9 || d.StartsWith("000", StringComparison.Ordinal) || d.StartsWith("666", StringComparison.Ordinal) ||
            d[0] == '9' || d.AsSpan(3, 2).SequenceEqual("00") || d.AsSpan(5).SequenceEqual("0000")) return 0;
        if (d is "123456789" or "078051120" or "219099999") return 0;
        return d.Distinct().Count() > 1 ? length : 0;
    }

    /// <summary>A passport number needs digits: five at least, and not one character repeated.</summary>
    public static int Passport(string text, int index, int length)
    {
        var v = text.AsSpan(index, length);
        var digits = 0;
        foreach (var c in v) if (c is >= '0' and <= '9') digits++;
        return digits >= 5 && v.IndexOfAnyExcept(v[0]) >= 0 ? length : 0;
    }

    private static readonly Dictionary<string, int> IbanLength = new()
    {
        ["AD"] = 24, ["AE"] = 23, ["AL"] = 28, ["AT"] = 20, ["AZ"] = 28, ["BA"] = 20, ["BE"] = 16, ["BG"] = 22, ["BH"] = 22, ["BI"] = 27,
        ["BR"] = 29, ["BY"] = 28, ["CH"] = 21, ["CR"] = 22, ["CY"] = 28, ["CZ"] = 24, ["DE"] = 22, ["DJ"] = 27, ["DK"] = 18, ["DO"] = 28,
        ["EE"] = 20, ["EG"] = 29, ["ES"] = 24, ["FI"] = 18, ["FK"] = 18, ["FO"] = 18, ["FR"] = 27, ["GB"] = 22, ["GE"] = 22, ["GI"] = 23,
        ["GL"] = 18, ["GR"] = 27, ["GT"] = 28, ["HN"] = 28, ["HR"] = 21, ["HU"] = 28, ["IE"] = 22, ["IL"] = 23, ["IQ"] = 23, ["IS"] = 26,
        ["IT"] = 27, ["JO"] = 30, ["KW"] = 30, ["KZ"] = 20, ["LB"] = 28, ["LC"] = 32, ["LI"] = 21, ["LT"] = 20, ["LU"] = 20, ["LV"] = 21,
        ["LY"] = 25, ["MC"] = 27, ["MD"] = 24, ["ME"] = 22, ["MK"] = 19, ["MN"] = 20, ["MR"] = 27, ["MT"] = 31, ["MU"] = 30, ["NI"] = 28,
        ["NL"] = 18, ["NO"] = 15, ["OM"] = 23, ["PK"] = 24, ["PL"] = 28, ["PS"] = 29, ["PT"] = 25, ["QA"] = 29, ["RO"] = 24, ["RS"] = 22,
        ["RU"] = 33, ["SA"] = 24, ["SC"] = 31, ["SD"] = 18, ["SE"] = 24, ["SI"] = 19, ["SK"] = 24, ["SM"] = 27, ["SO"] = 23, ["ST"] = 25,
        ["SV"] = 28, ["TL"] = 23, ["TN"] = 24, ["TR"] = 26, ["UA"] = 29, ["VA"] = 22, ["VG"] = 24, ["XK"] = 20, ["YE"] = 30,
    };

    /// <summary>An IBAN, as one word or in groups of four: its country's length and mod 97 equal to 1. A trailing capital word the pattern took as a last group is let go.</summary>
    public static int Iban(string text, int index, int length)
    {
        var end = index + length;
        while (true)
        {
            if (IbanOk(text.AsSpan(index, end - index))) return end - index;
            var space = text.AsSpan(index, end - index).LastIndexOf(' ');
            if (space < 0) return 0;
            end = index + space;
        }
    }

    private static bool IbanOk(ReadOnlySpan<char> value)
    {
        Span<char> v = stackalloc char[Math.Min(value.Length, 64)];
        var n = 0;
        foreach (var c in value)
        {
            if (c == ' ') continue;
            if (n == v.Length) return false;
            v[n++] = c;
        }
        v = v[..n];
        if (v.Length < 15 || !IbanLength.TryGetValue(v[..2].ToString(), out var expected) || expected != v.Length) return false;
        var remainder = 0;
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[(i + 4) % v.Length];
            if (c is >= '0' and <= '9') remainder = (remainder * 10 + (c - '0')) % 97;
            else if (c is >= 'A' and <= 'Z') remainder = (remainder * 100 + (c - 'A') + 10) % 97;
            else return false;
        }
        return remainder == 1;
    }

    /// <summary>A MAC address, but not the ones docs and code write for "an address", the broadcast one, or twelve decimal digits.</summary>
    public static int Mac(string text, int index, int length)
    {
        var l = text.Substring(index, length).ToLowerInvariant().Replace('-', ':');
        if (l is "00:00:00:00:00:00" or "ff:ff:ff:ff:ff:ff" or "00:11:22:33:44:55" or "aa:bb:cc:dd:ee:ff" or "01:23:45:67:89:ab" or "12:34:56:78:9a:bc")
            return 0;
        return l.AsSpan().IndexOfAnyInRange('a', 'f') >= 0 ? length : 0;
    }

    /// <summary>A date that can be someone's birthday: a real day, from 1900 on, and over a year ago (today's date after a birthday label is not one).</summary>
    public static int Birthday(string text, int index, int length)
    {
        var v = text.Substring(index, length);
        var numbers = new List<string>();
        var start = -1;
        for (var i = 0; i <= v.Length; i++)
        {
            var digit = i < v.Length && v[i] is >= '0' and <= '9';
            if (digit && start < 0) start = i;
            else if (!digit && start >= 0)
            {
                numbers.Add(v[start..i]);
                start = -1;
            }
        }
        var yearAgo = DateTime.Now.AddYears(-1);
        bool Valid(int y, int m, int d)
        {
            if (y < 1900 || y > 2099 || m < 1 || m > 12 || d < 1 || d > 31) return false;
            return DateTime.DaysInMonth(y, m) >= d && new DateTime(y, m, d) < yearAgo;
        }

        var ok = false;
        if (numbers.Count == 2)
        {
            // March 5, 1990 or 5 March 1990
            var lower = v.ToLowerInvariant();
            string[] months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];
            for (var m = 0; m < 12; m++)
            {
                if (!lower.Contains(months[m], StringComparison.Ordinal)) continue;
                int d = int.Parse(numbers[0], CultureInfo.InvariantCulture), y = int.Parse(numbers[1], CultureInfo.InvariantCulture);
                if (numbers[0].Length == 4) (d, y) = (y, d);
                ok = Valid(y, m + 1, d);
                break;
            }
        }
        else if (numbers.Count == 3 && numbers.All(n => n.Length <= 4))
        {
            var n = numbers.Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            ok = numbers[0].Length == 4 ? Valid(n[0], n[1], n[2]) : Valid(n[2], n[0], n[1]) || Valid(n[2], n[1], n[0]);
        }
        return ok ? length : 0;
    }

    /// <summary>A serial number: six characters at least, one of them a digit, and not one character repeated.</summary>
    public static int Serial(string text, int index, int length)
    {
        var v = text.AsSpan(index, length);
        return v.Length >= 6 && v.IndexOfAnyInRange('0', '9') >= 0 && v.IndexOfAnyExcept(v[0]) >= 0 ? length : 0;
    }

    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "shared", "public", "guest", "default", "default user", "all users", "user", "username", "user_name", "yourname", "your-name",
        "your_name", "yourusername", "you", "me", "name", "example", "foo", "bar", "someone", "john", "jdoe", "runner", "node", "vscode",
        "codespace", "linuxbrew", "...",
    };

    /// <summary>The user name in a home folder, unless it is what docs and containers write for one.</summary>
    public static int HomeUser(string text, int index, int length)
    {
        var end = index + length;
        while (end > index && text[end - 1] == '.') end--;
        if (end == index) return 0;
        return NotNames.Contains(text.Substring(index, end - index).ToLowerInvariant()) ? 0 : end - index;
    }

    private static readonly HashSet<string> Resolvers = new(StringComparer.Ordinal)
    {
        "8.8.8.8", "8.8.4.4", "1.1.1.1", "1.0.0.1", "9.9.9.9", "149.112.112.112", "208.67.222.222", "208.67.220.220", "114.114.114.114",
        "223.5.5.5", "223.6.6.6", "119.29.29.29", "180.76.76.76", "2001:4860:4860::8888", "2001:4860:4860::8844",
        "2606:4700:4700::1111", "2606:4700:4700::1001",
    };

    private static bool IsAlnum(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    /// <summary>
    /// A public IPv4 address: not a private, loopback, link-local, CGNAT, documentation or multicast one, not a public
    /// resolver everyone uses, and not a piece of a longer dotted number or of a version (1.2.3.4.5, v1.2.3.4,
    /// Chrome/120.0.6099.109, "version 2.31.0.1").
    /// </summary>
    public static int PublicIpv4(string text, int index, int length)
    {
        var end = index + length;
        if (index > 0)
        {
            var c = text[index - 1];
            if (IsAlnum(c) || c is '.' or '_' or '-' or '=' or '~' or '^') return 0;
            if (c == '/' && index >= 2 && (IsAlnum(text[index - 2]) || text[index - 2] is '.' or '_' or '-')) return 0;
        }
        if (end < text.Length)
        {
            var c = text[end];
            if (IsAlnum(c) || c is '_' or '-' || c == '.' && end + 1 < text.Length && text[end + 1] is >= '0' and <= '9') return 0;
        }
        var before = text.AsSpan(Math.Max(0, index - 16), index - Math.Max(0, index - 16)).ToString().ToLowerInvariant().TrimEnd(' ', '\t', ':', '=', '"', '\'');
        foreach (var w in new[] { "version", "ver", "release", "build", "rev", "版本" })
        {
            if (!before.EndsWith(w, StringComparison.Ordinal)) continue;
            if (before.Length == w.Length || !(IsAlnum(before[before.Length - w.Length - 1]) || before[before.Length - w.Length - 1] == '_')) return 0;
        }
        var value = text.Substring(index, length);
        return IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && IsPublicV4(ip, value) ? length : 0;
    }

    private static bool IsPublicV4(IPAddress ip, string text)
    {
        if (Resolvers.Contains(text)) return false;
        Span<byte> b = stackalloc byte[4];
        if (!ip.TryWriteBytes(b, out _)) return false;
        return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224 ||
                 b[0] == 100 && b[1] is >= 64 and <= 127 ||
                 b[0] == 169 && b[1] == 254 ||
                 b[0] == 172 && b[1] is >= 16 and <= 31 ||
                 b[0] == 192 && b[1] == 0 && b[2] is 0 or 2 ||
                 b[0] == 192 && b[1] == 168 ||
                 b[0] == 198 && b[1] is 18 or 19 ||
                 b[0] == 198 && b[1] == 51 && b[2] == 100 ||
                 b[0] == 203 && b[1] == 0 && b[2] == 113);
    }

    /// <summary>A global-unicast IPv6 address (2000::/3), not a documentation one or a public resolver.</summary>
    public static int PublicIpv6(string text, int index, int length)
    {
        var value = text.Substring(index, length);
        if (value.Count(c => c == ':') < 2 || Resolvers.Contains(value.ToLowerInvariant())) return 0;
        if (!IPAddress.TryParse(value, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return 0;
        Span<byte> b = stackalloc byte[16];
        if (!ip.TryWriteBytes(b, out _) || (b[0] & 0xe0) != 0x20) return 0;
        return b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8 ? 0 : length;
    }

    /// <summary>Whether <paramref name="domain"/> is one that exists for examples (RFC 2606 / 6761): never someone's mail.</summary>
    public static bool IsExampleDomain(ReadOnlySpan<char> domain)
    {
        var d = domain.ToString().TrimEnd('.').ToLowerInvariant();
        if (d is "localhost" or "example" or "test" or "invalid") return true;
        foreach (var suffix in new[] { ".example", ".test", ".invalid", ".localhost" })
            if (d.EndsWith(suffix, StringComparison.Ordinal)) return true;
        foreach (var root in new[] { "example.com", "example.org", "example.net" })
            if (d == root || d.EndsWith("." + root, StringComparison.Ordinal)) return true;
        return false;
    }
}
